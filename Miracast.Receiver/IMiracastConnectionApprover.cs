namespace Miracast.Receiver;

public interface IMiracastConnectionApprover
{
    event EventHandler<MiracastSourceChangedEventArgs>? SourceChanged;

    Task ApproveConnectionAsync(
        string sourceId,
        CancellationToken cancellationToken = default);
}

public sealed record MiracastSourceInfo(
    string Id,
    string Name,
    string HardwareAddress,
    byte Strength);

public sealed class MiracastSourceChangedEventArgs : EventArgs
{
    public required MiracastSourceInfo Source { get; init; }
    public required bool IsAvailable { get; init; }
}
