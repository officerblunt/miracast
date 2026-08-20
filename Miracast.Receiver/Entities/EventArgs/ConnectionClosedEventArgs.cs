namespace Miracast.Receiver.Entities.EventArgs;

public sealed class ConnectionClosedEventArgs : System.EventArgs
{
    public string? DeviceName { get; init; }
}
