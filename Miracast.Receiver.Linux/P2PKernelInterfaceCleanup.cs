using System.Diagnostics;

namespace Miracast.Receiver.Linux;

internal static class P2PKernelInterfaceCleanup
{
    internal const string HelperPath = "/usr/local/libexec/multiwall-miracast-delete-stale-p2p";

    internal static async Task<(bool succeeded, string? error)> TryDeleteAsync(
        string interfaceName,
        string parentInterfaceName,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()
            || !P2PAddressing.IsSafeKernelInterfaceName(interfaceName)
            || string.IsNullOrWhiteSpace(parentInterfaceName)
            || parentInterfaceName.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            return (false, "unsafe or unsupported interface name");
        }
        if (!File.Exists(HelperPath))
            return (false, $"the privileged cleanup helper is not installed at {HelperPath}");

        var startInfo = new ProcessStartInfo("/usr/bin/sudo")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(HelperPath);
        startInfo.ArgumentList.Add(interfaceName);
        startInfo.ArgumentList.Add(parentInterfaceName);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return (false, "the cleanup helper could not be started");
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = (await standardError.ConfigureAwait(false)).Trim();
            if (output.Length == 0)
                output = (await standardOutput.ConfigureAwait(false)).Trim();
            return process.ExitCode == 0
                ? (true, null)
                : (false, output.Length == 0 ? $"helper exit code {process.ExitCode}" : output);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            throw;
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }
}
