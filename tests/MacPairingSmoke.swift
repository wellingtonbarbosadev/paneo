import Foundation

@main
struct MacPairingSmoke {
    static func main() {
        let account = "test-" + UUID().uuidString
        defer { _ = PairingStore.forget(account: account) }
        let value = SavedPairing(host: "127.0.0.1", port: "9443", fingerprint: String(repeating: "0", count: 64), token: "synthetic-test-value")
        guard PairingStore.save(value, account: account), let loaded = PairingStore.load(account: account), loaded.token == value.token, loaded.host == value.host else {
            print("FAIL: salvar/restaurar item isolado no Chaves"); exit(1)
        }
        let changed = SavedPairing(host: value.host, port: "9444", fingerprint: value.fingerprint, token: "updated-synthetic-test-value")
        guard PairingStore.save(changed, account: account), PairingStore.load(account: account)?.token == changed.token,
              PairingStore.forget(account: account), PairingStore.load(account: account) == nil else {
            print("FAIL: atualizar/remover item isolado no Chaves"); exit(1)
        }
        print("PASS Keychain: salvar, restaurar, atualizar e remover item sintético isolado; perfil real não acessado.")
    }
}
