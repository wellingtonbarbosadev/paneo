import Foundation
import Security

struct SavedPairing: Codable {
    let host: String
    let port: String
    let fingerprint: String
    let token: String
}

enum PairingStore {
    private static func query(account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: "local.remoteDesktop.pairing",
         kSecAttrAccount as String: account]
    }
    static func load(account: String = "default-host") -> SavedPairing? {
        var request = query(account: account)
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(request as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        return try? JSONDecoder().decode(SavedPairing.self, from: data)
    }
    static func save(_ pairing: SavedPairing, account: String = "default-host") -> Bool {
        guard let data = try? JSONEncoder().encode(pairing) else { return false }
        let updates = [kSecValueData as String: data]
        let status = SecItemUpdate(query(account: account) as CFDictionary, updates as CFDictionary)
        if status == errSecSuccess { return true }
        guard status == errSecItemNotFound else { return false }
        var item = query(account: account)
        item[kSecValueData as String] = data
        item[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        return SecItemAdd(item as CFDictionary, nil) == errSecSuccess
    }
    static func forget(account: String = "default-host") -> Bool {
        let status = SecItemDelete(query(account: account) as CFDictionary)
        return status == errSecSuccess || status == errSecItemNotFound
    }
}
