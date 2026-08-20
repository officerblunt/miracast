namespace Miracast.Receiver.Linux;

public sealed class VideoSource(Uri streamUri, int width, int height) : IVideoSource
{
    public Uri StreamUri { get; } = streamUri;
    public int Width { get; } = width;
    public int Height { get; } = height;
}
