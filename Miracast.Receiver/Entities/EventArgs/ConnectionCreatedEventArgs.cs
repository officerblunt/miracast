namespace Miracast.Receiver.Entities.EventArgs;

public sealed class ConnectionCreatedEventArgs : System.EventArgs
{
    public string? DeviceName { get; init; }
}
