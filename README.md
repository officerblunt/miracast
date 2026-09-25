# Platform-specific Miracast receivers

A minimal Avalonia UI with two independent platform applications:

- Windows 10 1903 or newer: `Windows.Media.Miracast` receives the connection and the Windows media frame server copies video into an Avalonia `WriteableBitmap`.
- Linux: NetworkManager performs Wi-Fi Direct over its system D-Bus API, the application handles WFD/RTSP negotiation, and GStreamer receives the MPEG-TS/RTP stream and renders decoded frames inside the Avalonia window.

The applications share only the platform-neutral UI and receiver contracts. Each platform solution contains one executable host and one receiver implementation, so Windows builds never reference LibVLC or MiracleCast and Linux builds never reference the Windows SDK or Direct3D backend.

The receiver is advertised as the trimmed value of the `RECEIVER_NAME` environment variable when it is set and non-empty. Otherwise it uses the computer name. Linux truncates names longer than the 32-character Wi-Fi Direct device-name limit.

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
- `gst-launch-1.0` plus GStreamer RTP, MPEG-TS, H.264, audio and video conversion plugins (normally the base/good/bad/libav plugin sets, plus the ugly plugin set for Miracast LPCM audio).

The .NET side uses `Tmds.DBus` and talks to `org.freedesktop.NetworkManager` plus the global `fi.w1.wpa_supplicant1.WFDIEs` property on the system bus. Normal operation does not invoke `nmcli`, `wpa_cli`, `iw`, `systemctl`, MiracleCast, LibVLC, `su`, `pkexec`, or a shell. The desktop user must be permitted by the distribution's D-Bus policy to access `fi.w1.wpa_supplicant1` (Debian-family systems normally grant this to the `netdev` group). A narrow root-owned helper from `scripts/multiwall-miracast-delete-stale-p2p` may be installed at `/usr/local/libexec/multiwall-miracast-delete-stale-p2p` and allowed through `sudo -n`; it is used only when a buggy driver leaves a failed `p2p-*` netdev behind after wpa_supplicant has unregistered it. The helper validates that the stale interface and the selected parent adapter belong to the same PHY before calling `iw dev … del`.

Do not follow MiracleCast setup instructions for this backend: keep both NetworkManager and its `wpa_supplicant` integration running, and do not start `miracle-wifid`. MiracleCast's daemon is an alternative Wi-Fi Direct controller and conflicts with the NetworkManager-based design used here.

At startup it selects the first NetworkManager device whose `DeviceType` is `30` (`wifi-p2p`), publishes the Primary Sink WFD subelements `000600111c4400c8`, a Display WPS primary-device type and a receiver name through wpa_supplicant, then enters wpa_supplicant's dedicated P2P `Listen` state. Unlike NetworkManager's active peer search, this state continuously advertises the Sink in Probe Responses so Windows and Android can list it as a Miracast receiver. Remote WFD-capable Sources are cached but not contacted merely because they were discovered. A volatile `wifi-p2p` connection with WPS Push Button is activated only after wpa_supplicant reports an incoming provisioning or GO-negotiation request, meaning the user selected this receiver on the Source. NetworkManager may show the desktop's normal Polkit authorization dialog for activation. Previous global P2P/WFD settings are restored when the application stops.

Incoming provisioning and GO-negotiation notifications for the same peer are coalesced into one idempotent connection attempt. Each attempt has one 45-second deadline and a dedicated system D-Bus client; the volatile NetworkManager activation is bound to that client's lifetime, so cancelling or timing out an attempt cannot create a group minutes later. Late state notifications carry an attempt generation and stale groups are rejected instead of being promoted to sessions.

After P2P activation the application reads the local address and Source address from NetworkManager's IPv4 configuration. The Linux Sink then connects as a bidirectional RTSP client to the Source's TCP port `7236`, completes WFD M1-M7, and maintains `CSeq` and `Session` state while continuing to service Source requests.

Before answering the WFD capability request, an even UDP RTP/RTCP pair is reserved starting at `19000`. The volatile NetworkManager P2P profile is placed in the `trusted` firewall zone because the Source initiates the RTP/RTCP UDP flow; this trust applies only to the temporary direct link and the profile disappears after deactivation. The receiver advertises H.264 at 720p30/1080p30, LPCM 48 kHz stereo, UDP transport, and no HDCP or UIBC. After the Source selects a format, GStreamer takes ownership of the reserved port, binds specifically to the negotiated P2P IPv4 address, and must confirm its UDP bind before the Sink sends `SETUP` and `PLAY`.

The RTP MPEG-TS stream is passed through a 100 ms jitter buffer, demultiplexed without the `tsdemux` element's additional 700 ms smoothing delay, decoded by GStreamer, and copied as BGRA frames to the Avalonia `WriteableBitmap`. Encoded H.264 is never discarded mid-GOP; a one-frame leaky queue after decoding drops only complete stale raw frames if the UI transport falls behind. The stdout transport is not synchronized to a second media clock. H.264 is deliberately decoded by the software `avdec_h264` element instead of an auto-selected hardware decoder so that an interrupted Miracast session cannot leave the desktop GPU stack wedged. Miracast LPCM is decoded by GStreamer's `dvdlpcmdec`; when that optional element is unavailable, the receiver discards only the audio track and continues rendering video. An RTP watchdog requests a new IDR frame after four seconds without video and tears the session down after twelve seconds. HDCP, UIBC, TCP interleaving, PIN WPS and vendor-specific protocol extensions are not implemented.

Receiver startup and shutdown never wait indefinitely for NetworkManager's physical Wi-Fi scan: the window and worker start immediately, while P2P advertising waits for `Scanning=false` in the background. Advertising uses wpa_supplicant's continuous listen-only operation rather than active P2P Find, so discovery never interrupts the short interval in which Windows sends its provisioning request. If the selected physical adapter is connected to regular Wi-Fi, that connection and its autoconnect setting are left untouched; P2P group formation is pinned to the active Wi-Fi channel. Incoming first-time Windows connections are activated from the WPS Push Button request rather than the earlier GO signal, preventing discovery from being stopped before the paired WPS request arrives. Windows reinvokes the persistent group that it created for the previous projection before trying fresh WPS, so the receiver retains the local credential and enables wpa_supplicant's standard persistent reconnect. A merely visible paired Source does not start an activation. Instead, the receiver waits until wpa_supplicant creates the dedicated child interface for a real persistent-group reinvocation, then prepares NetworkManager before that interface emits `GroupStarted`; NetworkManager can adopt the group and apply the negotiated IP configuration and route isolation without false connection attempts caused by passive discovery. Cleanup disconnects and unregisters orphaned supplicant interfaces, restores only the four valid persistent-group properties (`bssid`, `ssid`, `psk`, and `mode`) if a failed reinvocation displaced the credential, and removes a kernel netdev through the validated helper only when the driver did not. Discovery stays paused until no child interface remains, preventing repeated attempts from making the receiver disappear or disconnecting regular Wi-Fi. An invitation for a credential that is missing locally cannot be converted to WPS in place and must fall back once, after which the newly paired group is retained. A concurrent regular-Wi-Fi scan gets at most one second to finish before P2P activation continues, because the source's configuration timeout is shorter than a typical scan. The ten-minute Listen operation is renewed every nine minutes and restarted if wpa_supplicant stops it early. If `Scanning` remains stuck for ten seconds during initial advertising, the receiver attempts the standard P2P Listen transition instead of waiting forever or issuing a manual scan-abort command. When the window closes, the application sends a best-effort RTSP `TEARDOWN`, asks GStreamer to drain the pipeline with EOS, and then deactivates the temporary P2P connection. Concurrent disconnect notifications are coalesced into one bounded cleanup operation. Failed GO negotiation interrupts the NetworkManager request immediately instead of waiting for its D-Bus timeout, while every other connection attempt has a 45-second overall deadline. Recovery cleanup is deferred until the physical scan has finished so wpa_supplicant cannot accumulate a delayed burst of P2P commands. Wi-Fi Direct/Miracast support remains highly dependent on the concrete adapter, kernel driver and firmware, so it must be validated on the target Linux hardware.
