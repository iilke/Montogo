import Foundation
import CryptoKit

// MARK: - Constants

enum MontogoProtocol {
    static let magic: UInt16 = 0x474D
    static let version: UInt8 = 9   // v9: response HMAC binds macPubKey (full-transcript auth) (v8: authenticated feedback)
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

// v7 layout: magic(2) ver(1) type(1) clientId(16) macPubKey(65) authTag(32) = 117 bytes.
// authTag = HMAC-SHA256(authKey, bytes[0..85]) authenticates the request and binds the
// ephemeral public key to the pairing secret.
struct HandshakeRequestPacket {
    static let size = 117
    static let pubKeyOffset = 20
    static let signedLen = 85   // bytes covered by the HMAC

    static func build(clientId: UUID, macPubKey: Data, authKey: SymmetricKey) -> Data {
        precondition(macPubKey.count == 65)
        var pkt = Data(count: size)
        pkt.write(MontogoProtocol.magic, at: 0)
        pkt[2] = MontogoProtocol.version
        pkt[3] = PacketType.handshakeRequest.rawValue
        var uuidBytes = clientId.uuid
        Swift.withUnsafeBytes(of: &uuidBytes) { buf in pkt.replaceSubrange(4..<20, with: buf) }
        pkt.replaceSubrange(20..<85, with: macPubKey)
        let tag = HMAC<SHA256>.authenticationCode(for: Data(pkt[0..<signedLen]), using: authKey)
        pkt.replaceSubrange(85..<117, with: Data(tag))
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

// Mac → Windows link-quality report (v8). Authenticated with the per-session feedback key
// (HKDF of the ECDH shared secret) + a monotonic counter, so it can't be forged or replayed.
// Layout: magic(2) ver(1) type(1) clientId(16) counter(4) lossPermille(2) fps(1) flags(1)
//         authTag(32) = HMAC-SHA256(feedbackKey, bytes[0..28]) = 60 bytes.
struct FeedbackPacket {
    static let size = 60
    static let signedLen = 28
    static let flagRequestKeyframe: UInt8 = 1 << 0   // set when the Mac saw a frameId gap

    static func build(clientId: UUID, counter: UInt32, lossPermille: UInt16,
                      fps: UInt8, flags: UInt8, feedbackKey: SymmetricKey) -> Data {
        var pkt = Data(count: size)
        pkt.write(MontogoProtocol.magic, at: 0)
        pkt[2] = MontogoProtocol.version
        pkt[3] = PacketType.feedback.rawValue
        var uuidBytes = clientId.uuid
        Swift.withUnsafeBytes(of: &uuidBytes) { buf in pkt.replaceSubrange(4..<20, with: buf) }
        pkt.write(counter, at: 20)
        pkt.write(lossPermille, at: 24)
        pkt[26] = fps
        pkt[27] = flags
        let tag = HMAC<SHA256>.authenticationCode(for: Data(pkt[0..<signedLen]), using: feedbackKey)
        pkt.replaceSubrange(28..<60, with: Data(tag))
        return pkt
    }
}

// MARK: - Parsed incoming packets

// v9 layout: magic(2) ver(1) type(1) width(2) height(2) fps(1) reserved(1) clientId(16)
// winPubKey(65) noncePrefix(8) authTag(32) = 131 bytes. authTag = HMAC(authKey, bytes[0..99] ‖
// macPubKey) — binds the full exchange transcript (both ephemeral keys), closing the
// spoofed-response vector and pinning the response to our request.
struct HandshakeResponse {
    let displayWidth: UInt16
    let displayHeight: UInt16
    let targetFps: UInt8
    let clientId: UUID
    let winPubKey: Data         // 65-byte P-256 X9.63 public key
    let noncePrefix: UInt64     // 8-byte LE nonce prefix for AES-GCM

    static let size = 131
    static let signedLen = 99   // response bytes covered by the HMAC (macPubKey is appended)

    static func parse(_ data: Data, authKey: SymmetricKey, expectedClientId: UUID, macPubKey: Data) -> HandshakeResponse? {
        guard data.count >= size,
              data.read(UInt16.self, at: 0) == MontogoProtocol.magic,
              data[2] == MontogoProtocol.version,
              data[3] == PacketType.handshakeResponse.rawValue
        else { return nil }

        // Authenticate the whole response — plus our own request's macPubKey — before trusting
        // any field. Must match the Windows side: HMAC over response[0..99] followed by macPubKey.
        var signed = Data(data[0..<signedLen])
        signed.append(macPubKey)
        let tag = Data(data[signedLen..<size])
        guard HMAC<SHA256>.isValidAuthenticationCode(tag, authenticating: signed, using: authKey)
        else { return nil }

        let width  = data.read(UInt16.self, at: 4)
        let height = data.read(UInt16.self, at: 6)
        let fps    = data[8]
        let cidData = data[10..<26]
        var raw = uuid_t(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0)
        cidData.withUnsafeBytes { buf in
            Swift.withUnsafeMutableBytes(of: &raw) { dst in dst.copyMemory(from: buf) }
        }
        let cid = UUID(uuid: raw)
        guard cid == expectedClientId else { return nil }
        let winPub = Data(data[26..<91])
        let prefix = data.read(UInt64.self, at: 91)
        return HandshakeResponse(displayWidth: width, displayHeight: height, targetFps: fps,
                                 clientId: cid, winPubKey: winPub, noncePrefix: prefix)
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
