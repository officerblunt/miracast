namespace Miracast.Receiver.Linux;

internal enum P2PListenMode
{
    Disabled,
    Normal,
    Boosted,
}

internal sealed class P2PListenAvailability : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly Func<bool> _canBoost;
    private readonly Func<P2PListenMode, CancellationToken, Task> _applyAsync;
    private readonly Action<string> _report;
    private readonly TimeSpan _boostDuration;
    private CancellationToken _lifetimeToken;
    private CancellationTokenSource? _boostLifetime;
    private Task? _boostTask;
    private bool _started;

    internal P2PListenAvailability(
        Func<bool> canBoost,
        Func<P2PListenMode, CancellationToken, Task> applyAsync,
        Action<string> report,
        TimeSpan boostDuration)
    {
        _canBoost = canBoost;
        _applyAsync = applyAsync;
        _report = report;
        _boostDuration = boostDuration;
    }

    internal void Start(CancellationToken lifetimeToken)
    {
        _lifetimeToken = lifetimeToken;
        _started = true;
    }

    internal Task ApplyNormalAsync(CancellationToken cancellationToken) =>
        ApplyAsync(P2PListenMode.Normal, cancellationToken);

    internal void Boost(string peerName)
    {
        lock (_sync)
        {
            if (!_started || _lifetimeToken.IsCancellationRequested || !_canBoost())
                return;
            if (_boostTask is { IsCompleted: false })
                return;

            _boostLifetime?.Dispose();
            _boostLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            _boostTask = RunBoostAsync(peerName, _boostLifetime.Token);
        }
    }

    internal async Task DisableAsync(CancellationToken cancellationToken)
    {
        await CancelBoostAsync().ConfigureAwait(false);
        await ApplyAsync(P2PListenMode.Disabled, cancellationToken).ConfigureAwait(false);
    }

    internal async Task StopAsync()
    {
        _started = false;
        await CancelBoostAsync().ConfigureAwait(false);
    }

    private async Task RunBoostAsync(string peerName, CancellationToken cancellationToken)
    {
        // Ensure Boost can publish the task before this method can complete.
        await Task.Yield();
        try
        {
            if (!_canBoost())
                return;

            await ApplyAsync(P2PListenMode.Boosted, cancellationToken).ConfigureAwait(false);
            if (!_canBoost())
                return;

            _report(
                $"Temporarily increasing Wi-Fi Direct listen availability for {peerName} "
                + $"for {_boostDuration.TotalSeconds:0} seconds…");
            await Task.Delay(_boostDuration, cancellationToken).ConfigureAwait(false);

            if (!_canBoost())
                return;
            await ApplyAsync(P2PListenMode.Normal, cancellationToken).ConfigureAwait(false);
            _report("Restored low-impact periodic P2P Listen mode after the peer discovery window.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _report($"Could not boost Wi-Fi Direct listen availability: {exception.Message}");
        }
    }

    private async Task ApplyAsync(P2PListenMode mode, CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _applyAsync(mode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    private async Task CancelBoostAsync()
    {
        Task? task;
        CancellationTokenSource? lifetime;
        lock (_sync)
        {
            task = _boostTask;
            lifetime = _boostLifetime;
            lifetime?.Cancel();
        }

        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        lock (_sync)
        {
            if (!ReferenceEquals(task, _boostTask))
                return;
            _boostTask = null;
            _boostLifetime = null;
            lifetime?.Dispose();
        }
    }

    public void Dispose()
    {
        _boostLifetime?.Cancel();
        _boostLifetime?.Dispose();
        _configurationGate.Dispose();
    }
}
