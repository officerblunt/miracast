namespace Miracast.Receiver.Linux;

internal sealed class P2PDiscoveryScheduler
{
    private readonly object _restartSync = new();
    private readonly Func<bool> _canAdvertise;
    private readonly Func<DateTime> _authorizationExpiresAt;
    private readonly Func<CancellationToken, Task> _startAdvertisingAsync;
    private readonly Action<string> _report;
    private CancellationToken _lifetimeToken;
    private Task? _renewal;
    private Task? _restart;
    private string? _pendingRestartReason;
    private volatile bool _enabled;
    private volatile bool _listening;

    internal P2PDiscoveryScheduler(
        Func<bool> canAdvertise,
        Func<DateTime> authorizationExpiresAt,
        Func<CancellationToken, Task> startAdvertisingAsync,
        Action<string> report)
    {
        _canAdvertise = canAdvertise;
        _authorizationExpiresAt = authorizationExpiresAt;
        _startAdvertisingAsync = startAdvertisingAsync;
        _report = report;
    }

    internal bool Enabled => _enabled;
    internal bool IsListening => _listening;

    internal void Start(CancellationToken lifetimeToken, string reason)
    {
        _lifetimeToken = lifetimeToken;
        _enabled = true;
        QueueRestart(reason);
        _renewal ??= RenewAsync(lifetimeToken);
    }

    internal void Pause()
    {
        _enabled = false;
        _listening = false;
    }

    internal void Resume(string reason)
    {
        _enabled = true;
        QueueRestart(reason);
    }

    internal void MarkListening() => _listening = true;

    internal void MarkStopped() => _listening = false;

    internal void QueueRestart(string reason)
    {
        var cancellationToken = _lifetimeToken;
        if (!_enabled || cancellationToken.IsCancellationRequested || !_canAdvertise())
            return;

        lock (_restartSync)
        {
            _pendingRestartReason = reason;
            if (_restart is not { IsCompleted: false })
                _restart = RunRestartQueueAsync(cancellationToken);
        }
    }

    internal async Task StopAsync()
    {
        Pause();
        if (_renewal is not null)
        {
            try { await _renewal.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _renewal = null;
        }
        if (_restart is not null)
        {
            try { await _restart.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _restart = null;
        }
        lock (_restartSync)
            _pendingRestartReason = null;
    }

    private async Task RenewAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(9), cancellationToken).ConfigureAwait(false);
                if (_enabled && _canAdvertise())
                    await _startAdvertisingAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _report($"Could not renew P2P discovery: {exception.Message}");
        }
    }

    private async Task RunRestartQueueAsync(CancellationToken cancellationToken)
    {
        // Let QueueRestart assign _restart before this worker can clear it.
        await Task.Yield();
        while (!cancellationToken.IsCancellationRequested)
        {
            string? reason;
            lock (_restartSync)
            {
                if (_listening)
                    _pendingRestartReason = null;
                reason = _pendingRestartReason;
                _pendingRestartReason = null;
                if (reason is null)
                {
                    _restart = null;
                    return;
                }
            }
            await RestartAsync(reason, cancellationToken).ConfigureAwait(false);
        }

        lock (_restartSync)
        {
            _pendingRestartReason = null;
            _restart = null;
        }
    }

    private async Task RestartAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var authorizationDelay = _authorizationExpiresAt() - DateTime.UtcNow;
                if (authorizationDelay <= TimeSpan.Zero || authorizationDelay >= TimeSpan.FromMinutes(2))
                    break;
                await Task.Delay(
                    authorizationDelay < TimeSpan.FromSeconds(1)
                        ? authorizationDelay
                        : TimeSpan.FromSeconds(1),
                    cancellationToken).ConfigureAwait(false);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            if (!_enabled || !_canAdvertise())
                return;
            _report($"Restarting Wi-Fi Direct discovery because {reason}…");
            await _startAdvertisingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _report($"Could not restart P2P discovery: {exception.Message}");
        }
    }
}
