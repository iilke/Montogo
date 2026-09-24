using System.Runtime.InteropServices;

namespace Montogo.Protocol;

// All structs use Pack=1 so their in-memory layout matches the wire format exactly.
// All multi-byte integers are little-endian (both Windows/x86-64 and Mac/ARM are LE).

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VideoChunkHeader
{
    public ushort Magic;         // 0x474D
    public byte Version;         // 2
    public byte PacketType;      // 0x01
    public uint SequenceNum;     // monotonic per-packet counter; doubles as GCM nonce base
    public uint FrameId;
    public ulong TimestampUs;    // capture time, µs since Unix epoch
    public ushort ChunkIndex;
    public ushort ChunkTotal;
    public ushort PayloadLength; // ciphertext + 16-byte GCM tag (i.e. plaintext_len + 16)
    public byte Flags;           // bit 0 = IDR frame
    public byte FecTotal;        // number of FEC parity packets for this frame (0 = none).
                                 // A packet is parity when ChunkIndex >= ChunkTotal;
                                 // its stripe g = ChunkIndex - ChunkTotal.
}

// Mac → Windows link-quality report (v8), sent a few times per second. Authenticated with a
// per-session key derived from the ECDH shared secret (HKDF info "montogo-feedback-v8") plus a
// monotonic counter, so it can be neither forged nor replayed by a LAN attacker who sniffs one.
// Layout, 60 bytes:
//   [0]magic(2) [2]ver(1) [3]type=0x13(1) [4]clientId(16) [20]counter(4) [24]lossPermille(2)
//   [26]fps(1) [27]flags(1) [28]authTag(32) = HMAC-SHA256(FeedbackKey, bytes[0..28])
public static class FeedbackLayout
{
    public const int ClientId     = 4;
    public const int Counter      = 20;
    public const int LossPermille = 24;
    public const int Fps          = 26;
    public const int Flags        = 27;
    public const int Signed       = 28;   // bytes covered by the HMAC
    public const int AuthTag      = Signed;
    public const int Size         = Signed + 32;   // 60
}

public static class FeedbackFlags
{
    public const byte RequestKeyframe = 1 << 0;
}

// Handshake wire layouts (v7). The 65-byte ephemeral P-256 public keys (X9.63:
// 0x04 || X(32) || Y(32)) make fixed structs awkward, so these are parsed/built by offset.
// Both messages end with a 32-byte HMAC-SHA256(authKey, all-preceding-bytes) that
// authenticates the whole message and binds the ephemeral key to the pairing secret.
//
// HandshakeRequest (Mac → Windows), 117 bytes:
//   [0]magic(2) [2]ver(1) [3]type=0x11(1) [4]clientId(16) [20]macPubKey(65) [85]authTag(32)
//
// HandshakeResponse (Windows → Mac), 131 bytes:
//   [0]magic(2) [2]ver(1) [3]type=0x12(1) [4]width(2) [6]height(2) [8]fps(1) [9]reserved(1)
//   [10]clientId(16) [26]winPubKey(65) [91]noncePrefix(8) [99]authTag(32)
public static class HandshakeLayout
{
    public const int PubKeyLen  = 65;   // P-256 X9.63 uncompressed point
    public const int AuthTagLen = 32;   // HMAC-SHA256

    // Request
    public const int ReqClientId  = 4;
    public const int ReqPubKey    = 20;
    public const int ReqSigned    = ReqPubKey + PubKeyLen;   // 85 bytes are HMAC-covered
    public const int ReqAuthTag   = ReqSigned;               // tag follows the signed region
    public const int ReqSize      = ReqSigned + AuthTagLen;  // 117

    // Response
    public const int RespWidth      = 4;
    public const int RespHeight     = 6;
    public const int RespFps        = 8;
    public const int RespReserved   = 9;
    public const int RespClientId   = 10;
    public const int RespPubKey     = 26;
    public const int RespNoncePrefix = RespPubKey + PubKeyLen; // 91
    public const int RespSigned     = RespNoncePrefix + 8;     // 99 bytes are HMAC-covered
    public const int RespAuthTag    = RespSigned;
    public const int RespSize       = RespSigned + AuthTagLen; // 131
}
