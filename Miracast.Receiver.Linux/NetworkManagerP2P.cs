using System.Net;
using Tmds.DBus;

namespace Miracast.Receiver.Linux;

internal sealed class NetworkManagerP2P : IAsyncDisposable
{
    private const string Service = "org.freedesktop.NetworkManager";
    private const uint WifiP2PDeviceType = 30;
    private const uint DeviceStateActivated = 100;
    private const uint DeviceStateFailed = 120;
    private static readonly byte[] SinkWfdInformationElements =
        [0x00, 0x00, 0x06, 0x00, 0x11, 0x1c, 0x44, 0x00, 0xc8];

    private readonly Connection _bus = new(Address.System);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly List<IDisposable> _subscriptions = [];
    private INetworkManager? _networkManager;
    private IWifiP2PDevice? _p2pDevice;
    private ObjectPath _devicePath;
    private ObjectPath? _activeConnection;
    private CancellationTokenSource? _lifetime;
    private Task? _findRenewal;
    private bool _finding;
    private bool _shouldFind;
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
            path => _ = InspectAndConnectAsync(path, _lifetime.Token),
            exception => Report($"Wi-Fi P2P discovery failed: {exception.Message}"))
            .WaitAsync(cancellationToken).ConfigureAwait(false));
        _subscriptions.Add(await _p2pDevice.WatchPeerRemovedAsync(
            path => Report($"Wi-Fi P2P peer disappeared: {path}"),
            exception => Report($"Wi-Fi P2P discovery failed: {exception.Message}"))
            .WaitAsync(cancellationToken).ConfigureAwait(false));

        await StartDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        _findRenewal = RenewDiscoveryAsync(_lifetime.Token);

        var peers = await _p2pDevice.GetAsync<ObjectPath[]>("Peers")
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var peer in peers)
            _ = InspectAndConnectAsync(peer, _lifetime.Token);
    }

    private async Task InspectAndConnectAsync(ObjectPath path, CancellationToken cancellationToken)
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

            Report($"Found {peer.Name} ({peer.HardwareAddress}), signal {peer.Strength}%.");
            await ConnectAsync(peer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report($"Could not inspect Wi-Fi P2P peer: {exception.Message}");
        }
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
        await _p2pDevice.StartFindAsync(new Dictionary<string, object>
        {
            ["timeout"] = 600,
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        _finding = true;
        Report("Searching for Miracast / Wi-Fi Display devices…");
    }

    private async Task RenewDiscoveryAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(9), cancellationToken).ConfigureAwait(false);
                if (_shouldFind && _p2pDevice is not null)
                {
                    await _p2pDevice.StartFindAsync(new Dictionary<string, object> { ["timeout"] = 600 })
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                    _finding = true;
                }
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
        if (!_finding || _p2pDevice is null)
            return;
        try { await _p2pDevice.StopFindAsync().ConfigureAwait(false); }
        catch (Exception exception) { Report($"Could not stop P2P discovery: {exception.Message}"); }
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
internal interface INetworkManager : IDBusObject
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
internal interface INetworkManagerDevice : IDBusObject
{
    Task<T> GetAsync<T>(string property);
    Task<IDisposable> WatchStateChangedAsync(Action<(uint newState, uint oldState, uint reason)> handler);
}

[DBusInterface("org.freedesktop.NetworkManager.Device.WifiP2P")]
internal interface IWifiP2PDevice : IDBusObject
{
    Task StartFindAsync(IDictionary<string, object> options);
    Task StopFindAsync();
    Task<T> GetAsync<T>(string property);
    Task<IDisposable> WatchPeerAddedAsync(Action<ObjectPath> handler, Action<Exception>? onError = null);
    Task<IDisposable> WatchPeerRemovedAsync(Action<ObjectPath> handler, Action<Exception>? onError = null);
}

[DBusInterface("org.freedesktop.NetworkManager.WifiP2PPeer")]
internal interface IWifiP2PPeer : IDBusObject
{
    Task<IDictionary<string, object>> GetAllAsync();
}

[DBusInterface("org.freedesktop.NetworkManager.IP4Config")]
internal interface INetworkManagerIP4Config : IDBusObject
{
    Task<IDictionary<string, object>> GetAllAsync();
}
