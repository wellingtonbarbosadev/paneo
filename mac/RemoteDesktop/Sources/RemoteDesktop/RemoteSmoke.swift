import AppKit

/// Teste de transmissão pelo protocolo v2: conecta com PIN + fingerprint,
/// recebe o vídeo (H.264) ou PNG de fallback e grava o relatório.
///
/// Dois modos:
/// - **Smoke original (preservado)**: sem `durationSeconds`, envia a sequência
///   fixa de entrada no primeiro quadro e encerra no décimo.
/// - **Desempenho**: `durationSeconds` (1–60 s) mede o vídeo por esse tempo
///   após o primeiro access unit H.264, SEM enviar entrada, com a janela e a
///   `AVSampleBufferDisplayLayer` visíveis. O relatório separa quadros
///   recebidos, decodificados e ENFILEIRADOS para a camada de apresentação.
///   `onPresented` conta enfileiramento — não prova vsync nem exibição física.
/// O PIN nunca aparece em stdout nem no relatório.
@MainActor
final class RemoteSmoke {
    private struct Session: Decodable {
        let host: String
        let port: UInt16
        let fingerprint: String?
        let pin: String
        /// Modo de desempenho: duração da medição em segundos (1–60). Ausente
        /// ou nulo mantém o smoke original de 10 quadros com entrada fixa.
        let durationSeconds: Int?
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

    private let startTimeNs = RemoteSmoke.monotonicNs()

    // MARK: Modo de desempenho

    /// Duração pedida para a janela de medição; nil mantém o smoke original.
    private let measureSeconds: TimeInterval?
    /// Access units H.264 recebidas. Nenhuma é descartada pelo smoke: todas
    /// passam pelo decodificador (a espera por keyframe é decisão dele).
    private var receivedAccessUnits = 0
    private var receivedPNGFrames = 0
    /// Quadros decodificados com sucesso (callback `onDecoded` do VideoToolbox).
    /// Pode conter menos que o recebido: falhas do callback não contam aqui.
    private var decodedFrames = 0
    private var decodedSize = CGSize.zero
    /// Samples ENFILEIRADOS na `AVSampleBufferDisplayLayer` (`onPresented`).
    /// Não prova vsync nem exibição física — nome explícito de enfileiramento.
    private var displayEnqueuedFrames = 0
    private var displayEnqueueRejected = 0
    private var lastHeaderWidth = 0
    private var lastHeaderHeight = 0
    private var unexpectedCodecReported = false

    private struct MeasureSnapshot {
        var received: Int
        var decoded: Int
        var enqueued: Int
    }

    private var measureStartNs: UInt64?
    private var measureSnapshot: MeasureSnapshot?
    private var guardTask: Task<Void, Never>?
    private var measureTask: Task<Void, Never>?

    /// NS monotônicos, imunes a ajustes do relógio de parede.
    private static func monotonicNs() -> UInt64 { clock_gettime_nsec_np(CLOCK_MONOTONIC_RAW) }

    private static let limitations: [String] = [
        "onPresented conta samples ENFILEIRADOS na AVSampleBufferDisplayLayer; não prova vsync nem exibição física em monitor.",
        "displayLayerStatus reflete o estado interno da camada; não é captura fótica.",
        "A medição começa no primeiro access unit H.264 recebido; a criação da sessão VideoToolbox acontece dentro da janela de medição.",
        "Sem entrada enviada no modo de desempenho: conteúdo majoritariamente estático pode limitar a cadência do host.",
        "Rede local medida pelo transport; não mede internet, encoder do host nem latência input-to-photon."
    ]

    init(sessionPath: String, outputPath: String) throws {
        output = URL(fileURLWithPath: outputPath)
        let session = try JSONDecoder().decode(Session.self, from: Data(contentsOf: URL(fileURLWithPath: sessionPath)))
        guard PasswordProof.isAcceptablePin(session.pin) else { throw ProtocolError.malformed }
        var fingerprint: String?
        if let raw = session.fingerprint, !raw.isEmpty {
            guard let hex = CertificatePin.normalizedFingerprint(raw) else { throw ProtocolError.malformed }
            fingerprint = hex
        }
        if let seconds = session.durationSeconds, !(1...60).contains(seconds) {
            throw ProtocolError.malformed // durationSeconds deve ficar em 1..60
        }
        measureSeconds = session.durationSeconds.map { TimeInterval($0) }

        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 800, height: 500),
                          styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = measureSeconds == nil
            ? "RemoteDesktop — teste de transmissão"
            : "RemoteDesktop — smoke de desempenho"
        if measureSeconds == nil {
            window.contentView = canvas
        } else {
            // A camada de apresentação só renderiza com a janela visível: host
            // com backing layer recebe o decoder.displayLayer como subcamada.
            let host = SmokeDisplayHost(videoLayer: decoder.displayLayer)
            window.contentView = host
        }
        window.isReleasedWhenClosed = false
        window.center()
        window.makeKeyAndOrderFront(nil)

        decoder.onNeedKeyframe = { [weak self] in self?.connection?.send(.needKeyframe) }
        decoder.onDecoded = { [weak self] size in
            guard let self, !self.completed else { return }
            self.decodedFrames += 1
            if size != .zero { self.decodedSize = size }
            if self.measureSeconds == nil { self.countFrame() }
        }
        decoder.onPresented = { [weak self] accepted in
            guard let self, !self.completed else { return }
            if accepted {
                self.displayEnqueuedFrames += 1
                if self.measureSeconds == nil { self.presentedFrames += 1 }
            } else {
                self.displayEnqueueRejected += 1
            }
        }
        if measureSeconds != nil {
            decoder.onError = { [weak self] message in
                self?.errors.append("Decodificador: \(message)")
            }
        }

        connection = RemoteConnection(
            onState: { [weak self] message, ready in
                guard let self, !ready, !self.completed else { return }
                self.errors.append(message)
                self.finish(success: self.measureSeconds == nil && self.frames >= 10)
            },
            onFrame: { [weak self] png, width, height, _ in
                guard let self, !self.completed else { return }
                self.receivedPNGFrames += 1
                guard self.canvas.present(png, width: width, height: height) else {
                    if self.measureSeconds == nil {
                        self.finish(success: false)
                    } else {
                        self.errors.append("PNG de fallback inválido.")
                    }
                    return
                }
                self.lastPNG = png
                if self.measureSeconds == nil { self.countFrame() }
            },
            onMedia: { [weak self] payload, codec, keyframe, width, height, ptsMs in
                guard let self, !self.completed else { return }
                guard codec == ProtocolV2.codecH264 else {
                    if !self.unexpectedCodecReported {
                        self.unexpectedCodecReported = true
                        // O smoke original ignora mídia não H.264 silenciosamente;
                        // no modo de desempenho isso vira evidência (uma vez só).
                        self.errors.append("Mídia de codec inesperado (\(codec)) recebida.")
                    }
                    return
                }
                // Nenhuma access unit H.264 é descartada pelo smoke.
                self.receivedAccessUnits += 1
                self.lastHeaderWidth = width
                self.lastHeaderHeight = height
                if self.measureStartNs == nil, self.measureSeconds != nil {
                    // Janela monotônica começa no primeiro access unit recebido.
                    self.measureStartNs = Self.monotonicNs()
                    self.measureSnapshot = MeasureSnapshot(received: self.receivedAccessUnits,
                                                           decoded: self.decodedFrames,
                                                           enqueued: self.displayEnqueuedFrames)
                    self.scheduleMeasurementEnd()
                }
                self.decoder.decode(accessUnit: payload,
                                    keyframe: keyframe,
                                    width: width,
                                    height: height,
                                    ptsMs: ptsMs)
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

        // Guarda de segurança idêntica à do smoke original: encerra após 25 s
        // caso nada chegue. Cancelada quando a medição começa.
        guardTask = Task { @MainActor [weak self] in
            // try? engoliria o cancelamento e encerraria a medição por engano:
            // a captura explícita distingue dormir de ser cancelado.
            do { try await Task.sleep(for: .seconds(25)) } catch { return }
            self?.finish(success: false)
        }
    }

    /// Agenda o fim da janela de medição (duração pedida) e desarma a guarda.
    private func scheduleMeasurementEnd() {
        guard let seconds = measureSeconds, measureTask == nil else { return }
        guardTask?.cancel()
        guardTask = nil
        measureTask = Task { @MainActor [weak self] in
            do { try await Task.sleep(for: .seconds(seconds)) } catch { return }
            self?.finish(success: true)
        }
    }

    /// Contador do smoke original: quadros de vídeo que entram (PNG ou H.264
    /// decodificado). Envia a sequência fixa de entrada no primeiro quadro.
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
        var passed = success
        do {
            if let lastPNG { try lastPNG.write(to: URL(fileURLWithPath: output.path + ".png")) }
            let elapsedMs = Date().timeIntervalSince(started) * 1000
            let report: [String: Any]
            if let measureSeconds {
                report = performanceReport(measureSeconds: measureSeconds,
                                           success: success,
                                           elapsedMs: elapsedMs)
            } else {
                // Relatório do smoke original, sem chaves novas.
                report = ["success": success,
                          "frames": frames,
                          "presentedFrames": presentedFrames,
                          "elapsedMs": elapsedMs,
                          "rendered": frames > 0,
                          "text": Self.fixedText,
                          "errors": errors]
            }
            passed = report["success"] as? Bool ?? false
            try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: output)
        } catch {
            exit(2)
        }
        exit(passed ? 0 : 1)
    }

    /// Relatório do modo de desempenho: separa recebidos / decodificados /
    /// enfileirados, codec, dimensões, duração monotônica da medição, FPS e erros.
    private func performanceReport(measureSeconds requested: TimeInterval,
                                   success passed: Bool,
                                   elapsedMs: Double) -> [String: Any] {
        let endNs = Self.monotonicNs()
        let startNs = measureStartNs ?? endNs
        let measuredSeconds = Double(endNs - startNs) / 1_000_000_000
        let snapshot = measureSnapshot ?? MeasureSnapshot(received: receivedAccessUnits,
                                                          decoded: decodedFrames,
                                                          enqueued: displayEnqueuedFrames)
        let inWindowReceived = receivedAccessUnits - snapshot.received
        let inWindowDecoded = decodedFrames - snapshot.decoded
        let inWindowEnqueued = displayEnqueuedFrames - snapshot.enqueued
        let decodedFps = measuredSeconds > 0 ? Double(inWindowDecoded) / measuredSeconds : 0
        let enqueuedFps = measuredSeconds > 0 ? Double(inWindowEnqueued) / measuredSeconds : 0

        let layer = decoder.displayLayer
        let statusName: String
        switch layer.status {
        case .unknown: statusName = "unknown"
        case .failed: statusName = "failed"
        case .rendering: statusName = "rendering"
        @unknown default: statusName = "desconhecido(\(layer.status.rawValue))"
        }

        var codec = "nenhum"
        if receivedAccessUnits > 0 { codec = "h264" } else if receivedPNGFrames > 0 { codec = "png" }

        var report: [String: Any] = [
            "mode": "performance",
            // Sucesso exige janela completa com quadros decodificados E
            // enfileirados — decodificar sem exibir é a falha conhecida.
            "success": passed && measuredSeconds >= requested && inWindowDecoded > 0 && inWindowEnqueued > 0 && errors.isEmpty && layer.status == .rendering,
            "targetFps": 60,
            "meets60Fps": decodedFps >= 60 && enqueuedFps >= 60,
            "requestedDurationSeconds": Int(requested),
            "measuredSeconds": measuredSeconds,
            "measurementClock": "CLOCK_MONOTONIC_RAW",
            "measurementStart": "primeiroAccessUnitH264Recebido",
            "codec": codec,
            "videoWidth": decodedSize.width > 0 ? Int(decodedSize.width) : lastHeaderWidth,
            "videoHeight": decodedSize.height > 0 ? Int(decodedSize.height) : lastHeaderHeight,
            "receivedAccessUnits": receivedAccessUnits,
            "receivedAccessUnitsInMeasurement": inWindowReceived,
            "decodedFrames": decodedFrames,
            "decodedFramesInMeasurement": inWindowDecoded,
            "decodedFps": decodedFps,
            "displayEnqueuedFrames": displayEnqueuedFrames,
            "displayEnqueuedFramesInMeasurement": inWindowEnqueued,
            "displayEnqueuedFps": enqueuedFps,
            "displayEnqueueRejected": displayEnqueueRejected,
            "displayLayerStatus": statusName,
            "displayLayerReadyForMoreMediaData": layer.isReadyForMoreMediaData,
            "decoderErrors": errors.filter { $0.hasPrefix("Decodificador:") }.count,
            "pngFallbackFrames": receivedPNGFrames,
            "elapsedMs": elapsedMs,
            "limitations": Self.limitations,
            "errors": errors
        ]
        if let firstMs = measureStartNs.map({ Double($0 - startTimeNs) / 1_000_000 }) {
            report["firstAccessUnitMs"] = firstMs
        }
        if layer.status == .failed {
            report["displayLayerFailure"] = layer.error?.localizedDescription ?? ""
        }
        return report
    }
}

/// Host da camada de apresentação no modo de desempenho: garante o anexo do
/// `decoder.displayLayer` à árvore de camadas de uma janela visível e mantém o
/// frame da subcamada em sincronia com os limites da view.
private final class SmokeDisplayHost: NSView {
    private let videoLayer: CALayer

    init(videoLayer: CALayer) {
        self.videoLayer = videoLayer
        super.init(frame: NSRect(x: 0, y: 0, width: 800, height: 500))
        wantsLayer = true
        needsLayout = true
        addVideoLayerIfNeeded()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("Sem Codable informado no smoke.") }

    private func addVideoLayerIfNeeded() {
        guard videoLayer.superlayer !== layer, let backing = layer else { return }
        backing.addSublayer(videoLayer)
    }

    override func layout() {
        super.layout()
        addVideoLayerIfNeeded()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        videoLayer.frame = bounds
        CATransaction.commit()
    }
}
