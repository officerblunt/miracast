using System.Collections.Concurrent;
using System.Net;
using Tmds.DBus;

namespace Miracast.Receiver.Linux;

internal sealed class NetworkManagerP2P : IAsyncDisposable
{
    private const string Service = "org.freedesktop.NetworkManager";
    private const string SupplicantService = "fi.w1.wpa_supplicant1";
    private const string SupplicantPath = "/fi/w1/wpa_supplicant1";
    private const uint WifiDeviceType = 2;
    private const uint WifiP2PDeviceType = 30;
    private const uint DeviceStateDisconnected = 30;
    private const uint DeviceStateActivated = 100;
    private const uint DeviceStateFailed = 120;
    private const int P2PExtendedListenPeriodMilliseconds = 500;
    private const int P2PExtendedListenIntervalMilliseconds = 2000;
    private static readonly TimeSpan ConnectionAttemptTimeout = TimeSpan.FromSeconds(30);
    private readonly Connection _bus = new(Address.System);
    private readonly PeerOperationCoalescer _authorizationOperations = new();
    private readonly PeerOperationCoalescer _disconnectionOperations = new();
    private readonly SemaphoreSlim _authorizationGate = new(1, 1);
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly P2PDiscoveryScheduler _discovery;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ConcurrentDictionary<string, WifiP2PPeer> _peersByAddress =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _peerAddressesByPath = new();
    private INetworkManager? _networkManager;
    private Connection? _activationBus;
    private IWpaSupplicant? _supplicant;
    private IWpaP2PDevice? _supplicantP2PDevice;
    private IWpaInterface? _supplicantInterface;
    private IWpaP2PDevice? _groupP2PDevice;
    private IWpaWps? _supplicantWps;
    private IWifiP2PDevice? _p2pDevice;
    private IDisposable? _activeStateSubscription;
    private ObjectPath _devicePath;
    private string? _p2pInterfaceName;
    private string? _parentWifiInterfaceName;
    private ObjectPath? _activeConnection;
    private CancellationTokenSource? _lifetime;
    private Task? _addressConfiguration;
    private CancellationTokenSource? _addressConfigurationLifetime;
    private TaskCompletionSource? _pendingActivation;
    private CancellationTokenSource? _connectionAttemptLifetime;
    private long _attemptSequence;
    private long _currentAttemptId;
    private string? _currentGroupObjectPath;
    private bool _wfdAdvertisementConfigured;
    private byte[]? _previousWfdInformationElements;
    private string? _previousP2PDeviceName;
    private byte[]? _previousPrimaryDeviceType;
    private uint? _previousGoIntent;
    private uint? _previousOperRegClass;
    private uint? _previousOperChannel;
    private bool? _previousNoGroupIface;
    private string? _previousWpsConfigMethods;
    private bool _p2pDeviceConfigured;
    private bool _operatingChannelConfigured;
    private bool _wpsConfigured;
    private bool _incomingRequestSubscriptionsConfigured;
    private bool _connecting;
    private bool _initialP2PResetCompleted;
    private bool _networkManagerActivationPending;
    private DateTime _authorizationExpiresAt;
    private string? _authorizedPeerAddress;
    private WifiP2PPeer? _authorizedPeer;
    private IPAddress? _negotiatedSourceAddress;
    private bool _disposed;

    public event EventHandler<P2PConnectionContext>? PeerConnected;
    public event EventHandler? PeerDisconnected;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<WifiP2PPeer>? PeerAvailable;
    public event EventHandler<WifiP2PPeer>? PeerUnavailable;

    public NetworkManagerP2P()
    {
        _discovery = new P2PDiscoveryScheduler(
            () => _p2pDevice is not null && _groupP2PDevice is null,
            () => _authorizationExpiresAt,
            StartDiscoveryAsync,
            Report);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await _bus.ConnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        _networkManager = _bus.CreateProxy<INetworkManager>(Service, "/org/freedesktop/NetworkManager");
        _supplicant = _bus.CreateProxy<IWpaSupplicant>(SupplicantService, SupplicantPath);

        var devices = await _networkManager.GetDevicesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var wifiStates = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var path in devices)
        {
            var device = _bus.CreateProxy<INetworkManagerDevice>(Service, path);
            var deviceType = await device.GetAsync<uint>("DeviceType")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (deviceType != WifiDeviceType)
                continue;

            var interfaceName = await device.GetAsync<string>("Interface")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            var state = await device.GetAsync<uint>("State")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            wifiStates[interfaceName] = state;
        }

        var candidates = new List<P2PDeviceCandidate>();
        foreach (var path in devices)
        {
            var device = _bus.CreateProxy<INetworkManagerDevice>(Service, path);
            var deviceType = await device.GetAsync<uint>("DeviceType")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (deviceType != WifiP2PDeviceType)
                continue;

            var interfaceName = await device.GetAsync<string>("Interface")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            var deviceState = await device.GetAsync<uint>("State")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            var parentInterface = P2PDeviceSelector.GetParentWifiInterfaceName(interfaceName);
            uint? parentState = parentInterface is not null
                && wifiStates.TryGetValue(parentInterface, out var knownParentState)
                    ? knownParentState
                    : null;
            candidates.Add(new P2PDeviceCandidate(
                path,
                interfaceName,
                parentInterface,
                deviceState,
                parentState));
        }

        var selected = P2PDeviceSelector.Select(candidates);
        if (selected is not null)
        {
            _devicePath = selected.Path;
            _p2pInterfaceName = selected.InterfaceName;
            _parentWifiInterfaceName = selected.ParentInterfaceName;
            _p2pDevice = _bus.CreateProxy<IWifiP2PDevice>(Service, selected.Path);

            if (selected.ParentState == DeviceStateDisconnected
                && wifiStates.Values.Any(static state => state == DeviceStateActivated))
            {
                Report(
                    $"Using idle Wi-Fi adapter {selected.ParentInterfaceName} for Wi-Fi Direct "
                    + "so the active regular Wi-Fi adapter remains untouched.");
            }
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

        // A NetworkManager-owned physical scan cannot be cancelled safely and
        // may keep the radio busy for an arbitrary amount of time. Receiver
        // startup must not wait for it: discovery will begin as soon as the
        // adapter becomes available.
        Report("Miracast receiver initialized. Starting Wi-Fi Direct discovery in the background…");
        _discovery.Start(_lifetime.Token, "the receiver started");

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
                P2PDiagnostics.GetProperty(properties, "Name", "Unknown device"),
                P2PDiagnostics.GetProperty(properties, "HwAddress", string.Empty),
                properties.TryGetValue("Strength", out var strength) && strength is byte value ? value : (byte)0,
                wfdIEs);
            if (string.IsNullOrWhiteSpace(peer.HardwareAddress))
                return;

            var address = P2PDiagnostics.NormalizeHardwareAddress(peer.HardwareAddress);
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
        var address = P2PDiagnostics.NormalizeHardwareAddress(sourceId);
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
        var peerAddress = P2PDiagnostics.NormalizeHardwareAddress(peer.HardwareAddress);
        await _authorizationOperations.RunAsync(
            peerAddress,
            () => RunAuthorizationAttemptAsync(peer, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAuthorizationAttemptAsync(
        WifiP2PPeer peer,
        CancellationToken cancellationToken)
    {
        if (!await _authorizationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Another Wi-Fi Direct authorization is already running.");

        if (_activeConnection is not null || _networkManagerActivationPending)
        {
            _authorizationGate.Release();
            return;
        }
        var normalizedAddress = P2PDiagnostics.NormalizeHardwareAddress(peer.HardwareAddress);
        if (_authorizedPeerAddress == normalizedAddress && _authorizationExpiresAt > DateTime.UtcNow)
        {
            _authorizationGate.Release();
            return;
        }

        _connecting = true;
        using var attemptLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptLifetime.CancelAfter(ConnectionAttemptTimeout);
        _connectionAttemptLifetime = attemptLifetime;
        var attemptToken = attemptLifetime.Token;
        var attemptId = Interlocked.Increment(ref _attemptSequence);
        Volatile.Write(ref _currentAttemptId, attemptId);
        try
        {
            _ = _networkManager
                ?? throw new InvalidOperationException("NetworkManager is unavailable.");

            _networkManagerActivationPending = true;
            _authorizedPeerAddress = normalizedAddress;
            _authorizedPeer = peer;
            _authorizationExpiresAt = DateTime.MaxValue;
            // AddAndActivateConnection2/P2P_CONNECT stops an active Find, but it
            // does not disable Extended Listen. Leaving Extended Listen enabled
            // lets a single radio return to its listen channel while the group
            // interface is authenticating on the negotiated operating channel.
            _discovery.Pause();
            await StopListeningAsync(attemptToken).ConfigureAwait(false);
            await WaitForPendingScanAsync(attemptToken).ConfigureAwait(false);
            await ConfigureConcurrentWifiChannelAsync(attemptToken).ConfigureAwait(false);
            Report(
                $"Connection attempt #{attemptId} from {peer.Name} started with a "
                + $"{ConnectionAttemptTimeout.TotalSeconds:0}-second deadline. "
                + "Handing WPS and P2P group creation to NetworkManager…");

            var device = _bus.CreateProxy<INetworkManagerDevice>(Service, _devicePath);
            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingActivation = activated;
            _activeStateSubscription?.Dispose();
            _activeStateSubscription = await device.WatchStateChangedAsync(change =>
            {
                if (Volatile.Read(ref _currentAttemptId) != attemptId)
                    return;
                if (change.newState == DeviceStateActivated)
                    activated.TrySetResult();
                else if (change.newState == DeviceStateFailed)
                    activated.TrySetException(new InvalidOperationException(
                        $"NetworkManager activation failed: {P2PDiagnostics.DescribeDeviceStateReason(change.reason)}."));
                else if (change.newState == 60)
                    Report("NetworkManager is preparing the Wi-Fi Direct device…");
                else if (change.newState == 70)
                    Report("NetworkManager started WPS pairing…");
                else if (_activeConnection is not null && change.newState <= 30)
                    _ = HandleDisconnectedAsync(attemptId, _lifetime?.Token ?? CancellationToken.None);
            }).WaitAsync(attemptToken).ConfigureAwait(false);

            var connection = P2PNetworkConfiguration.CreateConnectionSettings(
                peer.Name,
                peer.HardwareAddress);
            var options = new Dictionary<string, object>
            {
                ["persist"] = "volatile",
                ["bind-activation"] = "dbus-client",
            };

            _activationBus = new Connection(Address.System);
            await _activationBus.ConnectAsync().WaitAsync(attemptToken).ConfigureAwait(false);
            var activationNetworkManager =
                _activationBus.CreateProxy<INetworkManager>(Service, "/org/freedesktop/NetworkManager");
            var activationRequest = activationNetworkManager.AddAndActivateConnection2Async(
                connection,
                _devicePath,
                peer.Path,
                options);
            _ = ObserveAbandonedActivationAsync(activationRequest);
            var result = await WaitForActivationRequestAsync(
                activationRequest,
                activated.Task,
                attemptToken).ConfigureAwait(false);

            _activeConnection = result.activeConnection;
            Report(
                "NetworkManager accepted the route-isolated P2P activation. "
                + "Completing WPS pairing…");
            var state = await device.GetAsync<uint>("State").WaitAsync(attemptToken).ConfigureAwait(false);
            if (state == DeviceStateActivated)
                activated.TrySetResult();

            await activated.Task.WaitAsync(attemptToken).ConfigureAwait(false);
            var context = await CreateConnectionContextAsync(device, peer, attemptToken).ConfigureAwait(false);
            PeerConnected?.Invoke(this, context);
        }
        catch (Exception exception)
        {
            var timedOut = exception is OperationCanceledException
                && attemptLifetime.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested;
            var receiverToken = _lifetime?.Token ?? cancellationToken;
            using var recoveryLifetime = CancellationTokenSource.CreateLinkedTokenSource(receiverToken);
            recoveryLifetime.CancelAfter(TimeSpan.FromSeconds(2));
            var recoveryToken = recoveryLifetime.Token;
            CancelAddressConfiguration();
            if (_activeConnection is { } active && _networkManager is not null)
            {
                try
                {
                    await _networkManager.DeactivateConnectionAsync(active)
                        .WaitAsync(recoveryToken).ConfigureAwait(false);
                }
                catch { }
                _activeConnection = null;
            }
            DisposeActivationBus();
            Volatile.Write(ref _currentAttemptId, 0);
            _activeStateSubscription?.Dispose();
            _activeStateSubscription = null;
            _currentGroupObjectPath = null;
            _groupP2PDevice = null;
            // Defer supplicant cleanup until discovery has confirmed that the
            // physical scan is over. Otherwise these calls accumulate in the
            // driver and all execute much later in one burst.
            _initialP2PResetCompleted = false;
            ResetPendingAuthorization();
            if (!receiverToken.IsCancellationRequested)
            {
                _discovery.Resume("the connection attempt failed");
            }
            if (timedOut)
            {
                throw new TimeoutException(
                    $"The Wi-Fi Direct connection attempt did not finish within "
                    + $"{ConnectionAttemptTimeout.TotalSeconds:0} seconds and was cancelled completely.",
                    exception);
            }
            throw;
        }
        finally
        {
            if (ReferenceEquals(_connectionAttemptLifetime, attemptLifetime))
                _connectionAttemptLifetime = null;
            _pendingActivation = null;
            _networkManagerActivationPending = false;
            _connecting = false;
            _authorizationGate.Release();
        }
    }

    private static async Task ObserveAbandonedActivationAsync(
        Task<(ObjectPath connection, ObjectPath activeConnection, IDictionary<string, object> result)> activation)
    {
        try { await activation.ConfigureAwait(false); }
        catch { }
    }

    internal static async Task<T> WaitForActivationRequestAsync<T>(
        Task<T> activationRequest,
        Task activationState,
        CancellationToken cancellationToken)
    {
        var first = await Task.WhenAny(activationRequest, activationState)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (ReferenceEquals(first, activationState))
        {
            // A failed GO negotiation or group formation is authoritative and
            // must interrupt the still-pending NetworkManager D-Bus request.
            await activationState.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return await activationRequest.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void DisposeActivationBus()
    {
        var activationBus = Interlocked.Exchange(ref _activationBus, null);
        activationBus?.Dispose();
    }

    public async Task StopAsync()
    {
        _discovery.Pause();
        _lifetime?.Cancel();
        _connectionAttemptLifetime?.Cancel();
        DisposeActivationBus();
        using var cleanupLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var cleanupToken = cleanupLifetime.Token;
        var authorization = _authorizationOperations.Current;
        if (authorization is not null)
        {
            try { await authorization.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { }
        }
        var disconnection = _disconnectionOperations.Current;
        if (disconnection is not null)
        {
            try { await disconnection.WaitAsync(cleanupToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cleanupToken.IsCancellationRequested) { }
            catch { }
        }
        CancelAddressConfiguration();
        var addressConfiguration = _addressConfiguration;
        if (addressConfiguration is not null)
        {
            try { await addressConfiguration.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _addressConfiguration = null;
        }
        if (_activeConnection is { } active && _networkManager is not null)
        {
            try
            {
                await _networkManager.DeactivateConnectionAsync(active)
                    .WaitAsync(cleanupToken).ConfigureAwait(false);
            }
            catch (Exception exception) { Report($"Could not deactivate the P2P connection: {exception.Message}"); }
            _activeConnection = null;
        }
        DisposeActivationBus();
        _activeStateSubscription?.Dispose();
        _activeStateSubscription = null;
        _networkManagerActivationPending = false;
        Volatile.Write(ref _currentAttemptId, 0);
        _currentGroupObjectPath = null;
        _groupP2PDevice = null;
        await ReleaseSupplicantP2PStateAsync(cleanupToken).ConfigureAwait(false);

        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        await _discovery.StopAsync().ConfigureAwait(false);
        _lifetime?.Dispose();
        _lifetime = null;
        _peersByAddress.Clear();
        _peerAddressesByPath.Clear();
        await RestoreWfdAdvertisementAsync(cleanupToken).ConfigureAwait(false);
        _initialP2PResetCompleted = false;
        _authorizationExpiresAt = DateTime.MinValue;
        _authorizedPeerAddress = null;
        _authorizedPeer = null;
        _p2pDevice = null;
        _p2pInterfaceName = null;
        _parentWifiInterfaceName = null;
    }

    private async Task ResetStaleP2PStateAsync(
        IWpaP2PDevice p2pDevice,
        CancellationToken cancellationToken = default)
    {
        if (!await TryP2POperationAsync(p2pDevice.CancelAsync, cancellationToken).ConfigureAwait(false))
            return;
        if (await HasP2PGroupAsync(p2pDevice, cancellationToken).ConfigureAwait(false))
        {
            if (!await TryP2POperationAsync(p2pDevice.DisconnectAsync, cancellationToken).ConfigureAwait(false))
                return;
        }
        if (!await TryP2POperationAsync(p2pDevice.StopFindAsync, cancellationToken).ConfigureAwait(false))
            return;
        if (!await TryP2POperationAsync(p2pDevice.FlushAsync, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            await WaitForP2PGroupInterfacesToDisappearAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<bool> HasP2PGroupAsync(
        IWpaP2PDevice p2pDevice,
        CancellationToken cancellationToken)
    {
        try
        {
            var group = await p2pDevice.GetAsync<ObjectPath>("Group")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return P2PAddressing.IsConcreteGroupObjectPath(group);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
        }

        // A p2p-* interface elsewhere in the system does not prove that this
        // P2P management device owns it. Calling Disconnect on that assumption
        // can disconnect the selected adapter's regular STA connection.
        return false;
    }

    private static async Task<bool> TryP2POperationAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;
        try
        {
            await operation().WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private async Task ReleaseSupplicantP2PStateAsync(CancellationToken cancellationToken)
    {
        if (_groupP2PDevice is not null)
        {
            await TryP2POperationAsync(_groupP2PDevice.DisconnectAsync, cancellationToken)
                .ConfigureAwait(false);
            _groupP2PDevice = null;
        }

        var p2pDevice = _supplicantP2PDevice;
        if (p2pDevice is null)
            return;
        await StopListeningAsync(cancellationToken).ConfigureAwait(false);
        await ResetStaleP2PStateAsync(p2pDevice, cancellationToken).ConfigureAwait(false);
    }

    public Task DisconnectCurrentAsync()
    {
        _connectionAttemptLifetime?.Cancel();
        return _disconnectionOperations.RunAsync(
            "current-p2p-session",
            () => DisconnectCurrentCoreAsync(expectedAttemptId: null),
            CancellationToken.None);
    }

    private async Task DisconnectCurrentCoreAsync(long? expectedAttemptId)
    {
        if (expectedAttemptId is not null
            && Volatile.Read(ref _currentAttemptId) != expectedAttemptId.Value)
        {
            return;
        }

        var activeConnection = _activeConnection;
        var groupDevice = _groupP2PDevice;
        if (activeConnection is null
            && groupDevice is null
            && _currentGroupObjectPath is null
            && Volatile.Read(ref _currentAttemptId) == 0)
        {
            return;
        }

        CancelAddressConfiguration();
        _activeStateSubscription?.Dispose();
        _activeStateSubscription = null;
        _activeConnection = null;
        _groupP2PDevice = null;
        DisposeActivationBus();
        Volatile.Write(ref _currentAttemptId, 0);
        _currentGroupObjectPath = null;
        ResetPendingAuthorization();
        PeerDisconnected?.Invoke(this, EventArgs.Empty);

        using var deactivationLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        if (activeConnection is not null && _networkManager is not null)
        {
            try
            {
                await _networkManager.DeactivateConnectionAsync(activeConnection.Value)
                    .WaitAsync(deactivationLifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Report($"Could not deactivate the P2P connection: {exception.Message}");
            }
        }
        using var p2pCleanupLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var p2pCleanupToken = p2pCleanupLifetime.Token;
        if (activeConnection is null && groupDevice is not null)
        {
            await TryP2POperationAsync(groupDevice.DisconnectAsync, p2pCleanupToken).ConfigureAwait(false);
        }

        if (_supplicantP2PDevice is { } supplicantP2PDevice)
        {
            await ResetStaleP2PStateAsync(supplicantP2PDevice, p2pCleanupToken).ConfigureAwait(false);
        }

        var lifetime = _lifetime;
        if (lifetime is not null && !lifetime.IsCancellationRequested)
        {
            // Restart outside this cleanup operation. A pending driver scan must
            // not keep a duplicate disconnect or window shutdown waiting.
            _discovery.Resume("the Miracast session ended");
        }
    }

    private async Task WaitForP2PGroupInterfacesToDisappearAsync(CancellationToken cancellationToken)
    {
        var staleInterfaces = P2PAddressing.GetGroupInterfaceNames();
        if (staleInterfaces.Length == 0)
            return;

        Report($"Waiting for stale Wi-Fi Direct group interface(s) to disappear: {string.Join(", ", staleInterfaces)}…");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            staleInterfaces = P2PAddressing.GetGroupInterfaceNames();
            if (staleInterfaces.Length == 0)
                return;
        }

        Report(
            $"The Wi-Fi driver retained stale P2P group interface(s): {string.Join(", ", staleInterfaces)}. "
            + "A new group may fail until the driver releases them.");
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
        var localAddress = P2PAddressing.GetLocalAddress(properties)
            ?? throw new InvalidOperationException("NetworkManager did not provide the receiver IPv4 address.");
        var sourceAddress = P2PAddressing.GetIpAddress(properties, "Gateway") ?? _negotiatedSourceAddress
            ?? throw new InvalidOperationException("NetworkManager did not provide the Miracast Source IPv4 address.");
        var controlPort = P2PAddressing.GetWfdControlPort(peer.WfdIEs);

        Report($"P2P ready on {interfaceName}: {localAddress} → {sourceAddress}:{controlPort}.");
        return new P2PConnectionContext(peer, interfaceName, localAddress, sourceAddress, controlPort);
    }

    private async Task<bool> WaitForPendingScanAsync(CancellationToken cancellationToken)
    {
        var supplicantInterface = _supplicantInterface
            ?? throw new InvalidOperationException("The wpa_supplicant Wi-Fi interface proxy is unavailable.");
        var reported = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline
               && await supplicantInterface.GetAsync<bool>("Scanning")
                   .WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reported)
            {
                Report(
                    "The physical Wi-Fi adapter is running a NetworkManager scan. "
                    + "The receiver remains started and will enable Wi-Fi Direct discovery "
                    + "automatically when the scan finishes…");
                reported = true;
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        var scanning = await supplicantInterface.GetAsync<bool>("Scanning")
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!scanning && reported)
            Report("The NetworkManager scan finished. Starting Wi-Fi Direct discovery now…");
        else if (scanning)
        {
            Report(
                "The NetworkManager scan state did not clear within 10 seconds. "
                + "Trying the standard P2P Listen operation to recover discoverability "
                + "without restarting NetworkManager…");
        }
        return !scanning;
    }

    private async Task StartDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (_p2pDevice is null || cancellationToken.IsCancellationRequested)
            return;
        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var waitingReported = false;
            var busyReported = false;
            while (_discovery.Enabled && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ConfigureWfdAdvertisementAsync(cancellationToken).ConfigureAwait(false);
                    var p2pDevice = _supplicantP2PDevice
                        ?? throw new InvalidOperationException("The wpa_supplicant P2PDevice proxy is not available.");
                    // Never enqueue cleanup operations behind a physical scan.
                    // This wait is cancellable and the discovery retry remains
                    // outside the connection-attempt state machine.
                    var scanFinished = await WaitForPendingScanAsync(cancellationToken).ConfigureAwait(false);
                    if (_discovery.IsListening)
                        await StopListeningAsync(cancellationToken).ConfigureAwait(false);
                    if (!_initialP2PResetCompleted && scanFinished)
                    {
                        await ResetStaleP2PStateAsync(p2pDevice, cancellationToken).ConfigureAwait(false);
                        _initialP2PResetCompleted = true;
                    }
                    await StartSupplicantListenAsync(p2pDevice, cancellationToken).ConfigureAwait(false);
                    _initialP2PResetCompleted = true;
                    // Bounded Listen mode keeps the Sink discoverable without
                    // active Search scans or starving the connected STA link.
                    await ConfigureWfdAdvertisementAsync(cancellationToken).ConfigureAwait(false);
                    await VerifyWfdAdvertisementAsync(cancellationToken).ConfigureAwait(false);
                    _discovery.MarkListening();
                    return;
                }
                catch (DBusException exception) when (P2PDiagnostics.IsSupplicantTemporarilyUnavailable(exception))
                {
                    _discovery.MarkStopped();
                    if (!waitingReported)
                    {
                        Report(
                            "Wi-Fi Direct is waiting for NetworkManager's wpa_supplicant interface. "
                            + "Keep NetworkManager and wpa_supplicant running and make sure Wi-Fi is enabled.");
                        waitingReported = true;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                catch (DBusException exception) when (P2PDiagnostics.IsP2POperationBusy(exception))
                {
                    _discovery.MarkStopped();
                    if (!busyReported)
                    {
                        Report(
                            "The Wi-Fi driver is still busy. "
                            + "Retrying P2P Listen without restarting NetworkManager…");
                        busyReported = true;
                    }
                    // Do not enqueue Cancel/Disconnect/StopFind/Flush while the
                    // physical adapter is scanning. Some drivers serialize those
                    // calls and execute the accumulated queue after scan completion.
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private async Task StartSupplicantListenAsync(
        IWpaP2PDevice p2pDevice,
        CancellationToken cancellationToken)
    {
        // Extended Listen is scheduled by wpa_supplicant itself. The radio is
        // discoverable for a bounded period and returns to the connected STA
        // between periods, without relying on a one-shot Listen completion
        // signal that some drivers never emit over D-Bus.
        await p2pDevice.ExtendedListenAsync(new Dictionary<string, object>
            {
                ["period"] = P2PExtendedListenPeriodMilliseconds,
                ["interval"] = P2PExtendedListenIntervalMilliseconds,
            })
            .WaitAsync(cancellationToken).ConfigureAwait(false);
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

            await supplicant.SetAsync(
                    "WFDIEs",
                    P2PNetworkConfiguration.SinkWfdInformationElements)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            _wfdAdvertisementConfigured = true;
            await ConfigureP2PDeviceAsync(supplicant, cancellationToken).ConfigureAwait(false);
        }
        catch (DBusException exception) when (P2PDiagnostics.IsAccessDenied(exception))
        {
            throw new InvalidOperationException(
                "The system D-Bus policy denied access to wpa_supplicant.WFDIEs. "
                + "The receiver needs permission to publish its Miracast Sink capabilities.",
                exception);
        }
        catch (DBusException exception) when (P2PDiagnostics.IsUnsupportedProperty(exception))
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
                var interfaceProxy = _bus.CreateProxy<IWpaInterface>(SupplicantService, path);
                try
                {
                    var interfaceName = await interfaceProxy.GetAsync<string>("Ifname")
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (!P2PDeviceSelector.IsSupplicantInterfaceForP2PDevice(
                            interfaceName,
                            _p2pInterfaceName))
                        continue;

                    var candidate = _bus.CreateProxy<IWpaP2PDevice>(SupplicantService, path);
                    var existing = await candidate.GetAsync<IDictionary<string, object>>("P2PDeviceConfig")
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                    _supplicantP2PDevice = candidate;
                    _supplicantInterface = interfaceProxy;
                    _supplicantWps = _bus.CreateProxy<IWpaWps>(SupplicantService, path);
                    if (existing.TryGetValue("DeviceName", out var name) && name is string deviceName)
                        _previousP2PDeviceName = deviceName;
                    if (existing.TryGetValue("PrimaryDeviceType", out var type) && type is byte[] primaryType)
                        _previousPrimaryDeviceType = primaryType;
                    if (existing.TryGetValue("GOIntent", out var intent) && intent is uint goIntent)
                        _previousGoIntent = goIntent;
                    if (existing.TryGetValue("OperRegClass", out var regClass)
                        && regClass is uint operatingRegClass)
                    {
                        _previousOperRegClass = operatingRegClass;
                    }
                    if (existing.TryGetValue("OperChannel", out var channel)
                        && channel is uint operatingChannel)
                    {
                        _previousOperChannel = operatingChannel;
                    }
                    if (existing.TryGetValue("NoGroupIface", out var noGroupInterface)
                        && noGroupInterface is bool noGroupIface)
                    {
                        _previousNoGroupIface = noGroupIface;
                    }
                    break;
                }
                catch (DBusException exception) when (P2PDiagnostics.IsMissingP2PInterface(exception))
                {
                }
            }
        }

        var p2pDevice = _supplicantP2PDevice
            ?? throw new InvalidOperationException(
                $"wpa_supplicant did not expose a P2PDevice interface for Wi-Fi adapter "
                + $"'{_parentWifiInterfaceName ?? _p2pInterfaceName ?? "unknown"}'.");
        var receiverName = $"Miracast Receiver ({Environment.MachineName})";
        if (receiverName.Length > 32)
            receiverName = receiverName[..32];
        await p2pDevice.SetAsync(
                "P2PDeviceConfig",
                P2PNetworkConfiguration.CreateDeviceConfiguration(receiverName))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
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
                path => QueueIncomingConnection(path, "WPS Push Button request"),
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
                request => QueueIncomingConnection(
                    request.path,
                    $"GO negotiation request, password ID {request.devicePasswordId}"),
                exception => Report($"Could not monitor incoming GO negotiation: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchGONegotiationFailureAsync(
                OnGONegotiationFailure,
                exception => Report($"Could not monitor GO negotiation failures: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchGONegotiationSuccessAsync(
                OnGONegotiationSuccess,
                exception => Report($"Could not monitor GO negotiation success: {exception.Message}"))
                .WaitAsync(cancellationToken).ConfigureAwait(false));
            _subscriptions.Add(await p2pDevice.WatchGroupFormationFailureAsync(
                OnGroupFormationFailure,
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
            _incomingRequestSubscriptionsConfigured = true;
        }
    }

    private async Task ConfigureConcurrentWifiChannelAsync(CancellationToken cancellationToken)
    {
        var frequency = await GetConcurrentWifiFrequencyAsync(cancellationToken).ConfigureAwait(false);
        if (frequency is null)
            return;
        if (!P2PNetworkConfiguration.TryGetOperatingChannel(
                frequency.Value,
                out var regClass,
                out var channel))
        {
            Report(
                $"Regular Wi-Fi is using unsupported P2P frequency {frequency.Value} MHz; "
                + "P2P channel selection will remain automatic.");
            return;
        }

        var p2pDevice = _supplicantP2PDevice
            ?? throw new InvalidOperationException("The wpa_supplicant P2P interface is unavailable.");
        await p2pDevice.SetAsync("P2PDeviceConfig", new Dictionary<string, object>
        {
            ["OperRegClass"] = regClass,
            ["OperChannel"] = channel,
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        _operatingChannelConfigured = true;
        Report(
            $"Pinned Wi-Fi Direct to the active Wi-Fi channel {channel} "
            + $"({frequency.Value} MHz, operating class {regClass}) to preserve connectivity.");
    }

    private async Task<int?> GetConcurrentWifiFrequencyAsync(CancellationToken cancellationToken)
    {
        if (_networkManager is null)
            return null;

        try
        {
            var parentInterface = _parentWifiInterfaceName;
            if (parentInterface is null)
            {
                Report(
                    $"Could not identify the physical Wi-Fi interface behind "
                    + $"'{_p2pInterfaceName ?? "the selected P2P device"}'. "
                    + "P2P channel selection will remain automatic.");
                return null;
            }

            var devices = await _networkManager.GetDevicesAsync()
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            var activeRadios = new List<(string Interface, int? Frequency)>();
            foreach (var path in devices)
            {
                var device = _bus.CreateProxy<INetworkManagerDevice>(Service, path);
                if (await device.GetAsync<uint>("DeviceType").WaitAsync(cancellationToken)
                        .ConfigureAwait(false) != WifiDeviceType
                    || await device.GetAsync<uint>("State").WaitAsync(cancellationToken)
                        .ConfigureAwait(false) != DeviceStateActivated)
                {
                    continue;
                }

                var interfaceName = await device.GetAsync<string>("Interface")
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                int? frequency = null;
                var wirelessDevice = _bus.CreateProxy<INetworkManagerWirelessDevice>(Service, path);
                var accessPointPath = await wirelessDevice.GetAsync<ObjectPath>("ActiveAccessPoint")
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                if (accessPointPath.ToString() != "/")
                {
                    var accessPoint = _bus.CreateProxy<INetworkManagerAccessPoint>(Service, accessPointPath);
                    var value = await accessPoint.GetAsync<uint>("Frequency")
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (value is > 0 and <= int.MaxValue)
                        frequency = (int)value;
                }
                activeRadios.Add((interfaceName, frequency));
            }

            if (activeRadios.Count == 0)
                return null;

            var selected = activeRadios.FirstOrDefault(item =>
                item.Interface.Equals(parentInterface, StringComparison.Ordinal));

            var descriptions = string.Join(", ", activeRadios.Select(item =>
                item.Frequency is null
                    ? item.Interface
                    : $"{item.Interface} at {item.Frequency} MHz"));
            if (selected.Frequency is not null)
            {
                Report(
                    $"{descriptions} is connected to regular Wi-Fi. Keeping it managed and connected; "
                    + $"requesting same-channel P2P at {selected.Frequency} MHz.");
                return selected.Frequency;
            }

            if (selected == default)
            {
                Report(
                    $"Regular Wi-Fi is active on {descriptions}, while Wi-Fi Direct uses the separate "
                    + $"adapter {parentInterface}. Leaving the P2P channel automatic.");
                return null;
            }

            Report(
                $"Warning: {parentInterface} is connected to regular Wi-Fi, but its channel could not be read. "
                + "P2P will use automatic channel selection.");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Report(
                $"Could not read the regular Wi-Fi channel from NetworkManager: {exception.Message}. "
                + "P2P will use automatic channel selection.");
            return null;
        }
    }

    private void OnGroupStarted(IDictionary<string, object> properties)
    {
        var groupDevice = default(IWpaP2PDevice);
        if (properties.TryGetValue("interface_object", out var interfaceValue)
            && interfaceValue is ObjectPath interfacePath)
        {
            groupDevice = _bus.CreateProxy<IWpaP2PDevice>(SupplicantService, interfacePath);
        }
        Report($"Wi-Fi Direct group started: {P2PDiagnostics.FormatProperties(properties)}");

        if (!_networkManagerActivationPending && _activeConnection is null)
        {
            Report("Rejecting a stale Wi-Fi Direct group that does not belong to the current connection attempt.");
            if (groupDevice is not null)
                _ = DisconnectStaleGroupAsync(groupDevice);
            return;
        }

        _authorizationExpiresAt = DateTime.MaxValue;
        _groupP2PDevice = groupDevice;
        _currentGroupObjectPath = P2PAddressing.GetObjectPath(properties, "group_object");

        // NetworkManager owns every accepted group and applies its IP
        // configuration before the device reaches Activated.
        var activationLifetime = _lifetime;
        if (activationLifetime is not null
            && !activationLifetime.IsCancellationRequested
            && _addressConfiguration is not { IsCompleted: false })
        {
            _addressConfigurationLifetime?.Dispose();
            _addressConfigurationLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                activationLifetime.Token);
            _addressConfiguration = ApplyNegotiatedGroupAddressAsync(
                properties,
                _addressConfigurationLifetime.Token);
        }
    }

    private void OnGroupFinished(IDictionary<string, object> properties)
    {
        var finishedGroupObjectPath = P2PAddressing.GetObjectPath(properties, "group_object");
        if (_currentGroupObjectPath is not null
            && finishedGroupObjectPath is not null
            && !string.Equals(
                _currentGroupObjectPath,
                finishedGroupObjectPath,
                StringComparison.Ordinal))
        {
            Report($"Ignoring completion of an older Wi-Fi Direct group: {P2PDiagnostics.FormatProperties(properties)}");
            return;
        }
        if (_networkManagerActivationPending && _currentGroupObjectPath is null)
        {
            Report($"Ignoring a late Wi-Fi Direct group completion during a newer attempt: {P2PDiagnostics.FormatProperties(properties)}");
            return;
        }

        CancelAddressConfiguration();
        _groupP2PDevice = null;
        _currentGroupObjectPath = null;
        Report($"Wi-Fi Direct group finished: {P2PDiagnostics.FormatProperties(properties)}");

        if (_networkManagerActivationPending)
        {
            _pendingActivation?.TrySetException(new InvalidOperationException(
                "The Wi-Fi Direct group ended before NetworkManager completed activation."));
            return;
        }
        if (_activeConnection is not null)
            return;

        ResetPendingAuthorization();
        _discovery.QueueRestart("the previous Wi-Fi Direct group finished");
    }

    private void OnGONegotiationSuccess(IDictionary<string, object> properties)
    {
        Report($"Wi-Fi Direct GO negotiation succeeded: {P2PDiagnostics.FormatProperties(properties)}");
        // NetworkManager owns group formation and the attempt-wide deadline.
    }

    private void OnGONegotiationFailure(IDictionary<string, object> properties)
    {
        var details = P2PDiagnostics.FormatProperties(properties);
        Report($"Wi-Fi Direct GO negotiation failed: {details}");
        if (_networkManagerActivationPending)
        {
            _pendingActivation?.TrySetException(new InvalidOperationException(
                $"wpa_supplicant rejected Wi-Fi Direct GO negotiation: {details}"));
            return;
        }
        ResetPendingAuthorization();
        _discovery.QueueRestart("GO negotiation failed");
    }

    private void OnGroupFormationFailure(string reason)
    {
        Report($"Wi-Fi Direct group formation failed: {reason}");
        if (_networkManagerActivationPending)
        {
            _pendingActivation?.TrySetException(new InvalidOperationException(
                $"wpa_supplicant could not form the Wi-Fi Direct group: {reason}"));
            return;
        }
        ResetPendingAuthorization();
        _discovery.QueueRestart("group formation failed");
    }

    private async Task DisconnectStaleGroupAsync(IWpaP2PDevice groupDevice)
    {
        try { await groupDevice.DisconnectAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            Report($"Could not remove the stale Wi-Fi Direct group: {exception.Message}");
        }
    }

    private void ResetPendingAuthorization()
    {
        _authorizationExpiresAt = DateTime.MinValue;
        _authorizedPeerAddress = null;
        _authorizedPeer = null;
        _negotiatedSourceAddress = null;
    }

    private void CancelAddressConfiguration()
    {
        _addressConfigurationLifetime?.Cancel();
        _addressConfigurationLifetime?.Dispose();
        _addressConfigurationLifetime = null;
    }

    private async Task ApplyNegotiatedGroupAddressAsync(
        IDictionary<string, object> properties,
        CancellationToken cancellationToken)
    {
        try
        {
            var localAddress = P2PAddressing.GetGroupAddress(properties, "IpAddr");
            var netmask = P2PAddressing.GetGroupAddress(properties, "IpAddrMask");
            var sourceAddress = P2PAddressing.GetGroupAddress(properties, "IpAddrGo");
            var prefixLength = netmask is null ? null : P2PAddressing.GetPrefixLength(netmask);
            if (localAddress is null || sourceAddress is null || prefixLength is null)
            {
                Report(
                    "wpa_supplicant did not provide a complete IPv4 configuration for the P2P group; "
                    + "NetworkManager will continue its normal IP configuration.");
                return;
            }

            var device = _bus.CreateProxy<INetworkManagerDevice>(Service, _devicePath);
            var applied = await device.GetAppliedConnectionAsync(0)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            applied.connection["ipv4"] = new Dictionary<string, object>
            {
                ["method"] = "manual",
                ["address-data"] = new IDictionary<string, object>[]
                {
                    new Dictionary<string, object>
                    {
                        ["address"] = localAddress.ToString(),
                        ["prefix"] = (uint)prefixLength.Value,
                    },
                },
                ["never-default"] = true,
                ["ignore-auto-dns"] = true,
                ["may-fail"] = false,
            };
            applied.connection["ipv6"] = new Dictionary<string, object>
            {
                ["method"] = "disabled",
            };

            _negotiatedSourceAddress = sourceAddress;
            await device.ReapplyAsync(applied.connection, applied.versionId, 0)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            Report(
                $"Applied the negotiated P2P IPv4 configuration through NetworkManager: "
                + $"{localAddress}/{prefixLength} → {sourceAddress}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report($"Could not apply the negotiated P2P IPv4 configuration: {exception.Message}");
        }
    }

    private async Task VerifyWfdAdvertisementAsync(CancellationToken cancellationToken)
    {
        var supplicant = _supplicant!;
        var advertisedIes = await supplicant.GetAsync<byte[]>("WFDIEs")
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!advertisedIes.AsSpan().SequenceEqual(P2PNetworkConfiguration.SinkWfdInformationElements))
            throw new InvalidOperationException("wpa_supplicant did not retain the Miracast Sink WFD subelements.");

        var config = await _supplicantP2PDevice!.GetAsync<IDictionary<string, object>>("P2PDeviceConfig")
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var receiverName = config.TryGetValue("DeviceName", out var name) && name is string text
            ? text
            : "Miracast Receiver";
        if (!config.TryGetValue("PrimaryDeviceType", out var primaryType)
            || primaryType is not byte[] bytes
            || !bytes.AsSpan().SequenceEqual(P2PNetworkConfiguration.DisplayPrimaryDeviceType))
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
            $"Miracast receiver '{receiverName}' is advertising in periodic P2P Listen mode "
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

    private void QueueIncomingConnection(ObjectPath supplicantPeerPath, string reason)
    {
        var cancellationToken = _lifetime?.Token ?? CancellationToken.None;
        _ = AuthorizeIncomingPeerAsync(supplicantPeerPath, reason, cancellationToken);
    }

    private async Task AuthorizeIncomingPeerAsync(
        ObjectPath supplicantPeerPath,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var supplicantPeer = _bus.CreateProxy<IWpaPeer>(SupplicantService, supplicantPeerPath);
            var address = Convert.ToHexString(
                await supplicantPeer.GetAsync<byte[]>("DeviceAddress")
                    .WaitAsync(cancellationToken).ConfigureAwait(false));

            WifiP2PPeer? peer = null;
            for (var attempt = 0; attempt < 30 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                if (_peersByAddress.TryGetValue(address, out peer))
                    break;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            if (peer is null)
            {
                Report($"Incoming {reason} from {address}, but NetworkManager did not expose that peer.");
                return;
            }

            Report($"Incoming {reason} from {peer.Name}; authorizing automatically…");
            await AuthorizeIncomingConnectionAsync(peer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report($"Could not authorize the incoming Wi-Fi Direct connection: {exception.Message}");
        }
    }

    private async Task RestoreWfdAdvertisementAsync(CancellationToken cancellationToken)
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
                if (_operatingChannelConfigured)
                {
                    previousConfig["OperRegClass"] = _previousOperRegClass ?? 0u;
                    previousConfig["OperChannel"] = _previousOperChannel ?? 0u;
                }
                previousConfig["NoGroupIface"] = _previousNoGroupIface ?? false;
                if (previousConfig.Count > 0)
                    await _supplicantP2PDevice.SetAsync("P2PDeviceConfig", previousConfig)
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            if (_wpsConfigured && _supplicantWps is not null && _previousWpsConfigMethods is not null)
            {
                await _supplicantWps.SetAsync("ConfigMethods", _previousWpsConfigMethods)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            if (_wfdAdvertisementConfigured && _supplicant is not null)
            {
                await _supplicant.SetAsync("WFDIEs", _previousWfdInformationElements ?? [])
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _operatingChannelConfigured = false;
            _wpsConfigured = false;
            _incomingRequestSubscriptionsConfigured = false;
            _supplicantP2PDevice = null;
            _supplicantInterface = null;
            _supplicantWps = null;
            _previousP2PDeviceName = null;
            _previousPrimaryDeviceType = null;
            _previousGoIntent = null;
            _previousOperRegClass = null;
            _previousOperChannel = null;
            _previousNoGroupIface = null;
            _previousWpsConfigMethods = null;
        }
    }

    private async Task HandleDisconnectedAsync(long attemptId, CancellationToken cancellationToken)
    {
        try
        {
            await _disconnectionOperations.RunAsync(
                "current-p2p-session",
                () => DisconnectCurrentCoreAsync(attemptId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { Report($"Could not clean up the disconnected P2P session: {exception.Message}"); }
    }

    private async Task StopListeningAsync(CancellationToken cancellationToken = default)
    {
        if (_supplicantP2PDevice is not null)
        {
            try
            {
                await _supplicantP2PDevice.ExtendedListenAsync(new Dictionary<string, object>())
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (DBusException) { }
            catch (Exception exception) { Report($"Could not stop periodic P2P listen: {exception.Message}"); }
        }
        _discovery.MarkStopped();
    }

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
