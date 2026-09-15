using System.Net;
using System.Net.NetworkInformation;
using Tmds.DBus;

namespace Miracast.Receiver.Linux;

internal static class P2PAddressing
{
    internal static string[] GetGroupInterfaceNames() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Select(networkInterface => networkInterface.Name)
            .Where(IsGroupInterfaceName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static bool IsGroupInterfaceName(string name) =>
        name.StartsWith("p2p-", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("p2p-dev-", StringComparison.OrdinalIgnoreCase);

    internal static IPAddress? GetLocalAddress(IDictionary<string, object> properties)
    {
        if (!properties.TryGetValue("AddressData", out var value))
            return null;

        if (value is IEnumerable<IDictionary<string, object>> addresses)
        {
            foreach (var address in addresses)
            {
                if (address.TryGetValue("address", out var text)
                    && text is string ip
                    && IPAddress.TryParse(ip, out var parsed))
                {
                    return parsed;
                }
            }
        }
        return null;
    }

    internal static IPAddress? GetIpAddress(IDictionary<string, object> properties, string name) =>
        properties.TryGetValue(name, out var value)
        && value is string text
        && IPAddress.TryParse(text, out var address)
            ? address
            : null;

    internal static string? GetObjectPath(IDictionary<string, object> properties, string name) =>
        properties.TryGetValue(name, out var value) && value is ObjectPath path
            ? path.ToString()
            : null;

    internal static int GetWfdControlPort(ReadOnlySpan<byte> informationElements)
    {
        for (var offset = 0; offset + 8 < informationElements.Length; offset++)
        {
            if (informationElements[offset] != 0
                || informationElements[offset + 1] != 0
                || informationElements[offset + 2] != 6)
            {
                continue;
            }

            var port = (informationElements[offset + 5] << 8) | informationElements[offset + 6];
            if (port > 0)
                return port;
        }
        return 7236;
    }

    internal static IPAddress? GetGroupAddress(IDictionary<string, object> properties, string name) =>
        properties.TryGetValue(name, out var value)
        && value is byte[] bytes
        && bytes.Length == 4
        && bytes.Any(static item => item != 0)
            ? new IPAddress(bytes)
            : null;

    internal static int? GetPrefixLength(IPAddress netmask)
    {
        var prefixLength = 0;
        var zeroSeen = false;
        foreach (var value in netmask.GetAddressBytes())
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var set = (value & (1 << bit)) != 0;
                if (set && zeroSeen)
                    return null;
                if (set)
                    prefixLength++;
                else
                    zeroSeen = true;
            }
        }
        return prefixLength;
    }
}
