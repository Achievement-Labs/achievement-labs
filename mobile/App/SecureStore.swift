import Foundation
import Security

enum SecureStore {
    struct Failure: LocalizedError {
        let status: OSStatus
        var errorDescription: String? { "Keychain operation failed (OSStatus \(status))." }
    }
    static let service = "org.achievementlabs.mobile.session"
    static func read() throws -> Data? {
        let query: [String: Any] = [kSecClass as String:kSecClassGenericPassword, kSecAttrService as String:service, kSecAttrAccount as String:"current", kSecReturnData as String:true, kSecMatchLimit as String:kSecMatchLimitOne]
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else { throw Failure(status: status) }
        return result as? Data
    }
    static func save(_ data: Data, allowBackground: Bool = false) throws {
        let query: [String: Any] = [kSecClass as String:kSecClassGenericPassword, kSecAttrService as String:service, kSecAttrAccount as String:"current"]
        let update: [String: Any] = [kSecValueData as String:data, kSecAttrAccessible as String:(allowBackground ? kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly : kSecAttrAccessibleWhenUnlockedThisDeviceOnly)]
        let status = SecItemUpdate(query as CFDictionary, update as CFDictionary)
        if status == errSecItemNotFound {
            var item = query; update.forEach { item[$0] = $1 }
            let added = SecItemAdd(item as CFDictionary, nil)
            guard added == errSecSuccess else { throw Failure(status: added) }
        } else if status != errSecSuccess { throw Failure(status: status) }
    }
    static func remove() throws {
        let status = SecItemDelete([kSecClass as String:kSecClassGenericPassword,kSecAttrService as String:service,kSecAttrAccount as String:"current"] as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw Failure(status: status) }
    }
}
