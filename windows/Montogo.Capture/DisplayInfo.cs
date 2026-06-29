namespace Montogo.Capture;

public sealed record DisplayInfo(
    int    Index,
    string DeviceName,
    int    Left,
    int    Top,
    int    Width,
    int    Height,
    bool   IsPrimary);
