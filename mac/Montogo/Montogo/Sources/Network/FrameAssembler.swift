import Foundation
import QuartzCore

// Reassembles chunked H.264 frames.
// All methods are called from actor-isolated context (UDPReceiver).
final class FrameAssembler {
    struct PendingFrame {
        var chunks: [UInt16: Data]
        let total: UInt16
        let isIDR: Bool
        let deadline: Date
        let firstChunk: CFTimeInterval   // when this frame's first chunk arrived

        var isComplete: Bool { UInt16(chunks.count) == total }

        func reassemble() -> Data {
            var out = Data()
            for i in 0..<total { if let c = chunks[i] { out.append(c) } }
            return out
        }
    }

    private var pending: [UInt32: PendingFrame] = [:]

    // Returns (frameId, nalData, isIDR, trace) when the frame is complete, nil otherwise.
    // recvTime is when the chunk was drained from the socket, so the trace measures
    // network arrival rather than actor scheduling.
    func add(header: VideoChunkHeader, plaintext: Data, recvTime: CFTimeInterval) -> (UInt32, Data, Bool, FrameTrace)? {
        let id = header.frameId
        evictExpired()

        if pending[id] == nil {
            pending[id] = PendingFrame(chunks: [:], total: header.chunkTotal,
                                       isIDR: header.isIDR,
                                       deadline: Date().addingTimeInterval(MontogoProtocol.chunkDropTimeout),
                                       firstChunk: recvTime)
        }
        pending[id]!.chunks[header.chunkIndex] = plaintext

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

    private func evictExpired() {
        let now = Date()
        pending = pending.filter { $0.value.deadline > now }
    }
}
