namespace Miracast.Receiver.Linux;

internal sealed class NetworkManagerP2pConfigurator(Action<string> log)
{
    public async Task EnsureAddressAsync(string interfaceName, string role, CancellationToken cancellationToken)
    {
        // The group was created by NetworkManager's own wpa_supplicant. Marking the
        // virtual group interface managed lets NetworkManager attach its external
        // connection and run the appropriate IPv4 configuration without releasing
        // the parent Wi-Fi adapter.
        await CommandRunner.RunAsync(
            "nmcli", ["device", "set", interfaceName, "managed", "yes"], cancellationToken, ignoreFailure: true)
            .ConfigureAwait(false);
        await CommandRunner.RunAsync(
            "nmcli", ["device", "connect", interfaceName], cancellationToken, ignoreFailure: true)
            .ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                var output = await CommandRunner.RunAsync(
                        "nmcli",
                        ["-g", "IP4.ADDRESS", "device", "show", interfaceName],
                        timeout.Token,
                        ignoreFailure: true)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(output))
                {
                    log($"P2P group {interfaceName} ({role}) has IPv4 address {output.Trim()}.");
                    return;
                }

                await Task.Delay(250, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"NetworkManager did not configure IPv4 on P2P group '{interfaceName}'. " +
                "Make sure the NetworkManager Wi-Fi P2P backend and a DHCP client are available.");
        }
    }
}
