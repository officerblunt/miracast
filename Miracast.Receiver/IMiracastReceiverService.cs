using Miracast.Receiver.Entities.EventArgs;

namespace Miracast.Receiver;

public interface IMiracastReceiverService
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);

    event EventHandler<ConnectionCreatedEventArgs>? ConnectionCreated;
    event EventHandler<ConnectionClosedEventArgs>? ConnectionClosed;
    event EventHandler<VideoReceivedEventArgs>? VideoReceived;
}
