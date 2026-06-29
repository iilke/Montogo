using System.Text.RegularExpressions;

namespace Montogo.Driver;

public sealed record MonitorInfo(string Id, int Width, int Height, int RefreshRate)
{
    // The CLI emits ANSI color/dim/underline codes. Strip them before parsing.
    // \x1B = ESC (0x1B); the sequence is ESC [ <params> <letter>.
    private static readonly Regex AnsiEscape =
        new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    // After stripping ANSI the output looks like:
    //   Virtual monitors
    //   Monitor 0:
    //   - 1920x1080@60
    // Each monitor occupies exactly two lines: an id line then a resolution line.
    private static readonly Regex MonitorIdLine =
        new(@"^Monitor\s+(\w+)\s*:", RegexOptions.Compiled);
    private static readonly Regex ResolutionLine =
        new(@"(\d+)x(\d+)@(\d+)", RegexOptions.Compiled);

    public static IReadOnlyList<MonitorInfo> ParseCliOutput(string output)
    {
        var clean = AnsiEscape.Replace(output, "");
        var lines = clean.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = new List<MonitorInfo>();
        string? pendingId = null;

        foreach (var line in lines)
        {
            var idMatch = MonitorIdLine.Match(line);
            if (idMatch.Success)
            {
                pendingId = idMatch.Groups[1].Value;
                continue;
            }

            if (pendingId is not null)
            {
                var resMatch = ResolutionLine.Match(line);
                if (resMatch.Success)
                {
                    result.Add(new MonitorInfo(
                        Id:          pendingId,
                        Width:       int.Parse(resMatch.Groups[1].Value),
                        Height:      int.Parse(resMatch.Groups[2].Value),
                        RefreshRate: int.Parse(resMatch.Groups[3].Value)));
                    pendingId = null;
                }
            }
        }

        return result;
    }

    public override string ToString() => $"{Id}: {Width}x{Height}@{RefreshRate}";
}
