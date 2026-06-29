namespace Montogo.Capture;

public sealed record CaptureOptions
{
    /// <summary>
    /// Index of the display to capture, as returned by DxgiCapture.EnumerateDisplays().
    /// On a single-GPU machine this is the per-adapter output index and matches the
    /// global index from EnumerateDisplays(). Multi-GPU setups may need adjustment.
    /// </summary>
    public int OutputIndex { get; init; } = 0;

    public int TargetFps { get; init; } = 60;
}
