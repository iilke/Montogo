import Foundation
import Security

// Persists the pairing so the app can auto-reconnect on the next launch.
// The connection code is a secret → macOS Keychain (Security framework).
// The Windows IP is not sensitive → UserDefaults.
enum ConnectionStore {
    private static let service = "com.montogo.app"
    private static let account = "connection-code"
    private static let ipKey   = "windowsIP"

    // MARK: - Connection code (Keychain)

    static func saveCode(_ code: String) {
        let data = Data(code.utf8)
        let match: [String: Any] = [
            kSecClass as String:       kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        // Update the existing item if present, otherwise add a new one.
        let status = SecItemUpdate(match as CFDictionary,
                                   [kSecValueData as String: data] as CFDictionary)
        if status == errSecItemNotFound {
            var add = match
            add[kSecValueData as String]      = data
            add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            SecItemAdd(add as CFDictionary, nil)
        }
    }

    static func loadCode() -> String? {
        let query: [String: Any] = [
            kSecClass as String:       kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String:  true,
            kSecMatchLimit as String:  kSecMatchLimitOne,
        ]
        var out: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &out) == errSecSuccess,
              let data = out as? Data,
              let code = String(data: data, encoding: .utf8),
              !code.isEmpty
        else { return nil }
        return code
    }

    static func deleteCode() {
        SecItemDelete([
            kSecClass as String:       kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ] as CFDictionary)
    }

    // MARK: - Windows IP (UserDefaults)

    static func saveIP(_ ip: String) { UserDefaults.standard.set(ip, forKey: ipKey) }

    static func loadIP() -> String? {
        let ip = UserDefaults.standard.string(forKey: ipKey)
        return (ip?.isEmpty == false) ? ip : nil
    }

    static func deleteIP() { UserDefaults.standard.removeObject(forKey: ipKey) }

    // MARK: - Combined

    static func save(code: String, ip: String) {
        saveCode(code)
        saveIP(ip)
    }

    /// The saved pairing, or nil if either half is missing.
    static func saved() -> (code: String, ip: String)? {
        guard let code = loadCode(), let ip = loadIP() else { return nil }
        return (code, ip)
    }

    static func forget() {
        deleteCode()
        deleteIP()
    }
}
