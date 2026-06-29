using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Montogo.App;

internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Montogo", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Absolute path to virtual-display-driver-cli.exe.
    /// Leave null to use the default driver\ folder next to the exe.
    /// </summary>
    public string? DriverCliPath { get; set; }

    /// <summary>
    /// The 8-character connection code (e.g. "7X4K-9M2P").
    /// This IS the shared secret: both Windows and Mac derive all cryptographic keys from it.
    /// Generated once on first launch and never changes unless manually deleted.
    /// </summary>
    public string? SharedSecret { get; set; }

    /// <summary>
    /// Returns the persisted connection code, generating and saving one if absent.
    /// Called once at startup, synchronously — the file is tiny and in %APPDATA%.
    /// </summary>
    public string GetOrCreateConnectionCode()
    {
        if (SharedSecret is not null) return SharedSecret;

        SharedSecret = SecurityContext.EncodeConnectionCode(RandomNumberGenerator.GetBytes(5));
        Save();
        return SharedSecret;
    }

    public static AppSettings Load()
    {
        if (!File.Exists(FilePath))
            return new AppSettings();
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static string SettingsFilePath => FilePath;
}
