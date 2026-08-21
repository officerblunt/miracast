using Miracast.Receiver.Entities.EventArgs;

namespace Miracast.Receiver.Linux;

public sealed class MiracastReceiverService :
    IMiracastReceiverService,
    IMiracastConnectionApprover,
    IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly IVideoRenderer _videoRenderer;
    private NetworkManagerP2P? _networkManager;
    private WfdSession? _wfdSession;
    private Task? _sessionTask;
    private CancellationTokenSource? _lifetime;
    private WifiP2PPeer? _connectedPeer;
    private bool _started;

    public MiracastReceiverService(IVideoRenderer videoRenderer) => _videoRenderer = videoRenderer;

    public event EventHandler<ConnectionCreatedEventArgs>? ConnectionCreated;
    public event EventHandler<ConnectionClosedEventArgs>? ConnectionClosed;
    public event EventHandler<VideoReceivedEventArgs>? VideoReceived;
    public event EventHandler<ReceiverStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<MiracastSourceChangedEventArgs>? SourceChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The NetworkManager Miracast receiver can only run on Linux.");

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
                return;

            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _networkManager = new NetworkManagerP2P();
            _networkManager.StatusChanged += OnInternalStatusChanged;
            _networkManager.PeerConnected += OnPeerConnected;
            _networkManager.PeerDisconnected += OnPeerDisconnected;
            _networkManager.PeerAvailable += OnPeerAvailable;
            _networkManager.PeerUnavailable += OnPeerUnavailable;
            await _networkManager.StartAsync(_lifetime.Token).ConfigureAwait(false);
            _started = true;
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        _started = false;
        _lifetime?.Cancel();

        var sessionTask = _sessionTask;
        if (sessionTask is not null)
        {
            try { await sessionTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { }
            _sessionTask = null;
        }

        if (_wfdSession is not null)
        {
            await _wfdSession.DisposeAsync().ConfigureAwait(false);
            _wfdSession = null;
        }

        if (_networkManager is not null)
        {
            _networkManager.StatusChanged -= OnInternalStatusChanged;
            _networkManager.PeerConnected -= OnPeerConnected;
            _networkManager.PeerDisconnected -= OnPeerDisconnected;
            _networkManager.PeerAvailable -= OnPeerAvailable;
            _networkManager.PeerUnavailable -= OnPeerUnavailable;
            await _networkManager.DisposeAsync().ConfigureAwait(false);
            _networkManager = null;
        }

        _lifetime?.Dispose();
        _lifetime = null;
        CloseConnection();
    }

    private void OnPeerConnected(object? sender, P2PConnectionContext connection)
    {
        var lifetime = _lifetime;
        if (lifetime is null || lifetime.IsCancellationRequested || _sessionTask is not null)
            return;

        _connectedPeer = connection.Peer;
        ConnectionCreated?.Invoke(this, new ConnectionCreatedEventArgs { DeviceName = connection.Peer.Name });
        Report($"Connected to {connection.Peer.Name}. Starting WFD/RTSP negotiation…");
        _sessionTask = RunWfdSessionAsync(connection, lifetime.Token);
    }

    private async Task RunWfdSessionAsync(P2PConnectionContext connection, CancellationToken cancellationToken)
    {
        var session = new WfdSession(connection, _videoRenderer, OnMediaReady, Report);
        _wfdSession = session;
        try
        {
            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report($"WFD session failed: {exception.Message}");
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
            if (ReferenceEquals(_wfdSession, session))
                _wfdSession = null;
            CloseConnection();

            var networkManager = _networkManager;
            if (networkManager is not null && !cancellationToken.IsCancellationRequested)
                await networkManager.DisconnectCurrentAsync().ConfigureAwait(false);
            _sessionTask = null;
        }
    }

    private void OnPeerDisconnected(object? sender, EventArgs args)
    {
        CloseConnection();
        var session = _wfdSession;
        if (session is not null)
            _ = StopWfdAfterPeerDisconnectAsync(session);
    }

    private async Task StopWfdAfterPeerDisconnectAsync(WfdSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Report($"Could not stop the disconnected WFD session: {exception.Message}");
        }
    }

    private void OnMediaReady(VideoSource source) =>
        VideoReceived?.Invoke(this, new VideoReceivedEventArgs { Source = source });

    private void CloseConnection()
    {
        var peer = _connectedPeer;
        if (peer is null)
            return;
        _connectedPeer = null;
        ConnectionClosed?.Invoke(this, new ConnectionClosedEventArgs { DeviceName = peer.Name });
    }

    private void OnInternalStatusChanged(object? sender, string status) => Report(status);

    private void OnPeerAvailable(object? sender, WifiP2PPeer peer) =>
        ReportSource(peer, isAvailable: true);

    private void OnPeerUnavailable(object? sender, WifiP2PPeer peer) =>
        ReportSource(peer, isAvailable: false);

    private void ReportSource(WifiP2PPeer peer, bool isAvailable) =>
        SourceChanged?.Invoke(this, new MiracastSourceChangedEventArgs
        {
            Source = new MiracastSourceInfo(
                peer.HardwareAddress,
                peer.Name,
                peer.HardwareAddress,
                peer.Strength),
            IsAvailable = isAvailable,
        });

    public Task ApproveConnectionAsync(
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        var networkManager = _networkManager
            ?? throw new InvalidOperationException("The Miracast receiver is not running.");
        return networkManager.ApproveConnectionAsync(sourceId, cancellationToken);
    }

    private void Report(string status) =>
        StatusChanged?.Invoke(this, new ReceiverStatusChangedEventArgs { Status = status });

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}
