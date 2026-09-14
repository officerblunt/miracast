using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Miracast.Receiver.Entities.EventArgs;

namespace Miracast.Receiver.Linux;

public sealed class VideoRenderer : IVideoRenderer, IAsyncDisposable
{
    private const int SigInt = 2;
    private const int SigTerm = 15;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Process? _gstreamer;
    private CancellationTokenSource? _playback;
    private Task? _framePump;
    private Task? _errorPump;
    private int _framePending;
    private long _lastFrameUnixTimeMilliseconds;
    private string? _lastGStreamerError;
    private bool _audioPlaybackEnabled;
    private bool _disposed;

    public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;
    public DateTimeOffset? LastFrameReceivedAt
    {
        get
        {
            var value = Interlocked.Read(ref _lastFrameUnixTimeMilliseconds);
            return value == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
    }

    public async Task PlayAsync(IVideoSource source, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The GStreamer renderer can only run on Linux.");
        if (source is not VideoSource linuxSource)
            throw new ArgumentException("The source is not a Linux RTP source.", nameof(source));
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!await HasGStreamerElementAsync("avdec_h264", cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The software H.264 decoder avdec_h264 is unavailable. "
                    + "Install the GStreamer libav plugin.");
            }

            _audioPlaybackEnabled = await HasGStreamerElementAsync("dvdlpcmdec", cancellationToken)
                .ConfigureAwait(false);
            var process = new Process
            {
                StartInfo = CreateGStreamerStartInfo(linuxSource, _audioPlaybackEnabled),
            };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Could not start gst-launch-1.0.");
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                process.Dispose();
                throw new InvalidOperationException(
                    "Could not start gst-launch-1.0. Install GStreamer 1.x and the base/good/bad/libav plugins.",
                    exception);
            }

            _gstreamer = process;
            Interlocked.Exchange(ref _lastFrameUnixTimeMilliseconds, 0);
            _lastGStreamerError = null;
            _playback = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _framePump = PumpFramesAsync(process, linuxSource, _playback.Token);
            _errorPump = DrainErrorsAsync(process, _playback.Token);

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            if (process.HasExited)
            {
                await Task.WhenAll(_framePump, _errorPump).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"GStreamer exited before receiving video (exit code {process.ExitCode}). Check installed plugins.");
            }
            await WaitForUdpReceiverAsync(linuxSource.StreamUri.Port, process, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal bool AudioPlaybackEnabled => _audioPlaybackEnabled;

    internal static ProcessStartInfo CreateGStreamerStartInfo(VideoSource source, bool decodeAudio)
    {
        var info = new ProcessStartInfo("gst-launch-1.0")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        List<string> arguments =
        [
            "-e",
            "-q",
            "udpsrc", $"address={source.StreamUri.Host}", $"port={source.StreamUri.Port}",
            "caps=application/x-rtp,media=video,clock-rate=90000,encoding-name=MP2T,payload=33",
            "!", "rtpjitterbuffer", "latency=100", "drop-on-latency=true", "do-lost=true",
            "!", "rtpmp2tdepay",
            "!", "tsdemux", "name=demux",
            "demux.", "!", "queue", "max-size-buffers=3", "max-size-bytes=0", "max-size-time=0", "leaky=downstream",
            // Keep Miracast decoding away from the desktop GPU. An auto-selected
            // VAAPI decoder can leave the display stack wedged when a wireless
            // source disappears while the decoder is being torn down.
            "!", "h264parse", "!", "avdec_h264",
            "!", "videoconvert", "!", "videoscale",
            "!", $"video/x-raw,format=BGRA,width={source.Width},height={source.Height}",
            "!", "fdsink", "fd=1", "sync=true",
        ];
        arguments.AddRange(
        [
            "demux.", "!", "queue", "max-size-buffers=12", "max-size-bytes=0",
            "max-size-time=0", "leaky=downstream",
            "!", "audio/x-private2-lpcm",
        ]);
        if (decodeAudio)
        {
            arguments.AddRange(
            [
                "!", "dvdlpcmdec", "!", "audioconvert", "!", "audioresample",
                "!", "autoaudiosink", "sync=true",
            ]);
        }
        else
        {
            // LPCM support is optional at runtime. An unhandled audio pad makes
            // decodebin terminate the whole pipeline, including valid H.264.
            arguments.AddRange(["!", "fakesink", "sync=false"]);
        }
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task<bool> HasGStreamerElementAsync(
        string element,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("gst-inspect-1.0")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "--exists", element },
            },
        };
        try
        {
            if (!process.Start())
                return false;
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            return false;
        }
    }

    private static async Task WaitForUdpReceiverAsync(
        int port,
        Process process,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"GStreamer exited while binding UDP {port}.");

            try
            {
                using var probe = new UdpClient(AddressFamily.InterNetwork);
                probe.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                probe.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return;
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"GStreamer did not bind UDP {port} within 3 seconds.");
    }

    private async Task PumpFramesAsync(Process process, VideoSource source, CancellationToken cancellationToken)
    {
        var frameLength = checked(source.Width * source.Height * 4);
        var stream = process.StandardOutput.BaseStream;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[]? pixels = ArrayPool<byte>.Shared.Rent(frameLength);
                try
                {
                    var offset = 0;
                    while (offset < frameLength)
                    {
                        var count = await stream.ReadAsync(
                            pixels.AsMemory(offset, frameLength - offset), cancellationToken).ConfigureAwait(false);
                        if (count == 0)
                            return;
                        offset += count;
                    }

                    Interlocked.Exchange(
                        ref _lastFrameUnixTimeMilliseconds,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                    if (Interlocked.CompareExchange(ref _framePending, 1, 0) != 0)
                        continue;

                    var ownedPixels = pixels;
                    var frame = new VideoFrameReceivedEventArgs(
                        ownedPixels,
                        source.Width,
                        source.Height,
                        source.Width * 4,
                        () =>
                        {
                            ArrayPool<byte>.Shared.Return(ownedPixels);
                            Interlocked.Exchange(ref _framePending, 0);
                        });
                    pixels = null;

                    if (FrameReceived is null)
                        frame.Dispose();
                    else
                    {
                        try { FrameReceived.Invoke(this, frame); }
                        catch { frame.Dispose(); }
                    }
                }
                finally
                {
                    if (pixels is not null)
                        ArrayPool<byte>.Shared.Return(pixels);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task DrainErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    _lastGStreamerError = line.Trim();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal string? DescribeNoFrames()
    {
        var process = _gstreamer;
        if (process is null)
            return "GStreamer is not running.";
        if (process.HasExited)
            return $"GStreamer exited with code {process.ExitCode}: {_lastGStreamerError ?? "no diagnostic output"}.";
        if (_lastGStreamerError is not null)
            return $"Last GStreamer diagnostic: {_lastGStreamerError}";
        return $"GStreamer is listening on {process.StartInfo.ArgumentList.FirstOrDefault(argument => argument.StartsWith("address=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? "the P2P address"}.";
    }

    private async Task StopCoreAsync()
    {
        _playback?.Cancel();
        if (_gstreamer is not null)
        {
            try
            {
                await StopGStreamerProcessAsync(_gstreamer).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            await Task.WhenAll(_framePump ?? Task.CompletedTask, _errorPump ?? Task.CompletedTask)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _gstreamer?.Dispose();
        _gstreamer = null;
        _playback?.Dispose();
        _playback = null;
        _framePump = null;
        _errorPump = null;
        _lastGStreamerError = null;
        _audioPlaybackEnabled = false;
        Interlocked.Exchange(ref _lastFrameUnixTimeMilliseconds, 0);
    }

    private static async Task StopGStreamerProcessAsync(Process process)
    {
        if (process.HasExited)
            return;

        if (OperatingSystem.IsLinux())
        {
            // gst-launch handles SIGINT and, with -e, propagates EOS before
            // shutting the pipeline down. Escalate only if the graceful path
            // does not complete within a bounded interval.
            if (SendSignal(process.Id, SigInt) == 0
                && await WaitForExitAsync(process, TimeSpan.FromSeconds(2)).ConfigureAwait(false))
            {
                return;
            }
            if (!process.HasExited)
                _ = SendSignal(process.Id, SigTerm);
            if (await WaitForExitAsync(process, TimeSpan.FromSeconds(1)).ConfigureAwait(false))
                return;
        }

        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
        _ = await WaitForExitAsync(process, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
            return true;
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return process.HasExited;
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int processId, int signal);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}
