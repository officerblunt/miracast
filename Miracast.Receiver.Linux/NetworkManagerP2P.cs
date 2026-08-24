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
    private readonly SemaphoreSlim _authorizationGate = new(1, 1);
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ConcurrentDictionary<string, WifiP2PPeer> _peersByAddress =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _peerAddressesByPath = new();
    private INetworkManager? _networkManager;
    private IWpaSupplicant? _supplicant;
    private IWpaP2PDevice? _supplicantP2PDevice;
    private IWpaP2PDevice? _groupP2PDevice;
    private IWpaWps? _supplicantWps;
    private IWifiP2PDevice? _p2pDevice;
    private IWifiDevice? _wifiDevice;
    private ObjectPath _devicePath;
    private ObjectPath? _activeConnection;
    private CancellationTokenSource? _lifetime;
    private Task? _findRenewal;
    private Task? _findRestart;
    private bool _finding;
    private bool _shouldFind;
    private bool _wfdAdvertisementConfigured;
    private byte[]? _previousWfdInformationElements;
    private string? _previousP2PDeviceName;
    private byte[]? _previousPrimaryDeviceType;
    private uint? _previousGoIntent;
    private string? _previousWpsConfigMethods;
    private bool _p2pDeviceConfigured;
    private bool _wpsConfigured;
    private bool _incomingRequestSubscriptionsConfigured;
    private bool _connecting;
    private bool _initialP2PResetCompleted;
    private DateTime _authorizationExpiresAt;
    private bool _disposed;

    public event EventHandler<P2PConnectionContext>? PeerConnected;
    public event EventHandler? PeerDisconnected;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<WifiP2PPeer>? PeerAvailable;
    public event EventHandler<WifiP2PPeer>? PeerUnavailable;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await _bus.ConnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        _networkManager = _bus.CreateProxy<INetworkManager>(Service, "/org/freedesktop/NetworkManager");
        _supplicant = _bus.CreateProxy<IWpaSupplicant>(SupplicantService, SupplicantPath);

        var devices = await _networkManager.GetDevicesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var wifiDevices = new List<(ObjectPath path, string interfaceName)>();
        foreach (var path in devices)
        {
            var device = _bus.CreateProxy<INetworkManagerDevice>(Service, path);
            var deviceType = await device.GetAsync<uint>("DeviceType")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (deviceType == 2)
            {
                wifiDevices.Add((path, await device.GetAsync<string>("Interface")
                    .WaitAsync(cancellationToken).ConfigureAwait(false)));
                continue;
            }
            if (deviceType != WifiP2PDeviceType || _p2pDevice is not null)
                continue;

            _devicePath = path;
            _p2pDevice = _bus.CreateProxy<IWifiP2PDevice>(Service, path);
        }

        if (_p2pDevice is null)
        {
            throw new InvalidOperationException(
                "NetworkManager did not expose a Wi-Fi P2P device. Check the adapter, driver and wpa_supplicant P2P support.");
        }

        var p2pInterfaceName = await _bus.CreateProxy<INetworkManagerDevice>(Service, _devicePath)
            .GetAsync<string>("Interface").WaitAsync(cancellationToken).ConfigureAwait(false);
        var parentInterfaceName = p2pInterfaceName.StartsWith("p2p-dev-", StringComparison.Ordinal)
            ? p2pInterfaceName[8..]
            : string.Empty;
        var wifiDevice = wifiDevices.FirstOrDefault(candidate =>
            candidate.interfaceName.Equals(parentInterfaceName, StringComparison.Ordinal));
        if (wifiDevice == default && wifiDevices.Count == 1)
            wifiDevice = wifiDevices[0];
        if (wifiDevice != default)
        {
            _wifiDevice = _bus.CreateProxy<IWifiDevice>(Service, wifiDevice.path);
            var physicalDevice = _bus.CreateProxy<INetworkManagerDevice>(Service, wifiDevice.path);
            _subscriptions.Add(await physicalDevice.WatchStateChangedAsync(change =>
            {
                if (change.newState is 30 or DeviceStateActivated)
                    QueueDiscoveryRestart("the physical Wi-Fi adapter changed state");
            }).WaitAsync(cancellationToken).ConfigureAwait(false));
        }

        _subscriptions.Add(await _p2pDevice.WatchPeerAddedAsync(
            path => _ = InspectPeerAsync(path, _lifetime.Token),
            exception => Report($"Wi-Fi P2P discovery failed: {exception.Message}"))
            .WaitAsync(cancellationToken).ConfigureAwait(false));
        _subscriptions.Add(await _p2pDevice.WatchPeerRemovedAsync(
            OnPeerRemoved,
            exception => Report($"Wi-Fi P2P discovery failed: {exception.Message}"))
            .WaitAsync(cancellationToken).ConfigureAwait(false));

        await WakeWifiAdapterAsync(cancellationToken).ConfigureAwait(false);
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
            PeerAvailable?.Invoke(this, peer);
            if (!_connecting)
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
        if (_peerAddressesByPath.TryRemove(path.ToString(), out var address)
            && _peersByAddress.TryRemove(address, out var peer))
        {
            PeerUnavailable?.Invoke(this, peer);
        }
        if (!_connecting)
            Report($"Wi-Fi P2P peer disappeared: {path}");
    }

    public async Task ApproveConnectionAsync(string sourceId, CancellationToken cancellationToken)
    {
        var address = NormalizeHardwareAddress(sourceId);
        if (!_peersByAddress.TryGetValue(address, out var peer))
        {
            throw new InvalidOperationException(
                "The selected Miracast Source is no longer visible. Refresh discovery and try again.");
        }

        Report($"Connection from {peer.Name} was approved on the receiver.");
        await AuthorizeIncomingConnectionAsync(peer, cancellationToken).ConfigureAwait(false);
    }

    private async Task AuthorizeIncomingConnectionAsync(
        WifiP2PPeer peer,
        CancellationToken cancellationToken)
    {
        if (!await _authorizationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        _connecting = true;
        try
        {
            var p2pDevice = _supplicantP2PDevice
                ?? throw new InvalidOperationException("The wpa_supplicant P2P interface is unavailable.");
            var supplicantPeerPath = await FindSupplicantPeerPathAsync(peer, cancellationToken)
                .ConfigureAwait(false);

            await ReportConcurrentWifiConnectionsAsync(cancellationToken).ConfigureAwait(false);
            await p2pDevice.ConnectAsync(new Dictionary<string, object>
            {
                ["peer"] = supplicantPeerPath,
                ["persistent"] = false,
                ["authorize_only"] = true,
                ["go_intent"] = 0,
                ["wps_method"] = "pbc",
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
            _authorizationExpiresAt = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            Report(
                $"Authorized the incoming connection from {peer.Name}. "
                + "Waiting for the Source to continue WPS…");
        }
        finally
        {
            _connecting = false;
            _authorizationGate.Release();
        }
    }

    private async Task<ObjectPath> FindSupplicantPeerPathAsync(
        WifiP2PPeer peer,
        CancellationToken cancellationToken)
    {
        var p2pDevice = _supplicantP2PDevice
            ?? throw new InvalidOperationException("The wpa_supplicant P2P interface is unavailable.");
        var targetAddress = NormalizeHardwareAddress(peer.HardwareAddress);
        var supplicantPeers = await p2pDevice.GetAsync<ObjectPath[]>("Peers")
            .WaitAsync(cancellationToken).ConfigureAwait(false);

        foreach (var path in supplicantPeers)
        {
            var supplicantPeer = _bus.CreateProxy<IWpaPeer>(SupplicantService, path);
            var addressBytes = await supplicantPeer.GetAsync<byte[]>("DeviceAddress")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (Convert.ToHexString(addressBytes).Equals(targetAddress, StringComparison.OrdinalIgnoreCase))
                return path;
        }

        throw new InvalidOperationException(
            $"wpa_supplicant no longer exposes {peer.Name}. Wait until it appears again and retry.");
    }

    public async Task StopAsync()
    {
        _shouldFind = false;
        _lifetime?.Cancel();
        await ReleaseSupplicantP2PStateAsync().ConfigureAwait(false);

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
        if (_findRestart is not null)
        {
            try { await _findRestart.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _findRestart = null;
        }
        _lifetime?.Dispose();
        _lifetime = null;
        _peersByAddress.Clear();
        _peerAddressesByPath.Clear();
        await RestoreWfdAdvertisementAsync().ConfigureAwait(false);
        _initialP2PResetCompleted = false;
        _authorizationExpiresAt = DateTime.MinValue;
    }

    private static async Task ResetStaleP2PStateAsync(IWpaP2PDevice p2pDevice)
    {
        try { await p2pDevice.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { }
        try { await p2pDevice.StopFindAsync().ConfigureAwait(false); }
        catch (Exception) { }
        try { await p2pDevice.FlushAsync().ConfigureAwait(false); }
        catch (Exception) { }
    }

    private async Task ReleaseSupplicantP2PStateAsync()
    {
        if (_groupP2PDevice is not null)
        {
            try { await _groupP2PDevice.DisconnectAsync().ConfigureAwait(false); }
            catch (Exception exception) { Report($"Could not disconnect the P2P group: {exception.Message}"); }
            _groupP2PDevice = null;
        }

        var p2pDevice = _supplicantP2PDevice;
        if (p2pDevice is null)
            return;
        try { await p2pDevice.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception) { Report($"Could not cancel the pending P2P operation: {exception.Message}"); }
        try { await p2pDevice.StopFindAsync().ConfigureAwait(false); }
        catch (Exception exception) { Report($"Could not stop P2P discovery: {exception.Message}"); }
        try { await p2pDevice.FlushAsync().ConfigureAwait(false); }
        catch (Exception exception) { Report($"Could not flush stale P2P peers: {exception.Message}"); }
        _finding = false;
    }

    public async Task DisconnectCurrentAsync()
    {
        if (_activeConnection is { } active && _networkManager is not null)
        {
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

        if (_groupP2PDevice is not null)
        {
            try { await _groupP2PDevice.DisconnectAsync().ConfigureAwait(false); }
            catch (Exception exception) { Report($"Could not disconnect the P2P group: {exception.Message}"); }
            _groupP2PDevice = null;
        }
        if (_supplicantP2PDevice is not null)
        {
            try { await _supplicantP2PDevice.CancelAsync().ConfigureAwait(false); }
            catch (Exception) { }
        }
        _authorizationExpiresAt = DateTime.MinValue;
        QueueDiscoveryRestart("the Miracast session ended");
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

    private async Task WakeWifiAdapterAsync(CancellationToken cancellationToken)
    {
        if (_wifiDevice is null || cancellationToken.IsCancellationRequested)
            return;

        long previousScan;
        try
        {
            previousScan = await _wifiDevice.GetAsync<long>("LastScan")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            await _wifiDevice.RequestScanAsync(new Dictionary<string, object>())
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DBusException exception) when (IsScanAlreadyRunningOrUnavailable(exception))
        {
            // A NetworkManager background scan also wakes the radio; wait for it below.
            previousScan = await _wifiDevice.GetAsync<long>("LastScan")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DBusException exception)
        {
            Report($"Could not explicitly wake the physical Wi-Fi adapter: {exception.Message}");
            return;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var lastScan = await _wifiDevice.GetAsync<long>("LastScan")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (lastScan != previousScan)
            {
                Report("Physical Wi-Fi adapter is awake; starting Wi-Fi Direct discovery…");
                return;
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        // Some drivers do not update LastScan even though RequestScan brought the
        // interface up. P2P Find is still worth attempting in that state.
        Report("Physical Wi-Fi scan did not report completion; starting Wi-Fi Direct discovery anyway…");
    }

    private async Task StartDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (_p2pDevice is null || cancellationToken.IsCancellationRequested)
            return;
        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                    if (!_initialP2PResetCompleted)
                    {
                        await ResetStaleP2PStateAsync(p2pDevice).ConfigureAwait(false);
                        _initialP2PResetCompleted = true;
                    }
                    await p2pDevice.FindAsync(new Dictionary<string, object>
                    {
                        ["Timeout"] = 600,
                        ["DiscoveryType"] = "start_with_full",
                    }).WaitAsync(cancellationToken).ConfigureAwait(false);
                    // Find alternates Search and Listen states. Re-apply and verify the
                    // Sink identity used by Probe Responses during its Listen periods.
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
        finally
        {
            _discoveryGate.Release();
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
                    _supplicantWps = _bus.CreateProxy<IWpaWps>(SupplicantService, path);
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

        var wps = _supplicantWps
            ?? throw new InvalidOperationException(
                "wpa_supplicant did not expose a WPS interface for the Wi-Fi adapter.");
        if (!_wpsConfigured)
        {
            _previousWpsConfigMethods = await wps.GetAsync<string>("ConfigMethods")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        await wps.SetAsync("ConfigMethods", "push_button virtual_push_button")
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        _wpsConfigured = true;

        if (!_incomingRequestSubscriptionsConfigured)
        {
            _subscriptions.Add(await p2pDevice.WatchProvisionDiscoveryPBCRequestAsync(
                path => ReportPeerEvent(
                    path,
                    "requested WPS Push Button pairing; select this computer on the receiver to approve it"),
                exception => Report($"Could not monitor incoming WPS requests: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchProvisionDiscoveryRequestDisplayPinAsync(
                request => ReportUnsupportedPinRequest(request.peer, "display", request.pin),
                exception => Report($"Could not monitor incoming WPS display-PIN requests: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchProvisionDiscoveryRequestEnterPinAsync(
                path => ReportUnsupportedPinRequest(path, "keypad"),
                exception => Report($"Could not monitor incoming WPS keypad requests: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchProvisionDiscoveryPBCResponseAsync(
                path => ReportPeerEvent(path, "accepted the WPS Push Button request"),
                exception => Report($"Could not monitor WPS responses: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchProvisionDiscoveryResponseDisplayPinAsync(
                request => ReportPeerEvent(request.peer, $"returned a WPS display PIN ({request.pin})"),
                exception => Report($"Could not monitor WPS display-PIN responses: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchProvisionDiscoveryResponseEnterPinAsync(
                path => ReportPeerEvent(path, "accepted a WPS keypad request"),
                exception => Report($"Could not monitor WPS keypad responses: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchProvisionDiscoveryFailureAsync(
                failure => ReportPeerEvent(
                    failure.peer,
                    $"reported a WPS provisioning failure (status {failure.status})"),
                exception => Report($"Could not monitor WPS failures: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchGONegotiationRequestAsync(
                request => ReportPeerEvent(
                    request.path,
                    $"continued the incoming GO negotiation (password ID {request.devicePasswordId})"),
                exception => Report($"Could not monitor incoming GO negotiation: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchGONegotiationFailureAsync(
                properties => Report($"Wi-Fi Direct GO negotiation failed: {FormatProperties(properties)}"),
                exception => Report($"Could not monitor GO negotiation failures: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchGONegotiationSuccessAsync(
                properties => Report($"Wi-Fi Direct GO negotiation succeeded: {FormatProperties(properties)}"),
                exception => Report($"Could not monitor GO negotiation success: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchGroupFormationFailureAsync(
                reason => Report($"Wi-Fi Direct group formation failed: {reason}"),
                exception => Report($"Could not monitor group formation failures: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchGroupStartedAsync(
                OnGroupStarted,
                exception => Report($"Could not monitor P2P group creation: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchGroupFinishedAsync(
                OnGroupFinished,
                exception => Report($"Could not monitor P2P group shutdown: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchFindStoppedAsync(
                OnFindStopped,
                exception => Report($"Could not monitor P2P discovery state: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _incomingRequestSubscriptionsConfigured = true;
        }
    }

    private void OnFindStopped()
    {
        _finding = false;
        if (!_shouldFind || _discoveryGate.CurrentCount == 0)
            return;
        QueueDiscoveryRestart("wpa_supplicant stopped P2P discovery");
    }

    private void OnGroupStarted(IDictionary<string, object> properties)
    {
        _authorizationExpiresAt = DateTime.MaxValue;
        if (properties.TryGetValue("interface_object", out var interfaceValue)
            && interfaceValue is ObjectPath interfacePath)
        {
            _groupP2PDevice = _bus.CreateProxy<IWpaP2PDevice>(SupplicantService, interfacePath);
        }
        Report($"Wi-Fi Direct group started: {FormatProperties(properties)}");
    }

    private void OnGroupFinished(IDictionary<string, object> properties)
    {
        _groupP2PDevice = null;
        _authorizationExpiresAt = DateTime.MinValue;
        Report($"Wi-Fi Direct group finished: {FormatProperties(properties)}");
        QueueDiscoveryRestart("the previous Wi-Fi Direct group finished");
    }

    private async Task ReportConcurrentWifiConnectionsAsync(CancellationToken cancellationToken)
    {
        if (_networkManager is null)
            return;

        var devices = await _networkManager.GetDevicesAsync()
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var activeInterfaces = new List<string>();
        foreach (var path in devices)
        {
            var device = _bus.CreateProxy<INetworkManagerDevice>(Service, path);
            if (await device.GetAsync<uint>("DeviceType").WaitAsync(cancellationToken).ConfigureAwait(false) != 2
                || await device.GetAsync<uint>("State").WaitAsync(cancellationToken).ConfigureAwait(false)
                    != DeviceStateActivated)
            {
                continue;
            }

            activeInterfaces.Add(
                await device.GetAsync<string>("Interface").WaitAsync(cancellationToken).ConfigureAwait(false));
        }

        if (activeInterfaces.Count > 0)
        {
            Report(
                $"Warning: {string.Join(", ", activeInterfaces)} is connected to regular Wi-Fi. "
                + "If P2P times out, disconnect that Wi-Fi connection and retry over Ethernet.");
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

        var configMethods = await _supplicantWps!.GetAsync<string>("ConfigMethods")
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var wpsMethods = configMethods.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!wpsMethods.Contains("push_button", StringComparer.Ordinal)
            && !wpsMethods.Contains("virtual_push_button", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "wpa_supplicant did not retain the WPS Push Button pairing method.");
        }

        Report(
            $"Miracast receiver '{receiverName}' is searching and advertising in P2P mode "
            + "with WPS Push Button pairing. "
            + "Waiting for a Source to connect…");
    }

    private void ReportUnsupportedPinRequest(ObjectPath peerPath, string method, string? pin = null)
    {
        var pinText = string.IsNullOrEmpty(pin) ? string.Empty : $" (PIN {pin})";
        ReportPeerEvent(
            peerPath,
            $"requested unsupported WPS {method}{pinText}; receiver is advertising Push Button only");
    }

    private void ReportPeerEvent(ObjectPath peerPath, string description)
    {
        var cancellationToken = _lifetime?.Token ?? CancellationToken.None;
        _ = ReportPeerEventAsync(peerPath, description, cancellationToken);
    }

    private async Task ReportPeerEventAsync(
        ObjectPath peerPath,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            var peer = _bus.CreateProxy<IWpaPeer>(SupplicantService, peerPath);
            var address = Convert.ToHexString(
                await peer.GetAsync<byte[]>("DeviceAddress")
                    .WaitAsync(cancellationToken).ConfigureAwait(false));
            var name = _peersByAddress.TryGetValue(address, out var knownPeer)
                ? knownPeer.Name
                : address;
            Report($"Wi-Fi Direct peer {name} {description}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report($"Wi-Fi Direct peer {peerPath} {description}; could not read peer details: {exception.Message}");
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
            if (_wpsConfigured && _supplicantWps is not null && _previousWpsConfigMethods is not null)
            {
                await _supplicantWps.SetAsync("ConfigMethods", _previousWpsConfigMethods)
                    .ConfigureAwait(false);
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
            _wpsConfigured = false;
            _incomingRequestSubscriptionsConfigured = false;
            _supplicantP2PDevice = null;
            _supplicantWps = null;
            _previousP2PDeviceName = null;
            _previousPrimaryDeviceType = null;
            _previousGoIntent = null;
            _previousWpsConfigMethods = null;
        }
    }

    private static string FormatProperties(IDictionary<string, object> properties) =>
        properties.Count == 0
            ? "no details"
            : string.Join(", ", properties.Select(pair => $"{pair.Key}={pair.Value}"));

    private static string DescribeDeviceStateReason(uint reason) => reason switch
    {
        5 => "IPv4 configuration is unavailable (reason 5)",
        7 => "required WPS credentials were not supplied (reason 7)",
        8 => "wpa_supplicant disconnected (reason 8)",
        9 => "wpa_supplicant rejected the configuration (reason 9)",
        10 => "wpa_supplicant failed (reason 10)",
        11 => "wpa_supplicant timed out while forming the P2P group (reason 11)",
        _ => $"reason {reason}",
    };

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

    private static bool IsScanAlreadyRunningOrUnavailable(DBusException exception) =>
        exception.ErrorName is "org.freedesktop.NetworkManager.Device.Busy"
            or "org.freedesktop.NetworkManager.Device.NotAllowed"
            or DeviceNotActiveError;

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

    private void QueueDiscoveryRestart(string reason)
    {
        var lifetime = _lifetime;
        if (!_shouldFind || lifetime is null || lifetime.IsCancellationRequested || _groupP2PDevice is not null)
            return;
        if (_findRestart is { IsCompleted: false })
            return;
        _findRestart = RestartDiscoveryAsync(reason, lifetime.Token);
    }

    private async Task RestartDiscoveryAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            var authorizationDelay = _authorizationExpiresAt - DateTime.UtcNow;
            var delay = authorizationDelay > TimeSpan.Zero && authorizationDelay < TimeSpan.FromMinutes(2)
                ? authorizationDelay
                : TimeSpan.FromMilliseconds(750);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            if (!_shouldFind || _groupP2PDevice is not null)
                return;
            Report($"Restarting Wi-Fi Direct discovery because {reason}…");
            await WakeWifiAdapterAsync(cancellationToken).ConfigureAwait(false);
            await StartDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report($"Could not restart P2P discovery: {exception.Message}");
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
        if (_supplicantP2PDevice is not null)
        {
            try { await _supplicantP2PDevice.StopFindAsync().ConfigureAwait(false); }
            catch (DBusException) { }
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
        _authorizationGate.Dispose();
        _discoveryGate.Dispose();
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

[DBusInterface("org.freedesktop.NetworkManager.Device.Wireless")]
public interface IWifiDevice : IDBusObject
{
    Task RequestScanAsync(IDictionary<string, object> options);
    Task<T> GetAsync<T>(string property);
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
    Task FindAsync(IDictionary<string, object> options);
    Task ListenAsync(int timeout);
    Task StopFindAsync();
    Task CancelAsync();
    Task FlushAsync();
    Task DisconnectAsync();
    Task<string> ConnectAsync(IDictionary<string, object> options);
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

[DBusInterface("fi.w1.wpa_supplicant1.Peer")]
public interface IWpaPeer : IDBusObject
{
    Task<T> GetAsync<T>(string property);
}
