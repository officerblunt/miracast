using System.Collections.Concurrent;
using System.Net;
using Tmds.DBus;

namespace Miracast.Receiver.Linux;

internal sealed class NetworkManagerP2P : IAsyncDisposable
{
    private const string Service = "org.freedesktop.NetworkManager";
    private const string SupplicantService = "fi.w1.wpa_supplicant1";
    private const string SupplicantPath = "/fi/w1/wpa_supplicant1";
    private const uint WifiP2PDeviceType = 30;
    private const uint DeviceStateActivated = 100;
    private const uint DeviceStateFailed = 120;
    private const string DeviceNotActiveError = "org.freedesktop.NetworkManager.Device.NotActive";
    private static readonly byte[] SinkWfdInformationElements =
        [0x00, 0x00, 0x06, 0x00, 0x11, 0x1c, 0x44, 0x00, 0xc8];
    private static readonly byte[] DisplayPrimaryDeviceType =
        [0x00, 0x07, 0x00, 0x50, 0xf2, 0x04, 0x00, 0x01];

    private readonly Connection _bus = new(Address.System);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ConcurrentDictionary<string, WifiP2PPeer> _peersByAddress =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _peerAddressesByPath = new();
    private INetworkManager? _networkManager;
    private IWpaSupplicant? _supplicant;
    private IWpaP2PDevice? _supplicantP2PDevice;
    private IWifiP2PDevice? _p2pDevice;
    private ObjectPath _devicePath;
    private ObjectPath? _activeConnection;
    private CancellationTokenSource? _lifetime;
    private Task? _findRenewal;
    private bool _finding;
    private bool _shouldFind;
    private bool _wfdAdvertisementConfigured;
    private byte[]? _previousWfdInformationElements;
    private string? _previousP2PDeviceName;
    private byte[]? _previousPrimaryDeviceType;
    private uint? _previousGoIntent;
    private bool _p2pDeviceConfigured;
    private bool _incomingRequestSubscriptionsConfigured;
    private bool _disposed;

    public event EventHandler<P2PConnectionContext>? PeerConnected;
    public event EventHandler? PeerDisconnected;
    public event EventHandler<string>? StatusChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await _bus.ConnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        _networkManager = _bus.CreateProxy<INetworkManager>(Service, "/org/freedesktop/NetworkManager");
        _supplicant = _bus.CreateProxy<IWpaSupplicant>(SupplicantService, SupplicantPath);

        var devices = await _networkManager.GetDevicesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var path in devices)
        {
            var device = _bus.CreateProxy<INetworkManagerDevice>(Service, path);
            if (await device.GetAsync<uint>("DeviceType").WaitAsync(cancellationToken).ConfigureAwait(false)
                != WifiP2PDeviceType)
            {
                continue;
            }

            _devicePath = path;
            _p2pDevice = _bus.CreateProxy<IWifiP2PDevice>(Service, path);
            break;
        }

        if (_p2pDevice is null)
        {
            throw new InvalidOperationException(
                "NetworkManager did not expose a Wi-Fi P2P device. Check the adapter, driver and wpa_supplicant P2P support.");
        }

        _subscriptions.Add(await _p2pDevice.WatchPeerAddedAsync(
            path => _ = InspectPeerAsync(path, _lifetime.Token),
            exception => Report($"Wi-Fi P2P discovery failed: {exception.Message}"))
            .WaitAsync(cancellationToken).ConfigureAwait(false));
        _subscriptions.Add(await _p2pDevice.WatchPeerRemovedAsync(
            OnPeerRemoved,
            exception => Report($"Wi-Fi P2P discovery failed: {exception.Message}"))
            .WaitAsync(cancellationToken).ConfigureAwait(false));

        await StartDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        _findRenewal = RenewDiscoveryAsync(_lifetime.Token);

        var peers = await _p2pDevice.GetAsync<ObjectPath[]>("Peers")
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var peer in peers)
            _ = InspectPeerAsync(peer, _lifetime.Token);
    }

    private async Task InspectPeerAsync(ObjectPath path, CancellationToken cancellationToken)
    {
        try
        {
            var proxy = _bus.CreateProxy<IWifiP2PPeer>(Service, path);
            var properties = await proxy.GetAllAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!properties.TryGetValue("WfdIEs", out var wfdValue)
                || wfdValue is not byte[] wfdIEs
                || wfdIEs.Length == 0)
            {
                return;
            }

            var peer = new WifiP2PPeer(
                path,
                GetProperty(properties, "Name", "Unknown device"),
                GetProperty(properties, "HwAddress", string.Empty),
                properties.TryGetValue("Strength", out var strength) && strength is byte value ? value : (byte)0,
                wfdIEs);
            if (string.IsNullOrWhiteSpace(peer.HardwareAddress))
                return;

            var address = NormalizeHardwareAddress(peer.HardwareAddress);
            _peersByAddress[address] = peer;
            _peerAddressesByPath[path.ToString()] = address;
            Report($"Found {peer.Name} ({peer.HardwareAddress}), signal {peer.Strength}%.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report($"Could not inspect Wi-Fi P2P peer: {exception.Message}");
        }
    }

    private void OnPeerRemoved(ObjectPath path)
    {
        if (_peerAddressesByPath.TryRemove(path.ToString(), out var address))
            _peersByAddress.TryRemove(address, out _);
        Report($"Wi-Fi P2P peer disappeared: {path}");
    }

    private async Task ConnectAsync(WifiP2PPeer peer, CancellationToken cancellationToken)
    {
        if (!await _connectGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            if (_activeConnection is not null || _networkManager is null)
                return;

            Report($"Connecting to {peer.Name}; confirm WPS Push Button if prompted…");
            var id = $"Miracast {peer.Name}";
            var connection = new Dictionary<string, IDictionary<string, object>>
            {
                ["connection"] = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["type"] = "wifi-p2p",
                    ["uuid"] = Guid.NewGuid().ToString(),
                    ["autoconnect"] = false,
                },
                ["wifi-p2p"] = new Dictionary<string, object>
                {
                    ["peer"] = peer.HardwareAddress,
                    ["wfd-ies"] = SinkWfdInformationElements,
                    ["wps-method"] = 0x4u,
                },
            };
            var options = new Dictionary<string, object>
            {
                ["persist"] = "volatile",
                ["bind-activation"] = "dbus-name",
            };

            var result = await _networkManager.AddAndActivateConnection2Async(
                connection, _devicePath, peer.Path, options).WaitAsync(cancellationToken).ConfigureAwait(false);
            _activeConnection = result.activeConnection;

            var device = _bus.CreateProxy<INetworkManagerDevice>(Service, _devicePath);
            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stateSubscription = await device.WatchStateChangedAsync(change =>
            {
                if (change.newState == DeviceStateActivated)
                    activated.TrySetResult();
                else if (change.newState == DeviceStateFailed)
                    activated.TrySetException(new InvalidOperationException(
                        $"NetworkManager activation failed (reason {change.reason})."));
                else if (_activeConnection is not null && change.newState <= 30)
                    _ = HandleDisconnectedAsync(_lifetime?.Token ?? CancellationToken.None);
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
            _subscriptions.Add(stateSubscription);

            var state = await device.GetAsync<uint>("State").WaitAsync(cancellationToken).ConfigureAwait(false);
            if (state == DeviceStateActivated)
                activated.TrySetResult();

            await activated.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            await StopFindAsync(disable: true).ConfigureAwait(false);
            var context = await CreateConnectionContextAsync(device, peer, cancellationToken).ConfigureAwait(false);
            PeerConnected?.Invoke(this, context);
        }
        catch
        {
            if (_activeConnection is { } active && _networkManager is not null)
            {
                try { await _networkManager.DeactivateConnectionAsync(active).ConfigureAwait(false); }
                catch { }
                _activeConnection = null;
            }
            throw;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task StopAsync()
    {
        _lifetime?.Cancel();
        await StopFindAsync(disable: true).ConfigureAwait(false);

        if (_activeConnection is { } active && _networkManager is not null)
        {
            try { await _networkManager.DeactivateConnectionAsync(active).ConfigureAwait(false); }
            catch (Exception exception) { Report($"Could not deactivate the P2P connection: {exception.Message}"); }
            _activeConnection = null;
        }

        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        if (_findRenewal is not null)
        {
            try { await _findRenewal.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _findRenewal = null;
        }
        _lifetime?.Dispose();
        _lifetime = null;
        _peersByAddress.Clear();
        _peerAddressesByPath.Clear();
        await RestoreWfdAdvertisementAsync().ConfigureAwait(false);
    }

    public async Task DisconnectCurrentAsync()
    {
        if (_activeConnection is not { } active || _networkManager is null)
            return;

        _activeConnection = null;
        try
        {
            await _networkManager.DeactivateConnectionAsync(active).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Report($"Could not deactivate the P2P connection: {exception.Message}");
        }
    }

    private async Task<P2PConnectionContext> CreateConnectionContextAsync(
        INetworkManagerDevice device,
        WifiP2PPeer peer,
        CancellationToken cancellationToken)
    {
        var interfaceName = await device.GetAsync<string>("Interface")
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var ip4Path = await device.GetAsync<ObjectPath>("Ip4Config")
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (ip4Path.ToString() == "/")
            throw new InvalidOperationException("NetworkManager activated P2P without an IPv4 configuration.");

        var ip4Config = _bus.CreateProxy<INetworkManagerIP4Config>(Service, ip4Path);
        var properties = await ip4Config.GetAllAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var localAddress = GetLocalAddress(properties)
            ?? throw new InvalidOperationException("NetworkManager did not provide the receiver IPv4 address.");
        var sourceAddress = GetIpAddress(properties, "Gateway")
            ?? throw new InvalidOperationException("NetworkManager did not provide the Miracast Source IPv4 address.");
        var controlPort = GetWfdControlPort(peer.WfdIEs);

        Report($"P2P ready on {interfaceName}: {localAddress} → {sourceAddress}:{controlPort}.");
        return new P2PConnectionContext(peer, interfaceName, localAddress, sourceAddress, controlPort);
    }

    private static IPAddress? GetLocalAddress(IDictionary<string, object> properties)
    {
        if (!properties.TryGetValue("AddressData", out var value))
            return null;

        if (value is IDictionary<string, object>[] addresses)
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
        else if (value is IEnumerable<IDictionary<string, object>> addressSequence)
        {
            foreach (var address in addressSequence)
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

    private static IPAddress? GetIpAddress(IDictionary<string, object> properties, string name) =>
        properties.TryGetValue(name, out var value)
        && value is string text
        && IPAddress.TryParse(text, out var address)
            ? address
            : null;

    private static int GetWfdControlPort(ReadOnlySpan<byte> informationElements)
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

    private async Task StartDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (_p2pDevice is null || cancellationToken.IsCancellationRequested)
            return;
        _shouldFind = true;
        var waitingReported = false;
        while (_shouldFind && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_finding)
                    await StopFindAsync(disable: false).ConfigureAwait(false);
                await ConfigureWfdAdvertisementAsync(cancellationToken).ConfigureAwait(false);
                var p2pDevice = _supplicantP2PDevice
                    ?? throw new InvalidOperationException("The wpa_supplicant P2PDevice proxy is not available.");
                await p2pDevice.ListenAsync(600).WaitAsync(cancellationToken).ConfigureAwait(false);
                // Re-apply and verify the Sink identity used by Probe Responses in Listen state.
                await ConfigureWfdAdvertisementAsync(cancellationToken).ConfigureAwait(false);
                await VerifyWfdAdvertisementAsync(cancellationToken).ConfigureAwait(false);
                _finding = true;
                return;
            }
            catch (DBusException exception) when (IsSupplicantTemporarilyUnavailable(exception))
            {
                _finding = false;
                if (!waitingReported)
                {
                    Report(
                        "Wi-Fi Direct is waiting for NetworkManager's wpa_supplicant interface. "
                        + "Keep NetworkManager and wpa_supplicant running and make sure Wi-Fi is enabled.");
                    waitingReported = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ConfigureWfdAdvertisementAsync(CancellationToken cancellationToken)
    {
        var supplicant = _supplicant
            ?? throw new InvalidOperationException("The wpa_supplicant D-Bus proxy is not available.");
        try
        {
            if (!_wfdAdvertisementConfigured)
            {
                _previousWfdInformationElements = await supplicant.GetAsync<byte[]>("WFDIEs")
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await supplicant.SetAsync("WFDIEs", SinkWfdInformationElements)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            _wfdAdvertisementConfigured = true;
            await ConfigureP2PDeviceAsync(supplicant, cancellationToken).ConfigureAwait(false);
        }
        catch (DBusException exception) when (IsAccessDenied(exception))
        {
            throw new InvalidOperationException(
                "The system D-Bus policy denied access to wpa_supplicant.WFDIEs. "
                + "The receiver needs permission to publish its Miracast Sink capabilities.",
                exception);
        }
        catch (DBusException exception) when (IsUnsupportedProperty(exception))
        {
            throw new InvalidOperationException(
                "This wpa_supplicant build does not expose WFDIEs. "
                + "Install a build with CONFIG_WIFI_DISPLAY and P2P support.",
                exception);
        }
    }

    private async Task ConfigureP2PDeviceAsync(
        IWpaSupplicant supplicant,
        CancellationToken cancellationToken)
    {
        if (_supplicantP2PDevice is null)
        {
            var interfaces = await supplicant.GetAsync<ObjectPath[]>("Interfaces")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            foreach (var path in interfaces)
            {
                var candidate = _bus.CreateProxy<IWpaP2PDevice>(SupplicantService, path);
                try
                {
                    var existing = await candidate.GetAsync<IDictionary<string, object>>("P2PDeviceConfig")
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                    _supplicantP2PDevice = candidate;
                    if (existing.TryGetValue("DeviceName", out var name) && name is string deviceName)
                        _previousP2PDeviceName = deviceName;
                    if (existing.TryGetValue("PrimaryDeviceType", out var type) && type is byte[] primaryType)
                        _previousPrimaryDeviceType = primaryType;
                    if (existing.TryGetValue("GOIntent", out var intent) && intent is uint goIntent)
                        _previousGoIntent = goIntent;
                    break;
                }
                catch (DBusException exception) when (IsMissingP2PInterface(exception))
                {
                }
            }
        }

        var p2pDevice = _supplicantP2PDevice
            ?? throw new InvalidOperationException(
                "wpa_supplicant did not expose a P2PDevice interface for the Wi-Fi adapter.");
        var receiverName = $"Miracast Receiver ({Environment.MachineName})";
        if (receiverName.Length > 32)
            receiverName = receiverName[..32];
        await p2pDevice.SetAsync("P2PDeviceConfig", new Dictionary<string, object>
        {
            ["DeviceName"] = receiverName,
            ["PrimaryDeviceType"] = DisplayPrimaryDeviceType,
            ["GOIntent"] = 0u,
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        _p2pDeviceConfigured = true;

        if (!_incomingRequestSubscriptionsConfigured)
        {
            _subscriptions.Add(await p2pDevice.WatchProvisionDiscoveryPBCRequestAsync(
                path => QueueIncomingConnection(path, "WPS Push Button request"),
                exception => Report($"Could not monitor incoming WPS requests: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchGONegotiationRequestAsync(
                request => QueueIncomingConnection(request.path, "GO negotiation request"),
                exception => Report($"Could not monitor incoming GO negotiation: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _incomingRequestSubscriptionsConfigured = true;
        }
    }

    private async Task VerifyWfdAdvertisementAsync(CancellationToken cancellationToken)
    {
        var supplicant = _supplicant!;
        var advertisedIes = await supplicant.GetAsync<byte[]>("WFDIEs")
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!advertisedIes.AsSpan().SequenceEqual(SinkWfdInformationElements))
            throw new InvalidOperationException("wpa_supplicant did not retain the Miracast Sink WFD subelements.");

        var config = await _supplicantP2PDevice!.GetAsync<IDictionary<string, object>>("P2PDeviceConfig")
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var receiverName = config.TryGetValue("DeviceName", out var name) && name is string text
            ? text
            : "Miracast Receiver";
        if (!config.TryGetValue("PrimaryDeviceType", out var primaryType)
            || primaryType is not byte[] bytes
            || !bytes.AsSpan().SequenceEqual(DisplayPrimaryDeviceType))
        {
            throw new InvalidOperationException("wpa_supplicant did not retain the Miracast Display device type.");
        }

        Report(
            $"Miracast receiver '{receiverName}' is advertising in P2P Listen mode. "
            + "Waiting for a Source to connect…");
    }

    private void QueueIncomingConnection(ObjectPath supplicantPeerPath, string reason)
    {
        var cancellationToken = _lifetime?.Token ?? CancellationToken.None;
        _ = ConnectIncomingPeerAsync(supplicantPeerPath, reason, cancellationToken);
    }

    private async Task ConnectIncomingPeerAsync(
        ObjectPath supplicantPeerPath,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var supplicantPeer = _bus.CreateProxy<IWpaPeer>(SupplicantService, supplicantPeerPath);
            var addressBytes = await supplicantPeer.GetAsync<byte[]>("DeviceAddress")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            var address = Convert.ToHexString(addressBytes);

            WifiP2PPeer? peer = null;
            for (var attempt = 0; attempt < 30 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                if (_peersByAddress.TryGetValue(address, out peer))
                    break;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (peer is null)
            {
                Report($"Received an incoming {reason}, but NetworkManager did not expose peer {address}.");
                return;
            }

            Report($"{peer.Name} selected this receiver ({reason}); accepting the connection…");
            await ConnectAsync(peer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report($"Could not accept the incoming Wi-Fi Direct connection: {exception.Message}");
        }
    }

    private async Task RestoreWfdAdvertisementAsync()
    {
        try
        {
            if (_p2pDeviceConfigured && _supplicantP2PDevice is not null)
            {
                var previousConfig = new Dictionary<string, object>();
                if (_previousP2PDeviceName is not null)
                    previousConfig["DeviceName"] = _previousP2PDeviceName;
                if (_previousPrimaryDeviceType is not null)
                    previousConfig["PrimaryDeviceType"] = _previousPrimaryDeviceType;
                if (_previousGoIntent is not null)
                    previousConfig["GOIntent"] = _previousGoIntent.Value;
                if (previousConfig.Count > 0)
                    await _supplicantP2PDevice.SetAsync("P2PDeviceConfig", previousConfig).ConfigureAwait(false);
            }
            if (_wfdAdvertisementConfigured && _supplicant is not null)
            {
                await _supplicant.SetAsync("WFDIEs", _previousWfdInformationElements ?? [])
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Report($"Could not restore the previous WFD advertisement: {exception.Message}");
        }
        finally
        {
            _wfdAdvertisementConfigured = false;
            _previousWfdInformationElements = null;
            _p2pDeviceConfigured = false;
            _incomingRequestSubscriptionsConfigured = false;
            _supplicantP2PDevice = null;
            _previousP2PDeviceName = null;
            _previousPrimaryDeviceType = null;
            _previousGoIntent = null;
        }
    }

    private static bool IsSupplicantTemporarilyUnavailable(DBusException exception) =>
        exception.ErrorName is DeviceNotActiveError
            or "org.freedesktop.DBus.Error.ServiceUnknown"
            or "org.freedesktop.DBus.Error.NameHasNoOwner";

    private static bool IsAccessDenied(DBusException exception) =>
        exception.ErrorName is "org.freedesktop.DBus.Error.AccessDenied"
            or "org.freedesktop.DBus.Error.AuthFailed";

    private static bool IsUnsupportedProperty(DBusException exception) =>
        exception.ErrorName is "org.freedesktop.DBus.Error.UnknownProperty"
            or "org.freedesktop.DBus.Error.InvalidArgs";

    private static bool IsMissingP2PInterface(DBusException exception) =>
        exception.ErrorName is "org.freedesktop.DBus.Error.UnknownInterface"
            or "org.freedesktop.DBus.Error.UnknownProperty";

    private static string NormalizeHardwareAddress(string address) =>
        string.Concat(address.Where(Uri.IsHexDigit)).ToUpperInvariant();

    private async Task RenewDiscoveryAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(9), cancellationToken).ConfigureAwait(false);
                if (_shouldFind && _p2pDevice is not null)
                    await StartDiscoveryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report($"Could not renew P2P discovery: {exception.Message}");
        }
    }

    private async Task HandleDisconnectedAsync(CancellationToken cancellationToken)
    {
        if (_activeConnection is null)
            return;
        _activeConnection = null;
        PeerDisconnected?.Invoke(this, EventArgs.Empty);
        try { await StartDiscoveryAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { Report($"Could not restart P2P discovery: {exception.Message}"); }
    }

    private async Task StopFindAsync(bool disable)
    {
        if (disable)
            _shouldFind = false;
        if (!_finding)
            return;
        if (_supplicantP2PDevice is not null)
        {
            try { await _supplicantP2PDevice.StopFindAsync().ConfigureAwait(false); }
            catch (Exception exception) { Report($"Could not stop P2P listen: {exception.Message}"); }
        }
        _finding = false;
    }

    private static string GetProperty(IDictionary<string, object> properties, string name, string fallback) =>
        properties.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : fallback;

    private void Report(string status) => StatusChanged?.Invoke(this, status);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _connectGate.Dispose();
        _bus.Dispose();
    }
}

internal sealed record WifiP2PPeer(
    ObjectPath Path,
    string Name,
    string HardwareAddress,
    byte Strength,
    byte[] WfdIEs);

internal sealed record P2PConnectionContext(
    WifiP2PPeer Peer,
    string InterfaceName,
    IPAddress LocalAddress,
    IPAddress SourceAddress,
    int WfdControlPort);

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
    Task<IDisposable> WatchStateChangedAsync(Action<(uint newState, uint oldState, uint reason)> handler);
}

[DBusInterface("org.freedesktop.NetworkManager.Device.WifiP2P")]
public interface IWifiP2PDevice : IDBusObject
{
    Task StartFindAsync(IDictionary<string, object> options);
    Task StopFindAsync();
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

[DBusInterface("fi.w1.wpa_supplicant1")]
public interface IWpaSupplicant : IDBusObject
{
    Task<T> GetAsync<T>(string property);
    Task SetAsync(string property, object value);
}

[DBusInterface("fi.w1.wpa_supplicant1.Interface.P2PDevice")]
public interface IWpaP2PDevice : IDBusObject
{
    Task ListenAsync(int timeout);
    Task StopFindAsync();
    Task<T> GetAsync<T>(string property);
    Task SetAsync(string property, object value);
    Task<IDisposable> WatchProvisionDiscoveryPBCRequestAsync(
        Action<ObjectPath> handler,
        Action<Exception>? onError = null);
    Task<IDisposable> WatchGONegotiationRequestAsync(
        Action<(ObjectPath path, ushort devicePasswordId, byte deviceGoIntent)> handler,
        Action<Exception>? onError = null);
}

[DBusInterface("fi.w1.wpa_supplicant1.Peer")]
public interface IWpaPeer : IDBusObject
{
    Task<T> GetAsync<T>(string property);
}
