namespace Montogo.Transport;

/// <summary>
/// Reed–Solomon erasure coding over GF(256) (primitive polynomial 0x11D), used to build
/// the video FEC parity. The code is <b>systematic</b>: data chunks are transmitted
/// unchanged, so a frame that arrives complete needs no decoding at all. Parity row j is a
/// GF(256) linear combination of the data chunks using a Cauchy generator matrix, so ANY
/// k of the (k + m) chunks recover the original k — unlike interleaved XOR, this survives
/// many losses per frame (a keyframe included) as long as k of them arrive.
///
/// Constraint: k + m ≤ 255. This math must stay byte-for-byte identical to the Swift
/// decoder in FrameAssembler.swift (same primitive poly, same Cauchy layout).
/// </summary>
internal static class ReedSolomon
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static ReedSolomon()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256) x ^= 0x11D;
        }
        // Duplicate so Log[a] + Log[b] (≤ 508) never needs a modulo.
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    public static byte Mul(byte a, byte b)
        => (a == 0 || b == 0) ? (byte)0 : Exp[Log[a] + Log[b]];

    public static byte Inv(byte a) => Exp[255 - Log[a]];   // requires a != 0

    /// <summary>
    /// Cauchy generator coefficient for parity row j (x_j = j) over data column i
    /// (y_i = m + i). The x and y sets are disjoint, so x_j ^ y_i is never 0 and every
    /// square submatrix is invertible (the MDS property that makes any-k-of-n recovery work).
    /// </summary>
    public static byte Coeff(int j, int i, int m) => Inv((byte)(j ^ (m + i)));
}
