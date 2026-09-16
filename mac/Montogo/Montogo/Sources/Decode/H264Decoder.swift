import Foundation
import VideoToolbox
import CoreMedia
import QuartzCore

// File-scope C-compatible callback — closures with captures are not allowed here.
private func vtOutputCallback(
    refcon: UnsafeMutableRawPointer?,
    sourceFrameRefcon: UnsafeMutableRawPointer?,
    status: OSStatus,
    infoFlags: VTDecodeInfoFlags,
    imageBuffer: CVImageBuffer?,
    pts: CMTime,
    duration: CMTime
) {
    guard let refcon else { return }
    let decoder = Unmanaged<H264Decoder>.fromOpaque(refcon).takeUnretainedValue()
    if status != noErr || imageBuffer == nil {
        FrameTimingLog.shared.recordDecodeError(status)
        return
    }
    decoder.handleDecoded(imageBuffer! as CVPixelBuffer, pts: pts)
}

// VideoToolbox H.264 decoder. Call only from a single thread/task.
final class H264Decoder {
    var onFrame: ((CVPixelBuffer, FrameTrace?) -> Void)?

    private var session: VTDecompressionSession?
    private var formatDesc: CMVideoFormatDescription?
    private var currentSPS: Data?
    private var currentPPS: Data?

    // Frame traces cannot ride through VideoToolbox as Swift values, so each sample
    // is tagged with a monotonic counter as its PTS and the trace is stored here;
    // the output callback recovers it by PTS. Guarded because decode() runs on the
    // caller's thread while the callback fires on a VideoToolbox thread.
    private let traceLock = NSLock()
    private var pendingTraces: [Int64: FrameTrace] = [:]
    private var frameCounter: Int64 = 0

    func decode(nalData: Data, isIDR: Bool, trace: FrameTrace? = nil) {
        FrameTimingLog.shared.recordSubmit(isIDR: isIDR)
        if isIDR {
            processIDRBuffer(nalData, trace: trace)
        } else {
            guard session != nil else { return }
            decodeSample(nalData, trace: trace)
        }
    }

    // Called from the VideoToolbox output callback. Recovers the trace, stamps the
    // decode-complete time, and forwards the frame with its timing.
    fileprivate func handleDecoded(_ buffer: CVPixelBuffer, pts: CMTime) {
        traceLock.lock()
        var trace = pendingTraces.removeValue(forKey: pts.value)
        traceLock.unlock()
        trace?.decoded = CACurrentMediaTime()
        onFrame?(buffer, trace)
    }

    private func processIDRBuffer(_ data: Data, trace: FrameTrace?) {
        let nalus = splitAnnexB(data)
        var sps: Data?
        var pps: Data?
        var idr: Data?

        for nalu in nalus {
            guard let first = nalu.first else { continue }
            switch first & 0x1F {
            case 7: sps = nalu
            case 8: pps = nalu
            case 5: idr = nalu
            default: break
            }
        }

        if let sps, let pps, (currentSPS != sps || currentPPS != pps) {
            currentSPS = sps
            currentPPS = pps
            rebuildSession(sps: sps, pps: pps)
        }
        if let idr { decodeSample(idr, trace: trace) }
    }

    private func rebuildSession(sps: Data, pps: Data) {
        if let s = session { VTDecompressionSessionInvalidate(s) }
        session = nil
        formatDesc = nil

        let status: OSStatus = sps.withUnsafeBytes { spsRaw in
            pps.withUnsafeBytes { ppsRaw in
                var ptrs: [UnsafePointer<UInt8>] = [
                    spsRaw.bindMemory(to: UInt8.self).baseAddress!,
                    ppsRaw.bindMemory(to: UInt8.self).baseAddress!
                ]
                var sizes = [sps.count, pps.count]
                return CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    allocator: nil,
                    parameterSetCount: 2,
                    parameterSetPointers: &ptrs,
                    parameterSetSizes: &sizes,
                    nalUnitHeaderLength: 4,
                    formatDescriptionOut: &formatDesc
                )
            }
        }
        guard status == noErr, let desc = formatDesc else { return }

        let attrs: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            kCVPixelBufferMetalCompatibilityKey: true
        ]

        var outCallback = VTDecompressionOutputCallbackRecord(
            decompressionOutputCallback: vtOutputCallback,
            decompressionOutputRefCon: Unmanaged.passUnretained(self).toOpaque()
        )

        // Prefer hardware decode. Enable (not Require) so Macs without a hardware
        // H.264 decoder still fall back to software rather than failing to create
        // the session — RequireHardware would make CreateSession return an error.
        let spec: [CFString: Any] = [
            kVTVideoDecoderSpecification_EnableHardwareAcceleratedVideoDecoder: true
        ]

        var s: VTDecompressionSession?
        let createStatus = VTDecompressionSessionCreate(
            allocator: nil,
            formatDescription: desc,
            decoderSpecification: spec as CFDictionary,
            imageBufferAttributes: attrs as CFDictionary,
            outputCallback: &outCallback,
            decompressionSessionOut: &s
        )
        self.session = s

        if let s {
            // Hint the decoder that this is a live stream so it prioritizes
            // latency over throughput batching.
            VTSessionSetProperty(s, key: kVTDecompressionPropertyKey_RealTime,
                                 value: kCFBooleanTrue)
            logHardwareDecodeState(session: s)
        } else {
            print("[H264Decoder] VTDecompressionSessionCreate failed: status \(createStatus)")
        }
    }

    // Reports whether VideoToolbox actually chose a hardware decoder for this
    // session. Printed once per session (re)build, i.e. on the first IDR and
    // whenever SPS/PPS change.
    private func logHardwareDecodeState(session: VTDecompressionSession) {
        var value: CFTypeRef?
        let status = VTSessionCopyProperty(
            session,
            key: kVTDecompressionPropertyKey_UsingHardwareAcceleratedVideoDecoder,
            allocator: kCFAllocatorDefault,
            valueOut: &value)

        if status == noErr, let cf = value, CFGetTypeID(cf) == CFBooleanGetTypeID() {
            let usingHardware = CFBooleanGetValue((cf as! CFBoolean))
            print("[H264Decoder] hardware-accelerated decode active: \(usingHardware)")
        } else {
            print("[H264Decoder] hardware-decode state unknown (VTSessionCopyProperty status \(status))")
        }
    }

    private func decodeSample(_ naluData: Data, trace: FrameTrace?) {
        guard let session, let desc = formatDesc else { return }

        let avcc = toAVCC(naluData)
        guard let blockBuf = makeBlockBuffer(avcc) else { return }

        // Tag the sample with a monotonic counter as its PTS so the output callback
        // can recover this frame's trace. The value is for correlation only; the
        // renderer ignores PTS for actual presentation timing.
        let token = frameCounter
        frameCounter &+= 1
        if var trace {
            // Stamp the real decode-submit time: everything between frame-complete
            // and here (actor decrypt/assemble + decode-queue wait) is queuing, not
            // decoding, and is charged to the separate "queue" stage.
            trace.submitted = CACurrentMediaTime()
            traceLock.lock()
            pendingTraces[token] = trace
            // Bound the map in case a decode never calls back (e.g. decode error).
            if pendingTraces.count > 120 {
                for key in pendingTraces.keys.sorted().prefix(pendingTraces.count - 120) {
                    pendingTraces.removeValue(forKey: key)
                }
            }
            traceLock.unlock()
        }

        var timingInfo = CMSampleTimingInfo(
            duration: .invalid,
            presentationTimeStamp: CMTime(value: token, timescale: 600),
            decodeTimeStamp: .invalid
        )
        var sampleBuf: CMSampleBuffer?
        CMSampleBufferCreateReady(
            allocator: nil,
            dataBuffer: blockBuf,
            formatDescription: desc,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timingInfo,
            sampleSizeEntryCount: 0,
            sampleSizeArray: nil,
            sampleBufferOut: &sampleBuf
        )
        guard let sb = sampleBuf else { return }
        // Asynchronous decompression lets the hardware decoder pipeline frames.
        // A synchronous call (flags: []) blocks the decode queue for the full
        // hardware round trip per frame, which showed up as a fixed ~2-frame
        // (~33 ms) latency floor in the frame timing. Without
        // ._EnableTemporalProcessing callbacks still fire in decode order.
        VTDecompressionSessionDecodeFrame(session, sampleBuffer: sb,
                                          flags: [._EnableAsynchronousDecompression],
                                          frameRefcon: nil, infoFlagsOut: nil)
    }

    // MARK: - Helpers

    private func splitAnnexB(_ data: Data) -> [Data] {
        var nalus: [Data] = []
        var start = data.startIndex
        let bytes = [UInt8](data)
        var i = 0
        while i < bytes.count {
            if i + 3 < bytes.count && bytes[i] == 0 && bytes[i+1] == 0 {
                let is4byte = i + 3 < bytes.count && bytes[i+2] == 0 && bytes[i+3] == 1
                let is3byte = !is4byte && bytes[i+2] == 1
                if is4byte || is3byte {
                    let end = data.startIndex + i
                    if end > start { nalus.append(data[start..<end]) }
                    let skip = is4byte ? 4 : 3
                    start = data.startIndex + i + skip
                    i += skip
                    continue
                }
            }
            i += 1
        }
        if start < data.endIndex { nalus.append(data[start...]) }
        return nalus.filter { !$0.isEmpty }
    }

    // Convert an Annex B access unit to AVCC: every NAL becomes [4-byte BE length][data].
    // P-frames arrive as a multi-NAL access unit (often a leading AUD + the slice), so
    // treating the whole thing as ONE NAL — as the old single-NAL version did — produced
    // a malformed sample that VideoToolbox rejected, dropping every P-frame. The IDR path
    // passes a single already-split slice NAL, which this still handles correctly.
    private func toAVCC(_ data: Data) -> Data {
        let nalus = splitAnnexB(data)
        let list  = nalus.isEmpty ? [data] : nalus
        var out = Data()
        for nal in list where !nal.isEmpty {
            let type = (nal.first ?? 0) & 0x1F
            if type == 9 { continue }   // drop AUD — VideoToolbox rejects it inside AVCC
            var length = UInt32(nal.count).bigEndian
            out.append(Swift.withUnsafeBytes(of: &length) { Data($0) })
            out.append(nal)
        }
        return out
    }

    private func makeBlockBuffer(_ data: Data) -> CMBlockBuffer? {
        let count = data.count
        // Allocate a block buffer that OWNS its memory (memoryBlock nil + AssureMemoryNow)
        // and copy the AVCC bytes into it. The previous version referenced the caller's
        // Data with kCFAllocatorNull (no copy); with asynchronous decode the Data is
        // freed before VideoToolbox reads it, so it decoded freed/recycled memory —
        // producing on-screen corruption with no packet loss and no decode error.
        var blockBuf: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: nil,
            blockLength: count,
            blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: count,
            flags: kCMBlockBufferAssureMemoryNowFlag,
            blockBufferOut: &blockBuf) == noErr,
            let blockBuf
        else { return nil }

        let status = data.withUnsafeBytes { raw -> OSStatus in
            guard let base = raw.baseAddress else { return -1 }
            return CMBlockBufferReplaceDataBytes(
                with: base, blockBuffer: blockBuf, offsetIntoDestination: 0, dataLength: count)
        }
        return status == noErr ? blockBuf : nil
    }

    deinit {
        if let s = session { VTDecompressionSessionInvalidate(s) }
    }
}
