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
    // HEVC parameter sets: VPS + SPS + PPS (H.264 had only SPS + PPS).
    private var currentVPS: Data?
    private var currentSPS: Data?
    private var currentPPS: Data?

    // Frame traces cannot ride through VideoToolbox as Swift values, so each sample
    // is tagged with a monotonic counter as its PTS and the trace is stored here;
    // the output callback recovers it by PTS. Guarded because decode() runs on the
    // caller's thread while the callback fires on a VideoToolbox thread.
    private let traceLock = NSLock()
    private var pendingTraces: [Int64: FrameTrace] = [:]
    private var frameCounter: Int64 = 0

    // Bytes of the previously submitted frame, for duplicate suppression. On a static
    // desktop Windows re-emits the same image, and the encoder produces byte-identical
    // skip-frames — which carry an identical HEVC picture-order-count (POC). Submitting
    // two frames with the same POC makes VideoToolbox reject the second with
    // kVTVideoDecoderBadDataErr (-12909). A byte-identical frame carries no new picture
    // information, so dropping it is lossless and avoids the collision. Real motion always
    // changes the POC, so genuine frames are never identical and never dropped.
    private var lastFrameBytes: Data?

    func decode(nalData: Data, isIDR: Bool, trace: FrameTrace? = nil) {
        FrameTimingLog.shared.recordSubmit(isIDR: isIDR)
        // Drop a frame byte-identical to the previous one (duplicate POC → -12909). See
        // lastFrameBytes. This includes IDRs: Windows emits a duplicate keyframe on a
        // static/blank desktop, and a second IDR with the same POC=0 poisons VideoToolbox's
        // reference state so every following P-frame fails with -12909 until the next IDR.
        // The first copy already built the session and decoded, so skipping the duplicate is
        // lossless.
        if let last = lastFrameBytes, last == nalData { return }
        lastFrameBytes = nalData
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
        var vps: Data?
        var sps: Data?
        var pps: Data?
        var hasVCL = false

        for nalu in nalus {
            switch hevcNalType(nalu) {
            case 32: vps = nalu
            case 33: sps = nalu
            case 34: pps = nalu
            case 0...31: hasVCL = true    // slice NAL (IDR keyframe slices are types 19/20)
            default: break
            }
        }

        if let vps, let sps, let pps, (currentVPS != vps || currentSPS != sps || currentPPS != pps) {
            currentVPS = vps
            currentSPS = sps
            currentPPS = pps
            rebuildSession(vps: vps, sps: sps, pps: pps)
        }
        // Decode the keyframe's slices; toAVCC strips VPS/SPS/PPS/AUD, keeping every VCL
        // slice — so multi-slice keyframes decode fully, not just the last slice.
        if hasVCL { decodeSample(data, trace: trace) }
    }

    private func rebuildSession(vps: Data, sps: Data, pps: Data) {
        if let s = session { VTDecompressionSessionInvalidate(s) }
        session = nil
        formatDesc = nil

        let status: OSStatus = vps.withUnsafeBytes { vpsRaw in
            sps.withUnsafeBytes { spsRaw in
                pps.withUnsafeBytes { ppsRaw in
                    var ptrs: [UnsafePointer<UInt8>] = [
                        vpsRaw.bindMemory(to: UInt8.self).baseAddress!,
                        spsRaw.bindMemory(to: UInt8.self).baseAddress!,
                        ppsRaw.bindMemory(to: UInt8.self).baseAddress!
                    ]
                    var sizes = [vps.count, sps.count, pps.count]
                    return CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                        allocator: nil,
                        parameterSetCount: 3,
                        parameterSetPointers: &ptrs,
                        parameterSetSizes: &sizes,
                        nalUnitHeaderLength: 4,
                        extensions: nil,
                        formatDescriptionOut: &formatDesc
                    )
                }
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

        // Give HEVC a real, monotonic DTS (== PTS, since the stream has no B-frames).
        // With decodeTimeStamp: .invalid VideoToolbox has no decode-order timeline, which
        // can make it reject inter frames (kVTVideoDecoderBadDataErr) even though the
        // reference IDR decoded fine.
        let ts = CMTime(value: token, timescale: 600)
        var timingInfo = CMSampleTimingInfo(
            duration: .invalid,
            presentationTimeStamp: ts,
            decodeTimeStamp: ts
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
        // Synchronous decode. Bufferbloat delivers frames in clumps; feeding those to an
        // ASYNCHRONOUS decoder back-to-back lets a P-frame reach the decoder before its
        // reference frame's decode has finished, so the reference isn't in the DPB yet and
        // the frame fails with kVTVideoDecoderBadDataErr (-12909) — which then cascades to
        // every following P-frame until the next keyframe (the multi-second freezes). A
        // synchronous call finishes each frame (and its reference) before the next is
        // submitted, so a clumped burst can't scramble reference order. HEVC decode is ~2.4 ms
        // here, well inside the 16 ms/frame budget at 60 fps.
        VTDecompressionSessionDecodeFrame(session, sampleBuffer: sb,
                                          flags: [],
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
            let type = hevcNalType(nal)
            // Drop parameter sets (they live in the format description) and the AUD;
            // keep the VCL slice NALs. VideoToolbox rejects a sample that carries these.
            if type == 32 || type == 33 || type == 34 || type == 35 { continue }
            var length = UInt32(nal.count).bigEndian
            out.append(Swift.withUnsafeBytes(of: &length) { Data($0) })
            out.append(nal)
        }
        return out
    }

    // HEVC NAL unit type = bits 1-6 of the first header byte (H.264 used bits 0-4).
    private func hevcNalType(_ nal: Data) -> UInt8 {
        guard let first = nal.first else { return 0xFF }
        return (first >> 1) & 0x3F
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
