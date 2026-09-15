using System.Net;
using Tmds.DBus;

namespace Miracast.Receiver.Linux;

internal sealed record WifiP2PPeer(
    ObjectPath Path,
    string Name,
    string HardwareAddress,
    byte Strength,
    byte[] WfdIEs);

internal sealed record P2PDeviceCandidate(
    ObjectPath Path,
    string InterfaceName,
    string? ParentInterfaceName,
    uint DeviceState,
    uint? ParentState);

internal sealed record P2PConnectionContext(
    WifiP2PPeer Peer,
    string InterfaceName,
    IPAddress LocalAddress,
    IPAddress SourceAddress,
    int WfdControlPort);
