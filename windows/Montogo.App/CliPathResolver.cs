using Montogo.Driver;

namespace Montogo.App;

internal static class CliPathResolver
{
    /// <summary>
    /// Resolution order:
    ///   1. driver\virtual-display-driver-cli.exe next to the running exe
    ///   2. AppSettings.DriverCliPath (set in %APPDATA%\Montogo\settings.json)
    /// Throws with an actionable message if neither location has the file.
    /// </summary>
    public static string Resolve(AppSettings settings)
    {
        var relative = DriverLocator.TryFindRelative();
        if (relative is not null)
            return relative;

        if (!string.IsNullOrWhiteSpace(settings.DriverCliPath) &&
            File.Exists(settings.DriverCliPath))
        {
            if (!settings.DriverCliPath.EndsWith(DriverLocator.CliName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"DriverCliPath must point to {DriverLocator.CliName}, got: {settings.DriverCliPath}");
            return settings.DriverCliPath;
        }

        throw new FileNotFoundException(
            $"{DriverLocator.CliName} not found. " +
            $"Either copy it to the driver\\ folder next to this app, " +
            $"or add \"DriverCliPath\" to {AppSettings.SettingsFilePath}");
    }
}
