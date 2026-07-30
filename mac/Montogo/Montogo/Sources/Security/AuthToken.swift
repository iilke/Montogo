import Foundation
import CryptoKit

enum AuthToken {
    // Returns the first 16 bytes of HMAC-SHA256(authKey, clientId.bytes).
    // clientId.bytes = the 16-byte UUID value as laid out in memory (uuid_t),
    // matching the LE representation used by .NET's Guid.ToByteArray().
    static func make(authKey: SymmetricKey, clientId: UUID) -> Data {
        var uuidCopy = clientId.uuid
        let bytes = Swift.withUnsafeBytes(of: &uuidCopy) { Data($0) }
        let mac = HMAC<SHA256>.authenticationCode(for: bytes, using: authKey)
        return Data(mac.prefix(16))
    }
}
