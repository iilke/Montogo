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

    // Additional entropy mixed into DPAPI so the ciphertext is bound to this app,
    // not just the user account. Must be identical for Protect and Unprotect.
    private static readonly byte[] DpapiEntropy = "montogo-shared-secret-v1"u8.ToArray();

    /// <summary>
    /// Absolute path to virtual-display-driver-cli.exe.
    /// Leave null to use the default driver\ folder next to the exe.
    /// </summary>
    public string? DriverCliPath { get; set; }

    /// <summary>
    /// On-disk form of the connection code: DPAPI-encrypted
    /// (DataProtectionScope.CurrentUser) and base64-encoded. Legacy installs may
    /// hold the plaintext code here — it is migrated to the encrypted form on first
    /// read. Do NOT read this directly for the plaintext; use
    /// <see cref="GetOrCreateConnectionCode"/> / <see cref="SetConnectionCode"/>.
    /// </summary>
    public string? SharedSecret { get; set; }

    /// <summary>
    /// Returns the plaintext connection code, generating and saving one if absent.
    /// Called once at startup, synchronously — the file is tiny and in %APPDATA%.
    /// </summary>
    public string GetOrCreateConnectionCode()
    {
        if (SharedSecret is null)
        {
            string fresh = SecurityContext.EncodeConnectionCode(RandomNumberGenerator.GetBytes(5));
            SharedSecret = Protect(fresh);
            Save();
            return fresh;
        }

        // Encrypted value → decrypt. If that fails, this is a legacy plaintext code
        // (pre-DPAPI): migrate it to the encrypted form and rewrite the file.
        string? decrypted = TryUnprotect(SharedSecret);
        if (decrypted is not null) return decrypted;

        string legacyPlaintext = SharedSecret;
        SharedSecret = Protect(legacyPlaintext);
        Save();
        return legacyPlaintext;
    }

    /// <summary>Encrypts and persists a replacement connection code (tray "Reset").</summary>
    public void SetConnectionCode(string code)
    {
        SharedSecret = Protect(code);
        Save();
    }

    // ── DPAPI (Windows Data Protection API) ────────────────────────────────────

    private static string Protect(string plaintext)
    {
        byte[] cipher = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(plaintext), DpapiEntropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    /// <summary>Decrypts a stored value, or null if it isn't a valid DPAPI blob
    /// (e.g. a legacy plaintext code, which contains a '-' and isn't valid base64).</summary>
    private static string? TryUnprotect(string stored)
    {
        try
        {
            byte[] cipher = Convert.FromBase64String(stored);
            byte[] plain  = ProtectedData.Unprotect(cipher, DpapiEntropy, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (FormatException)        { return null; } // not base64 → plaintext
        catch (CryptographicException) { return null; } // not our DPAPI blob
    }

    // ── Load / Save ────────────────────────────────────────────────────────────

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
