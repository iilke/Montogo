import Foundation
import CryptoKit

// Decrypts AES-256-GCM video chunk payloads.
// Thread-safe: AES.GCM is value-type in CryptoKit; each decrypt call is independent.
struct StreamDecryptor {
    private let encKey: SymmetricKey
    private var noncePrefix: Data = Data(count: 8)    // set once from HandshakeResponse

    init(encKey: SymmetricKey) {
        self.encKey = encKey
    }

    mutating func setNoncePrefix(_ prefix: UInt64) {
        var le = prefix.littleEndian
        noncePrefix = withUnsafeBytes(of: &le) { Data($0) }
    }

    // Returns decrypted plaintext, or nil if GCM verification fails. `aad` is the plaintext
    // chunk header (28 bytes), authenticated but not encrypted, so tampering with routing/
    // reassembly fields fails the tag. Must be byte-identical to what the sender authenticated.
    func decrypt(payload: Data, sequenceNum: UInt32, aad: Data) -> Data? {
        guard payload.count >= MontogoProtocol.gcmTagSize else { return nil }

        // Build 12-byte nonce: 8-byte prefix || 4-byte LE sequenceNum
        var nonce = noncePrefix
        var seqLE = sequenceNum.littleEndian
        nonce.append(contentsOf: withUnsafeBytes(of: &seqLE) { Data($0) })

        let ciphertextLen = payload.count - MontogoProtocol.gcmTagSize
        let ciphertext = payload.prefix(ciphertextLen)
        let tag = payload.suffix(MontogoProtocol.gcmTagSize)

        do {
            let gcmNonce = try AES.GCM.Nonce(data: nonce)
            let sealed = try AES.GCM.SealedBox(nonce: gcmNonce,
                                               ciphertext: ciphertext,
                                               tag: tag)
            return try AES.GCM.open(sealed, using: encKey, authenticating: aad)
        } catch {
            return nil  // GCM tag mismatch (or tampered header) — silently discard
        }
    }
}
