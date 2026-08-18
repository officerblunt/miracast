using Miracast.Receiver.Entities.EventArgs;

namespace Miracast.Receiver;

public interface IMiracastReceiverService
{
    public Task StartAsync(CancellationToken cancellationToken = default);
    public Task StopAsync(CancellationToken cancellationToken = default);

    public event EventHandler<ConnectionCreatedEventArgs> ConnectionCreated;
    public event EventHandler<ConnectionClosedEventArgs> ConnectionClosed;
    public event EventHandler<VideoReceivedEventArgs> VideoReceived;
}