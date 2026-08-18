namespace Miracast.Receiver;

public interface IVideoRenderer
{
    Task PlayAsync(IVideoSource source);
}