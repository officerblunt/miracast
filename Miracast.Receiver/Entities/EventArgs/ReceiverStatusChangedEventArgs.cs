namespace Miracast.Receiver.Entities.EventArgs;

public sealed class ReceiverStatusChangedEventArgs : System.EventArgs
{
    public required string Status { get; init; }
}
