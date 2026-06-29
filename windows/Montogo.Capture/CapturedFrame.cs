namespace Montogo.Capture;

/// <summary>
/// One captured desktop frame in BGRA format.
/// BgraData is a freshly allocated array owned by the receiver — hold it or copy it freely.
/// The FrameCaptured handler must return quickly; it runs on the capture thread.
/// </summary>
public readonly struct CapturedFrame
{
    public required ReadOnlyMemory<byte> BgraData { get; init; }
    public required int Width  { get; init; }
    public required int Height { get; init; }
}
