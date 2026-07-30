import Foundation
import QuartzCore

// Per-frame timing trace threaded through the receive → decode → render pipeline.
// All timestamps are CACurrentMediaTime() — monotonic seconds since boot.
// A trace is created when the first chunk of a frame arrives and is filled in as
// the frame passes each stage boundary; the renderer stamps the final time and
// reports the four stage durations to FrameTimingLog.
struct FrameTrace {
    let firstChunk: CFTimeInterval    // first chunk of this frame received (reassembly start)
    var complete:   CFTimeInterval = 0 // all chunks reassembled (reassembly end)
    var submitted:  CFTimeInterval = 0 // handed to VTDecompressionSessionDecodeFrame (decode start)
    var decoded:    CFTimeInterval = 0 // VideoToolbox produced the pixel buffer (decode end / render start)
    var bytes:      Int = 0            // reassembled frame size, to gauge packets/frame and bitrate
    var chunks:     Int = 0            // number of UDP packets this frame was split into
}

// Thread-safe aggregator. Collects the four pipeline stages and prints a
// min / avg / max summary every `window` frames, then resets.
// Instrumentation only — set `enabled = false` to silence with near-zero cost.
final class FrameTimingLog {
    static let shared = FrameTimingLog()
    static var enabled = true

    private let lock = NSLock()
    private let window = 60
    private var reassembly: [Double] = []
    private var queue:      [Double] = []
    private var decode:     [Double] = []
    private var render:     [Double] = []
    private var total:      [Double] = []
    private var bytes:      [Int] = []
    private var chunks:     [Int] = []

    // Packet-loss tracking from the continuous per-chunk SequenceNum.
    private var seqMin: UInt32?
    private var seqMax: UInt32 = 0
    private var chunksSeen: Int = 0
    private var decryptFails: Int = 0
    private var lastFlush: CFTimeInterval = CACurrentMediaTime()

    // Decode accounting: how many IDR/P frames are submitted to VideoToolbox vs
    // how many produce a decoded picture, to catch P-frames that fail to decode.
    private var submitIDR = 0, submitP = 0, decodeErrors = 0

    func recordSubmit(isIDR: Bool) {
        guard FrameTimingLog.enabled else { return }
        lock.lock(); if isIDR { submitIDR += 1 } else { submitP += 1 }; lock.unlock()
    }
    func recordDecodeError(_ status: Int32) {
        guard FrameTimingLog.enabled else { return }
        lock.lock(); decodeErrors += 1; lock.unlock()
    }

    // Called for every video chunk that arrives (before frame reassembly), so loss
    // is measured at the packet level. `decrypted` is false when the GCM tag failed.
    func recordChunk(seq: UInt32, decrypted: Bool) {
        guard FrameTimingLog.enabled else { return }
        lock.lock()
        if seqMin == nil { seqMin = seq; seqMax = seq }
        else { if seq < seqMin! { seqMin = seq }; if seq > seqMax { seqMax = seq } }
        chunksSeen += 1
        if !decrypted { decryptFails += 1 }
        lock.unlock()
    }

    func record(reassembly r: Double, queue q: Double, decode d: Double, render rn: Double,
                total t: Double, bytes b: Int, chunks c: Int) {
        guard FrameTimingLog.enabled else { return }
        lock.lock()
        reassembly.append(r); queue.append(q); decode.append(d); render.append(rn); total.append(t)
        bytes.append(b); chunks.append(c)
        let ready = total.count >= window
        lock.unlock()
        if ready { flush() }
    }

    private func flush() {
        lock.lock()
        let r = reassembly, q = queue, d = decode, rn = render, t = total, b = bytes, c = chunks
        reassembly.removeAll(keepingCapacity: true)
        queue.removeAll(keepingCapacity: true)
        decode.removeAll(keepingCapacity: true)
        render.removeAll(keepingCapacity: true)
        total.removeAll(keepingCapacity: true)
        bytes.removeAll(keepingCapacity: true)
        chunks.removeAll(keepingCapacity: true)
        // Snapshot + reset packet-loss counters and the window clock.
        let sMin = seqMin, sMax = seqMax, seen = chunksSeen, dfail = decryptFails
        let subIDR = submitIDR, subP = submitP, decErr = decodeErrors
        seqMin = nil; seqMax = 0; chunksSeen = 0; decryptFails = 0
        submitIDR = 0; submitP = 0; decodeErrors = 0
        let now = CACurrentMediaTime()
        let elapsed = now - lastFlush
        lastFlush = now
        lock.unlock()

        func fmt(_ xs: [Double]) -> String {
            guard !xs.isEmpty else { return "n/a" }
            let mn = xs.min()! * 1000, mx = xs.max()! * 1000
            let avg = xs.reduce(0, +) / Double(xs.count) * 1000
            return String(format: "min %5.1f  avg %5.1f  max %5.1f ms", mn, avg, mx)
        }
        let avgKB     = b.isEmpty ? 0 : b.reduce(0, +) / b.count / 1024
        let maxKB     = (b.max() ?? 0) / 1024
        let avgChunks = c.isEmpty ? 0 : c.reduce(0, +) / c.count
        let maxChunks = c.max() ?? 0

        // Packet loss: over the window the sender's SequenceNum is contiguous, so the
        // expected count is (max - min + 1); anything missing was lost in transit.
        var lossStr = "n/a"
        if let sMin, sMax >= sMin {
            let expected = Int(sMax - sMin) + 1
            let lost = max(0, expected - seen)
            let pct = expected > 0 ? Double(lost) * 100.0 / Double(expected) : 0
            lossStr = String(format: "%.1f%% (%d lost / %d expected, %d received)", pct, lost, expected, seen)
        }
        // Rendered-frame rate: this window's frames over its wall-clock span.
        let fps = elapsed > 0 ? Double(t.count) / elapsed : 0

        print("""
        [FrameTiming] last \(t.count) frames  (rendered \(String(format: "%.1f", fps)) fps)
          1. reassembly (first → last chunk)     : \(fmt(r))
          2. queue      (last chunk → submit)    : \(fmt(q))
          3. decode     (submit → decoded)       : \(fmt(d))
          4. render     (decoded → on screen)    : \(fmt(rn))
          5. total end-to-end                    : \(fmt(t))
          frame size: avg \(avgKB) KB (max \(maxKB) KB), packets/frame avg \(avgChunks) (max \(maxChunks))
          packet loss: \(lossStr);  decrypt failures: \(dfail)
          decode submitted: IDR=\(subIDR) P=\(subP);  decode errors: \(decErr);  rendered: \(t.count)
        """)
    }
}
