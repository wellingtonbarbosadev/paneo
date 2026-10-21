import AppKit

/// Teste de transmissão pelo protocolo v2: conecta com PIN + fingerprint,
/// recebe o vídeo (H.264) ou PNG de fallback, envia a sequência fixa de
/// entrada e grava o relatório. O PIN nunca aparece em stdout nem no relatório.
@MainActor
final class RemoteSmoke {
    private struct Session: Decodable {
        let host: String
        let port: UInt16
        let fingerprint: String?
        let pin: String
    }

    private static let fixedText = "Teste remoto: ação 123."

    private let output: URL
    private let canvas = RemoteCanvas()
    private let decoder = H264Decoder()
    private let window: NSWindow
    private var connection: RemoteConnection?
    private var frames = 0
    private var presentedFrames = 0
    private var completed = false
    private var lastPNG: Data?
    private var started = Date()
    private var errors: [String] = []

    init(sessionPath: String, outputPath: String) throws {
        output = URL(fileURLWithPath: outputPath)
        let session = try JSONDecoder().decode(Session.self, from: Data(contentsOf: URL(fileURLWithPath: sessionPath)))
        guard PasswordProof.isAcceptablePin(session.pin) else { throw ProtocolError.malformed }
        var fingerprint: String?
        if let raw = session.fingerprint, !raw.isEmpty {
            guard let hex = CertificatePin.normalizedFingerprint(raw) else { throw ProtocolError.malformed }
            fingerprint = hex
        }
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 800, height: 500),
                          styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = "RemoteDesktop — teste de transmissão"
        window.contentView = canvas
        window.isReleasedWhenClosed = false
        window.center()
        window.makeKeyAndOrderFront(nil)
        decoder.onDecoded = { [weak self] _ in self?.countFrame() }
        decoder.onPresented = { [weak self] accepted in
            guard let self, accepted else { return }
            self.presentedFrames += 1
        }

        connection = RemoteConnection(
            onState: { [weak self] message, ready in
                guard let self, !ready, !self.completed else { return }
                self.errors.append(message)
                self.finish(success: self.frames >= 10)
            },
            onFrame: { [weak self] png, width, height, _ in
                guard let self, !self.completed else { return }
                guard self.canvas.present(png, width: width, height: height) else {
                    self.finish(success: false)
                    return
                }
                self.lastPNG = png
                self.countFrame()
            },
            onMedia: { [weak self] payload, codec, keyframe, width, height, ptsMs in
                guard let self, !self.completed else { return }
                if codec == ProtocolV2.codecH264 {
                    self.decoder.decode(accessUnit: payload,
                                        keyframe: keyframe,
                                        width: width,
                                        height: height,
                                        ptsMs: ptsMs)
                }
            },
            onWindows: { _ in },
            onDevicePaired: { _, _ in },
            onRoundTrip: { _ in },
            onNotice: { [weak self] message in
                guard let self else { return }
                self.errors.append(message)
            }
        )
        connection?.connect(host: session.host, port: session.port, pin: session.pin,
                            deviceToken: nil, fingerprint: fingerprint)
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(25))
            self?.finish(success: false)
        }
    }

    /// Conta um quadro recebido (PNG direto ou quadro H.264 decodificado).
    private func countFrame() {
        guard !completed else { return }
        frames += 1
        if frames == 1 {
            connection?.send(.click(x: 0.5, y: 0.48, button: .left))
            connection?.send(.text("rascunho"))
            connection?.send(.key(.a, ctrl: true, alt: false, shift: false))
            connection?.send(.text(Self.fixedText))
            connection?.send(.text("!"))
            connection?.send(.key(.backspace, ctrl: false, alt: false, shift: false))
        }
        if frames == 10 {
            // Dá tempo da entrada chegar ao host antes de encerrar.
            Task { @MainActor [weak self] in
                try? await Task.sleep(for: .milliseconds(300))
                self?.finish(success: true)
            }
        }
    }

    private func finish(success: Bool) {
        guard !completed else { return }
        completed = true
        connection?.disconnect()
        do {
            if let lastPNG { try lastPNG.write(to: URL(fileURLWithPath: output.path + ".png")) }
            let report: [String: Any] = [
                "success": success,
                "frames": frames,
                "presentedFrames": presentedFrames,
                "elapsedMs": Date().timeIntervalSince(started) * 1000,
                "rendered": frames > 0,
                "text": Self.fixedText,
                "errors": errors
            ]
            try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: output)
        } catch {
            exit(2)
        }
        exit(success ? 0 : 1)
    }
}
