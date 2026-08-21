# Platform-specific Miracast receivers

A minimal Avalonia UI with two independent platform applications:

- Windows 10 1903 or newer: `Windows.Media.Miracast` receives the connection and the Windows media frame server copies video into an Avalonia `WriteableBitmap`.
- Linux: NetworkManager performs Wi-Fi Direct over its system D-Bus API, the application handles WFD/RTSP negotiation, and GStreamer receives the MPEG-TS/RTP stream and renders decoded frames inside the Avalonia window.

The applications share only the platform-neutral UI and receiver contracts. Each platform solution contains one executable host and one receiver implementation, so Windows builds never reference LibVLC or MiracleCast and Linux builds never reference the Windows SDK or Direct3D backend.

## Build and run

.NET 8 SDK is required.

```text
dotnet restore CrossplatformMiracast.Windows.sln
dotnet build CrossplatformMiracast.Windows.sln
dotnet run --project Miracast.Avalonia.Windows/Miracast.Avalonia.Windows.csproj
```

or, on Linux:

```text
dotnet restore CrossplatformMiracast.Linux.sln
dotnet build CrossplatformMiracast.Linux.sln
dotnet run --project Miracast.Avalonia.Linux/Miracast.Avalonia.Linux.csproj
```

`Miracast.Receiver` contains interfaces and event data only. `Miracast.Avalonia` is a shared UI library. Platform service registration is compiled into the corresponding Windows or Linux host; there is no runtime backend selection.

## Windows requirements

- Windows 10 version 1903 or newer.
- A Wi-Fi adapter and driver with Wi-Fi Direct/Miracast receiver support.
- No VLC or MiracleCast installation is used on Windows.

## Linux requirements

The host must provide:

- NetworkManager 1.16 or newer with an adapter exposed as device type `wifi-p2p`;
- `wpa_supplicant` and a Wi-Fi driver/firmware combination with Wi-Fi Direct support;
- `gst-launch-1.0` plus GStreamer RTP, MPEG-TS, H.264, audio and video conversion plugins (normally the base/good/bad/libav plugin sets).

The .NET side uses `Tmds.DBus` and talks to `org.freedesktop.NetworkManager` plus the global `fi.w1.wpa_supplicant1.WFDIEs` property on the system bus. It does not invoke `nmcli`, `systemctl`, MiracleCast, LibVLC, `sudo`, `su`, `pkexec`, or a shell. The desktop user must be permitted by the distribution's D-Bus policy to access `fi.w1.wpa_supplicant1` (Debian-family systems normally grant this to the `netdev` group).

Do not follow MiracleCast setup instructions for this backend: keep both NetworkManager and its `wpa_supplicant` integration running, and do not start `miracle-wifid`. MiracleCast's daemon is an alternative Wi-Fi Direct controller and conflicts with the NetworkManager-based design used here.

At startup it selects the first NetworkManager device whose `DeviceType` is `30` (`wifi-p2p`), publishes the Primary Sink WFD subelements `000600111c4400c8` through wpa_supplicant, subscribes to peer changes, and starts P2P discovery/listen. Publishing the local WFD capability before discovery is what makes Windows and Android list the machine as a Miracast receiver. It filters remote peers by a non-empty `WfdIEs` property and activates the first WFD-capable Source using a volatile `wifi-p2p` connection with WPS Push Button. NetworkManager may show the desktop's normal Polkit authorization dialog for this operation. The previous global WFD subelements are restored when the application stops.

After P2P activation the application reads the local address and Source address from NetworkManager's IPv4 configuration. The Linux Sink then connects as a bidirectional RTSP client to the Source's TCP port `7236`, completes WFD M1-M7, and maintains `CSeq` and `Session` state while continuing to service Source requests.

Before answering the WFD capability request, an even UDP RTP/RTCP pair is reserved starting at `19000`. The receiver advertises H.264 at 720p30/1080p30, LPCM 48 kHz stereo, UDP transport, and no HDCP or UIBC. After the Source selects a format, GStreamer takes ownership of the reserved port and must confirm its UDP bind before the Sink sends `SETUP` and `PLAY`.

The RTP MPEG-TS stream is passed through a 100 ms jitter buffer, demultiplexed into H.264 and audio, decoded by GStreamer, and copied as synchronized BGRA frames to the Avalonia `WriteableBitmap`. Bounded leaky queues discard stale media. An RTP watchdog requests a new IDR frame after four seconds without video and tears the session down after twelve seconds. HDCP, UIBC, TCP interleaving, PIN WPS and vendor-specific protocol extensions are not implemented.

When the window closes, the application sends a best-effort RTSP `TEARDOWN`, stops GStreamer, calls `DeactivateConnection()` and `StopFind()`, and closes the RTSP client. Wi-Fi Direct/Miracast support remains highly dependent on the concrete adapter, kernel driver and firmware, so it must be validated on the target Linux hardware.
