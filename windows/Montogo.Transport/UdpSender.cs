using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Montogo.Protocol;

namespace Montogo.Transport;

/// <summary>
/// Chunks encoded H.264 frames, encrypts each chunk with AES-256-GCM, and sends
/// them to the Mac over UDP.  Each chunk is at most ProtocolConstants.MaxChunkPayload
/// bytes of plaintext; the wire payload is plaintext_len + 16 (the GCM tag).
///
/// Nonce construction (12 bytes):
///   [0..7]  = NoncePrefix  — 8 random bytes generated once per UdpSender instance
///   [8..11] = SequenceNum  — 4-byte LE counter, unique per chunk within a session
///
/// The prefix eliminates cross-session nonce reuse: each app run produces a fresh
/// UdpSender with a fresh prefix, so the same EncKey never sees nonce 0 twice.
/// The prefix is sent to the Mac in HandshakeResponsePacket.NoncePrefix.
///
/// AesGcm is NOT thread-safe; this class must be used from a single thread/task.
/// </summary>
public sealed class UdpSender : IDisposable
{
    private static readonly int HeaderSize = Marshal.SizeOf<VideoChunkHeader>();

    private readonly UdpClient _client      = new();
    private readonly AesGcm    _cipher;
    private readonly byte[]    _noncePrefix = RandomNumberGenerator.GetBytes(8);
    private IPEndPoint? _target;
    private uint        _sequenceNum;

    public UdpSender(AesGcm cipher) => _cipher = cipher;

    /// <summary>
    /// The 8-byte per-session nonce prefix as a little-endian uint64, ready for
    /// insertion into HandshakeResponsePacket.NoncePrefix.
    /// </summary>
    public ulong NoncePrefix => BinaryPrimitives.ReadUInt64LittleEndian(_noncePrefix);

    public IPEndPoint? Target => _target;

    public void SetTarget(IPEndPoint endpoint) => _target = endpoint;

    public async ValueTask SendFrameAsync(
        ReadOnlyMemory<byte> encodedFrame,
        uint   frameId,
        ulong  timestampUs,
        bool   isIdr,
        CancellationToken ct = default)
    {
        if (_target is null) throw new InvalidOperationException("Target endpoint not set.");

        int totalLen = encodedFrame.Length;
        if (totalLen == 0) return;

        int chunkCount = (totalLen + ProtocolConstants.MaxChunkPayload - 1) / ProtocolConstants.MaxChunkPayload;
        byte flags = isIdr ? (byte)1 : (byte)0;

        // One rented buffer covers the header + max ciphertext + GCM tag for any chunk.
        int bufSize = HeaderSize + ProtocolConstants.MaxChunkPayload + ProtocolConstants.GcmTagSize;
        byte[] buf  = ArrayPool<byte>.Shared.Rent(bufSize);

        // Nonce layout: [0..7] = session prefix (set once here), [8..11] = SequenceNum (per-chunk).
        byte[] nonce = new byte[ProtocolConstants.GcmNonceSize];
        _noncePrefix.CopyTo(nonce, 0);

        try
        {
            for (int i = 0; i < chunkCount; i++)
            {
                int offset   = i * ProtocolConstants.MaxChunkPayload;
                int chunkLen = Math.Min(ProtocolConstants.MaxChunkPayload, totalLen - offset);

                var header = new VideoChunkHeader
                {
                    Magic         = ProtocolConstants.Magic,
                    Version       = ProtocolConstants.CurrentVersion,
                    PacketType    = (byte)PacketType.VideoChunk,
                    SequenceNum   = _sequenceNum++,
                    FrameId       = frameId,
                    TimestampUs   = timestampUs,
                    ChunkIndex    = (ushort)i,
                    ChunkTotal    = (ushort)chunkCount,
                    PayloadLength = (ushort)(chunkLen + ProtocolConstants.GcmTagSize),
                    Flags         = flags,
                };

                // Span usage lives in a synchronous helper (C# 12 restriction on async methods).
                int sendLen = BuildAndEncrypt(buf, nonce, in header, encodedFrame, offset, chunkLen);

                await _client.SendAsync(buf.AsMemory(0, sendLen), _target, ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // Synchronous: updates nonce[8..11], writes header, encrypts chunk into buf.
    private int BuildAndEncrypt(
        byte[] buf, byte[] nonce, in VideoChunkHeader header,
        ReadOnlyMemory<byte> frame, int offset, int chunkLen)
    {
        // nonce[0..7] = prefix already set; only the SequenceNum part changes per chunk.
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), header.SequenceNum);

        MemoryMarshal.Write(buf, in header);

        _cipher.Encrypt(
            nonce,
            frame.Slice(offset, chunkLen).Span,
            buf.AsSpan(HeaderSize, chunkLen),
            buf.AsSpan(HeaderSize + chunkLen, ProtocolConstants.GcmTagSize));

        return HeaderSize + chunkLen + ProtocolConstants.GcmTagSize;
    }

    public void Dispose() => _client.Dispose();
}
