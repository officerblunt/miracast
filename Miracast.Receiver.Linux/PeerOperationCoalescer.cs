namespace Miracast.Receiver.Linux;

internal sealed class PeerOperationCoalescer
{
    private readonly object _sync = new();
    private Task? _current;
    private string? _currentPeer;

    public Task? Current
    {
        get
        {
            lock (_sync)
                return _current is { IsCompleted: false } current ? current : null;
        }
    }

    public Task RunAsync(
        string peer,
        Func<Task> operationFactory,
        CancellationToken callerCancellationToken)
    {
        Task operation;
        lock (_sync)
        {
            if (_current is { IsCompleted: false } current)
            {
                if (!string.Equals(_currentPeer, peer, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromException(new InvalidOperationException(
                        $"A connection attempt from {_currentPeer} is already in progress."));
                }
                operation = current;
            }
            else
            {
                operation = operationFactory();
                _current = operation;
                _currentPeer = peer;
                _ = ClearWhenCompletedAsync(operation);
            }
        }

        return operation.WaitAsync(callerCancellationToken);
    }

    private async Task ClearWhenCompletedAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch { }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_current, operation))
                {
                    _current = null;
                    _currentPeer = null;
                }
            }
        }
    }
}
