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
    let ikm: Data   // 5 raw bytes decoded from the 8-char code

    // Accepts "XXXX-XXXX" or "XXXXXXXX", case-insensitive.
    init(code: String) throws {
        let stripped = code.uppercased().replacingOccurrences(of: "-", with: "")
        guard stripped.count == 8 else { throw ConnectionCodeError.invalidLength }
        var bits: UInt64 = 0
        for ch in stripped {
            guard let val = decodeMap[ch] else { throw ConnectionCodeError.invalidCharacter }
            bits = (bits << 5) | UInt64(val)
        }
        // 40 bits → 5 bytes (big-endian extraction)
        var bytes = [UInt8](repeating: 0, count: 5)
        for i in 0..<5 { bytes[4 - i] = UInt8((bits >> (i * 8)) & 0xFF) }
        self.ikm = Data(bytes)
    }
}

struct DerivedKeys {
    let encKey: SymmetricKey     // 32-byte AES-256 key
    let authKey: SymmetricKey    // 32-byte HMAC key

    init(from code: ConnectionCode) {
        let ikm = code.ikm
        encKey  = DerivedKeys.hkdf(ikm: ikm, info: "montogo-enc-v1")
        authKey = DerivedKeys.hkdf(ikm: ikm, info: "montogo-auth-v1")
    }

    private static func hkdf(ikm: Data, info: String) -> SymmetricKey {
        // HKDF-SHA256, empty salt, 32-byte output.
        let inputKey = SymmetricKey(data: ikm)
        return HKDF<SHA256>.deriveKey(
            inputKeyMaterial: inputKey,
            info: Data(info.utf8),
            outputByteCount: 32
        )
    }
}
