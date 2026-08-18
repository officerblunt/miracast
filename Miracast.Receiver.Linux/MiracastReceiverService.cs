using System.Diagnostics;
using Miracast.Receiver.Entities.EventArgs;

namespace Miracast.Receiver.Linux;

public class MiracastReceiverService : IMiracastReceiverService
{
    private Process? _miracleWifidProcess;
    private Process? _miracleSinkctlProcess;

    public event EventHandler<ConnectionCreatedEventArgs>? ConnectionCreated;
    public event EventHandler<ConnectionClosedEventArgs>? ConnectionClosed;
    public event EventHandler<VideoReceivedEventArgs>? VideoReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _miracleWifidProcess = Process.Start(GetWifidPsi());
        _miracleSinkctlProcess = Process.Start(GetSinkCtl());
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_miracleSinkctlProcess is not null) await _miracleSinkctlProcess.WaitForExitAsync(cancellationToken);
        if (_miracleWifidProcess is not null) await _miracleWifidProcess.WaitForExitAsync(cancellationToken);
    }

    private static ProcessStartInfo GetWifidPsi() => new()
    {
        FileName = "/usr/local/bin/miracle-wifid",
        UseShellExecute = false,
    };

    private static ProcessStartInfo GetSinkCtl() => new()
    {
        FileName = "/usr/local/bin/miracle-sinkctl",
        UseShellExecute = false,
    };
    
    
    /*
    FOR RECEIVING:
    WIFI_IFACE=wlxb8fbb3dfa1e4
    pkill -TERM gnome-network-displays 2>/dev/null || true
    sudo nmcli device disconnect "$WIFI_IFACE" 2>/dev/null || true
    sudo nmcli device set "$WIFI_IFACE" managed no
    sudo systemctl stop wpa_supplicant.service
    sudo miracle-wifid --interface "$WIFI_IFACE"
     */
}