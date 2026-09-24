import Foundation
import CryptoKit

// Base-32 alphabet matching the Windows side (omits 0, 1, I, O).
private let alphabet = Array("23456789ABCDEFGHJKLMNPQRSTUVWXYZ")
private let decodeMap: [Character: UInt8] = {
    var map = [Character: UInt8]()
    for (i, c) in alphabet.enumerated() { map[c] = UInt8(i) }
    return map
}()

enum ConnectionCodeError: Error {
    case invalidLength, invalidCharacter
}

struct ConnectionCode {
    let ikm: Data   // 8 raw bytes decoded from the 13-char code

    // Accepts "XXXX-XXXX-XXXXX" or "XXXXXXXXXXXXX", case-insensitive.
    init(code: String) throws {
        let stripped = code.uppercased().replacingOccurrences(of: "-", with: "")
        guard stripped.count == 13 else { throw ConnectionCodeError.invalidLength }
        var bits: UInt64 = 0
        for ch in stripped {
            guard let val = decodeMap[ch] else { throw ConnectionCodeError.invalidCharacter }
            bits = (bits << 5) | UInt64(val)   // 13×5 = 65 bits; the unused top bit overflows away
        }
        // 64 bits → 8 bytes (big-endian), matching the Windows encoder.
        var bytes = [UInt8](repeating: 0, count: 8)
        for i in 0..<8 { bytes[7 - i] = UInt8((bits >> (i * 8)) & 0xFF) }
        self.ikm = Data(bytes)
    }
}

struct DerivedKeys {
    let authKey: SymmetricKey    // 32-byte HMAC key for handshake auth + feedback token

    init(from code: ConnectionCode) {
        // Only the auth key comes from the code now. The video (content) key is established
        // per session by the ephemeral ECDH handshake — see sessionKey(from:).
        authKey = DerivedKeys.hkdf(ikm: code.ikm, info: "montogo-auth-v1")
    }

    // Derives the AES-256-GCM session key from the ECDH shared secret. Mirrors the Windows
    // side exactly: HKDF-SHA256 over the raw shared secret, no salt, info "montogo-session-v7".
    static func sessionKey(from shared: SharedSecret) -> SymmetricKey {
        let raw = shared.withUnsafeBytes { Data($0) }
        return hkdf(ikm: raw, info: "montogo-session-v7")
    }

    // Per-session key that authenticates feedback packets (HMAC), from the same ECDH secret.
    static func feedbackKey(from shared: SharedSecret) -> SymmetricKey {
        let raw = shared.withUnsafeBytes { Data($0) }
        return hkdf(ikm: raw, info: "montogo-feedback-v8")
    }

    private static func hkdf(ikm: Data, info: String) -> SymmetricKey {
        // HKDF-SHA256, no salt, 32-byte output — matches .NET HKDF.DeriveKey(salt: null).
        return HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: ikm),
            info: Data(info.utf8),
            outputByteCount: 32
        )
    }
}
