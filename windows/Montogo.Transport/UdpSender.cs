using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
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

    private readonly UdpClient _client;
    private readonly bool      _ownsClient;
    private readonly AesGcm    _cipher;
    private readonly byte[]    _noncePrefix = RandomNumberGenerator.GetBytes(8);
    private IPEndPoint? _target;
    private uint        _sequenceNum;

    /// <param name="cipher">AES-GCM cipher shared with the handshake security context.</param>
    /// <param name="sendSocket">
    /// Optional existing UdpClient to reuse for sending.  When supplied the caller
    /// retains ownership and UdpSender will not dispose it.  Use this to send video
    /// from the same local port as the handshake listener so the Mac's stateful
    /// firewall treats video as a reply to the outbound handshake packet.
    /// </param>
    public UdpSender(AesGcm cipher, UdpClient? sendSocket = null)
    {
        _cipher     = cipher;
        _client     = sendSocket ?? new UdpClient();
        _ownsClient = sendSocket is null;
    }

    /// <summary>
    /// The 8-byte per-session nonce prefix as a little-endian uint64, ready for
    /// insertion into HandshakeResponsePacket.NoncePrefix.
    /// </summary>
    public ulong NoncePrefix => BinaryPrimitives.ReadUInt64LittleEndian(_noncePrefix);

    public IPEndPoint? Target => _target;

    public void SetTarget(IPEndPoint endpoint) => _target = endpoint;

    /// <summary>True once the per-chunk SequenceNum counter (the GCM nonce base) is close to
    /// wrapping. A wrap would repeat a (key, nonce) pair, which is catastrophic for GCM, so the
    /// caller tears the session down and re-handshakes for a fresh nonce prefix before then.
    /// ~0.27 billion packets of margin remain at this point (hours, even at a high packet rate).</summary>
    public bool NonceSpaceNearlyExhausted => _sequenceNum > 0xF000_0000u;

    // --- Forward error correction (Reed–Solomon over GF(256)) ------------------
    // Every frame ships with m parity packets so the Mac can rebuild chunks the Wi-Fi
    // drops, with no retransmission round-trip. The code is systematic: data chunks go out
    // unchanged, and parity row j is a Cauchy GF(256) linear combination of ALL data
    // chunks, computed over a fixed-width unit [2-byte LE length][plaintext][zero pad] so a
    // rebuilt chunk knows its own length. Because it is MDS, ANY k of the (k + m) chunks
    // recover the original k — so a frame survives up to m losses no matter where they land
    // (a keyframe included), which interleaved XOR could not do at high loss.
    //
    // Overhead is ADAPTIVE: the controller raises _fecPercent as the Mac reports more loss
    // (SetFecPercent), and IDR frames get an extra margin since losing a keyframe costs
    // ~1 s of corruption. FecMaxParity caps the cost; k + m ≤ 255 is a hard GF(256) limit.
    private volatile int _fecPercent = 25;      // adaptive; set from the feedback controller
    private const int    FecMaxParity = 48;
    private const int    FecIdrBonus  = 30;      // unequal error protection: harden keyframes
    private static readonly int FecUnitSize = ProtocolConstants.MaxChunkPayload + 2;

    /// <summary>Set the FEC overhead (percent of data chunks) — the adaptive controller
    /// raises this under loss and lowers it when the link is clean.</summary>
    public void SetFecPercent(int percent) => _fecPercent = Math.Clamp(percent, 10, 100);

    private int FecParityCount(int chunkCount, bool isIdr)
    {
        if (chunkCount <= 0) return 0;
        int pct = _fecPercent + (isIdr ? FecIdrBonus : 0);
        int r = (chunkCount * pct + 99) / 100; // ceil
        if (r < 1) r = 1;
        if (r > FecMaxParity) r = FecMaxParity;
        if (r > chunkCount)   r = chunkCount;
        if (r > 255 - chunkCount) r = Math.Max(0, 255 - chunkCount); // RS: k + m ≤ 255
        return r;
    }

    // --- Send pacing -----------------------------------------------------------
    // A large frame (keyframe + its parity ≈ hundreds of packets) handed to the socket at
    // once bursts past the Wi-Fi buffer — the dominant loss source, and FEC made the burst
    // bigger. Spreading such a frame's packets at a steady byte rate keeps the buffer
    // shallow so it stops overflowing; FEC then covers the residual random loss.
    //
    // This runs on the dedicated send loop (TrayApplicationContext.SendLoopAsync), NOT the
    // encoder pump — so unlike the earlier attempt it cannot stall the encoder. Frames that
    // pile up during a paced burst simply wait in the send channel and drain right after,
    // so there are no dropped frames and no broken references. Small frames send at once
    // (no added latency). Sub-ms gaps need a spin wait (Windows timer resolution is ~15 ms).
    private const long PacingBytesPerSecond = 10_000_000; // ~80 Mbps
    private const int  PacingChunkThreshold = 96;         // only frames larger than this are paced
    private static readonly double PacingTicksPerByte =
        (double)Stopwatch.Frequency / PacingBytesPerSecond;

    private static void PaceGate(Stopwatch? pacer, long pacedBytes)
    {
        if (pacer is null) return;
        long dueTicks = (long)(pacedBytes * PacingTicksPerByte);
        while (pacer.ElapsedTicks < dueTicks) Thread.SpinWait(8);
    }

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

        int  chunkCount = (totalLen + ProtocolConstants.MaxChunkPayload - 1) / ProtocolConstants.MaxChunkPayload;
        int  fecCount   = FecParityCount(chunkCount, isIdr);
        byte flags      = isIdr ? (byte)1 : (byte)0;

        // Pace only large frames (keyframe + parity); small frames burst fine.
        Stopwatch? pacer      = (chunkCount + fecCount) > PacingChunkThreshold ? Stopwatch.StartNew() : null;
        long       pacedBytes = 0;

        // One rented buffer covers the header + the largest payload (a parity unit) + tag.
        int bufSize = HeaderSize + FecUnitSize + ProtocolConstants.GcmTagSize;
        byte[] buf  = ArrayPool<byte>.Shared.Rent(bufSize);

        // Parity accumulators: fecCount stripes × FecUnitSize plaintext bytes, XORed as we go.
        byte[]? parity = fecCount > 0 ? ArrayPool<byte>.Shared.Rent(fecCount * FecUnitSize) : null;
        if (parity is not null) Array.Clear(parity, 0, fecCount * FecUnitSize);

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
                    FecTotal      = (byte)fecCount,
                };

                // Span usage + parity XOR live in a synchronous helper (C# 12 forbids
                // Span locals across await in this async method).
                int sendLen = BuildDataChunk(buf, nonce, in header, encodedFrame, offset, chunkLen,
                                             parity, fecCount, i);

                PaceGate(pacer, pacedBytes);
                await _client.SendAsync(buf.AsMemory(0, sendLen), _target, ct);
                pacedBytes += sendLen;
            }

            // Parity packets follow the data. ChunkIndex = chunkCount + g marks stripe g.
            for (int g = 0; g < fecCount; g++)
            {
                var header = new VideoChunkHeader
                {
                    Magic         = ProtocolConstants.Magic,
                    Version       = ProtocolConstants.CurrentVersion,
                    PacketType    = (byte)PacketType.VideoChunk,
                    SequenceNum   = _sequenceNum++,
                    FrameId       = frameId,
                    TimestampUs   = timestampUs,
                    ChunkIndex    = (ushort)(chunkCount + g),
                    ChunkTotal    = (ushort)chunkCount,
                    PayloadLength = (ushort)(FecUnitSize + ProtocolConstants.GcmTagSize),
                    Flags         = flags,
                    FecTotal      = (byte)fecCount,
                };

                int sendLen = BuildParityChunk(buf, nonce, in header, parity!, g * FecUnitSize);

                PaceGate(pacer, pacedBytes);
                await _client.SendAsync(buf.AsMemory(0, sendLen), _target, ct);
                pacedBytes += sendLen;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            if (parity is not null) ArrayPool<byte>.Shared.Return(parity);
        }
    }

    // Synchronous: writes+encrypts a data chunk into buf, and folds its length-prefixed
    // padded unit into parity stripe (chunkIndex % fecCount). Returns the packet length.
    private int BuildDataChunk(
        byte[] buf, byte[] nonce, in VideoChunkHeader header,
        ReadOnlyMemory<byte> frame, int offset, int chunkLen,
        byte[]? parity, int fecCount, int chunkIndex)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), header.SequenceNum);
        MemoryMarshal.Write(buf, in header);

        ReadOnlySpan<byte> plaintext = frame.Span.Slice(offset, chunkLen);
        // Authenticate the plaintext header (frameId, chunk indices, flags, lengths) as GCM
        // associated data so an on-path attacker can't tamper with routing/reassembly fields
        // without failing the tag. The header bytes are already serialized into buf[0..Header).
        _cipher.Encrypt(
            nonce,
            plaintext,
            buf.AsSpan(HeaderSize, chunkLen),
            buf.AsSpan(HeaderSize + chunkLen, ProtocolConstants.GcmTagSize),
            buf.AsSpan(0, HeaderSize));

        if (parity is not null)
        {
            // Fold this data chunk's length-prefixed unit into every parity row, each scaled
            // by its Cauchy coefficient (RS systematic encode). Padding bytes are 0 and
            // contribute nothing, so only the length prefix + payload are folded.
            byte lenLo = (byte)(chunkLen & 0xFF);
            byte lenHi = (byte)((chunkLen >> 8) & 0xFF);
            for (int j = 0; j < fecCount; j++)
            {
                byte c = ReedSolomon.Coeff(j, chunkIndex, fecCount);
                if (c == 0) continue;
                int p = j * FecUnitSize;
                parity[p]     ^= ReedSolomon.Mul(c, lenLo);
                parity[p + 1] ^= ReedSolomon.Mul(c, lenHi);
                Span<byte> row = parity.AsSpan(p + 2, chunkLen);
                for (int b = 0; b < chunkLen; b++) row[b] ^= ReedSolomon.Mul(c, plaintext[b]);
            }
        }

        return HeaderSize + chunkLen + ProtocolConstants.GcmTagSize;
    }

    // Synchronous: writes+encrypts one full parity stripe (FecUnitSize plaintext) into buf.
    private int BuildParityChunk(
        byte[] buf, byte[] nonce, in VideoChunkHeader header, byte[] parity, int stripeOffset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), header.SequenceNum);
        MemoryMarshal.Write(buf, in header);

        // Header authenticated as GCM associated data — see BuildDataChunk.
        _cipher.Encrypt(
            nonce,
            parity.AsSpan(stripeOffset, FecUnitSize),
            buf.AsSpan(HeaderSize, FecUnitSize),
            buf.AsSpan(HeaderSize + FecUnitSize, ProtocolConstants.GcmTagSize),
            buf.AsSpan(0, HeaderSize));

        return HeaderSize + FecUnitSize + ProtocolConstants.GcmTagSize;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
        _cipher.Dispose();   // per-session AES-GCM key from the ECDH handshake
    }
}
