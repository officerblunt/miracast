namespace Miracast.Receiver.Linux;

internal static class P2PNetworkConfiguration
{
    internal static readonly byte[] SinkWfdInformationElements =
        [0x00, 0x00, 0x06, 0x00, 0x11, 0x1c, 0x44, 0x00, 0xc8];

    internal static readonly byte[] DisplayPrimaryDeviceType =
        [0x00, 0x07, 0x00, 0x50, 0xf2, 0x04, 0x00, 0x01];

    internal static Dictionary<string, IDictionary<string, object>> CreateConnectionSettings(
        string peerName,
        string peerHardwareAddress) =>
        new()
        {
            ["connection"] = new Dictionary<string, object>
            {
                ["id"] = $"Miracast {peerName}",
                ["type"] = "wifi-p2p",
                ["uuid"] = Guid.NewGuid().ToString(),
                ["autoconnect"] = false,
                // The Source initiates the media flow, so the volatile P2P
                // link must accept its inbound RTP/RTCP packets.
                ["zone"] = "trusted",
            },
            ["wifi-p2p"] = new Dictionary<string, object>
            {
                ["peer"] = peerHardwareAddress,
                ["wfd-ies"] = SinkWfdInformationElements,
                ["wps-method"] = 0x4u,
            },
            ["ipv4"] = new Dictionary<string, object>
            {
                // Isolate the temporary P2P route before activation; applying
                // this only after GroupStarted can replace the default route.
                ["method"] = "auto",
                ["never-default"] = true,
                ["ignore-auto-dns"] = true,
                ["may-fail"] = false,
            },
            ["ipv6"] = new Dictionary<string, object>
            {
                ["method"] = "auto",
                ["never-default"] = true,
                ["ignore-auto-dns"] = true,
                ["may-fail"] = true,
            },
        };

    internal static Dictionary<string, object> CreateDeviceConfiguration(string receiverName) =>
        new()
        {
            ["DeviceName"] = receiverName,
            ["PrimaryDeviceType"] = DisplayPrimaryDeviceType,
            ["GOIntent"] = 0u,
            // Keep regular STA Wi-Fi on the physical interface and create a
            // dedicated group interface for concurrent P2P operation.
            ["NoGroupIface"] = false,
        };

    internal static bool TryGetOperatingChannel(
        int frequency,
        out uint operatingClass,
        out uint channel)
    {
        (operatingClass, channel) = frequency switch
        {
            2484 => (82u, 14u),
            >= 2412 and <= 2472 when (frequency - 2407) % 5 == 0 =>
                (81u, (uint)((frequency - 2407) / 5)),
            >= 5180 and <= 5240 when (frequency - 5000) % 5 == 0 =>
                (115u, (uint)((frequency - 5000) / 5)),
            >= 5260 and <= 5320 when (frequency - 5000) % 5 == 0 =>
                (118u, (uint)((frequency - 5000) / 5)),
            >= 5500 and <= 5720 when (frequency - 5000) % 5 == 0 =>
                (121u, (uint)((frequency - 5000) / 5)),
            >= 5745 and <= 5805 when (frequency - 5000) % 5 == 0 =>
                (124u, (uint)((frequency - 5000) / 5)),
            5825 => (125u, 165u),
            _ => (0u, 0u),
        };
        return operatingClass != 0;
    }
}
