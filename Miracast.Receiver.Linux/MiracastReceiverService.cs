using Miracast.Receiver.Entities.EventArgs;

namespace Miracast.Receiver.Linux;

public sealed class MiracastReceiverService : IMiracastReceiverService, IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private NetworkManagerP2P? _networkManager;
    private WfdRtspServer? _rtspServer;
    private CancellationTokenSource? _lifetime;
    private WifiP2PPeer? _connectedPeer;
    private bool _started;

    public event EventHandler<ConnectionCreatedEventArgs>? ConnectionCreated;
    public event EventHandler<ConnectionClosedEventArgs>? ConnectionClosed;
    public event EventHandler<VideoReceivedEventArgs>? VideoReceived;
    public event EventHandler<ReceiverStatusChangedEventArgs>? StatusChanged;

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
            _rtspServer = new WfdRtspServer();
            _rtspServer.StatusChanged += OnInternalStatusChanged;
            _rtspServer.StreamReady += OnStreamReady;
            _rtspServer.SessionClosed += OnRtspSessionClosed;
            _rtspServer.Start(_lifetime.Token);

            _networkManager = new NetworkManagerP2P();
            _networkManager.StatusChanged += OnInternalStatusChanged;
            _networkManager.PeerConnected += OnPeerConnected;
            _networkManager.PeerDisconnected += OnPeerDisconnected;
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

        if (_networkManager is not null)
        {
            _networkManager.StatusChanged -= OnInternalStatusChanged;
            _networkManager.PeerConnected -= OnPeerConnected;
            _networkManager.PeerDisconnected -= OnPeerDisconnected;
            await _networkManager.DisposeAsync().ConfigureAwait(false);
            _networkManager = null;
        }

        if (_rtspServer is not null)
        {
            _rtspServer.StatusChanged -= OnInternalStatusChanged;
            _rtspServer.StreamReady -= OnStreamReady;
            _rtspServer.SessionClosed -= OnRtspSessionClosed;
            await _rtspServer.DisposeAsync().ConfigureAwait(false);
            _rtspServer = null;
        }

        _lifetime?.Dispose();
        _lifetime = null;
        _connectedPeer = null;
    }

    private void OnPeerConnected(object? sender, WifiP2PPeer peer)
    {
        _connectedPeer = peer;
        ConnectionCreated?.Invoke(this, new ConnectionCreatedEventArgs { DeviceName = peer.Name });
        Report($"Connected to {peer.Name}. Waiting for WFD/RTSP negotiation…");
    }

    private void OnPeerDisconnected(object? sender, EventArgs args) => CloseConnection();

    private void OnRtspSessionClosed(object? sender, EventArgs args)
    {
        if (_connectedPeer is not null)
            Report("RTSP session closed; waiting for the P2P connection to end…");
    }

    private void OnStreamReady(object? sender, EventArgs args)
    {
        VideoReceived?.Invoke(this, new VideoReceivedEventArgs
        {
            Source = new VideoSource(
                new Uri($"rtp://@0.0.0.0:{WfdRtspServer.RtpPort}"),
                WfdRtspServer.OutputWidth,
                WfdRtspServer.OutputHeight),
        });
    }

    private void CloseConnection()
    {
        var peer = _connectedPeer;
        if (peer is null)
            return;
        _connectedPeer = null;
        ConnectionClosed?.Invoke(this, new ConnectionClosedEventArgs { DeviceName = peer.Name });
    }

    private void OnInternalStatusChanged(object? sender, string status) => Report(status);

    private void Report(string status) =>
        StatusChanged?.Invoke(this, new ReceiverStatusChangedEventArgs { Status = status });

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}
