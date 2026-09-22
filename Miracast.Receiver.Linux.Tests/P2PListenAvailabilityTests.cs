using Xunit;

namespace Miracast.Receiver.Linux.Tests;

public sealed class P2PListenAvailabilityTests
{
    [Fact]
    public async Task BoostTemporarilyRaisesAvailabilityAndRestoresNormalMode()
    {
        var boosted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modes = new List<P2PListenMode>();
        using var lifetime = new CancellationTokenSource();
        using var availability = new P2PListenAvailability(
            () => true,
            (mode, _) =>
            {
                lock (modes)
                    modes.Add(mode);
                if (mode == P2PListenMode.Boosted)
                    boosted.TrySetResult();
                else if (mode == P2PListenMode.Normal)
                    restored.TrySetResult();
                return Task.CompletedTask;
            },
            _ => { },
            TimeSpan.FromMilliseconds(50));

        availability.Start(lifetime.Token);
        availability.Boost("Source");

        await boosted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([P2PListenMode.Boosted, P2PListenMode.Normal], modes);

        lifetime.Cancel();
        await availability.StopAsync();
    }

    [Fact]
    public async Task DisableCancelsBoostWithoutRestoringNormalMode()
    {
        var boosted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modes = new List<P2PListenMode>();
        using var lifetime = new CancellationTokenSource();
        using var availability = new P2PListenAvailability(
            () => true,
            (mode, _) =>
            {
                lock (modes)
                    modes.Add(mode);
                if (mode == P2PListenMode.Boosted)
                    boosted.TrySetResult();
                return Task.CompletedTask;
            },
            _ => { },
            TimeSpan.FromMinutes(1));

        availability.Start(lifetime.Token);
        availability.Boost("Source");
        await boosted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await availability.DisableAsync(CancellationToken.None);

        Assert.Equal([P2PListenMode.Boosted, P2PListenMode.Disabled], modes);
        lifetime.Cancel();
        await availability.StopAsync();
    }
}
