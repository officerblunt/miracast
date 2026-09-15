using System.Net;
using Tmds.DBus;
using Xunit;

namespace Miracast.Receiver.Linux.Tests;

public sealed class NetworkManagerP2PTests
{
    [Fact]
    public async Task ActivationFailureInterruptsPendingNetworkManagerRequest()
    {
        var request = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("GO negotiation failed");

        var waiting = NetworkManagerP2P.WaitForActivationRequestAsync(
            request.Task,
            state.Task,
            CancellationToken.None);
        state.SetException(failure);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);
        Assert.Same(failure, exception);
        Assert.False(request.Task.IsCompleted);
    }

    [Fact]
    public async Task CompletedNetworkManagerRequestDoesNotWaitForLaterStateEvent()
    {
        var state = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await NetworkManagerP2P.WaitForActivationRequestAsync(
            Task.FromResult("active connection"),
            state.Task,
            CancellationToken.None);

        Assert.Equal("active connection", result);
        Assert.False(state.Task.IsCompleted);
    }

    [Theory]
    [InlineData("p2p-0", true)]
    [InlineData("p2p-wlan0-1", true)]
    [InlineData("p2p-dev-wlan0", false)]
    [InlineData("wlan0", false)]
    public void IdentifiesOnlyP2PGroupInterfaces(string name, bool expected)
    {
        Assert.Equal(expected, P2PAddressing.IsGroupInterfaceName(name));
    }

    [Fact]
    public void GetGroupAddress_ParsesAddressReportedBySupplicant()
    {
        var properties = new Dictionary<string, object>
        {
            ["IpAddr"] = new byte[] { 192, 168, 49, 24 },
        };

        var address = P2PAddressing.GetGroupAddress(properties, "IpAddr");

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

        Assert.Null(P2PAddressing.GetGroupAddress(properties, "IpAddr"));
    }

    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.0.0", 16)]
    [InlineData("255.0.255.0", null)]
    public void GetPrefixLength_ValidatesNetmask(string netmask, int? expected)
    {
        Assert.Equal(expected, P2PAddressing.GetPrefixLength(IPAddress.Parse(netmask)));
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
        var converted = P2PNetworkConfiguration.TryGetOperatingChannel(
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
        Assert.False(P2PNetworkConfiguration.TryGetOperatingChannel(
            frequency,
            out _,
            out _));
    }

    [Fact]
    public void P2PConnectionIsIsolatedFromDefaultRouteAndDnsBeforeActivation()
    {
        var settings = P2PNetworkConfiguration.CreateConnectionSettings(
            "Source",
            "42:AE:30:AB:8C:A2");

        var ipv4 = settings["ipv4"];
        Assert.Equal("auto", ipv4["method"]);
        Assert.Equal(true, ipv4["never-default"]);
        Assert.Equal(true, ipv4["ignore-auto-dns"]);
        Assert.Equal(false, ipv4["may-fail"]);

        var ipv6 = settings["ipv6"];
        Assert.Equal("auto", ipv6["method"]);
        Assert.Equal(true, ipv6["never-default"]);
        Assert.Equal(true, ipv6["ignore-auto-dns"]);
        Assert.Equal(true, ipv6["may-fail"]);
    }

    [Fact]
    public void P2PDeviceUsesDedicatedGroupInterface()
    {
        var configuration = P2PNetworkConfiguration.CreateDeviceConfiguration("Receiver");

        Assert.Equal(false, configuration["NoGroupIface"]);
    }

    [Theory]
    [InlineData("p2p-dev-wlan0", "wlan0")]
    [InlineData("p2p-dev-wlxb8fbb3dfa1e4", "wlxb8fbb3dfa1e4")]
    [InlineData("p2p-wlan0-0", null)]
    [InlineData("wlan0", null)]
    public void ExtractsOnlyNetworkManagerP2PParentInterface(string name, string? expected)
    {
        Assert.Equal(expected, P2PDeviceSelector.GetParentWifiInterfaceName(name));
    }

    [Theory]
    [InlineData("wlan0", "p2p-dev-wlan0", true)]
    [InlineData("p2p-wlan0-0", "p2p-dev-wlan0", false)]
    [InlineData("wlan1", "p2p-dev-wlan0", false)]
    [InlineData("wlan0", "p2p-wlan0-0", false)]
    public void MatchesSupplicantInterfaceToSelectedP2PDevice(
        string supplicantInterface,
        string p2pInterface,
        bool expected)
    {
        Assert.Equal(
            expected,
            P2PDeviceSelector.IsSupplicantInterfaceForP2PDevice(
                supplicantInterface,
                p2pInterface));
    }

    [Fact]
    public void PrefersIdleAdapterOverAdapterCarryingRegularWifi()
    {
        var candidates = new[]
        {
            new P2PDeviceCandidate(
                new ObjectPath("/active"),
                "p2p-dev-wlan0",
                "wlan0",
                30,
                100),
            new P2PDeviceCandidate(
                new ObjectPath("/idle"),
                "p2p-dev-wlan1",
                "wlan1",
                30,
                30),
        };

        var selected = P2PDeviceSelector.Select(candidates);

        Assert.NotNull(selected);
        Assert.Equal("p2p-dev-wlan1", selected.InterfaceName);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(70)]
    [InlineData(110)]
    [InlineData(120)]
    public void DoesNotTreatTransitionalOrFailedAdapterAsIdle(uint otherState)
    {
        var candidates = new[]
        {
            new P2PDeviceCandidate(
                new ObjectPath("/other"),
                "p2p-dev-wlan1",
                "wlan1",
                30,
                otherState),
            new P2PDeviceCandidate(
                new ObjectPath("/active"),
                "p2p-dev-wlan0",
                "wlan0",
                30,
                100),
        };

        var selected = P2PDeviceSelector.Select(candidates);

        Assert.NotNull(selected);
        Assert.Equal("p2p-dev-wlan0", selected.InterfaceName);
    }

    [Fact]
    public void IgnoresUnavailableP2PDeviceEvenWhenItsParentIsIdle()
    {
        var candidates = new[]
        {
            new P2PDeviceCandidate(
                new ObjectPath("/unavailable"),
                "p2p-dev-wlan1",
                "wlan1",
                20,
                30),
            new P2PDeviceCandidate(
                new ObjectPath("/active_parent"),
                "p2p-dev-wlan0",
                "wlan0",
                30,
                100),
        };

        var selected = P2PDeviceSelector.Select(candidates);

        Assert.NotNull(selected);
        Assert.Equal("p2p-dev-wlan0", selected.InterfaceName);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(70)]
    [InlineData(110)]
    [InlineData(120)]
    public void RejectsP2PDeviceThatIsUnavailableBusyOrFailed(uint state)
    {
        var candidate = new P2PDeviceCandidate(
            new ObjectPath("/candidate"),
            "p2p-dev-wlan0",
            "wlan0",
            state,
            30);

        Assert.Null(P2PDeviceSelector.Select(new[] { candidate }));
    }

    [Fact]
    public void DoesNotPreferOrphanedP2PDeviceOverKnownActiveParent()
    {
        var candidates = new[]
        {
            new P2PDeviceCandidate(
                new ObjectPath("/orphan"),
                "p2p-dev-wlan9",
                "wlan9",
                30,
                null),
            new P2PDeviceCandidate(
                new ObjectPath("/known"),
                "p2p-dev-wlan0",
                "wlan0",
                30,
                100),
        };

        var selected = P2PDeviceSelector.Select(candidates);

        Assert.NotNull(selected);
        Assert.Equal("p2p-dev-wlan0", selected.InterfaceName);
    }
}
