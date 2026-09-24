import Foundation
import Darwin
import QuartzCore
import CryptoKit

enum ConnectionState: Equatable {
    case idle
    case connecting
    case connected(fps: UInt8, width: UInt16, height: UInt16)
    case lost
}

// Single POSIX UDP socket bound to port 47921.
// All traffic (HandshakeResponse + video from any Windows source port) arrives
// on the same socket, so there is no NWListener / NWConnection split.
actor UDPReceiver {
    private let authKey: SymmetricKey
    private let clientId: UUID
    // nil until the handshake completes: the content key is established per session by ECDH,
    // not derived from the code, so there is no decryptor before the response arrives.
    private var decryptor: StreamDecryptor?
    // Ephemeral P-256 key for this handshake cycle; regenerated each beginHandshaking().
    private var ephemeralKey: P256.KeyAgreement.PrivateKey?
    // Per-session feedback authentication key (from the ECDH secret) + a monotonic counter,
    // so Windows can reject forged/replayed feedback. Set on a successful handshake.
    private var feedbackKey: SymmetricKey?
    private var feedbackCounter: UInt32 = 0
    private let assembler = FrameAssembler()

    private var fd: Int32 = -1
    private var readSource: DispatchSourceRead?
    private var pumpTask: Task<Void, Never>?
    private var datagramFeed: AsyncStream<(batch: [Data], recvTime: CFTimeInterval)>.Continuation?
    private var windowsHost: String = ""
    private var lastPacketTime: Date = .distantPast
    private var heartbeatSeq: UInt32 = 0
    private var handshakeTimer: Task<Void, Never>?
    private var heartbeatTask: Task<Void, Never>?
    private var watchdogTask: Task<Void, Never>?
    private var feedbackTask: Task<Void, Never>?

    // Rolling packet-loss accounting for the feedback report, reset each interval.
    private var fbSeqMin: UInt32?
    private var fbSeqMax: UInt32 = 0
    private var fbCount: Int = 0

    // Frame-ordering gate for fast gap recovery. Frames form an IPPP chain, so a missing
    // frameId (lost in transit or dropped in Windows's send queue) breaks the reference
    // chain: the next P-frame and everything until the following keyframe fail to decode.
    // On a gap we stop feeding the decoder broken frames and ask Windows for a keyframe
    // immediately (out-of-band + a flag on the periodic feedback), which re-syncs in ~1 RTT
    // instead of freezing until the next periodic keyframe.
    private var expectedFrameId: UInt32?
    private var awaitingKeyframe = false
    private var lastKeyframeRequest: Date = .distantPast

    var onStateChange: ((ConnectionState) -> Void)?
    var onFrame: ((UInt32, Data, Bool, FrameTrace) -> Void)?

    init(keys: DerivedKeys) {
        self.authKey = keys.authKey
        self.clientId = UUID()
    }

    func configure(
        onStateChange: @escaping (ConnectionState) -> Void,
        onFrame: @escaping (UInt32, Data, Bool, FrameTrace) -> Void
    ) {
        self.onStateChange = onStateChange
        self.onFrame = onFrame
    }

    func setWindowsHost(_ host: String) {
        windowsHost = host
    }

    func start() {
        openSocket()
        beginHandshaking()
    }

    func stop() {
        handshakeTimer?.cancel()
        heartbeatTask?.cancel()
        watchdogTask?.cancel()
        feedbackTask?.cancel()
        readSource?.cancel()
        readSource = nil
        datagramFeed?.finish()
        datagramFeed = nil
        pumpTask?.cancel()
        pumpTask = nil
        if fd >= 0 { Darwin.close(fd); fd = -1 }
    }

    // MARK: - Socket

    private func openSocket() {
        fd = Darwin.socket(AF_INET, SOCK_DGRAM, 0)
        guard fd >= 0 else { return }

        var reuseAddr: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &reuseAddr, socklen_t(MemoryLayout<Int32>.size))

        var addr = sockaddr_in()
        addr.sin_len    = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port   = in_port_t(MontogoProtocol.port).bigEndian
        addr.sin_addr.s_addr = INADDR_ANY

        let ok = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard ok == 0 else { Darwin.close(fd); fd = -1; return }

        // Non-blocking so the read handler can drain every queued datagram per
        // event instead of doing one recv per wakeup.
        let flags = fcntl(fd, F_GETFL, 0)
        _ = fcntl(fd, F_SETFL, flags | O_NONBLOCK)

        // Datagram batches flow through an AsyncStream consumed by a single task:
        // strict FIFO into the actor. (Task-per-packet spawning has no ordering
        // guarantee — reordered chunks can complete frames out of order, and
        // P-frames decoded out of order corrupt the picture.)
        var feedVar: AsyncStream<(batch: [Data], recvTime: CFTimeInterval)>.Continuation?
        let stream = AsyncStream<(batch: [Data], recvTime: CFTimeInterval)> { feedVar = $0 }
        guard let feed = feedVar else { return }
        datagramFeed = feed
        // High priority so the pump keeps pace with the userInteractive socket
        // drainer; at default priority it fell behind and a backlog formed between
        // last-chunk arrival and decode submit (the "queue" timing stage).
        pumpTask = Task(priority: .high) { [weak self] in
            for await (batch, recvTime) in stream {
                await self?.handleBatch(batch, recvTime: recvTime)
            }
        }

        let capturedFd = fd
        let src = DispatchSource.makeReadSource(fileDescriptor: capturedFd,
                                                queue: .global(qos: .userInteractive))
        src.setEventHandler {
            var batch: [Data] = []
            var buf = [UInt8](repeating: 0, count: 65536)
            while true {
                let n = Darwin.recv(capturedFd, &buf, 65536, 0)
                if n <= 0 { break }   // EWOULDBLOCK: queue drained (or fd closed)
                batch.append(Data(buf[0..<n]))
            }
            // Stamped here, at socket drain, so frame timing measures network
            // arrival rather than when the actor got around to the packet.
            if !batch.isEmpty { feed.yield((batch, CACurrentMediaTime())) }
        }
        src.resume()
        readSource = src
    }

    private func handleBatch(_ batch: [Data], recvTime: CFTimeInterval) {
        for datagram in batch { handleDatagram(datagram, recvTime: recvTime) }
    }

    // MARK: - Send

    private func send(_ data: Data) {
        guard fd >= 0, !windowsHost.isEmpty else { return }
        var dest = sockaddr_in()
        dest.sin_len    = UInt8(MemoryLayout<sockaddr_in>.size)
        dest.sin_family = sa_family_t(AF_INET)
        dest.sin_port   = in_port_t(MontogoProtocol.port).bigEndian
        dest.sin_addr.s_addr = inet_addr(windowsHost)
        data.withUnsafeBytes { buf in
            _ = withUnsafePointer(to: &dest) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    Darwin.sendto(fd, buf.baseAddress, data.count, 0, $0,
                                  socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
    }

    // MARK: - Handshake

    private func beginHandshaking() {
        onStateChange?(.connecting)
        // Fresh ephemeral key for this handshake cycle, reused across retries so any response
        // Windows sends matches. A reconnect (watchdog) makes a new one → a new session key.
        let ephemeral = P256.KeyAgreement.PrivateKey()
        ephemeralKey = ephemeral
        let macPub = ephemeral.publicKey.x963Representation   // 65 bytes
        handshakeTimer = Task {
            while !Task.isCancelled {
                let pkt = HandshakeRequestPacket.build(clientId: clientId, macPubKey: macPub, authKey: authKey)
                send(pkt)
                try? await Task.sleep(nanoseconds: UInt64(MontogoProtocol.handshakeRetryInterval * 1_000_000_000))
            }
        }
    }

    private func handleHandshakeResponse(_ data: Data) {
        guard let ephemeral = ephemeralKey else { return }
        // Authenticate the response with the code-derived authKey — the tag binds our own
        // request's public key too (full transcript) — and confirm it echoes our clientId.
        let ourMacPub = ephemeral.publicKey.x963Representation
        guard let resp = HandshakeResponse.parse(data, authKey: authKey,
                                                 expectedClientId: clientId, macPubKey: ourMacPub),
              let winPub = try? P256.KeyAgreement.PublicKey(x963Representation: resp.winPubKey),
              let shared = try? ephemeral.sharedSecretFromKeyAgreement(with: winPub)
        else { return }

        // Derive this session's keys from the ECDH shared secret (forward secrecy): the video
        // decryption key and the feedback-authentication key.
        var dec = StreamDecryptor(encKey: DerivedKeys.sessionKey(from: shared))
        dec.setNoncePrefix(resp.noncePrefix)
        decryptor = dec
        feedbackKey = DerivedKeys.feedbackKey(from: shared)
        feedbackCounter = 0

        handshakeTimer?.cancel()
        handshakeTimer = nil
        // Fresh session: discard any stale frame-ordering state and wait for the keyframe
        // Windows forces on (re)connect before decoding P-frames.
        expectedFrameId = nil
        awaitingKeyframe = true
        onStateChange?(.connected(fps: resp.targetFps, width: resp.displayWidth, height: resp.displayHeight))
        startHeartbeat()
        startWatchdog()
        startFeedback()
    }

    // MARK: - Receive

    private func handleDatagram(_ data: Data, recvTime: CFTimeInterval) {
        guard data.count >= 4,
              data.read(UInt16.self, at: 0) == MontogoProtocol.magic,
              data[2] == MontogoProtocol.version
        else { return }

        lastPacketTime = Date()

        switch data[3] {
        case PacketType.handshakeResponse.rawValue: handleHandshakeResponse(data)
        case PacketType.videoChunk.rawValue:        handleVideoChunk(data, recvTime: recvTime)
        case PacketType.heartbeat.rawValue:         break
        default:                                    break
        }
    }

    private func handleVideoChunk(_ data: Data, recvTime: CFTimeInterval) {
        guard let header = VideoChunkHeader.parse(data) else { return }
        let payloadStart = VideoChunkHeader.size
        let payloadEnd   = payloadStart + Int(header.payloadLength)
        guard data.count >= payloadEnd else { return }
        guard let decryptor else { return }   // no session key until the handshake completes
        let payload = Data(data[payloadStart..<payloadEnd])
        // Authenticate the wire header (bytes 0..<28) as GCM AAD — must match exactly what the
        // sender authenticated, so a tampered routing/reassembly field fails the tag.
        let aad = Data(data[0..<VideoChunkHeader.size])
        let plaintext = decryptor.decrypt(payload: payload, sequenceNum: header.sequenceNum, aad: aad)
        // Count every arriving chunk by its continuous SequenceNum so gaps = packet loss.
        FrameTimingLog.shared.recordChunk(seq: header.sequenceNum, decrypted: plaintext != nil)
        // Same accounting over the shorter feedback window that drives adaptive bitrate.
        if fbSeqMin == nil { fbSeqMin = header.sequenceNum; fbSeqMax = header.sequenceNum }
        else {
            if header.sequenceNum < fbSeqMin! { fbSeqMin = header.sequenceNum }
            if header.sequenceNum > fbSeqMax { fbSeqMax = header.sequenceNum }
        }
        fbCount += 1
        guard let plaintext else { return }
        if let (frameId, nalData, isIDR, trace) = assembler.add(header: header, plaintext: plaintext,
                                                                recvTime: recvTime) {
            gateAndDeliver(frameId, nalData, isIDR, trace)
        }
    }

    // Enforces in-order, gap-free delivery of the IPPP frame chain. A keyframe always
    // re-syncs; a P-frame is delivered only if it's the next expected id. A gap (or the
    // pre-first-IDR state) means the reference chain is broken, so P-frames are dropped
    // until a keyframe arrives and Windows is asked to send one now.
    private func gateAndDeliver(_ frameId: UInt32, _ nalData: Data, _ isIDR: Bool, _ trace: FrameTrace) {
        if isIDR {
            expectedFrameId = frameId &+ 1
            awaitingKeyframe = false
            onFrame?(frameId, nalData, isIDR, trace)
            return
        }
        guard !awaitingKeyframe, let expected = expectedFrameId else {
            // No valid reference (broken chain, or no IDR seen yet): drop and request a key.
            if !awaitingKeyframe { awaitingKeyframe = true }
            requestKeyframeNow()
            return
        }
        if frameId == expected {
            expectedFrameId = frameId &+ 1
            onFrame?(frameId, nalData, isIDR, trace)
        } else if frameId < expected {
            return   // stale or duplicate frameId — drop
        } else {
            // Gap: this P-frame's reference never arrived. Stop until the next keyframe.
            awaitingKeyframe = true
            requestKeyframeNow()
        }
    }

    // Builds + sends one authenticated feedback packet (monotonic counter, HMAC with the
    // per-session feedback key). No-op until the handshake has established the key. Actor
    // isolation keeps the counter strictly increasing.
    private func sendFeedback(lossPermille: UInt16, flags: UInt8) {
        guard let feedbackKey else { return }
        feedbackCounter &+= 1
        send(FeedbackPacket.build(clientId: clientId, counter: feedbackCounter,
                                  lossPermille: lossPermille, fps: 0, flags: flags,
                                  feedbackKey: feedbackKey))
    }

    // Sends an out-of-band feedback packet flagged to request an immediate keyframe. Rate-
    // limited; the periodic feedback also carries the flag while awaitingKeyframe is set.
    private func requestKeyframeNow() {
        let now = Date()
        guard now.timeIntervalSince(lastKeyframeRequest) >= 0.1 else { return }
        lastKeyframeRequest = now
        sendFeedback(lossPermille: 0, flags: FeedbackPacket.flagRequestKeyframe)
    }

    // MARK: - Heartbeat

    private func startHeartbeat() {
        heartbeatTask?.cancel()
        heartbeatTask = Task {
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: UInt64(MontogoProtocol.heartbeatInterval * 1_000_000_000))
                let pkt = HeartbeatPacket.build(seq: heartbeatSeq)
                heartbeatSeq &+= 1
                send(pkt)
            }
        }
    }

    // MARK: - Feedback (adaptive bitrate)

    // Reports packet loss over the last window to Windows a few times a second, so the
    // encoder can drop its bitrate to match the link (and probe back up when it clears).
    private func startFeedback() {
        feedbackTask?.cancel()
        feedbackTask = Task {
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 400_000_000)   // 400 ms
                var permille: UInt16 = 0
                if let mn = fbSeqMin, fbSeqMax >= mn {
                    let expected = Int(fbSeqMax - mn) + 1
                    if expected > 0 {
                        let lost = max(0, expected - fbCount)
                        permille = UInt16(min(1000, lost * 1000 / expected))
                    }
                }
                fbSeqMin = nil; fbSeqMax = 0; fbCount = 0
                // Keep asking for a keyframe until the chain is healed, in case the out-of-band
                // request was itself lost.
                let flags: UInt8 = awaitingKeyframe ? FeedbackPacket.flagRequestKeyframe : 0
                sendFeedback(lossPermille: permille, flags: flags)
            }
        }
    }

    // MARK: - Watchdog

    private func startWatchdog() {
        watchdogTask?.cancel()
        watchdogTask = Task {
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                if Date().timeIntervalSince(lastPacketTime) > MontogoProtocol.connectionLostTimeout {
                    heartbeatTask?.cancel()
                    feedbackTask?.cancel()
                    onStateChange?(.lost)
                    beginHandshaking()
                    return
                }
            }
        }
    }
}
