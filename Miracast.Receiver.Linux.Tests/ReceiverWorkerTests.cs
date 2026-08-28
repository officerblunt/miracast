using System.IO.Pipes;
using Miracast.Receiver.Entities.EventArgs;
using Miracast.Worker;
using Xunit;

namespace Miracast.Receiver.Linux.Tests;

public sealed class ReceiverWorkerTests
{
    [Fact]
    public async Task ShutdownCancelsReceiverStartupBeforeItCompletes()
    {
        var pipeName = $"miracast-worker-test-{Guid.NewGuid():N}";
        var receiver = new BlockingReceiver();
        var renderer = new FakeRenderer();
        await using var worker = new ReceiverWorker(pipeName, receiver, renderer);
        var run = worker.RunAsync();

        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await receiver.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await client.WriteAsync(new byte[] { 3 });
        await client.FlushAsync();

        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(receiver.StartupWasCancelled);
    }

    private sealed class BlockingReceiver : IMiracastReceiverService
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StartupWasCancelled { get; private set; }

        public event EventHandler<ConnectionCreatedEventArgs>? ConnectionCreated { add { } remove { } }
        public event EventHandler<ConnectionClosedEventArgs>? ConnectionClosed { add { } remove { } }
        public event EventHandler<VideoReceivedEventArgs>? VideoReceived { add { } remove { } }
        public event EventHandler<ReceiverStatusChangedEventArgs>? StatusChanged { add { } remove { } }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StartupWasCancelled = true;
                throw;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeRenderer : IVideoRenderer
    {
        public event EventHandler<VideoFrameReceivedEventArgs>? FrameReceived { add { } remove { } }

        public Task PlayAsync(IVideoSource source, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;
    }
}
