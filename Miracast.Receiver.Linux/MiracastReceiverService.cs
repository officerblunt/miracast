using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Miracast.Receiver.Entities.EventArgs;

namespace Miracast.Receiver.Linux;

public sealed partial class MiracastReceiverService : IMiracastReceiverService, IAsyncDisposable
{
    public const int RtpPort = 7236;

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Process? _wifid;
    private Process? _sinkctl;
    private CancellationTokenSource? _outputCancellation;
    private string? _wifiInterface;
    private int _videoAnnounced;
    private bool _started;

    public event EventHandler<ConnectionCreatedEventArgs>? ConnectionCreated;
    public event EventHandler<ConnectionClosedEventArgs>? ConnectionClosed;
    public event EventHandler<VideoReceivedEventArgs>? VideoReceived;
    public event EventHandler<string>? LogReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("MiracleCast receiver can only run on Linux.");
        if (GetEffectiveUserId() == 0)
            throw new InvalidOperationException("Do not run the receiver as root; start it as the desktop user.");

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
                return;

            _wifiInterface = await FindWifiInterfaceAsync(cancellationToken).ConfigureAwait(false);

            // These commands intentionally inherit the desktop user's identity. Do not add sudo/pkexec.
            await RunCommandAsync("nmcli", ["device", "disconnect", _wifiInterface], cancellationToken, ignoreFailure: true)
                .ConfigureAwait(false);
            await RunCommandAsync("nmcli", ["device", "set", _wifiInterface, "managed", "no"], cancellationToken)
                .ConfigureAwait(false);
            await RunCommandAsync("systemctl", ["stop", "wpa_supplicant.service"], cancellationToken)
                .ConfigureAwait(false);

            _outputCancellation = new CancellationTokenSource();
            _wifid = StartProcess("miracle-wifid", ["--interface", _wifiInterface]);
            _ = PumpOutputAsync(_wifid, HandleWifidLine, _outputCancellation.Token);

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            _sinkctl = StartProcess("miracle-sinkctl",
                ["--external-player", "/bin/true", "--port", RtpPort.ToString()]);

            var linkReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = PumpOutputAsync(_sinkctl, line => HandleSinkLine(line, linkReady), _outputCancellation.Token);

            var link = await linkReady.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
            await _sinkctl.StandardInput.WriteLineAsync($"run {link}").ConfigureAwait(false);
            await _sinkctl.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
            LogReceived?.Invoke(this, $"MiracleCast is listening on {_wifiInterface} (link {link}).");
        }
        catch
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
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
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        _started = false;
        _outputCancellation?.Cancel();
        StopProcess(_sinkctl);
        StopProcess(_wifid);
        _sinkctl = null;
        _wifid = null;
        _outputCancellation?.Dispose();
        _outputCancellation = null;
        Interlocked.Exchange(ref _videoAnnounced, 0);

        if (_wifiInterface is null)
            return;

        // Best-effort restoration of the networking state changed during startup.
        await RunCommandAsync("systemctl", ["start", "wpa_supplicant.service"], cancellationToken, ignoreFailure: true)
            .ConfigureAwait(false);
        await RunCommandAsync("nmcli", ["device", "set", _wifiInterface, "managed", "yes"], cancellationToken, ignoreFailure: true)
            .ConfigureAwait(false);
        await RunCommandAsync("nmcli", ["device", "connect", _wifiInterface], cancellationToken, ignoreFailure: true)
            .ConfigureAwait(false);
        _wifiInterface = null;
    }

    private static Process StartProcess(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = CreateStartInfo(fileName, arguments);
        startInfo.RedirectStandardInput = true;

        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start {fileName}.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not start '{fileName}'. Make sure it is installed and available in PATH.", exception);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    private async Task<string> FindWifiInterfaceAsync(CancellationToken cancellationToken)
    {
        var configuredInterface = Environment.GetEnvironmentVariable("MIRACAST_WIFI_INTERFACE");
        if (!string.IsNullOrWhiteSpace(configuredInterface))
            return configuredInterface.Trim();

        var output = await RunCommandAsync(
            "nmcli", ["-t", "-f", "DEVICE,TYPE", "device", "status"], cancellationToken).ConfigureAwait(false);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.LastIndexOf(':');
            if (separator > 0 && line[(separator + 1)..].Equals("wifi", StringComparison.OrdinalIgnoreCase))
                return line[..separator].Replace("\\:", ":", StringComparison.Ordinal);
        }

        throw new InvalidOperationException("nmcli did not report a Wi-Fi interface.");
    }

    private static async Task<string> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool ignoreFailure = false)
    {
        using var process = new Process { StartInfo = CreateStartInfo(fileName, arguments) };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Could not start {fileName}.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not start '{fileName}'. Make sure it is installed and available in PATH.", exception);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (!ignoreFailure && process.ExitCode != 0)
        {
            var command = $"{fileName} {string.Join(' ', arguments)}";
            throw new InvalidOperationException($"Command '{command}' failed: {stderr.Trim()}");
        }

        return stdout;
    }

    private async Task PumpOutputAsync(Process process, Action<string> handler, CancellationToken cancellationToken)
    {
        async Task ReadAsync(StreamReader reader)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;
                handler(AnsiEscapeRegex().Replace(line, string.Empty));
            }
        }

        try
        {
            await Task.WhenAll(ReadAsync(process.StandardOutput), ReadAsync(process.StandardError)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void HandleWifidLine(string line) => LogReceived?.Invoke(this, line);

    private void HandleSinkLine(string line, TaskCompletionSource<string> linkReady)
    {
        LogReceived?.Invoke(this, line);

        var linkMatch = LinkRegex().Match(line);
        if (linkMatch.Success)
            linkReady.TrySetResult(linkMatch.Groups[1].Value);

        if (line.Contains("SINK connected", StringComparison.OrdinalIgnoreCase))
            ConnectionCreated?.Invoke(this, new ConnectionCreatedEventArgs());

        if (line.Contains("SINK disconnected", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _videoAnnounced, 0);
            ConnectionClosed?.Invoke(this, new ConnectionClosedEventArgs());
        }

        var resolutionMatch = ResolutionRegex().Match(line);
        if (resolutionMatch.Success
            && Interlocked.Exchange(ref _videoAnnounced, 1) == 0)
        {
            var width = int.Parse(resolutionMatch.Groups[1].Value);
            var height = int.Parse(resolutionMatch.Groups[2].Value);
            VideoReceived?.Invoke(this, new VideoReceivedEventArgs
            {
                Source = new VideoSource(new Uri($"rtp://@0.0.0.0:{RtpPort}"), width, height),
            });
        }
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    [GeneratedRegex(@"\[ADD\]\s+Link:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"SINK\s+set\s+resolution\D+(\d+)\s*x\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();

    [GeneratedRegex("\\x1B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiEscapeRegex();

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
