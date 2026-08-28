using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Miracast.Receiver.Entities.EventArgs;

namespace Miracast.Receiver.Linux;

public sealed class VideoRenderer : IVideoRenderer, IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Process? _gstreamer;
    private CancellationTokenSource? _playback;
    private Task? _framePump;
    private Task? _errorPump;
    private int _framePending;
    private long _lastFrameUnixTimeMilliseconds;
    private string? _lastGStreamerError;
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

            var process = new Process { StartInfo = CreateGStreamerStartInfo(linuxSource) };
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

    private static ProcessStartInfo CreateGStreamerStartInfo(VideoSource source)
    {
        var info = new ProcessStartInfo("gst-launch-1.0")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        string[] arguments =
        [
            "-q",
            "udpsrc", $"address={source.StreamUri.Host}", $"port={source.StreamUri.Port}",
            "caps=application/x-rtp,media=video,clock-rate=90000,encoding-name=MP2T,payload=33",
            "!", "rtpjitterbuffer", "latency=100", "drop-on-latency=true", "do-lost=true",
            "!", "rtpmp2tdepay",
            "!", "tsdemux", "name=demux",
            "demux.", "!", "queue", "max-size-buffers=3", "max-size-bytes=0", "max-size-time=0", "leaky=downstream",
            "!", "h264parse", "!", "decodebin",
            "!", "videoconvert", "!", "videoscale",
            "!", $"video/x-raw,format=BGRA,width={source.Width},height={source.Height}",
            "!", "fdsink", "fd=1", "sync=true",
            "demux.", "!", "queue", "max-size-buffers=12", "max-size-bytes=0", "max-size-time=0", "leaky=downstream",
            "!", "decodebin", "!", "audioconvert", "!", "audioresample",
            "!", "autoaudiosink", "sync=true",
        ];
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
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
                if (!_gstreamer.HasExited)
                    _gstreamer.Kill(entireProcessTree: true);
                await _gstreamer.WaitForExitAsync().ConfigureAwait(false);
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
        Interlocked.Exchange(ref _lastFrameUnixTimeMilliseconds, 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}
