using Xunit;

namespace Miracast.Receiver.Linux.Tests;

public sealed class P2PDiscoverySchedulerTests
{
    [Fact]
    public async Task StartSchedulesAdvertisingAndPausePreventsAnotherRestart()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        using var lifetime = new CancellationTokenSource();
        var scheduler = new P2PDiscoveryScheduler(
            () => true,
            () => DateTime.MinValue,
            _ =>
            {
                Interlocked.Increment(ref starts);
                started.TrySetResult();
                return Task.CompletedTask;
            },
            _ => { });

        scheduler.Start(lifetime.Token, "the receiver started");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.Pause();
        scheduler.QueueRestart("a stale event");
        await Task.Delay(350);

        Assert.Equal(1, Volatile.Read(ref starts));
        lifetime.Cancel();
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task ListeningStateCoalescesRedundantRestartRequests()
    {
        var firstStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        using var lifetime = new CancellationTokenSource();
        var scheduler = new P2PDiscoveryScheduler(
            () => true,
            () => DateTime.MinValue,
            _ =>
            {
                Interlocked.Increment(ref starts);
                firstStart.TrySetResult();
                return Task.CompletedTask;
            },
            _ => { });

        scheduler.Start(lifetime.Token, "the receiver started");
        await firstStart.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.MarkListening();
        scheduler.QueueRestart("duplicate one");
        scheduler.QueueRestart("duplicate two");
        await Task.Delay(350);

        Assert.Equal(1, Volatile.Read(ref starts));
        lifetime.Cancel();
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task RestartCanLeaveARecoveryWindowForTheStaConnection()
    {
        var firstStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        using var lifetime = new CancellationTokenSource();
        var scheduler = new P2PDiscoveryScheduler(
            () => true,
            () => DateTime.MinValue,
            _ =>
            {
                if (Interlocked.Increment(ref starts) == 1)
                    firstStart.TrySetResult();
                else
                    secondStart.TrySetResult();
                return Task.CompletedTask;
            },
            _ => { });

        scheduler.Start(lifetime.Token, "the receiver started");
        await firstStart.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.MarkStopped();
        scheduler.QueueRestart("listen interval ended", TimeSpan.FromMilliseconds(400));

        await Task.Delay(200);
        Assert.False(secondStart.Task.IsCompleted);
        await secondStart.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lifetime.Cancel();
        await scheduler.StopAsync();
    }
}
