using Miracast.Receiver.Entities.EventArgs;
using Windows.Media.Miracast;

namespace Miracast.Receiver.Windows;

public class MiracastReceiverService : IMiracastReceiverService
{
    private MiracastReceiver? _miracastReceiver;
    private MiracastReceiverSession? _miracastSession;

    public event EventHandler<ConnectionCreatedEventArgs>? ConnectionCreated;
    public event EventHandler<ConnectionClosedEventArgs>? ConnectionClosed;
    public event EventHandler<VideoReceivedEventArgs>? VideoReceived;

    public MiracastReceiverService()
    {
        Task.Run(async () => await InitializeMiracastAsync()).Wait();
    }

    private async Task InitializeMiracastAsync()
    {
        _miracastReceiver = new();
        _miracastReceiver.StatusChanged += (receiver, o) =>
        {
            Console.WriteLine($"StatusChanged: {receiver.GetStatus().ListeningStatus}");
        };

        var settings = _miracastReceiver.GetDefaultSettings();
        
        settings.FriendlyName += "officerblunt";
        settings.AuthorizationMethod = MiracastReceiverAuthorizationMethod.None;
        settings.RequireAuthorizationFromKnownTransmitters = false;
        
        var applyResult = await _miracastReceiver.DisconnectAllAndApplySettingsAsync(settings);
        
        Console.WriteLine($"DisconnectAllAndApplySettingsAsync={applyResult.Status}");


        _miracastSession = await _miracastReceiver.CreateSessionAsync(null /* CoreApplication.MainView */);
        Console.WriteLine($"CreateSession={_miracastSession}");
        _miracastSession.AllowConnectionTakeover = true;
        _miracastSession.ConnectionCreated += (session, args) =>
        {
            Console.WriteLine($"ConnectionCreated {args.Connection.Transmitter.Name}");
        };
        _miracastSession.Disconnected += (session, args) =>
        {
            Console.WriteLine($"Disconnected {args.Connection.Transmitter.Name}");
        };
        _miracastSession.MediaSourceCreated += (sender, args) => VideoReceived?.Invoke(this, new()
        {
            Source = new VideoSource(args.MediaSource)
        });

        var startResult = await _miracastSession.StartAsync();
        Console.WriteLine($"Session.Start={startResult.Status}");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await InitializeMiracastAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}