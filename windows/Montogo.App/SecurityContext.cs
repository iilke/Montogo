using System.Buffers.Binary;
using System.Security.Cryptography;
using Montogo.Protocol;

namespace Montogo.App;

/// <summary>
/// Derives and holds all cryptographic material for a Montogo session.
///
/// The shared secret is the 8-character connection code displayed in the tray
/// (e.g. "7X4K-9M2P"), which the user types into the Mac app once.
/// All keys are derived from its 5-byte representation via HKDF-SHA256.
///
/// Key material (40 bits of entropy from the code):
///   EncKey  = HKDF(ikm, info="montogo-enc-v1",  len=32)  → AES-256-GCM key
///   AuthKey = HKDF(ikm, info="montogo-auth-v1", len=32)  → HMAC-SHA256 key for token
///
/// AesGcm is NOT thread-safe; callers must ensure single-threaded use.
/// </summary>
internal sealed class SecurityContext : IDisposable
{
    // Base-32 alphabet: digits 2–9 + uppercase letters, omitting 0/O and 1/I to
    // avoid transcription errors. 32 symbols × 5 bits = 8 chars encode 40 bits.
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    // HKDF info strings — must be byte[] for the HKDF.DeriveKey(byte[]) overload.
    private static readonly byte[] EncInfo  = "montogo-enc-v1"u8.ToArray();
    private static readonly byte[] AuthInfo = "montogo-auth-v1"u8.ToArray();

    private readonly byte[] _authKey;
    private readonly AesGcm _aesGcm;

    public string ConnectionCode { get; }

    /// <summary>The AES-256-GCM cipher used by UdpSender to encrypt each chunk.</summary>
    public AesGcm Cipher => _aesGcm;

    public SecurityContext(string connectionCode)
    {
        ConnectionCode = connectionCode;

        byte[] ikm     = DecodeConnectionCode(connectionCode);
        byte[] encKey  = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt: null, info: EncInfo);
        _authKey       = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt: null, info: AuthInfo);
        _aesGcm        = new AesGcm(encKey, tagSizeInBytes: 16);
    }

    // ── Connection code ──────────────────────────────────────────────────────

    /// <summary>
    /// Encodes 5 bytes as an 8-character connection code (e.g. "7X4K-9M2P").
    /// Called by AppSettings when generating a fresh secret.
    /// </summary>
    public static string EncodeConnectionCode(byte[] ikm)
    {
        if (ikm.Length < 5) throw new ArgumentException("Need at least 5 bytes.", nameof(ikm));

        // Pack the first 5 bytes into a 40-bit integer then extract 8 × 5-bit groups.
        ulong bits = ((ulong)ikm[0] << 32) | ((ulong)ikm[1] << 24) |
                     ((ulong)ikm[2] << 16) | ((ulong)ikm[3] <<  8) | ikm[4];

        char[] chars = new char[8];
        for (int i = 7; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(bits & 0x1F)];
            bits >>= 5;
        }
        return $"{chars[0]}{chars[1]}{chars[2]}{chars[3]}-{chars[4]}{chars[5]}{chars[6]}{chars[7]}";
    }

    private static byte[] DecodeConnectionCode(string code)
    {
        ReadOnlySpan<char> chars = code.Replace("-", "").AsSpan();
        if (chars.Length != 8)
            throw new ArgumentException("Connection code must be 8 characters (e.g. 7X4K-9M2P).", nameof(code));

        ulong bits = 0;
        foreach (char c in chars)
        {
            int idx = Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (idx < 0)
                throw new ArgumentException($"Invalid character '{c}' in connection code.", nameof(code));
            bits = (bits << 5) | (uint)idx;
        }

        return [(byte)(bits >> 32), (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits];
    }

    // ── Handshake authentication ─────────────────────────────────────────────

    /// <summary>
    /// Returns true if the token in the received HandshakeRequest is valid.
    /// Uses a constant-time comparison to prevent timing side-channels.
    ///
    /// Expected token = HMAC-SHA256(authKey, clientId.ToByteArray())[0..15]
    /// </summary>
    public bool ValidateToken(Guid clientId, HandshakeToken token)
    {
        // Reconstruct the 16 bytes the Mac sent, respecting LE byte order
        // (matching how MemoryMarshal.Read placed them into the two ulong fields).
        Span<byte> received = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(received,       token.Part0);
        BinaryPrimitives.WriteUInt64LittleEndian(received[8..],  token.Part1);

        byte[] expected = HMACSHA256.HashData(_authKey, clientId.ToByteArray());
        return CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, 16), received);
    }

    public void Dispose() => _aesGcm.Dispose();
}
