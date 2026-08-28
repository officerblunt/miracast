using System.Net;
using Xunit;

namespace Miracast.Receiver.Linux.Tests;

public sealed class NetworkManagerP2PTests
{
    [Theory]
    [InlineData("p2p-0", true)]
    [InlineData("p2p-wlan0-1", true)]
    [InlineData("p2p-dev-wlan0", false)]
    [InlineData("wlan0", false)]
    public void IdentifiesOnlyP2PGroupInterfaces(string name, bool expected)
    {
        Assert.Equal(expected, NetworkManagerP2P.IsP2PGroupInterfaceName(name));
    }

    [Fact]
    public void GetGroupAddress_ParsesAddressReportedBySupplicant()
    {
        var properties = new Dictionary<string, object>
        {
            ["IpAddr"] = new byte[] { 192, 168, 49, 24 },
        };

        var address = NetworkManagerP2P.GetGroupAddress(properties, "IpAddr");

        Assert.Equal(IPAddress.Parse("192.168.49.24"), address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[] { 0, 0, 0, 0 })]
    [InlineData(new byte[] { 192, 168, 49 })]
    public void GetGroupAddress_RejectsMissingOrInvalidAddress(byte[]? value)
    {
        var properties = new Dictionary<string, object>();
        if (value is not null)
            properties["IpAddr"] = value;

        Assert.Null(NetworkManagerP2P.GetGroupAddress(properties, "IpAddr"));
    }

    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.0.0", 16)]
    [InlineData("255.0.255.0", null)]
    public void GetPrefixLength_ValidatesNetmask(string netmask, int? expected)
    {
        Assert.Equal(expected, NetworkManagerP2P.GetPrefixLength(IPAddress.Parse(netmask)));
    }

    [Theory]
    [InlineData("p2p-dev-wlan0", "wlan0", true)]
    [InlineData("p2p-wlan0-0", "wlan0", true)]
    [InlineData("wlan0", "wlan0", true)]
    [InlineData("p2p-dev-wlan1", "wlan0", false)]
    public void MatchesP2PDeviceToItsPhysicalWifiInterface(
        string p2pInterface,
        string wifiInterface,
        bool expected)
    {
        Assert.Equal(expected, NetworkManagerP2P.IsSameRadioInterface(p2pInterface, wifiInterface));
    }

    [Theory]
    [InlineData(2412, 81, 1)]
    [InlineData(2437, 81, 6)]
    [InlineData(2484, 82, 14)]
    [InlineData(5180, 115, 36)]
    [InlineData(5220, 115, 44)]
    [InlineData(5500, 121, 100)]
    [InlineData(5745, 124, 149)]
    [InlineData(5825, 125, 165)]
    public void ConvertsWifiFrequencyToP2POperatingChannel(
        int frequency,
        uint expectedClass,
        uint expectedChannel)
    {
        var converted = NetworkManagerP2P.TryGetP2POperatingChannel(
            frequency,
            out var operatingClass,
            out var channel);

        Assert.True(converted);
        Assert.Equal(expectedClass, operatingClass);
        Assert.Equal(expectedChannel, channel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2400)]
    [InlineData(5955)]
    public void RejectsUnsupportedP2POperatingFrequency(int frequency)
    {
        Assert.False(NetworkManagerP2P.TryGetP2POperatingChannel(
            frequency,
            out _,
            out _));
    }
}
