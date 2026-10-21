import Foundation
import Security

/// Pareamento de um host Windows salvo no Chaves do Mac.
/// O PIN nunca é salvo; o `deviceToken` vem do `ready` em modo `pin`.
struct HostRecord: Codable, Sendable {
    let host: String
    let port: String
    let fingerprint: String
    let deviceToken: String   // base64
    let name: String
}

/// Um item por `host:porta` no serviço `local.remoteDesktop.pairing` mais a
/// conta reservada `last`, que guarda a chave do último host usado.
enum PairingStore {

    private static let service = "local.remoteDesktop.pairing"
    private static let lastAccount = "last"

    /// Chave do último host usado (JSON `{host, port}`) na conta `last`.
    private struct LastKey: Codable {
        let host: String
        let port: String
    }

    // MARK: - Consultas públicas

    static func load(host: String, port: String) -> HostRecord? {
        guard let data = read(account: account(host: host, port: port)) else { return nil }
        return try? JSONDecoder().decode(HostRecord.self, from: data)
    }

    static func loadLast() -> HostRecord? {
        guard let data = read(account: lastAccount),
              let key = try? JSONDecoder().decode(LastKey.self, from: data)
        else { return nil }
        return load(host: key.host, port: key.port)
    }

    /// Grava o registro do host e atualiza a referência do último host usado.
    static func save(_ record: HostRecord) -> Bool {
        guard let data = try? JSONEncoder().encode(record),
              write(data, account: account(host: record.host, port: record.port))
        else { return false }
        guard let lastData = try? JSONEncoder().encode(LastKey(host: record.host, port: record.port)) else {
            return false
        }
        return write(lastData, account: lastAccount)
    }

    static func forget(host: String, port: String) -> Bool {
        delete(account(host: host, port: port))
    }

    static func forgetAll() -> Bool {
        // Sem conta: remove todos os itens (inclusive `last`) do serviço.
        let status = SecItemDelete(baseQuery as CFDictionary)
        return status == errSecSuccess || status == errSecItemNotFound
    }

    // MARK: - Keychain

    private static func account(host: String, port: String) -> String {
        "host:\(host):\(port)"
    }

    private static var baseQuery: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service]
    }

    private static func query(account: String) -> [String: Any] {
        baseQuery.merging([kSecAttrAccount as String: account]) { _, new in new }
    }

    private static func read(account: String) -> Data? {
        var request = query(account: account)
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(request as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        return data
    }

    private static func write(_ data: Data, account: String) -> Bool {
        let updates = [kSecValueData as String: data]
        let status = SecItemUpdate(query(account: account) as CFDictionary, updates as CFDictionary)
        if status == errSecSuccess { return true }
        guard status == errSecItemNotFound else { return false }
        var item = query(account: account)
        item[kSecValueData as String] = data
        item[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        return SecItemAdd(item as CFDictionary, nil) == errSecSuccess
    }

    private static func delete(_ account: String) -> Bool {
        let status = SecItemDelete(query(account: account) as CFDictionary)
        return status == errSecSuccess || status == errSecItemNotFound
    }
}
