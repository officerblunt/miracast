namespace Miracast.Receiver.Linux;

internal static class P2PDeviceSelector
{
    private const uint DeviceStateDisconnected = 30;
    private const uint DeviceStateActivated = 100;

    internal static string? GetParentWifiInterfaceName(string p2pInterface) =>
        p2pInterface.StartsWith("p2p-dev-", StringComparison.Ordinal)
            ? p2pInterface["p2p-dev-".Length..]
            : null;

    internal static bool IsSupplicantInterfaceForP2PDevice(
        string supplicantInterface,
        string? p2pInterface) =>
        p2pInterface is not null
        && GetParentWifiInterfaceName(p2pInterface) is { } parentInterface
        && supplicantInterface.Equals(parentInterface, StringComparison.Ordinal);

    internal static P2PDeviceCandidate? Select(IEnumerable<P2PDeviceCandidate> candidates) =>
        candidates
            .Where(static candidate => candidate.DeviceState is DeviceStateDisconnected or DeviceStateActivated)
            .OrderBy(static candidate => candidate.DeviceState == DeviceStateDisconnected ? 0 : 1)
            .ThenBy(static candidate => candidate.ParentInterfaceName is null
                || candidate.ParentState is null
                ? 3
                : candidate.ParentState == DeviceStateDisconnected
                    ? 0
                    : candidate.ParentState == DeviceStateActivated
                        ? 1
                        : 2)
            .FirstOrDefault();
}
