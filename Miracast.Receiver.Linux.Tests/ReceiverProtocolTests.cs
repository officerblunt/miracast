using System.Text;
using Xunit;

namespace Miracast.Receiver.Linux.Tests;

public sealed class ReceiverProtocolTests
{
    [Fact]
    public void WfdInformationElements_AdvertisePrimarySinkOnRtspPort7236()
    {
        Assert.Equal(
            new byte[] { 0x00, 0x00, 0x06, 0x01, 0x51, 0x1c, 0x44, 0x00, 0x32 },
            WpaSupplicantP2pController.SinkWfdInformationElements);
    }

    [Fact]
    public void SplitNmcliLine_PreservesEscapedSeparators()
    {
        var fields = MiracastReceiverService.SplitNmcliLine("wlx00\\:11:wifi:disconnected");

        Assert.Equal(new[] { "wlx00:11", "wifi", "disconnected" }, fields);
    }

    [Theory]
    [InlineData("wfd_video_formats: 00 00 02 10 00000200 00000000 00000000 00 0000 0000 00 none none", 1920, 1080)]
    [InlineData("wfd_video_formats: 00 00 02 10 00000020 00000000 00000000 00 0000 0000 00 none none", 1280, 720)]
    public void VideoFormatParser_UsesAdvertisedCeaMode(string line, int expectedWidth, int expectedHeight)
    {
        var parsed = WfdVideoFormatParser.TryParseResolution(line, out var width, out var height);

        Assert.True(parsed);
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public async Task RtspMessage_RoundTripsHeadersAndBody()
    {
        var original = new RtspMessage(
            "GET_PARAMETER rtsp://sink/wfd1.0 RTSP/1.0",
            new Dictionary<string, string> { ["CSeq"] = "7" },
            "wfd_video_formats\r\n");
        await using var stream = new MemoryStream(original.Serialize());

        var parsed = await RtspMessage.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(parsed);
        Assert.Equal(original.StartLine, parsed.StartLine);
        Assert.Equal("7", parsed.Headers["cseq"]);
        Assert.Equal(original.Body, parsed.Body);
        Assert.Contains("Content-Length", Encoding.ASCII.GetString(original.Serialize()));
    }
}
