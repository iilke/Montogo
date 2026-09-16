import Foundation
import QuartzCore

// Reassembles chunked H.264 frames, rebuilding chunks lost in transit from Reed–Solomon
// FEC parity.
//
// FEC scheme (must match the Windows UdpSender / ReedSolomon.cs): each frame carries
// `fecTotal` (= m) systematic RS parity packets over GF(256). Data chunks are sent
// unchanged; parity row j is a Cauchy linear combination of all data chunks over a
// fixed-width unit [2-byte LE length][payload][zero pad] of `unitSize` bytes. Because the
// code is MDS, ANY k of the (k + m) chunks recover the original k — so a frame survives up
// to m losses wherever they land (a keyframe included). A frame that arrives complete needs
// no decoding at all.
//
// All methods are called from actor-isolated context (UDPReceiver).
final class FrameAssembler {
    // Length-prefixed padded unit width; matches UdpSender.FecUnitSize.
    static let unitSize = MontogoProtocol.maxChunkPayload + 2

    struct PendingFrame {
        var chunks: [UInt16: Data]        // data chunks, key = chunkIndex 0..<total
        var parity: [UInt16: Data]        // parity units, key = row j 0..<fecTotal
        let total: UInt16                 // k = number of data chunks
        let fecTotal: UInt8               // m = number of parity packets
        let isIDR: Bool
        let deadline: Date
        let firstChunk: CFTimeInterval    // when this frame's first chunk arrived

        var isComplete: Bool { UInt16(chunks.count) >= total }

        func reassemble() -> Data {
            var out = Data()
            for i in 0..<total { if let c = chunks[i] { out.append(c) } }
            return out
        }
    }

    private var pending: [UInt32: PendingFrame] = [:]

    // Returns (frameId, nalData, isIDR, trace) when the frame is complete (natively or via
    // FEC recovery), nil otherwise. recvTime is when the chunk was drained from the socket.
    func add(header: VideoChunkHeader, plaintext: Data, recvTime: CFTimeInterval) -> (UInt32, Data, Bool, FrameTrace)? {
        let id = header.frameId
        evictExpired()

        if pending[id] == nil {
            pending[id] = PendingFrame(chunks: [:], parity: [:],
                                       total: header.chunkTotal, fecTotal: header.fecTotal,
                                       isIDR: header.isIDR,
                                       deadline: Date().addingTimeInterval(MontogoProtocol.chunkDropTimeout),
                                       firstChunk: recvTime)
        }

        if header.isParity {
            let g = header.chunkIndex - header.chunkTotal
            pending[id]!.parity[g] = plaintext
        } else {
            pending[id]!.chunks[header.chunkIndex] = plaintext
        }

        // If data isn't complete yet, try to rebuild the gaps from parity.
        if !pending[id]!.isComplete && pending[id]!.fecTotal > 0 {
            recover(id)
        }

        if pending[id]!.isComplete {
            let frame = pending.removeValue(forKey: id)!
            let data = frame.reassemble()
            // complete = arrival of the last chunk: stage 1 is pure network spread;
            // any queueing before decode is charged to the decode stage instead.
            var trace = FrameTrace(firstChunk: frame.firstChunk, complete: recvTime)
            trace.bytes  = data.count
            trace.chunks = Int(frame.total)
            return (id, data, frame.isIDR, trace)
        }
        return nil
    }

    // Reed–Solomon erasure decode. Solve only for the missing data units, using just enough
    // present parity rows. Any e present parity rows suffice (the Cauchy submatrix is always
    // invertible), so this recovers up to m losses anywhere in the frame.
    private func recover(_ id: UInt32) {
        guard var frame = pending[id], frame.fecTotal > 0 else { return }
        let k = Int(frame.total)
        let m = Int(frame.fecTotal)
        let W = FrameAssembler.unitSize

        // Erased (missing) data indices.
        var erasures: [Int] = []
        erasures.reserveCapacity(k)
        for i in 0..<k where frame.chunks[UInt16(i)] == nil { erasures.append(i) }
        let e = erasures.count
        if e == 0 { return }

        // Present parity rows — need at least e to solve for e unknowns.
        var presentParity: [Int] = []
        for j in 0..<m where frame.parity[UInt16(j)] != nil { presentParity.append(j) }
        if presentParity.count < e { return }
        let rows = Array(presentParity.prefix(e))

        // Right-hand side: S[a] = parity[rows[a]] XOR Σ (present data i) coeff(rows[a], i)·U_i
        var rhs = [[UInt8]](repeating: [], count: e)
        for a in 0..<e {
            guard let par = frame.parity[UInt16(rows[a])], par.count == W else { return }
            var s = [UInt8](par)
            for i in 0..<k {
                guard let c = frame.chunks[UInt16(i)] else { continue }   // present data only
                let coef = GF256.coeff(rows[a], i, m)
                if coef == 0 { continue }
                let len = c.count
                if len < 1 || len > MontogoProtocol.maxChunkPayload { continue }
                s[0] ^= GF256.mul(coef, UInt8(len & 0xFF))
                s[1] ^= GF256.mul(coef, UInt8((len >> 8) & 0xFF))
                c.withUnsafeBytes { raw in
                    let src = raw.bindMemory(to: UInt8.self)
                    for b in 0..<len { s[2 + b] ^= GF256.mul(coef, src[b]) }
                }
            }
            rhs[a] = s
        }

        // Coefficient matrix M[a][b] = coeff(rows[a], erasures[b]); invert over GF(256).
        var mtx = [[UInt8]](repeating: [UInt8](repeating: 0, count: e), count: e)
        for a in 0..<e {
            for b in 0..<e { mtx[a][b] = GF256.coeff(rows[a], erasures[b], m) }
        }
        guard let inv = GF256.invert(mtx) else { return }

        // X[b] = Σ_a inv[b][a]·S[a]  → the recovered unit for erasures[b].
        for b in 0..<e {
            var unit = [UInt8](repeating: 0, count: W)
            for a in 0..<e {
                let coef = inv[b][a]
                if coef == 0 { continue }
                let sa = rhs[a]
                for w in 0..<W { unit[w] ^= GF256.mul(coef, sa[w]) }
            }
            let len = Int(unit[0]) | (Int(unit[1]) << 8)
            guard len >= 1, len <= MontogoProtocol.maxChunkPayload, 2 + len <= W else { continue }
            frame.chunks[UInt16(erasures[b])] = Data(unit[2..<(2 + len)])
        }
        pending[id] = frame
    }

    private func evictExpired() {
        let now = Date()
        pending = pending.filter { $0.value.deadline > now }
    }
}

// GF(256) arithmetic for the RS decoder. Primitive polynomial 0x11D and Cauchy layout must
// stay byte-for-byte identical to ReedSolomon.cs on the Windows side.
enum GF256 {
    static let tables: (exp: [UInt8], log: [UInt8]) = {
        var exp = [UInt8](repeating: 0, count: 512)
        var log = [UInt8](repeating: 0, count: 256)
        var x = 1
        for i in 0..<255 {
            exp[i] = UInt8(x)
            log[x] = UInt8(i)
            x <<= 1
            if x >= 256 { x ^= 0x11D }
        }
        for i in 255..<512 { exp[i] = exp[i - 255] }
        return (exp, log)
    }()

    @inline(__always) static func mul(_ a: UInt8, _ b: UInt8) -> UInt8 {
        if a == 0 || b == 0 { return 0 }
        return tables.exp[Int(tables.log[Int(a)]) + Int(tables.log[Int(b)])]
    }

    @inline(__always) static func inv(_ a: UInt8) -> UInt8 {
        tables.exp[255 - Int(tables.log[Int(a)])]
    }

    // Cauchy coefficient: parity row j (x_j = j) over data column i (y_i = m + i).
    @inline(__always) static func coeff(_ j: Int, _ i: Int, _ m: Int) -> UInt8 {
        inv(UInt8((j ^ (m + i)) & 0xFF))
    }

    // Gauss–Jordan inversion over GF(256); nil if the matrix is singular.
    static func invert(_ mat: [[UInt8]]) -> [[UInt8]]? {
        let n = mat.count
        var a = mat
        var r = [[UInt8]](repeating: [UInt8](repeating: 0, count: n), count: n)
        for i in 0..<n { r[i][i] = 1 }
        for col in 0..<n {
            var piv = col
            while piv < n && a[piv][col] == 0 { piv += 1 }
            if piv == n { return nil }
            if piv != col { a.swapAt(piv, col); r.swapAt(piv, col) }
            let ip = inv(a[col][col])
            for x in 0..<n { a[col][x] = mul(a[col][x], ip); r[col][x] = mul(r[col][x], ip) }
            for row in 0..<n where row != col {
                let f = a[row][col]
                if f == 0 { continue }
                for x in 0..<n {
                    a[row][x] ^= mul(f, a[col][x])
                    r[row][x] ^= mul(f, r[col][x])
                }
            }
        }
        return r
    }
}
