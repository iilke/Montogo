namespace Montogo.Encoding;

public sealed class EncodedFrame
{
    public required byte[] Data { get; init; }
    public bool IsKeyFrame { get; init; }
    public long TimestampUs { get; init; }
}
