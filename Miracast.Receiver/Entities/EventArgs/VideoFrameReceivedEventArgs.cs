namespace Miracast.Receiver.Entities.EventArgs;

public sealed class VideoFrameReceivedEventArgs : System.EventArgs, IDisposable
{
    private readonly Action _release;
    private int _disposed;

    public VideoFrameReceivedEventArgs(
        byte[] pixels,
        int width,
        int height,
        int rowBytes,
        Action release)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        RowBytes = rowBytes;
        _release = release;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int RowBytes { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _release();
    }
}
