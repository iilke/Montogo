using System.Buffers.Binary;
using System.Security.Cryptography;
using Montogo.Protocol;

namespace Montogo.App;

/// <summary>
/// Holds the pairing secret and performs the authenticated key exchange for a session.
///
/// The shared secret is the 13-character connection code shown in the tray
/// (e.g. "7X4K-9M2P-QRST"), typed into the Mac app once. Its 8 raw bytes (64 bits)
/// derive only the HMAC authentication key:
///   AuthKey = HKDF(code, info="montogo-auth-v1", len=32)
///
/// The VIDEO key is NOT derived from the code. Each session runs an ephemeral
/// ECDH (NIST P-256) exchange during the handshake, authenticated by AuthKey, and
/// the AES-256-GCM content key comes from the ECDH shared secret:
///   SessionKey = HKDF(ecdh_shared, info="montogo-session-v7", len=32)
/// This gives forward secrecy (throwaway keys per session) and means a passive
/// eavesdropper — even one who later brute-forces the code — cannot decrypt captured
/// traffic. The code only lets a peer prove it is authorized (and, if brute-forced,
/// mount an active MITM — which 64 bits makes infeasible offline).
/// </summary>
internal sealed class SecurityContext
{
    // Base-32 alphabet: digits 2–9 + uppercase letters, omitting 0/O and 1/I to
    // avoid transcription errors. 32 symbols × 5 bits.
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const int CodeBytes = 8;   // 64-bit pairing secret
    private const int CodeChars = 13;  // 8 bytes → 13 base-32 chars (65-bit capacity, top bit unused)

    private static readonly byte[] AuthInfo     = "montogo-auth-v1"u8.ToArray();
    private static readonly byte[] SessionInfo  = "montogo-session-v7"u8.ToArray();
    private static readonly byte[] FeedbackInfo = "montogo-feedback-v8"u8.ToArray();

    private readonly byte[] _authKey;

    public string ConnectionCode { get; }

    public SecurityContext(string connectionCode)
    {
        ConnectionCode = connectionCode;
        byte[] ikm = DecodeConnectionCode(connectionCode);
        _authKey    = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt: null, info: AuthInfo);
    }

    // ── Connection code ──────────────────────────────────────────────────────

    /// <summary>Encodes 8 random bytes as a 13-char code, e.g. "7X4K-9M2P-QRST".</summary>
    public static string EncodeConnectionCode(byte[] ikm)
    {
        if (ikm.Length < CodeBytes) throw new ArgumentException($"Need at least {CodeBytes} bytes.", nameof(ikm));

        ulong bits = 0;
        for (int i = 0; i < CodeBytes; i++) bits = (bits << 8) | ikm[i];   // 64-bit value, ikm[0] = MSB

        char[] chars = new char[CodeChars];
        for (int i = CodeChars - 1; i >= 0; i--)   // fill LSB group first
        {
            chars[i] = Alphabet[(int)(bits & 0x1F)];
            bits >>= 5;
        }
        // Group 4-4-5 for readability.
        return new string(chars, 0, 4) + "-" + new string(chars, 4, 4) + "-" + new string(chars, 8, 5);
    }

    private static byte[] DecodeConnectionCode(string code)
    {
        ReadOnlySpan<char> chars = code.Replace("-", "").AsSpan();
        if (chars.Length != CodeChars)
            throw new ArgumentException($"Connection code must be {CodeChars} characters (e.g. 7X4K-9M2P-QRST).", nameof(code));

        ulong bits = 0;
        foreach (char c in chars)
        {
            int idx = Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (idx < 0)
                throw new ArgumentException($"Invalid character '{c}' in connection code.", nameof(code));
            bits = (bits << 5) | (uint)idx;   // 13×5 = 65 bits; the unused top bit overflows away
        }

        byte[] ikm = new byte[CodeBytes];
        for (int i = 0; i < CodeBytes; i++) ikm[i] = (byte)(bits >> (8 * (CodeBytes - 1 - i)));  // big-endian
        return ikm;
    }

    // ── Handshake authentication ─────────────────────────────────────────────

    /// <summary>HMAC-SHA256(authKey, message) — the full 32-byte tag.</summary>
    public byte[] ComputeAuthTag(ReadOnlySpan<byte> message)
        => HMACSHA256.HashData(_authKey, message);

    /// <summary>Constant-time check that <paramref name="tag"/> is HMAC(authKey, message).</summary>
    public bool VerifyAuthTag(ReadOnlySpan<byte> message, ReadOnlySpan<byte> tag)
    {
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(_authKey, message, expected);
        return CryptographicOperations.FixedTimeEquals(expected, tag);
    }

    // ── Ephemeral ECDH (NIST P-256) ──────────────────────────────────────────

    /// <summary>Creates an ephemeral P-256 key pair; returns the agreement object and
    /// its public key in X9.63 uncompressed form (0x04 || X(32) || Y(32) = 65 bytes).</summary>
    public static ECDiffieHellman CreateEphemeral(out byte[] publicKeyX963)
    {
        var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ECParameters p = ecdh.PublicKey.ExportParameters();
        publicKeyX963 = new byte[65];
        publicKeyX963[0] = 0x04;
        CopyRightAligned(p.Q.X!, publicKeyX963.AsSpan(1, 32));
        CopyRightAligned(p.Q.Y!, publicKeyX963.AsSpan(33, 32));
        return ecdh;
    }

    /// <summary>Derives this session's keys from our private key and the peer's X9.63 public
    /// key: the AES-256-GCM video cipher, and a separate HMAC key that authenticates feedback
    /// packets. Both come from the one ECDH shared secret via HKDF with distinct info strings.
    /// Returns false (and null outputs) if the peer key is malformed.</summary>
    public bool DeriveSession(ECDiffieHellman ours, ReadOnlySpan<byte> peerX963,
                              out AesGcm? cipher, out byte[] feedbackKey)
    {
        cipher = null;
        feedbackKey = Array.Empty<byte>();
        if (peerX963.Length != 65 || peerX963[0] != 0x04) return false;
        try
        {
            var peerParams = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = peerX963.Slice(1, 32).ToArray(), Y = peerX963.Slice(33, 32).ToArray() },
            };
            using var peer = ECDiffieHellman.Create(peerParams);
            byte[] shared = ours.DeriveRawSecretAgreement(peer.PublicKey);   // 32-byte X coordinate
            byte[] videoKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt: null, info: SessionInfo);
            feedbackKey     = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt: null, info: FeedbackInfo);
            CryptographicOperations.ZeroMemory(shared);
            cipher = new AesGcm(videoKey, tagSizeInBytes: 16);
            return true;
        }
        catch (CryptographicException) { return false; }   // invalid point (not on curve, etc.)
    }

    /// <summary>Constant-time check that <paramref name="tag"/> is HMAC-SHA256(feedbackKey, message).</summary>
    public static bool VerifyFeedbackTag(byte[] feedbackKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> tag)
    {
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(feedbackKey, message, expected);
        return CryptographicOperations.FixedTimeEquals(expected, tag);
    }

    // Copies a big-endian integer into a fixed 32-byte field, right-aligned (pads leading
    // zeros) — ExportParameters usually already returns 32 bytes, but guard against trimming.
    private static void CopyRightAligned(byte[] src, Span<byte> dst)
    {
        dst.Clear();
        if (src.Length <= dst.Length) src.CopyTo(dst[(dst.Length - src.Length)..]);
        else src.AsSpan(src.Length - dst.Length).CopyTo(dst);
    }
}
