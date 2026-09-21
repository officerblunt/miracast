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
        Assert.Contains("-e", arguments);
        Assert.Contains("h264parse", arguments);
        Assert.Contains("avdec_h264", arguments);
        Assert.DoesNotContain("decodebin", arguments);
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

    [Fact]
    public void VideoPathBuffersNetworkJitterWithoutDroppingEncodedFrames()
    {
        var source = new VideoSource(new Uri("rtp://127.0.0.1:19000"), 1920, 1080);

        var startInfo = VideoRenderer.CreateGStreamerStartInfo(source, decodeAudio: false);
        var arguments = startInfo.ArgumentList.ToArray();

        var jitterBuffer = Array.IndexOf(arguments, "rtpjitterbuffer");
        var demux = Array.IndexOf(arguments, "tsdemux");
        var decoder = Array.IndexOf(arguments, "avdec_h264");
        var rawCaps = Array.FindIndex(arguments, argument =>
            argument.StartsWith("video/x-raw,", StringComparison.Ordinal));
        var sink = Array.IndexOf(arguments, "fdsink");

        Assert.True(jitterBuffer >= 0 && demux > jitterBuffer);
        Assert.Contains("latency=100", arguments[(jitterBuffer + 1)..demux]);
        Assert.Contains("drop-on-latency=false", arguments[(jitterBuffer + 1)..demux]);
        Assert.Contains("latency=0", arguments[(demux + 1)..decoder]);
        Assert.DoesNotContain("leaky=downstream", arguments[(demux + 1)..decoder]);

        Assert.True(rawCaps > decoder && sink > rawCaps);
        Assert.Contains("max-size-buffers=1", arguments[(rawCaps + 1)..sink]);
        Assert.Contains("leaky=downstream", arguments[(rawCaps + 1)..sink]);
        Assert.Contains("sync=false", arguments[(sink + 1)..]);
        Assert.Contains("async=false", arguments[(sink + 1)..]);
    }
}
