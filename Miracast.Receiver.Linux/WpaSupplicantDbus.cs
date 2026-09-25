using Tmds.DBus;

namespace Miracast.Receiver.Linux;

[DBusInterface("fi.w1.wpa_supplicant1")]
public interface IWpaSupplicant : IDBusObject
{
    Task<T> GetAsync<T>(string property);
    Task SetAsync(string property, object value);
    Task RemoveInterfaceAsync(ObjectPath path);
    Task<IDisposable> WatchInterfaceAddedAsync(
        Action<(ObjectPath path, IDictionary<string, object> properties)> handler,
        Action<Exception>? onError = null);
}

[DBusInterface("fi.w1.wpa_supplicant1.Interface.P2PDevice")]
public interface IWpaP2PDevice : IDBusObject
{
    Task FindAsync(IDictionary<string, object> options);
    Task ListenAsync(int timeout);
    Task StopFindAsync();
    Task CancelAsync();
    Task FlushAsync();
    Task<ObjectPath> AddPersistentGroupAsync(IDictionary<string, object> properties);
    Task RemoveAllPersistentGroupsAsync();
    Task DisconnectAsync();
    Task<T> GetAsync<T>(string property);
    Task SetAsync(string property, object value);
    Task<IDisposable> WatchProvisionDiscoveryPBCRequestAsync(
        Action<ObjectPath> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchProvisionDiscoveryPBCResponseAsync(
        Action<ObjectPath> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchProvisionDiscoveryRequestDisplayPinAsync(
        Action<(ObjectPath peer, string pin)> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchProvisionDiscoveryResponseDisplayPinAsync(
        Action<(ObjectPath peer, string pin)> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchProvisionDiscoveryRequestEnterPinAsync(
        Action<ObjectPath> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchProvisionDiscoveryResponseEnterPinAsync(
        Action<ObjectPath> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchProvisionDiscoveryFailureAsync(
        Action<(ObjectPath peer, int status)> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchInvitationReceivedAsync(
        Action<IDictionary<string, object>> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchGONegotiationRequestAsync(
        Action<(ObjectPath path, ushort devicePasswordId, byte deviceGoIntent)> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchGONegotiationFailureAsync(
        Action<IDictionary<string, object>> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchGONegotiationSuccessAsync(
        Action<IDictionary<string, object>> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchGroupFormationFailureAsync(
        Action<string> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchGroupStartedAsync(
        Action<IDictionary<string, object>> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchGroupFinishedAsync(
        Action<IDictionary<string, object>> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchFindStoppedAsync(
        Action handler,
        Action<Exception>? onError = null);
}

[DBusInterface("fi.w1.wpa_supplicant1.Interface.WPS")]
public interface IWpaWps : IDBusObject
{
    Task<T> GetAsync<T>(string property);
    Task SetAsync(string property, object value);
}

[DBusInterface("fi.w1.wpa_supplicant1.Interface")]
public interface IWpaInterface : IDBusObject
{
    Task DisconnectAsync();
    Task<T> GetAsync<T>(string property);
}

[DBusInterface("fi.w1.wpa_supplicant1.Peer")]
public interface IWpaPeer : IDBusObject
{
    Task<T> GetAsync<T>(string property);
}

[DBusInterface("fi.w1.wpa_supplicant1.PersistentGroup")]
public interface IWpaPersistentGroup : IDBusObject
{
    Task<T> GetAsync<T>(string property);
}
