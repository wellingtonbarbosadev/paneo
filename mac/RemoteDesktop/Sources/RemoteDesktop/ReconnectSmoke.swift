import AppKit

/// Exercita o controlador real (queda + reconexão) sem ler/escrever o perfil
/// do Chaves. O `session.json` traz host, porta, fingerprint e PIN do v2; o PIN
/// nunca é impresso nem gravado no relatório.
@MainActor
final class ReconnectSmoke {
    private struct Session: Decodable {
        let host: String
        let port: UInt16
        let fingerprint: String?
        let pin: String
    }

    private let controller: MainWindowController
    private let output: String
    private var connections = 0
    private var finished = false

    init(sessionPath: String, outputPath: String) throws {
        let session = try JSONDecoder().decode(Session.self, from: Data(contentsOf: URL(fileURLWithPath: sessionPath)))
        guard PasswordProof.isAcceptablePin(session.pin) else { throw ProtocolError.malformed }
        var fingerprint: String?
        if let raw = session.fingerprint, !raw.isEmpty {
            guard let hex = CertificatePin.normalizedFingerprint(raw) else { throw ProtocolError.malformed }
            fingerprint = hex
        }
        output = outputPath
        let preset = ConnectionPreset(host: session.host, port: String(session.port),
                                      pin: session.pin, fingerprint: fingerprint)
        controller = MainWindowController(preset: preset, persistPairing: false)
        controller.onReady = { [weak self] in
            guard let self else { return }
            self.connections += 1
            if self.connections == 2 { self.finish(success: true) }
        }
        controller.showWindow()
        controller.startConnection()
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(25))
            self?.finish(success: false)
        }
    }

    private func finish(success: Bool) {
        guard !finished else { return }
        finished = true
        let value: [String: Any] = ["success": success, "authenticatedConnections": connections, "scope": "Reconexão do controlador AppKit com fixture TLS; sem persistência no Chaves"]
        if let data = try? JSONSerialization.data(withJSONObject: value) { try? data.write(to: URL(fileURLWithPath: output)) }
        controller.window.close()
        exit(success ? 0 : 1)
    }
}
