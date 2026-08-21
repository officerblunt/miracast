namespace Miracast.Receiver;

/// <summary>
/// Marks a source whose platform receiver has already started the shared renderer.
/// This is used when a protocol requires media readiness before sending PLAY.
/// </summary>
public interface IPreparedVideoSource : IVideoSource
{
}
