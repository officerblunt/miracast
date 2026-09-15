using System.Net;
using Tmds.DBus;

namespace Miracast.Receiver.Linux;

internal static class P2PDiagnostics
{
    internal static string FormatProperties(IDictionary<string, object> properties) =>
        properties.Count == 0
            ? "no details"
            : string.Join(", ", properties.Select(pair => $"{pair.Key}={FormatPropertyValue(pair.Value)}"));

    internal static string DescribeDeviceStateReason(uint reason) => reason switch
    {
        5 => "IPv4 configuration is unavailable (reason 5)",
        7 => "required WPS credentials were not supplied (reason 7)",
        8 => "wpa_supplicant disconnected (reason 8)",
        9 => "wpa_supplicant rejected the configuration (reason 9)",
        10 => "wpa_supplicant failed (reason 10)",
        11 => "wpa_supplicant timed out while forming the P2P group (reason 11)",
        _ => $"reason {reason}",
    };

    internal static bool IsSupplicantTemporarilyUnavailable(DBusException exception) =>
        exception.ErrorName is "org.freedesktop.NetworkManager.Device.NotActive"
            or "org.freedesktop.DBus.Error.ServiceUnknown"
            or "org.freedesktop.DBus.Error.NameHasNoOwner";

    internal static bool IsP2POperationBusy(DBusException exception) =>
        exception.Message.Contains("Could not start P2P listen", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("scan trigger", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("scan pending", StringComparison.OrdinalIgnoreCase);

    internal static bool IsAccessDenied(DBusException exception) =>
        exception.ErrorName is "org.freedesktop.DBus.Error.AccessDenied"
            or "org.freedesktop.DBus.Error.AuthFailed";

    internal static bool IsUnsupportedProperty(DBusException exception) =>
        exception.ErrorName is "org.freedesktop.DBus.Error.UnknownProperty"
            or "org.freedesktop.DBus.Error.InvalidArgs";

    internal static bool IsMissingP2PInterface(DBusException exception) =>
        exception.ErrorName is "org.freedesktop.DBus.Error.UnknownInterface"
            or "org.freedesktop.DBus.Error.UnknownProperty";

    internal static string NormalizeHardwareAddress(string address) =>
        string.Concat(address.Where(Uri.IsHexDigit)).ToUpperInvariant();

    internal static string GetProperty(
        IDictionary<string, object> properties,
        string name,
        string fallback) =>
        properties.TryGetValue(name, out var value)
        && value is string text
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : fallback;

    private static string FormatPropertyValue(object value) => value switch
    {
        byte[] bytes when bytes.Length == 4 => new IPAddress(bytes).ToString(),
        byte[] bytes => Convert.ToHexString(bytes),
        _ => value.ToString() ?? string.Empty,
    };
}
