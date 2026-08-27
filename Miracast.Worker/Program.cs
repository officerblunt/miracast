using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Miracast.Receiver;
using Miracast.Receiver.Entities.EventArgs;
using LinuxReceiverService = Miracast.Receiver.Linux.MiracastReceiverService;
using LinuxVideoRenderer = Miracast.Receiver.Linux.VideoRenderer;
using WindowsReceiverService = Miracast.Receiver.Windows.MiracastReceiverService;
using WindowsVideoRenderer = Miracast.Receiver.Windows.VideoRenderer;

namespace Miracast.Worker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var pipeName = GetRequiredArgument(args, "--pipe");
            await using var worker = ReceiverWorker.Create(pipeName);
            return await worker.RunAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static string GetRequiredArgument(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        }

        throw new ArgumentException($"Required argument '{name}' was not provided.");
    }
}

internal sealed class ReceiverWorker : IAsyncDisposable
{
    private const byte ReadyMessage = 1;
    private const byte ResetMessage = 2;
    private const byte FrameMessage = 3;
    private const byte ErrorMessage = 4;
    private const byte LogMessage = 5;

    private const byte EnableCommand = 1;
    private const byte DisableCommand = 2;
    private const byte ShutdownCommand = 3;

    private readonly IMiracastReceiverService _receiver;
    private readonly IVideoRenderer _renderer;
    private readonly IAsyncDisposable _receiverLifetime;
    private readonly IAsyncDisposable _rendererLifetime;
    private readonly NamedPipeServerStream _pipe;
    private readonly SemaphoreSlim _outputLock = new(1, 1);
    private readonly SemaphoreSlim _receiverLifecycle = new(1, 1);
    private readonly SemaphoreSlim _frameAvailable = new(0, 1);
    private readonly CancellationTokenSource _stopping = new();
    private VideoFrameReceivedEventArgs? _pendingFrame;
    private Task? _frameWriter;
    private bool _enabled;
    private int _stopped;
    private int _disposed;

    private ReceiverWorker(
        string pipeName,
        IMiracastReceiverService receiver,
        IVideoRenderer renderer,
        IAsyncDisposable receiverLifetime,
        IAsyncDisposable rendererLifetime)
    {
        _receiver = receiver;
        _renderer = renderer;
        _receiverLifetime = receiverLifetime;
        _rendererLifetime = rendererLifetime;
        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    public static ReceiverWorker Create(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("Pipe name cannot be empty.", nameof(pipeName));

        if (OperatingSystem.IsWindows())
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
                throw new PlatformNotSupportedException("The Windows receiver requires Windows 10 version 1903 or newer.");

            var receiver = new WindowsReceiverService();
            var renderer = new WindowsVideoRenderer();
            return new(pipeName, receiver, renderer, receiver, renderer);
        }

        if (OperatingSystem.IsLinux())
        {
            var receiver = new LinuxReceiverService();
            var renderer = new LinuxVideoRenderer();
            return new(pipeName, receiver, renderer, receiver, renderer);
        }

        throw new PlatformNotSupportedException("The Miracast receiver supports Windows and Linux only.");
    }

    public async Task<int> RunAsync()
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
            Subscribe();
            _frameWriter = WriteFramesAsync(_stopping.Token);
            await SetEnabledAsync(true, _stopping.Token).ConfigureAwait(false);
            await ReadCommandsAsync(_stopping.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return 0;
        }
        catch (IOException) when (_pipe.IsConnected is false)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            try
            {
                await SendTextAsync(ErrorMessage, exception.Message, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The Widget may already have disconnected.
            }

            return 1;
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    private void Subscribe()
    {
        _receiver.ConnectionClosed += OnConnectionClosed;
        _receiver.VideoReceived += OnVideoReceived;
        _receiver.LogReceived += OnLogReceived;
        _renderer.FrameReceived += OnFrameReceived;
    }

    private void Unsubscribe()
    {
        _receiver.ConnectionClosed -= OnConnectionClosed;
        _receiver.VideoReceived -= OnVideoReceived;
        _receiver.LogReceived -= OnLogReceived;
        _renderer.FrameReceived -= OnFrameReceived;
    }

    private async Task ReadCommandsAsync(CancellationToken cancellationToken)
    {
        var command = new byte[1];
        while (true)
        {
            var read = await _pipe.ReadAsync(command, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return;

            switch (command[0])
            {
                case EnableCommand:
                    await SetEnabledAsync(true, cancellationToken).ConfigureAwait(false);
                    break;
                case DisableCommand:
                    await SetEnabledAsync(false, cancellationToken).ConfigureAwait(false);
                    break;
                case ShutdownCommand:
                    return;
                default:
                    throw new InvalidDataException($"Unknown worker command: {command[0]}.");
            }
        }
    }

    private async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        await _receiverLifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (enabled == _enabled)
                return;

            if (enabled)
            {
                await _receiver.StartAsync(cancellationToken).ConfigureAwait(false);
                _enabled = true;
                await SendMessageAsync(ReadyMessage, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                return;
            }

            _enabled = false;
            Interlocked.Exchange(ref _pendingFrame, null)?.Dispose();
            await _renderer.StopAsync().ConfigureAwait(false);
            await _receiver.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _receiverLifecycle.Release();
        }
    }

    private async void OnConnectionClosed(object? sender, ConnectionClosedEventArgs args)
    {
        try
        {
            if (!_enabled)
                return;

            await _renderer.StopAsync().ConfigureAwait(false);
            await SendMessageAsync(ResetMessage, ReadOnlyMemory<byte>.Empty, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
        }
    }

    private async void OnLogReceived(object? sender, string message)
    {
        try
        {
            await SendTextAsync(LogMessage, message, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (IOException) when (!_pipe.IsConnected)
        {
        }
    }

    private async void OnVideoReceived(object? sender, VideoReceivedEventArgs args)
    {
        try
        {
            if (_enabled)
                await _renderer.PlayAsync(args.Source, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            try
            {
                await SendTextAsync(ErrorMessage, exception.Message, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
        }
    }

    private void OnFrameReceived(object? sender, VideoFrameReceivedEventArgs frame)
    {
        if (!_enabled)
        {
            frame.Dispose();
            return;
        }

        Interlocked.Exchange(ref _pendingFrame, frame)?.Dispose();
        if (_frameAvailable.CurrentCount == 0)
            _frameAvailable.Release();
    }

    private async Task WriteFramesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _frameAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
                var frame = Interlocked.Exchange(ref _pendingFrame, null);
                if (frame is null)
                    continue;

                using (frame)
                {
                    var pixelLength = checked(frame.RowBytes * frame.Height);
                    var payloadLength = checked(12 + pixelLength);
                    var header = new byte[17];
                    header[0] = FrameMessage;
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1, 4), payloadLength);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5, 4), frame.Width);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(9, 4), frame.Height);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(13, 4), frame.RowBytes);

                    await _outputLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await _pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                        await _pipe.WriteAsync(frame.Pixels.AsMemory(0, pixelLength), cancellationToken).ConfigureAwait(false);
                        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _outputLock.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested || !_pipe.IsConnected)
        {
        }
    }

    private Task SendTextAsync(byte messageType, string message, CancellationToken cancellationToken) =>
        SendMessageAsync(messageType, Encoding.UTF8.GetBytes(message), cancellationToken);

    private async Task SendMessageAsync(
        byte messageType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (!_pipe.IsConnected)
            return;

        var header = new byte[5];
        header[0] = messageType;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);

        await _outputLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            if (!payload.IsEmpty)
                await _pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _outputLock.Release();
        }
    }

    private async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        _stopping.Cancel();
        Unsubscribe();
        Interlocked.Exchange(ref _pendingFrame, null)?.Dispose();

        await _receiverLifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _enabled = false;
            await _renderer.StopAsync().ConfigureAwait(false);
            await _receiver.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
        }
        finally
        {
            _receiverLifecycle.Release();
        }

        if (_frameWriter is not null)
            await _frameWriter.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await StopAsync().ConfigureAwait(false);
        await _rendererLifetime.DisposeAsync().ConfigureAwait(false);
        await _receiverLifetime.DisposeAsync().ConfigureAwait(false);
        _pipe.Dispose();
        _stopping.Dispose();
        _frameAvailable.Dispose();
        _receiverLifecycle.Dispose();
        _outputLock.Dispose();
    }
}
