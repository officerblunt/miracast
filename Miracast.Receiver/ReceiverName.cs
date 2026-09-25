namespace Miracast.Receiver;

public static class ReceiverName
{
    public const string EnvironmentVariable = "RECEIVER_NAME";

    public static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? Environment.MachineName
            : configured.Trim();
    }
}
