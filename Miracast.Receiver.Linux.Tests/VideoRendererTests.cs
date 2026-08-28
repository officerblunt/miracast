using Xunit;

namespace Miracast.Receiver.Linux.Tests;

public sealed class VideoRendererTests
{
    [Fact]
    public void MissingLpcmDecoderDiscardsOnlyAudioTrack()
    {
        var source = new VideoSource(new Uri("rtp://192.168.137.247:19000"), 1920, 1080);

        var startInfo = VideoRenderer.CreateGStreamerStartInfo(source, decodeAudio: false);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Contains("address=192.168.137.247", arguments);
        Assert.Contains("h264parse", arguments);
        Assert.Contains("audio/x-private2-lpcm", arguments);
        Assert.Contains("fakesink", arguments);
        Assert.DoesNotContain("dvdlpcmdec", arguments);
        Assert.DoesNotContain("autoaudiosink", arguments);
    }

    [Fact]
    public void AvailableLpcmDecoderEnablesAudioPlayback()
    {
        var source = new VideoSource(new Uri("rtp://127.0.0.1:19000"), 1280, 720);

        var startInfo = VideoRenderer.CreateGStreamerStartInfo(source, decodeAudio: true);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Contains("dvdlpcmdec", arguments);
        Assert.Contains("autoaudiosink", arguments);
        Assert.DoesNotContain("fakesink", arguments);
    }
}
