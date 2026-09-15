using Tmds.DBus;

namespace Miracast.Receiver.Linux;

[DBusInterface("org.freedesktop.NetworkManager")]
public interface INetworkManager : IDBusObject
{
    Task<ObjectPath[]> GetDevicesAsync();
    Task<(ObjectPath connection, ObjectPath activeConnection, IDictionary<string, object> result)>
        AddAndActivateConnection2Async(
            IDictionary<string, IDictionary<string, object>> connection,
            ObjectPath device,
            ObjectPath specificObject,
            IDictionary<string, object> options);
    Task DeactivateConnectionAsync(ObjectPath activeConnection);
}

[DBusInterface("org.freedesktop.NetworkManager.Device")]
public interface INetworkManagerDevice : IDBusObject
{
    Task<T> GetAsync<T>(string property);
    Task<(IDictionary<string, IDictionary<string, object>> connection, ulong versionId)>
        GetAppliedConnectionAsync(uint flags);
    Task ReapplyAsync(
        IDictionary<string, IDictionary<string, object>> connection,
        ulong versionId,
        uint flags);
    Task<IDisposable> WatchStateChangedAsync(Action<(uint newState, uint oldState, uint reason)> handler);
}

[DBusInterface("org.freedesktop.NetworkManager.Device.Wireless")]
public interface INetworkManagerWirelessDevice : IDBusObject
{
    Task<T> GetAsync<T>(string property);
}

[DBusInterface("org.freedesktop.NetworkManager.AccessPoint")]
public interface INetworkManagerAccessPoint : IDBusObject
{
    Task<T> GetAsync<T>(string property);
}

[DBusInterface("org.freedesktop.NetworkManager.Device.WifiP2P")]
public interface IWifiP2PDevice : IDBusObject
{
    Task<T> GetAsync<T>(string property);
    Task<IDisposable> WatchPeerAddedAsync(Action<ObjectPath> handler, Action<Exception>? onError = null);
    Task<IDisposable> WatchPeerRemovedAsync(Action<ObjectPath> handler, Action<Exception>? onError = null);
}

[DBusInterface("org.freedesktop.NetworkManager.WifiP2PPeer")]
public interface IWifiP2PPeer : IDBusObject
{
    Task<IDictionary<string, object>> GetAllAsync();
}

[DBusInterface("org.freedesktop.NetworkManager.IP4Config")]
public interface INetworkManagerIP4Config : IDBusObject
{
    Task<IDictionary<string, object>> GetAllAsync();
}
