using Avalonia.Controls;
using Miracast.Receiver;
using Miracast.Receiver.Entities.EventArgs;

namespace Miracast.Avalonia.Views;

public partial class MainWindow : Window
{
    private readonly IMiracastReceiverService? _miracastReceiver;
    private readonly IVideoRenderer? _videoRenderer;

    public MainWindow()
    {
        InitializeComponent();
        
        _miracastReceiver.VideoReceived += OnVideoReceived;
    }
    
    private async void OnVideoReceived(
        object? sender,
        VideoReceivedEventArgs e)
    {
        await _videoRenderer.PlayAsync(e.Source);
    }
}