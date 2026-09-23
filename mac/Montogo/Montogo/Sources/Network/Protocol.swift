import Foundation

// MARK: - Constants

enum MontogoProtocol {
    static let magic: UInt16 = 0x474D
    static let version: UInt8 = 5   // v5: HEVC (H.265) video
    static let port: UInt16 = 47921
    static let maxChunkPayload = 1400
    static let gcmTagSize = 16
    static let heartbeatInterval: TimeInterval = 1.0
    static let handshakeRetryInterval: TimeInterval = 0.5
    static let chunkDropTimeout: TimeInterval = 0.1
    static let connectionLostTimeout: TimeInterval = 3.0
}

enum PacketType: UInt8 {
    case videoChunk        = 0x01
    case heartbeat         = 0x10
    case handshakeRequest  = 0x11
    case handshakeResponse = 0x12
    case feedback          = 0x13   // Mac → Windows: measured loss, drives adaptive bitrate
}

// MARK: - Packet builders

struct HandshakeRequestPacket {
    static let size = 40

    static func build(clientId: UUID, authToken: Data) -> Data {
        var pkt = Data(count: size)
        pkt.write(MontogoProtocol.magic, at: 0)
        pkt[2] = MontogoProtocol.version
        pkt[3] = PacketType.handshakeRequest.rawValue
        pkt.write(UInt16(2), at: 4)     // ProtoVersion
        pkt.write(UInt16(0), at: 6)     // Reserved
        var uuidBytes = clientId.uuid
        Swift.withUnsafeBytes(of: &uuidBytes) { buf in pkt.replaceSubrange(8..<24, with: buf) }
        pkt.replaceSubrange(24..<40, with: authToken)
        return pkt
    }
}

struct HeartbeatPacket {
    static let size = 16

    static func build(seq: UInt32) -> Data {
        var pkt = Data(count: size)
        pkt.write(MontogoProtocol.magic, at: 0)
        pkt[2] = MontogoProtocol.version
        pkt[3] = PacketType.heartbeat.rawValue
        let ts = UInt64(Date().timeIntervalSince1970 * 1_000_000)
        pkt.write(ts, at: 4)
        pkt.write(seq, at: 12)
        return pkt
    }
}

// Mac → Windows link-quality report. Layout mirrors the C# FeedbackPacket (Pack=1):
// magic(2) version(1) type(1) clientId(16) token(16) lossPermille(2) fps(1) flags(1).
struct FeedbackPacket {
    static let size = 40
    static let flagRequestKeyframe: UInt8 = 1 << 0   // set when the Mac saw a frameId gap

    static func build(clientId: UUID, authToken: Data, lossPermille: UInt16,
                      fps: UInt8, flags: UInt8 = 0) -> Data {
        var pkt = Data(count: size)
        pkt.write(MontogoProtocol.magic, at: 0)
        pkt[2] = MontogoProtocol.version
        pkt[3] = PacketType.feedback.rawValue
        var uuidBytes = clientId.uuid
        Swift.withUnsafeBytes(of: &uuidBytes) { buf in pkt.replaceSubrange(4..<20, with: buf) }
        pkt.replaceSubrange(20..<36, with: authToken)
        pkt.write(lossPermille, at: 36)
        pkt[38] = fps
        pkt[39] = flags
        return pkt
    }
}

// MARK: - Parsed incoming packets

struct HandshakeResponse {
    let negotiatedVersion: UInt16
    let displayWidth: UInt16
    let displayHeight: UInt16
    let targetFps: UInt8
    let clientId: UUID
    let noncePrefix: UInt64     // 8-byte LE nonce prefix for AES-GCM

    static func parse(_ data: Data) -> HandshakeResponse? {
        guard data.count >= 36,
              data.read(UInt16.self, at: 0) == MontogoProtocol.magic,
              data[2] == MontogoProtocol.version,
              data[3] == PacketType.handshakeResponse.rawValue
        else { return nil }
        let ver    = data.read(UInt16.self, at: 4)
        let width  = data.read(UInt16.self, at: 6)
        let height = data.read(UInt16.self, at: 8)
        let fps    = data[10]
        let cidData = data[12..<28]
        var raw = uuid_t(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0)
        cidData.withUnsafeBytes { buf in
            Swift.withUnsafeMutableBytes(of: &raw) { dst in dst.copyMemory(from: buf) }
        }
        let cid = UUID(uuid: raw)
        let prefix = data.read(UInt64.self, at: 28)
        return HandshakeResponse(negotiatedVersion: ver, displayWidth: width,
                                 displayHeight: height, targetFps: fps,
                                 clientId: cid, noncePrefix: prefix)
    }
}

struct VideoChunkHeader {
    static let size = 28
    let sequenceNum: UInt32
    let frameId: UInt32
    let timestampUs: UInt64
    let chunkIndex: UInt16
    let chunkTotal: UInt16
    let payloadLength: UInt16
    let flags: UInt8
    let fecTotal: UInt8    // number of FEC parity packets for this frame (0 = none)
    let isIDR: Bool

    // A packet is FEC parity when chunkIndex >= chunkTotal; its stripe is chunkIndex - chunkTotal.
    var isParity: Bool { chunkIndex >= chunkTotal }

    static func parse(_ data: Data) -> VideoChunkHeader? {
        guard data.count >= size,
              data.read(UInt16.self, at: 0) == MontogoProtocol.magic,
              data[2] == MontogoProtocol.version,
              data[3] == PacketType.videoChunk.rawValue
        else { return nil }
        let seq     = data.read(UInt32.self, at: 4)
        let frameId = data.read(UInt32.self, at: 8)
        let ts      = data.read(UInt64.self, at: 12)
        let ci      = data.read(UInt16.self, at: 20)
        let ct      = data.read(UInt16.self, at: 22)
        let pl      = data.read(UInt16.self, at: 24)
        let flags   = data[26]
        let fec     = data[27]
        return VideoChunkHeader(sequenceNum: seq, frameId: frameId, timestampUs: ts,
                                chunkIndex: ci, chunkTotal: ct, payloadLength: pl,
                                flags: flags, fecTotal: fec, isIDR: (flags & 1) != 0)
    }
}

// MARK: - Data helpers (little-endian read/write)

extension Data {
    @inline(__always)
    func read<T: FixedWidthInteger>(_ type: T.Type, at offset: Int) -> T {
        let raw = self.subdata(in: offset..<(offset + MemoryLayout<T>.size))
        return raw.withUnsafeBytes { T(littleEndian: $0.load(as: T.self)) }
    }

    @inline(__always)
    mutating func write<T: FixedWidthInteger>(_ value: T, at offset: Int) {
        var le = value.littleEndian
        Swift.withUnsafeBytes(of: &le) { src in
            self.replaceSubrange(offset..<(offset + MemoryLayout<T>.size), with: src)
        }
    }
}
