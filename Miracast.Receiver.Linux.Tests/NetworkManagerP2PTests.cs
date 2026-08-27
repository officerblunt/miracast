using System.Net;
using Xunit;

namespace Miracast.Receiver.Linux.Tests;

public sealed class NetworkManagerP2PTests
{
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
}
