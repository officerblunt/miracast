# Platform-specific Miracast receivers

A minimal Avalonia UI with two independent platform applications:

- Windows 10 1903 or newer: `Windows.Media.Miracast` receives the connection and the Windows media frame server copies video into an Avalonia `WriteableBitmap`.
- Linux: MiracleCast performs Wi-Fi Direct and RTSP negotiation; LibVLC receives the MPEG-TS/RTP stream on UDP port `7236` and renders it inside the Avalonia window.

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

The host must provide these executables/libraries:

- `nmcli` (NetworkManager CLI);
- `systemctl`;
- `miracle-wifid` and `miracle-sinkctl` in `PATH`;
- LibVLC (`libvlc`) with its plugins; decoded BGRA frames are copied into the same Avalonia `WriteableBitmap` path used on Windows.

MiracleCast's system D-Bus policy must be installed as part of the machine's normal MiracleCast setup. The desktop user must be allowed by the machine's polkit configuration to change NetworkManager state and stop/start `wpa_supplicant.service`.

Some upstream MiracleCast builds also contain an explicit `getuid() != 0` check in `miracle-sinkctl`. Such a build cannot satisfy this application's non-root requirement and must be rebuilt/patched for an appropriately configured desktop user; the application never elevates it automatically.

At runtime the application never invokes `sudo`, `su`, `pkexec`, or a shell. It refuses to start the Linux receiver when its effective user is root. Every command is started directly with the identity of the desktop user. Startup performs:

```text
nmcli device disconnect <wifi-interface>
nmcli device set <wifi-interface> managed no
systemctl stop wpa_supplicant.service
miracle-wifid --interface <wifi-interface>
miracle-sinkctl --external-player /bin/true --port 7236
```

The first interface reported as `wifi` by `nmcli` is selected. Set `MIRACAST_WIFI_INTERFACE` to choose a specific adapter. `miracle-sinkctl` link detection and its `run <link>` command are automatic.

Startup waits for MiracleCast to expose its Wi-Fi link instead of imposing a fixed readiness timeout. If either MiracleCast process exits first, its exit code and recent stdout/stderr are included in the receiver error.

When the window closes, child processes are stopped and the application makes a best-effort attempt to start `wpa_supplicant`, return the adapter to NetworkManager, and reconnect it.

Because the selected Wi-Fi adapter is temporarily removed from NetworkManager, use a second adapter or Ethernet if the machine must keep network access while receiving.
