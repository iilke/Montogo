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

/// <summary>
/// 16-byte HMAC-SHA256 token sent in HandshakeRequest.
/// Token = HMAC-SHA256(authKey, clientId_bytes)[0..15]
/// Stored as two little-endian uint64 fields — matches the LE byte order used by
/// MemoryMarshal.Read on both Windows (x86-64) and Mac (ARM).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HandshakeToken
{
    public ulong Part0; // bytes  0–7 of the truncated HMAC
    public ulong Part1; // bytes 8–15 of the truncated HMAC
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HeartbeatPacket
{
    public ushort Magic;
    public byte Version;
    public byte PacketType;      // 0x10
    public ulong TimestampUs;
    public uint SequenceNum;
}

/// <summary>
/// Mac → Windows link-quality report, sent a few times per second. Authenticated with
/// the same ClientId + HMAC token as the handshake so only the paired Mac can steer the
/// encoder. LossPermille is packet loss over the last interval in 0…1000 (‰).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FeedbackPacket
{
    public ushort Magic;
    public byte Version;
    public byte PacketType;      // 0x13
    public Guid ClientId;        // 16 bytes
    public HandshakeToken Token; // 16 bytes; HMAC-SHA256(authKey, clientId)[0..15]
    public ushort LossPermille;  // 0..1000 packet loss over the last window
    public byte Fps;             // rendered fps (diagnostics)
    public byte Flags;           // bit 0 = request keyframe (Mac saw a frameId gap)
}                                // total: 40 bytes

public static class FeedbackFlags
{
    public const byte RequestKeyframe = 1 << 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HandshakeRequestPacket
{
    public ushort Magic;
    public byte Version;
    public byte PacketType;      // 0x11
    public ushort ProtoVersion;
    public ushort Reserved;
    public Guid ClientId;        // 16 bytes; random UUID per session
    public HandshakeToken Token; // 16 bytes; HMAC-SHA256(authKey, clientId)[0..15]
}                                // total: 40 bytes

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HandshakeResponsePacket
{
    public ushort Magic;
    public byte Version;
    public byte PacketType;          // 0x12
    public ushort NegotiatedVersion;
    public ushort DisplayWidth;
    public ushort DisplayHeight;
    public byte TargetFps;
    public byte Reserved;
    public Guid ClientId;            // echo of request ClientId; 16 bytes
    public ulong NoncePrefix;        // UdpSender's per-session 8-byte GCM nonce prefix (LE)
}                                    // total: 36 bytes
