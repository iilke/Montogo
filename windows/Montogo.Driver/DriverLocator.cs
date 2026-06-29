namespace Montogo.Driver;

public static class DriverLocator
{
    public const string CliName = "virtual-display-driver-cli.exe";

    /// <summary>
    /// Returns the path to the CLI if it exists in the driver\ folder next to the
    /// running executable, otherwise returns null.
    /// </summary>
    public static string? TryFindRelative()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "driver", CliName);
        return File.Exists(path) ? path : null;
    }
}
