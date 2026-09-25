using System.Net;
using System.Net.NetworkInformation;
using Tmds.DBus;

namespace Miracast.Receiver.Linux;

internal static class P2PAddressing
{
    private static readonly string[] PersistentGroupPropertyNames =
        ["bssid", "ssid", "psk", "mode"];

    internal static string[] GetGroupInterfaceNames(string? parentInterfaceName = null)
    {
        var parentPhy = parentInterfaceName is null
            ? null
            : GetPhyPath(parentInterfaceName);
        if (parentInterfaceName is not null && parentPhy is null)
            return [];

        return NetworkInterface.GetAllNetworkInterfaces()
            .Select(networkInterface => networkInterface.Name)
            .Where(IsGroupInterfaceName)
            .Where(name => parentPhy is null || IsInterfaceOnPhy(name, parentPhy))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsGroupInterfaceForParent(string name, string parentInterfaceName)
    {
        var parentPhy = GetPhyPath(parentInterfaceName);
        return parentPhy is not null
               && IsGroupInterfaceName(name)
               && IsInterfaceOnPhy(name, parentPhy);
    }

    private static bool IsInterfaceOnPhy(string name, string phyPath) =>
        string.Equals(GetPhyPath(name), phyPath, StringComparison.Ordinal);

    internal static bool IsGroupInterfaceName(string name) =>
        name.StartsWith("p2p-", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("p2p-dev-", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSafeKernelInterfaceName(string name) =>
        IsGroupInterfaceName(name)
        && name.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    internal static Dictionary<string, object> CopyPersistentGroupProperties(
        IDictionary<string, object> properties)
    {
        var copy = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var name in PersistentGroupPropertyNames)
        {
            if (properties.TryGetValue(name, out var value))
                copy[name] = value;
        }
        return copy;
    }

    private static string? GetPhyPath(string interfaceName)
    {
        if (interfaceName.Length == 0
            || interfaceName.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            return null;
        }

        try
        {
            var link = new DirectoryInfo($"/sys/class/net/{interfaceName}/phy80211");
            if (!link.Exists)
                return null;
            return link.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsConcreteGroupObjectPath(ObjectPath groupPath) =>
        groupPath.ToString() != "/";

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

    internal static string? GetHardwareAddress(
        IDictionary<string, object> properties,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!properties.TryGetValue(name, out var value))
                continue;

            if (value is byte[] bytes && bytes.Length == 6)
            {
                return Convert.ToHexString(bytes);
            }
            if (value is string text)
            {
                var normalized = P2PDiagnostics.NormalizeHardwareAddress(text);
                if (normalized.Length == 12)
                    return normalized;
            }
        }
        return null;
    }

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

    internal static bool IsWfdSource(ReadOnlySpan<byte> informationElements)
    {
        // WFD Device Information is subelement 0. Its first two payload bytes
        // are a big-endian bitmap whose low two bits identify the device role:
        // 0 = Source, 1 = Primary Sink, 2 = Secondary Sink, 3 = dual-role.
        for (var offset = 0; offset + 4 < informationElements.Length; offset++)
        {
            if (informationElements[offset] != 0)
                continue;

            var length = (informationElements[offset + 1] << 8)
                         | informationElements[offset + 2];
            if (length < 2 || offset + 3 + length > informationElements.Length)
                continue;

            var role = informationElements[offset + 4] & 0x03;
            return role is 0 or 3;
        }
        return false;
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
