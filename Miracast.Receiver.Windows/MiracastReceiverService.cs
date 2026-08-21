using System.Runtime.Versioning;
using Miracast.Receiver.Entities.EventArgs;
using Windows.Media.Miracast;

namespace Miracast.Receiver.Windows;

[SupportedOSPlatform("windows10.0.18362")]
public sealed class MiracastReceiverService : IMiracastReceiverService, IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private MiracastReceiver? _receiver;
    private MiracastReceiverSession? _session;

    public event EventHandler<ConnectionCreatedEventArgs>? ConnectionCreated;
    public event EventHandler<ConnectionClosedEventArgs>? ConnectionClosed;
    public event EventHandler<VideoReceivedEventArgs>? VideoReceived;
    public event EventHandler<ReceiverStatusChangedEventArgs>? StatusChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null)
                return;

            cancellationToken.ThrowIfCancellationRequested();
            var receiver = new MiracastReceiver();
            var settings = receiver.GetDefaultSettings();
            settings.FriendlyName = $"{Environment.MachineName} Miracast";
            settings.AuthorizationMethod = MiracastReceiverAuthorizationMethod.None;
            settings.RequireAuthorizationFromKnownTransmitters = false;

            var applyResult = await receiver.DisconnectAllAndApplySettingsAsync(settings);
            if (applyResult.Status != MiracastReceiverApplySettingsStatus.Success)
                throw new InvalidOperationException(
                    $"Windows rejected the Miracast receiver settings: {applyResult.Status}. {applyResult.ExtendedError}");

            cancellationToken.ThrowIfCancellationRequested();
            // A desktop Avalonia app has no CoreApplicationView; null is the documented desktop-host value.
            var session = await receiver.CreateSessionAsync(null);
            session.AllowConnectionTakeover = true;
            session.ConnectionCreated += OnConnectionCreated;
            session.Disconnected += OnDisconnected;
            session.MediaSourceCreated += OnMediaSourceCreated;

            var startResult = await session.StartAsync();
            if (startResult.Status != MiracastReceiverSessionStartStatus.Success)
            {
                session.Dispose();
                throw new InvalidOperationException(
                    $"Windows could not start the Miracast receiver: {startResult.Status}. {startResult.ExtendedError}");
            }

            _receiver = receiver;
            _session = session;
            StatusChanged?.Invoke(this, new ReceiverStatusChangedEventArgs
            {
                Status = "Ready — choose this PC in the source device's Cast menu.",
            });
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
            if (_session is null)
                return;

            _session.ConnectionCreated -= OnConnectionCreated;
            _session.Disconnected -= OnDisconnected;
            _session.MediaSourceCreated -= OnMediaSourceCreated;
            _session.Dispose();
            _session = null;
            _receiver = null;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void OnConnectionCreated(MiracastReceiverSession sender, MiracastReceiverConnectionCreatedEventArgs args) =>
        ConnectionCreated?.Invoke(this, new ConnectionCreatedEventArgs
        {
            DeviceName = args.Connection.Transmitter.Name,
        });

    private void OnDisconnected(MiracastReceiverSession sender, MiracastReceiverDisconnectedEventArgs args) =>
        ConnectionClosed?.Invoke(this, new ConnectionClosedEventArgs
        {
            DeviceName = args.Connection.Transmitter.Name,
        });

    private void OnMediaSourceCreated(MiracastReceiverSession sender, MiracastReceiverMediaSourceCreatedEventArgs args) =>
        VideoReceived?.Invoke(this, new VideoReceivedEventArgs
        {
            Source = new VideoSource(args.MediaSource),
        });

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}
