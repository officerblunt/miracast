namespace Miracast.Receiver.Linux;

public class VideoSource : IVideoSource
{
    public required Uri StreamUri { get; init; }
}