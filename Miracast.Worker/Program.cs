using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Miracast.Receiver;
using Miracast.Receiver.Entities.EventArgs;

namespace Miracast.Worker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var pipeName = GetRequiredArgument(args, "--pipe");
            var displayWidth = GetRequiredPositiveIntegerArgument(args, "--display-width");
            var displayHeight = GetRequiredPositiveIntegerArgument(args, "--display-height");
            var nativeWidth = GetRequiredPositiveIntegerArgument(args, "--native-width");
            var nativeHeight = GetRequiredPositiveIntegerArgument(args, "--native-height");
            var (receiver, renderer) = CreatePlatformReceiver(
                displayWidth,
                displayHeight,
                nativeWidth,
                nativeHeight);
            await using var worker = new ReceiverWorker(pipeName, receiver, renderer);
            await worker.RunAsync().ConfigureAwait(false);
            return 0;
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
                return string.IsNullOrWhiteSpace(args[index + 1])
                    ? throw new ArgumentException("Pipe name cannot be empty.", name)
                    : args[index + 1];
        }
        throw new ArgumentException($"Required argument '{name}' was not provided.", name);
    }

    private static int GetRequiredPositiveIntegerArgument(IReadOnlyList<string> args, string name)
    {
        var value = GetRequiredArgument(args, name);
        return int.TryParse(value, out var result) && result is > 0 and <= ushort.MaxValue
            ? result
            : throw new ArgumentOutOfRangeException(name, value, "Display dimensions must be between 1 and 65535.");
    }

    private static (IMiracastReceiverService Receiver, IVideoRenderer Renderer) CreatePlatformReceiver(
        int displayWidth,
        int displayHeight,
        int nativeWidth,
        int nativeHeight)
    {
#if WINDOWS_WORKER
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            throw new PlatformNotSupportedException(
                "The Windows receiver requires Windows 10 version 1903 or newer.");
        var renderer = new Miracast.Receiver.Windows.VideoRenderer();
        return (new Miracast.Receiver.Windows.MiracastReceiverService(), renderer);
#elif LINUX_WORKER
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Linux receiver can only run on Linux.");
        var renderer = new Miracast.Receiver.Linux.VideoRenderer();
        return (
            new Miracast.Receiver.Linux.MiracastReceiverService(
                renderer,
                displayWidth,
                displayHeight,
                nativeWidth,
                nativeHeight),
            renderer);
#else
        throw new PlatformNotSupportedException("The Miracast receiver supports Windows and Linux only.");
#endif
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

    private readonly NamedPipeServerStream _pipe;
    private readonly IMiracastReceiverService _receiver;
    private readonly IVideoRenderer _renderer;
    private readonly SemaphoreSlim _receiverLifecycle = new(1, 1);
    private readonly SemaphoreSlim _outputLock = new(1, 1);
    private readonly SemaphoreSlim _frameAvailable = new(0, 1);
    private readonly CancellationTokenSource _stopping = new();
    private CancellationTokenSource? _receiverLifetime;
    private VideoFrameReceivedEventArgs? _pendingFrame;
    private Task? _frameWriter;
    private bool _enabled;
    private int _disposed;

    public ReceiverWorker(string pipeName, IMiracastReceiverService receiver, IVideoRenderer renderer)
    {
        _receiver = receiver;
        _renderer = renderer;
        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    public async Task RunAsync()
    {
        await _pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
        Subscribe();
        _frameWriter = WriteFramesAsync(_stopping.Token);
        var commandReader = ReadCommandsAsync(_stopping.Token);

        try
        {
            try
            {
                await SetEnabledAsync(true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                await TrySendErrorAsync(exception).ConfigureAwait(false);
                _stopping.Cancel();
            }

            await commandReader.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException) when (!_pipe.IsConnected)
        {
        }
    }

    private void Subscribe()
    {
        _receiver.ConnectionClosed += OnConnectionClosed;
        _receiver.VideoReceived += OnVideoReceived;
        _receiver.StatusChanged += OnStatusChanged;
        _renderer.FrameReceived += OnFrameReceived;
    }

    private void Unsubscribe()
    {
        _receiver.ConnectionClosed -= OnConnectionClosed;
        _receiver.VideoReceived -= OnVideoReceived;
        _receiver.StatusChanged -= OnStatusChanged;
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
                    await SetEnabledAsync(true).ConfigureAwait(false);
                    break;
                case DisableCommand:
                    _receiverLifetime?.Cancel();
                    await SetEnabledAsync(false).ConfigureAwait(false);
                    break;
                case ShutdownCommand:
                    _receiverLifetime?.Cancel();
                    _stopping.Cancel();
                    return;
                default:
                    throw new InvalidDataException($"Unknown worker command: {command[0]}.");
            }
        }
    }

    private async Task SetEnabledAsync(bool enabled)
    {
        if (!enabled)
            _receiverLifetime?.Cancel();

        await _receiverLifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_enabled == enabled)
                return;
            if (enabled)
            {
                _receiverLifetime?.Dispose();
                _receiverLifetime = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                try
                {
                    await _receiver.StartAsync(_receiverLifetime.Token).ConfigureAwait(false);
                    _enabled = true;
                    await SendMessageAsync(ReadyMessage, ReadOnlyMemory<byte>.Empty, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    _receiverLifetime.Dispose();
                    _receiverLifetime = null;
                    throw;
                }
                return;
            }

            _enabled = false;
            await _renderer.StopAsync().ConfigureAwait(false);
            await _receiver.StopAsync().ConfigureAwait(false);
            _receiverLifetime?.Dispose();
            _receiverLifetime = null;
            DiscardPendingFrame();
            await SendMessageAsync(ResetMessage, ReadOnlyMemory<byte>.Empty, CancellationToken.None)
                .ConfigureAwait(false);
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
            await _renderer.StopAsync().ConfigureAwait(false);
            DiscardPendingFrame();
            await SendMessageAsync(ResetMessage, ReadOnlyMemory<byte>.Empty, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await TrySendErrorAsync(exception).ConfigureAwait(false);
        }
    }

    private async void OnVideoReceived(object? sender, VideoReceivedEventArgs args)
    {
        if (args.Source is IPreparedVideoSource)
            return;
        try
        {
            await _renderer.PlayAsync(args.Source, _receiverLifetime?.Token ?? _stopping.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await TrySendErrorAsync(exception).ConfigureAwait(false);
        }
    }

    private async void OnStatusChanged(object? sender, ReceiverStatusChangedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Status))
            return;
        try
        {
            await SendTextAsync(LogMessage, args.Status, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException) when (!_pipe.IsConnected)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnFrameReceived(object? sender, VideoFrameReceivedEventArgs frame)
    {
        if (!_enabled || _stopping.IsCancellationRequested)
        {
            frame.Dispose();
            return;
        }
        Interlocked.Exchange(ref _pendingFrame, frame)?.Dispose();
        try
        {
            if (_frameAvailable.CurrentCount == 0)
                _frameAvailable.Release();
        }
        catch (SemaphoreFullException)
        {
        }
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
                try { await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false); }
                finally { frame.Dispose(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (!_pipe.IsConnected)
        {
        }
    }

    private async Task SendFrameAsync(VideoFrameReceivedEventArgs frame, CancellationToken cancellationToken)
    {
        var pixelLength = checked(frame.RowBytes * frame.Height);
        var messageHeader = new byte[5];
        messageHeader[0] = FrameMessage;
        BinaryPrimitives.WriteInt32LittleEndian(messageHeader.AsSpan(1), checked(12 + pixelLength));
        var frameHeader = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(frameHeader.AsSpan(0, 4), frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(frameHeader.AsSpan(4, 4), frame.Height);
        BinaryPrimitives.WriteInt32LittleEndian(frameHeader.AsSpan(8, 4), frame.RowBytes);

        await _outputLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(messageHeader, cancellationToken).ConfigureAwait(false);
            await _pipe.WriteAsync(frameHeader, cancellationToken).ConfigureAwait(false);
            await _pipe.WriteAsync(frame.Pixels.AsMemory(0, pixelLength), cancellationToken)
                .ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _outputLock.Release();
        }
    }

    private Task SendTextAsync(byte type, string message, CancellationToken cancellationToken) =>
        SendMessageAsync(type, Encoding.UTF8.GetBytes(message), cancellationToken);

    private async Task SendMessageAsync(
        byte type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var header = new byte[5];
        header[0] = type;
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

    private async Task TrySendErrorAsync(Exception exception)
    {
        try { await SendTextAsync(ErrorMessage, exception.Message, CancellationToken.None).ConfigureAwait(false); }
        catch (IOException) when (!_pipe.IsConnected) { }
        catch (ObjectDisposedException) { }
    }

    private void DiscardPendingFrame() => Interlocked.Exchange(ref _pendingFrame, null)?.Dispose();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Unsubscribe();
        _receiverLifetime?.Cancel();
        _stopping.Cancel();
        try { await SetEnabledAsync(false).ConfigureAwait(false); }
        catch { }
        DiscardPendingFrame();
        if (_frameWriter is not null)
        {
            try { await _frameWriter.ConfigureAwait(false); }
            catch { }
        }
        if (_receiver is IAsyncDisposable asyncReceiver)
            await asyncReceiver.DisposeAsync().ConfigureAwait(false);
        if (_renderer is IAsyncDisposable asyncRenderer)
            await asyncRenderer.DisposeAsync().ConfigureAwait(false);
        _receiverLifetime?.Dispose();
        _pipe.Dispose();
        _stopping.Dispose();
        _frameAvailable.Dispose();
        _outputLock.Dispose();
        _receiverLifecycle.Dispose();
    }
}
