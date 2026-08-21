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

The .NET side uses `Tmds.DBus` and talks directly to `org.freedesktop.NetworkManager` on the system bus. It does not invoke `nmcli`, `systemctl`, MiracleCast, LibVLC, `sudo`, `su`, `pkexec`, or a shell.

Do not follow MiracleCast setup instructions for this backend: keep both NetworkManager and its `wpa_supplicant` integration running, and do not start `miracle-wifid`. MiracleCast's daemon is an alternative Wi-Fi Direct controller and conflicts with the NetworkManager-based design used here.

At startup it selects the first NetworkManager device whose `DeviceType` is `30` (`wifi-p2p`), subscribes to peer changes, starts discovery, and filters peers by a non-empty `WfdIEs` property. It activates the first WFD-capable peer using a volatile `wifi-p2p` connection with WPS Push Button. NetworkManager may show the desktop's normal Polkit authorization dialog for this operation.

The built-in RTSP service listens on TCP port `7236`. The RTP MPEG-TS stream is received on UDP port `7236`, decoded by GStreamer, scaled to the renderer's BGRA frame size, and copied to the Avalonia `WriteableBitmap`. The current WFD implementation covers the basic unprotected UDP transport; HDCP, UIBC, TCP interleaving, PIN WPS, dynamic format changes and vendor-specific protocol extensions are not implemented.

When the window closes, the application calls `DeactivateConnection()` and `StopFind()`, closes the RTSP listener, and stops the GStreamer process. Wi-Fi Direct/Miracast support remains highly dependent on the concrete adapter, kernel driver and firmware, so it must be validated on the target Linux hardware.
