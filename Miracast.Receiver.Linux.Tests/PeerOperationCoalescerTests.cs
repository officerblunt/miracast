using Xunit;

namespace Miracast.Receiver.Linux.Tests;

public sealed class PeerOperationCoalescerTests
{
    [Fact]
    public async Task DuplicatePeerRequestsShareOneOperation()
    {
        var coalescer = new PeerOperationCoalescer();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;

        var first = coalescer.RunAsync(
            "42AE30AB8CA2",
            () =>
            {
                starts++;
                return completion.Task;
            },
            CancellationToken.None);
        var duplicate = coalescer.RunAsync(
            "42ae30ab8ca2",
            () =>
            {
                starts++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, starts);
        completion.SetResult();
        await Task.WhenAll(first, duplicate);
    }

    [Fact]
    public async Task DifferentPeerCannotReplaceRunningOperation()
    {
        var coalescer = new PeerOperationCoalescer();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = coalescer.RunAsync("peer-a", () => completion.Task, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coalescer.RunAsync("peer-b", () => Task.CompletedTask, CancellationToken.None));

        Assert.Contains("peer-a", exception.Message);
        completion.SetResult();
    }

    [Fact]
    public async Task CancellingDuplicateWaitDoesNotCancelSharedOperation()
    {
        var coalescer = new PeerOperationCoalescer();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coalescer.RunAsync("peer", () => completion.Task, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coalescer.RunAsync("peer", () => Task.CompletedTask, cancelled.Token));

        Assert.False(first.IsCompleted);
        completion.SetResult();
        await first;
    }

    [Fact]
    public async Task CompletedOperationDoesNotBlockNextAttempt()
    {
        var coalescer = new PeerOperationCoalescer();
        var starts = 0;

        await coalescer.RunAsync(
            "peer",
            () =>
            {
                starts++;
                return Task.CompletedTask;
            },
            CancellationToken.None);
        await coalescer.RunAsync(
            "peer",
            () =>
            {
                starts++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(2, starts);
    }
}
