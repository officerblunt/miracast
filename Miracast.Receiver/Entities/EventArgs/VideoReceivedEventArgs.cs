namespace Miracast.Receiver.Entities.EventArgs;

public class VideoReceivedEventArgs : System.EventArgs
{
    public required IVideoSource Source { get; init; }
}