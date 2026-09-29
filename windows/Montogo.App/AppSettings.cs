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
        // Recover a stored code only if it's still in the current 13-char format. A pre-v7
        // 8-char code (or anything malformed) is discarded and a fresh secret generated —
        // v7 lengthened the code from 40 to 64 bits, so old codes can't be reused.
        if (SharedSecret is not null)
        {
            string? decrypted = TryUnprotect(SharedSecret);   // encrypted blob → plaintext, else null
            if (decrypted is not null)
            {
                if (IsCurrentFormat(decrypted)) return decrypted;
            }
            else if (IsCurrentFormat(SharedSecret))
            {
                // Legacy plaintext code (pre-DPAPI) in the current format: migrate to encrypted.
                string legacy = SharedSecret;
                SharedSecret = Protect(legacy);
                Save();
                return legacy;
            }
            // else: pre-v7 short code or corrupt → fall through and regenerate.
        }

        string fresh = SecurityContext.EncodeConnectionCode(RandomNumberGenerator.GetBytes(8));
        SharedSecret = Protect(fresh);
        Save();
        return fresh;
    }

    private static bool IsCurrentFormat(string code) => code.Replace("-", "").Length == 13;

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
