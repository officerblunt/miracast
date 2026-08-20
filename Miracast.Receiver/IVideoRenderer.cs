namespace Miracast.Receiver;

public interface IVideoRenderer
{
    event EventHandler<Entities.EventArgs.VideoFrameReceivedEventArgs>? FrameReceived;

    Task PlayAsync(IVideoSource source, CancellationToken cancellationToken = default);
    Task StopAsync();
}
