using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Miracast.Receiver.Entities.EventArgs;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Playback;
using static Vortice.Direct3D11.D3D11;

namespace Miracast.Receiver.Windows;

[SupportedOSPlatform("windows10.0.18362")]
public sealed class VideoRenderer : IVideoRenderer, IAsyncDisposable
{
    private readonly object _frameLock = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private MediaPlayer? _player;
    private ID3D11Texture2D? _renderTexture;
    private ID3D11Texture2D? _stagingTexture;
    private IDirect3DSurface? _renderSurface;
    private int _width;
    private int _height;
    private int _framePending;

    public VideoRenderer()
    {
        _device = D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0]);
        _context = _device.ImmediateContext;
    }

    public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived;

    public Task PlayAsync(IVideoSource source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is not VideoSource windowsSource)
            throw new ArgumentException("The source is not a Windows Miracast media source.", nameof(source));

        StopPlayer();
        var player = new MediaPlayer
        {
            IsVideoFrameServerEnabled = true,
            RealTimePlayback = true,
            AutoPlay = false,
            Source = windowsSource.MediaSource,
        };
        player.VideoFrameAvailable += OnVideoFrameAvailable;
        player.Play();
        _player = player;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopPlayer();
        return Task.CompletedTask;
    }

    private void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        if (Interlocked.CompareExchange(ref _framePending, 1, 0) != 0)
            return;

        byte[]? pixels = null;
        var ownershipTransferred = false;
        try
        {
            lock (_frameLock)
            {
                var width = checked((int)sender.PlaybackSession.NaturalVideoWidth);
                var height = checked((int)sender.PlaybackSession.NaturalVideoHeight);
                if (width <= 0 || height <= 0)
                    return;

                EnsureTextures(width, height);
                sender.CopyFrameToVideoSurface(_renderSurface!);
                _context.CopyResource(_stagingTexture!, _renderTexture!);

                var rowBytes = checked(width * 4);
                pixels = ArrayPool<byte>.Shared.Rent(checked(rowBytes * height));
                var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    for (var row = 0; row < height; row++)
                    {
                        Marshal.Copy(
                            IntPtr.Add(mapped.DataPointer, checked(row * (int)mapped.RowPitch)),
                            pixels,
                            row * rowBytes,
                            rowBytes);
                    }
                }
                finally
                {
                    _context.Unmap(_stagingTexture!, 0);
                }

                var rentedPixels = pixels;
                var frame = new VideoFrameReceivedEventArgs(
                    rentedPixels,
                    width,
                    height,
                    rowBytes,
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
        }
        catch
        {
            if (pixels is not null)
                ArrayPool<byte>.Shared.Return(pixels);
            Interlocked.Exchange(ref _framePending, 0);
        }
        finally
        {
            if (!ownershipTransferred)
                Interlocked.Exchange(ref _framePending, 0);
        }
    }

    private void EnsureTextures(int width, int height)
    {
        if (_renderTexture is not null && width == _width && height == _height)
            return;

        DisposeTextures();
        _width = width;
        _height = height;

        var renderDescription = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)width,
            (uint)height,
            1,
            1,
            BindFlags.RenderTarget | BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.None);
        _renderTexture = _device.CreateTexture2D(renderDescription);

        var stagingDescription = renderDescription;
        stagingDescription.BindFlags = BindFlags.None;
        stagingDescription.Usage = ResourceUsage.Staging;
        stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
        _stagingTexture = _device.CreateTexture2D(stagingDescription);

        using var dxgiSurface = _renderTexture.QueryInterface<IDXGISurface>();
        var result = CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out var inspectable);
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);

        try
        {
            _renderSurface = WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    private void StopPlayer()
    {
        lock (_frameLock)
        {
            if (_player is not null)
            {
                _player.VideoFrameAvailable -= OnVideoFrameAvailable;
                _player.Dispose();
                _player = null;
            }

            DisposeTextures();
        }
    }

    private void DisposeTextures()
    {
        (_renderSurface as IDisposable)?.Dispose();
        _renderSurface = null;
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _renderTexture?.Dispose();
        _renderTexture = null;
        _width = 0;
        _height = 0;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _context.Dispose();
        _device.Dispose();
    }

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(
        IntPtr dxgiSurface,
        out IntPtr graphicsSurface);
}
