namespace Miracast.Receiver.Entities.EventArgs;

public sealed class VideoReceivedEventArgs : System.EventArgs
{
    public required IVideoSource Source { get; init; }
}
