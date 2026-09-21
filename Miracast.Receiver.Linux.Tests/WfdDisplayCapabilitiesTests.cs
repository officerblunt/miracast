using Xunit;

namespace Miracast.Receiver.Linux.Tests;

public sealed class WfdDisplayCapabilitiesTests
{
    [Fact]
    public void AdvertisesExactWallModeAndValidEdid()
    {
        var capabilities = new WfdDisplayCapabilities(3840, 1080);

        Assert.Equal("0f00 0438 001e", capabilities.MicrosoftCustomVideoFormats);
        Assert.Equal("000000000000", capabilities.MicrosoftVideoFormats);
        Assert.StartsWith("38 01 ", capabilities.LegacyVideoFormats);
        Assert.StartsWith("0098 01 ", capabilities.ExtendedVideoFormats);
        Assert.EndsWith(" 1000 0870", capabilities.ExtendedVideoFormats);
        Assert.StartsWith("0001 ", capabilities.DisplayEdid);

        var edid = Convert.FromHexString(capabilities.DisplayEdid[5..]);
        Assert.Equal(128, edid.Length);
        Assert.Equal(0, edid.Sum(value => value) & 0xff);

        var width = edid[56] | ((edid[58] & 0xf0) << 4);
        var height = edid[59] | ((edid[61] & 0xf0) << 4);
        Assert.Equal(3840, width);
        Assert.Equal(1080, height);
    }

    [Fact]
    public void AdvertisesExtraWideWallThroughMicrosoftCustomFormat()
    {
        var capabilities = new WfdDisplayCapabilities(5760, 1080);

        Assert.Equal("1680 0438 001e", capabilities.MicrosoftCustomVideoFormats);
        Assert.StartsWith("0098 01 0002 0080", capabilities.ExtendedVideoFormats);
        Assert.EndsWith(" 1680 0870", capabilities.ExtendedVideoFormats);
        Assert.StartsWith("0002 ", capabilities.DisplayEdid);

        var edid = Convert.FromHexString(capabilities.DisplayEdid[5..]);
        Assert.Equal(256, edid.Length);
        Assert.Equal(0, edid.Take(128).Sum(value => value) & 0xff);
        Assert.Equal(0, edid.Skip(128).Take(128).Sum(value => value) & 0xff);
        Assert.Equal(0x70, edid[128]);
        Assert.Equal(0x20, edid[129]);
        Assert.Equal(0x20, edid[133]);
        Assert.Equal(0x21, edid[157]);
        Assert.Equal(0x22, edid[189]);
        Assert.Equal(0x26, edid[212]);
        Assert.Equal(0, edid.Skip(129).Take(edid[130] + 5).Sum(value => value) & 0xff);

        var displayIdTimingOffset = 192;
        var width = BitConverter.ToUInt16(edid, displayIdTimingOffset + 4) + 1;
        var height = BitConverter.ToUInt16(edid, displayIdTimingOffset + 12) + 1;
        Assert.Equal(5760, width);
        Assert.Equal(1080, height);
    }

    [Fact]
    public void SeparatesExactWallModeFromMirrorDecoderModes()
    {
        var capabilities = new WfdDisplayCapabilities(1280, 720);

        Assert.True(capabilities.Contains(1280, 720));
        Assert.False(capabilities.Contains(1920, 1080));
        Assert.True(capabilities.CanDecode(1920, 1080));
        Assert.True(capabilities.CanDecode(3240, 2160));
        Assert.Equal("000000000000", capabilities.MicrosoftVideoFormats);
    }

    [Fact]
    public void AdvertisesScaledThreeMonitorWallAndSingleOutputNativeMode()
    {
        var capabilities = new WfdDisplayCapabilities(7680, 1440, 2560, 1440);

        Assert.Equal(7680, capabilities.Width);
        Assert.Equal(1440, capabilities.Height);
        Assert.Equal(2560, capabilities.NativeWidth);
        Assert.Equal(1440, capabilities.NativeHeight);
        Assert.Equal("1e00 05a0 001e", capabilities.MicrosoftCustomVideoFormats);
        Assert.Equal("000000000000", capabilities.MicrosoftVideoFormats);
        Assert.StartsWith("0098 01 ", capabilities.ExtendedVideoFormats);
        Assert.StartsWith("60 01 01 0080 ", capabilities.Wfd2VideoFormats);
        Assert.Contains(", 01 02 0080 ", capabilities.Wfd2VideoFormats);
        Assert.EndsWith(" 00", capabilities.Wfd2VideoFormats);
        Assert.StartsWith("0002 ", capabilities.DisplayEdid);

        var edid = Convert.FromHexString(capabilities.DisplayEdid[5..]);
        Assert.Equal(256, edid.Length);
        Assert.Equal(0x06, edid[24] & 0x06);
        Assert.Equal(2, BitConverter.ToUInt16(edid, 10));
        Assert.Equal(((uint)1440 << 16) | 7680u, BitConverter.ToUInt32(edid, 12));

        var baseWidth = edid[56] | ((edid[58] & 0xf0) << 4);
        var baseHeight = edid[59] | ((edid[61] & 0xf0) << 4);
        Assert.Equal(2560, baseWidth);
        Assert.Equal(1440, baseHeight);

        Assert.Equal(0x70, edid[128]);
        Assert.Equal(0x20, edid[129]);
        Assert.Equal(0x20, edid[133]);
        Assert.Equal(2, BitConverter.ToUInt16(edid, 139));
        Assert.Equal(((uint)1440 << 16) | 7680u, BitConverter.ToUInt32(edid, 141));
        Assert.Equal(0x21, edid[157]);
        Assert.Equal(2560, BitConverter.ToUInt16(edid, 164));
        Assert.Equal(1440, BitConverter.ToUInt16(edid, 166));
        Assert.Equal(0x22, edid[189]);
        Assert.Equal(40, edid[191]);

        var nativeTimingOffset = 192;
        Assert.Equal(0x88, edid[nativeTimingOffset + 3]);
        Assert.Equal(2560, BitConverter.ToUInt16(edid, nativeTimingOffset + 4) + 1);
        Assert.Equal(1440, BitConverter.ToUInt16(edid, nativeTimingOffset + 12) + 1);

        var wallTimingOffset = nativeTimingOffset + 20;
        Assert.Equal(0x08, edid[wallTimingOffset + 3]);
        var wallWidth = BitConverter.ToUInt16(edid, wallTimingOffset + 4) + 1;
        var wallHeight = BitConverter.ToUInt16(edid, wallTimingOffset + 12) + 1;
        Assert.Equal(7680, wallWidth);
        Assert.Equal(1440, wallHeight);
        Assert.Equal(0x26, edid[232]);
        Assert.Equal(0, edid.Skip(129).Take(edid[130] + 5).Sum(value => value) & 0xff);
        Assert.Equal(0, edid.Take(128).Sum(value => value) & 0xff);
        Assert.Equal(0, edid.Skip(128).Take(128).Sum(value => value) & 0xff);
    }

    [Fact]
    public void ChoosesMaximumAspectPreservingModeForWideReceiver()
    {
        var capabilities = new WfdDisplayCapabilities(7680, 1440, 2560, 1440);

        // WFD2 CEA index 24 is 4096x2160. Although it is taller than the
        // 1440-line wall, proportional fitting lets it cover more of the very
        // wide receiver than any of the 16:9 VESA modes.
        Assert.StartsWith("60 01 01 0080 ", capabilities.Wfd2VideoFormats);
        Assert.StartsWith("0098 01 ", capabilities.ExtendedVideoFormats);
    }

    [Fact]
    public void KeepsExactNativeModeForOrdinaryReceiver()
    {
        var capabilities = new WfdDisplayCapabilities(2560, 1440, 2560, 1440);

        Assert.StartsWith("79 01 01 0080 ", capabilities.Wfd2VideoFormats);
        Assert.StartsWith("00e9 01 ", capabilities.ExtendedVideoFormats);
    }
}
