import AppKit

/// Janela remota: uma janela do Mac mostrando uma janela do PC, com barra
/// de rodapé em vidro para estado, métricas e ações, sem cobrir o vídeo.
@MainActor
final class RemoteWindowController: NSObject, NSWindowDelegate {

    let window: NSWindow
    var onClose: (() -> Void)?

    private let videoView = RemoteVideoView()
    private let titleLabel = NSTextField(labelWithString: "")
    private let statusLabel = NSTextField(labelWithString: "Conectando…")
    private let metricsLabel = NSTextField(labelWithString: "")
    private var connection: RemoteConnection?
    private var generation = UUID()
    private var wantsConnection = false
    private var retryCount = 0
    private var retryTask: Task<Void, Never>?
    private var sampleStart = ProcessInfo.processInfo.systemUptime
    private var sampleFrames = 0
    private var sampleBytes = 0

    private let host: String
    private let port: UInt16
    private let pin: String?
    private let deviceToken: Data?
    private let fingerprint: String?
    private let target: RemoteWindow

    init(target: RemoteWindow, host: String, port: UInt16, pin: String?, deviceToken: Data?, fingerprint: String?) {
        self.target = target
        self.host = host
        self.port = port
        self.pin = pin
        self.deviceToken = deviceToken
        self.fingerprint = fingerprint

        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 960, height: 640),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered,
                          defer: false)
        super.init()
        window.title = target.title.isEmpty ? "RemoteDesktop" : target.title
        window.collectionBehavior.insert(.fullScreenPrimary)
        window.minSize = NSSize(width: 480, height: 320)
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.tabbingMode = .disallowed

        let content = NSView()
        window.contentView = content

        videoView.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(videoView)
        NSLayoutConstraint.activate([
            videoView.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            videoView.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            videoView.topAnchor.constraint(equalTo: content.topAnchor)
        ])

        // Rodapé de vidro separado da área de vídeo.
        let bar = Glass.panel(cornerRadius: 16)
        bar.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(bar)

        titleLabel.font = .systemFont(ofSize: 13, weight: .medium)
        titleLabel.stringValue = target.title
        titleLabel.lineBreakMode = .byTruncatingTail
        statusLabel.font = .systemFont(ofSize: 12)
        statusLabel.textColor = .secondaryLabelColor
        metricsLabel.font = .monospacedDigitSystemFont(ofSize: 11, weight: .regular)
        metricsLabel.textColor = .tertiaryLabelColor

        let reconnect = NSButton(title: "Reconectar", target: self, action: #selector(reconnect))
        Glass.style(reconnect)
        let close = NSButton(title: "Encerrar", target: self, action: #selector(closeSession))
        Glass.style(close)

        let row = NSStackView(views: [titleLabel, statusLabel, metricsLabel, reconnect, close])
        row.orientation = .horizontal
        row.spacing = 12
        row.alignment = .centerY
        row.translatesAutoresizingMaskIntoConstraints = false
        bar.addSubview(row)

        NSLayoutConstraint.activate([
            videoView.bottomAnchor.constraint(equalTo: bar.topAnchor, constant: -8),
            bar.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 16),
            bar.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -16),
            bar.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -16),
            row.leadingAnchor.constraint(equalTo: bar.leadingAnchor, constant: 16),
            row.trailingAnchor.constraint(equalTo: bar.trailingAnchor, constant: -16),
            row.topAnchor.constraint(equalTo: bar.topAnchor, constant: 8),
            row.bottomAnchor.constraint(equalTo: bar.bottomAnchor, constant: -8)
        ])

        videoView.send = { [weak self] event in self?.connection?.send(event) }
    }

    func showWindow() {
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func start() {
        wantsConnection = true
        connect()
    }

    // MARK: - Conexão

    private func connect() {
        generation = UUID()
        let current = generation

        let client = RemoteConnection(
            onState: { [weak self] message, ready in
                guard let self, self.generation == current else { return }
                self.statusLabel.stringValue = message
                if ready {
                    self.retryCount = 0
                } else {
                    self.videoView.resetInput()
                    self.connection = nil
                    // Recusa de credencial não vira reconexão: repetir dispararia
                    // o bloqueio progressivo do host no PC.
                    if message.hasPrefix(RemoteConnection.authFailurePrefix) {
                        self.wantsConnection = false
                        self.statusLabel.stringValue = message
                        return
                    }
                    self.scheduleReconnect()
                }
            },
            onFrame: { [weak self] png, width, height, _ in
                guard let self, self.generation == current else { return }
                self.videoView.presentPng(png, width: width, height: height)
                self.sampleFrames += 1
                self.sampleBytes += png.count
                self.updateMetrics()
            },
            onMedia: { [weak self] payload, codec, keyframe, width, height, ptsMs in
                guard let self, self.generation == current else { return }
                if codec == ProtocolV2.codecH264 {
                    self.videoView.decoder.decode(accessUnit: payload,
                                                  keyframe: keyframe,
                                                  width: width,
                                                  height: height,
                                                  ptsMs: ptsMs)
                } else if codec == ProtocolV2.codecJpeg {
                    // Fallback do host sem encoder H.264.
                    self.videoView.presentJpeg(payload, width: width, height: height)
                } else if codec == ProtocolV2.codecPng {
                    self.videoView.presentPng(payload, width: width, height: height)
                }
                self.sampleFrames += 1
                self.sampleBytes += payload.count
                self.updateMetrics()
            },
            onWindows: { _ in },
            onDevicePaired: { _, _ in },
            onRoundTrip: { _ in },
            onNotice: { [weak self] message in
                guard let self, self.generation == current else { return }
                self.statusLabel.stringValue = message
            },
            onViewport: { [weak self] x, y, width, height in
                guard let self, self.generation == current else { return }
                self.videoView.setBaseRect(x: x, y: y, width: width, height: height)
            }
        )
        videoView.setBaseRect(x: 0, y: 0, width: 0, height: 0)

        videoView.decoder.onError = { [weak self] error in
            guard let self, self.generation == current else { return }
            self.statusLabel.stringValue = "Vídeo: \(error)"
        }

        connection = client
        statusLabel.stringValue = "Conectando…"
        client.connect(host: host, port: port, pin: pin, deviceToken: deviceToken, fingerprint: fingerprint, role: "view",
                       windowId: target.monitorId == nil ? target.id : nil,
                       monitorId: target.monitorId)
    }

    private func scheduleReconnect() {
        guard wantsConnection, retryCount < 5 else {
            wantsConnection = false
            return
        }
        let delay = 1 << retryCount
        retryCount += 1
        statusLabel.stringValue = "Reconectando em \(delay)s…"
        retryTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled, let self, self.wantsConnection else { return }
            self.connect()
        }
    }

    private func updateMetrics() {
        let elapsed = ProcessInfo.processInfo.systemUptime - sampleStart
        guard elapsed >= 2 else { return }
        metricsLabel.stringValue = String(format: "%.0f FPS · %.2f MB/s", Double(sampleFrames) / elapsed, Double(sampleBytes) / elapsed / 1_000_000)
        sampleStart = ProcessInfo.processInfo.systemUptime
        sampleFrames = 0
        sampleBytes = 0
    }

    @objc private func reconnect() {
        retryCount = 0
        wantsConnection = true
        connection?.disconnect()
        connection = nil
        connect()
    }

    @objc private func closeSession() {
        wantsConnection = false
        retryTask?.cancel()
        connection?.disconnect()
        connection = nil
        statusLabel.stringValue = "Encerrado."
    }

    // MARK: - NSWindowDelegate

    func windowDidResignKey(_ notification: Notification) {
        // Manter a sessão viva ao trocar de foco, mas liberar botão pressionado.
        videoView.resetInput()
        videoView.restoreCursor()
    }

    func windowDidBecomeKey(_ notification: Notification) {
        videoView.restoreCursor()
    }

    func windowWillClose(_ notification: Notification) {
        videoView.resetInput()
        wantsConnection = false
        retryTask?.cancel()
        connection?.disconnect()
        connection = nil
        // Remove os retângulos de cursor associados à janela encerrada.
        videoView.restoreCursor()
        onClose?()
    }
}
