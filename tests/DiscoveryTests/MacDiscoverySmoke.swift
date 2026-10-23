import Foundation

/// Prova Bonjour entre máquinas; não autentica nem abre uma sessão remota.
@main
@MainActor
struct MacDiscoverySmoke {
    static func main() {
        guard CommandLine.arguments.count == 2, let port = Int(CommandLine.arguments[1]),
              (1...65535).contains(port) else {
            print("Uso: MacDiscoverySmoke <porta do anúncio temporário>")
            exit(2)
        }
        let discovery = HostDiscovery()
        var found = false
        discovery.onHostsChanged = { hosts in
            let matching = hosts.filter { $0.port == port }
            if !matching.isEmpty {
                guard matching.allSatisfy({ !$0.addresses.isEmpty && !$0.hostname.isEmpty }) else { exit(1) }
                found = true
                print("DISCOVERED resolvedAddress=True")
            } else if found {
                print("PASS Bonjour: anúncio resolvido e remoção observada")
                discovery.onHostsChanged = nil
                discovery.stop()
                exit(0)
            }
        }
        discovery.start()
        Task { @MainActor in
            try? await Task.sleep(for: .seconds(20))
            print(found ? "FAIL: remoção não observada" : "FAIL: anúncio não descoberto")
            discovery.onHostsChanged = nil
            discovery.stop()
            exit(1)
        }
        RunLoop.main.run()
    }
}
