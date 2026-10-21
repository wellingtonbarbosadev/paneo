import AppKit

@MainActor
final class RemoteSmoke {
    private struct Session: Decodable { let host: String; let port: UInt16; let fingerprint: String; let token: String }
    private let output: URL
    private let canvas = RemoteCanvas()
    private let window: NSWindow
    private var connection: RemoteConnection?
    private var frames = 0
    private var completed = false
    private var lastPNG: Data?
    private var started = Date()
    init(sessionPath: String, outputPath: String) throws {
        output = URL(fileURLWithPath: outputPath)
        let session = try JSONDecoder().decode(Session.self, from: Data(contentsOf: URL(fileURLWithPath: sessionPath)))
        guard let pin = CertificatePin.normalizedFingerprint(session.fingerprint) else { throw FramingError.malformed }
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 800, height: 500), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = "RemoteDesktop — teste de transmissão"; window.contentView = canvas
        window.isReleasedWhenClosed = false; window.center(); window.makeKeyAndOrderFront(nil)
        connection = RemoteConnection(onState: { [weak self] _, ready in
            if !ready, let self { self.finish(success: self.frames >= 10) }
        }, onFrame: { [weak self] png, width, height, id in
            guard let self, !self.completed else { return }
            guard self.canvas.present(png, width: width, height: height) else { self.finish(success: false); return }
            self.lastPNG = png; self.frames += 1
            if self.frames == 1 {
                self.connection?.send(.click(x: 0.5, y: 0.48, button: .left))
                self.connection?.send(.text("rascunho"))
                self.connection?.send(.key(.a, ctrl: true, alt: false, shift: false))
                self.connection?.send(.text("Teste remoto: ação 123."))
                self.connection?.send(.text("!"))
                self.connection?.send(.key(.backspace, ctrl: false, alt: false, shift: false))
            }
            self.connection?.acknowledge(id)
            if self.frames == 10 {
                // Aguarda o ACK sair antes de encerrar a conexão e escrever evidências.
                Task { @MainActor [weak self] in
                    try? await Task.sleep(for: .milliseconds(300)); self?.finish(success: true)
                }
            }
        })
        connection?.connect(host: session.host, port: session.port, fingerprint: pin, token: session.token)
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(25)); self?.finish(success: false)
        }
    }
    private func finish(success: Bool) {
        guard !completed else { return }; completed = true
        connection?.disconnect()
        do {
            if let lastPNG { try lastPNG.write(to: URL(fileURLWithPath: output.path + ".png")) }
            let report: [String: Any] = ["success": success, "frames": frames, "elapsedMs": Date().timeIntervalSince(started) * 1000, "rendered": frames > 0]
            try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: output)
        } catch { exit(2) }
        exit(success ? 0 : 1)
    }
}
