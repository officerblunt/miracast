using System.Buffers;
using System.Diagnostics;
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
    private bool _disposed;

    public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;

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
            "udpsrc", $"port={source.StreamUri.Port}",
            "caps=application/x-rtp,media=video,clock-rate=90000,encoding-name=MP2T,payload=33",
            "!", "rtpjitterbuffer", "latency=100", "drop-on-latency=true",
            "!", "rtpmp2tdepay",
            "!", "tsdemux", "name=demux",
            "demux.", "!", "queue", "!", "h264parse", "!", "decodebin",
            "!", "videoconvert", "!", "videoscale",
            "!", $"video/x-raw,format=BGRA,width={source.Width},height={source.Height}",
            "!", "fdsink", "fd=1", "sync=false",
            "demux.", "!", "queue", "!", "decodebin", "!", "audioconvert", "!", "audioresample",
            "!", "autoaudiosink", "sync=false",
        ];
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
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

    private static async Task DrainErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                // Draining stderr prevents a full pipe from stalling gst-launch.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
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
