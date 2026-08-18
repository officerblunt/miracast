using Windows.Media.Core;

namespace Miracast.Receiver.Windows;

public class VideoSource(MediaSource mediaSource) : IVideoSource
{
    public MediaSource MediaSource { get; } = mediaSource;
}