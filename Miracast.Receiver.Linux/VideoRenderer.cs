using System.Buffers;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Miracast.Receiver.Entities.EventArgs;

namespace Miracast.Receiver.Linux;

public sealed class VideoRenderer : IVideoRenderer, IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _media;
    private IntPtr _videoAllocation;
    private IntPtr _videoBuffer;
    private int _width;
    private int _height;
    private int _rowBytes;
    private int _framePending;
    private bool _disposed;

    public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;

    public async Task PlayAsync(IVideoSource source, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The MiracleCast video renderer can only run on Linux.");
        if (source is not VideoSource linuxSource)
            throw new ArgumentException("The source is not a MiracleCast RTP source.", nameof(source));
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCore();

            Core.Initialize();
            _libVlc ??= new LibVLC("--network-caching=100", "--clock-jitter=0", "--clock-synchro=0");
            _width = linuxSource.Width;
            _height = linuxSource.Height;
            _rowBytes = checked(_width * 4);
            var bufferSize = checked(_rowBytes * _height);
            _videoAllocation = Marshal.AllocHGlobal(checked(bufferSize + 31));
            _videoBuffer = (IntPtr)((_videoAllocation.ToInt64() + 31) & ~31L);

            var player = new MediaPlayer(_libVlc);
            player.SetVideoFormat("RV32", (uint)_width, (uint)_height, (uint)_rowBytes);
            player.SetVideoCallbacks(LockVideo, UnlockVideo, DisplayVideo);

            var media = new Media(_libVlc, linuxSource.StreamUri);
            media.AddOption(":network-caching=100");
            media.AddOption(":live-caching=100");
            if (!player.Play(media))
            {
                media.Dispose();
                player.Dispose();
                FreeBuffer();
                throw new InvalidOperationException("LibVLC rejected the MiracleCast RTP stream.");
            }

            _player = player;
            _media = media;
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
            StopCore();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private IntPtr LockVideo(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _videoBuffer);
        return _videoBuffer;
    }

    private static void UnlockVideo(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
    }

    private void DisplayVideo(IntPtr opaque, IntPtr picture)
    {
        if (Interlocked.CompareExchange(ref _framePending, 1, 0) != 0)
            return;

        byte[]? pixels = null;
        var ownershipTransferred = false;
        try
        {
            var length = checked(_rowBytes * _height);
            pixels = ArrayPool<byte>.Shared.Rent(length);
            Marshal.Copy(_videoBuffer, pixels, 0, length);

            var rentedPixels = pixels;
            var frame = new VideoFrameReceivedEventArgs(
                rentedPixels,
                _width,
                _height,
                _rowBytes,
                () =>
                {
                    ArrayPool<byte>.Shared.Return(rentedPixels);
                    Interlocked.Exchange(ref _framePending, 0);
                });
            pixels = null;
            ownershipTransferred = true;

            if (FrameReceived is null)
                frame.Dispose();
            else
            {
                try
                {
                    FrameReceived.Invoke(this, frame);
                }
                catch
                {
                    frame.Dispose();
                }
            }
        }
        catch
        {
            if (pixels is not null)
                ArrayPool<byte>.Shared.Return(pixels);
        }
        finally
        {
            if (!ownershipTransferred)
                Interlocked.Exchange(ref _framePending, 0);
        }
    }

    private void StopCore()
    {
        _player?.Stop();
        _media?.Dispose();
        _media = null;
        _player?.Dispose();
        _player = null;
        FreeBuffer();
        _width = 0;
        _height = 0;
        _rowBytes = 0;
    }

    private void FreeBuffer()
    {
        if (_videoAllocation == IntPtr.Zero)
            return;

        Marshal.FreeHGlobal(_videoAllocation);
        _videoAllocation = IntPtr.Zero;
        _videoBuffer = IntPtr.Zero;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _libVlc?.Dispose();
        _libVlc = null;
        _lifecycle.Dispose();
    }
}
