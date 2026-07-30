import Foundation
import Darwin
import QuartzCore

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
    private let keys: DerivedKeys
    private let clientId: UUID
    private let authToken: Data
    private var decryptor: StreamDecryptor
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

    var onStateChange: ((ConnectionState) -> Void)?
    var onFrame: ((UInt32, Data, Bool, FrameTrace) -> Void)?

    init(keys: DerivedKeys) {
        self.keys = keys
        self.clientId = UUID()
        self.authToken = AuthToken.make(authKey: keys.authKey, clientId: clientId)
        self.decryptor = StreamDecryptor(encKey: keys.encKey)
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
        handshakeTimer = Task {
            while !Task.isCancelled {
                let pkt = HandshakeRequestPacket.build(clientId: clientId, authToken: authToken)
                send(pkt)
                try? await Task.sleep(nanoseconds: UInt64(MontogoProtocol.handshakeRetryInterval * 1_000_000_000))
            }
        }
    }

    private func handleHandshakeResponse(_ data: Data) {
        guard let resp = HandshakeResponse.parse(data),
              resp.clientId == clientId
        else { return }
        handshakeTimer?.cancel()
        handshakeTimer = nil
        decryptor.setNoncePrefix(resp.noncePrefix)
        onStateChange?(.connected(fps: resp.targetFps, width: resp.displayWidth, height: resp.displayHeight))
        startHeartbeat()
        startWatchdog()
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
        let payload = Data(data[payloadStart..<payloadEnd])
        let plaintext = decryptor.decrypt(payload: payload, sequenceNum: header.sequenceNum)
        // Count every arriving chunk by its continuous SequenceNum so gaps = packet loss.
        FrameTimingLog.shared.recordChunk(seq: header.sequenceNum, decrypted: plaintext != nil)
        guard let plaintext else { return }
        if let (frameId, nalData, isIDR, trace) = assembler.add(header: header, plaintext: plaintext,
                                                                recvTime: recvTime) {
            onFrame?(frameId, nalData, isIDR, trace)
        }
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

    // MARK: - Watchdog

    private func startWatchdog() {
        watchdogTask?.cancel()
        watchdogTask = Task {
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                if Date().timeIntervalSince(lastPacketTime) > MontogoProtocol.connectionLostTimeout {
                    heartbeatTask?.cancel()
                    onStateChange?(.lost)
                    beginHandshaking()
                    return
                }
            }
        }
    }
}
