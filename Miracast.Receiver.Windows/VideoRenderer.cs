namespace Miracast.Receiver.Windows;

public class VideoRenderer : IVideoRenderer
{
    public Task PlayAsync(IVideoSource source)
    {
        var windowsSource = (VideoSource)source;

        var mediaSource = windowsSource.MediaSource;

        // Windows Media Foundation / MediaPlayer / etc.

        return Task.CompletedTask;
    }
}