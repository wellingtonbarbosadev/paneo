import AppKit

/// Janela remota: uma janela do Mac mostrando uma janela do PC, com barra
/// de rodapé em vidro para estado, métricas e ações, sem cobrir o vídeo.
@MainActor
final class RemoteWindowController: NSObject, NSWindowDelegate {

    let window: NSWindow
    var onClose: (() -> Void)?

    private let videoView = RemoteVideoView()
    private lazy var zoomControls = ZoomControlsView(videoView: videoView)
    private let titleLabel = NSTextField(labelWithString: "")
    private let statusLabel = NSTextField(labelWithString: "Conectando…")
    private let detailsButton = NSButton(title: "Detalhes", target: nil, action: nil)
    private let settingsButton = NSButton()
    private let windowActionsView = WindowActionsView(frame: .zero)
    private let detailsPopover = NSPopover()
    private let detailsLabel = NSTextField(labelWithString: "Aguardando métricas…")
    private var metricsText = "Aguardando métricas…"
    private var reconnectButton: NSButton!
    private var connection: RemoteConnection?
    private var generation = UUID()
    private var wantsConnection = false
    private var retryCount = 0
    private var retryTask: Task<Void, Never>?
    private var sampleStart = ProcessInfo.processInfo.systemUptime
    private var sampleFrames = 0
    private var sampleBytes = 0
    private var remoteWindowState: RemoteWindowState?
    private var restoreOnInitialWindowState = false
    private var readingResizeTask: Task<Void, Never>?
    private var lastReadingSize: (Int, Int)?
    private let settings: AppSettings
    private var autoFitEnabled: Bool
    private var appliedAutoFitPreference: Bool

    private let host: String
    private let port: UInt16
    private let pin: String?
    private let deviceToken: Data?
    private let fingerprint: String?
    private let target: RemoteWindow

    init(target: RemoteWindow, host: String, port: UInt16, pin: String?, deviceToken: Data?, fingerprint: String?,
         settings: AppSettings = .shared) {
        self.settings = settings
        autoFitEnabled = settings.autoFitWindows
        appliedAutoFitPreference = settings.autoFitWindows
        self.target = target
        restoreOnInitialWindowState = target.minimized
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
        NotificationCenter.default.addObserver(self, selector: #selector(settingsDidChange),
                                               name: AppSettings.didChange, object: settings)
        window.title = target.title.isEmpty ? "Paneo" : "Paneo — \(target.title)"
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
        titleLabel.stringValue = target.title.isEmpty ? target.process : target.title
        titleLabel.lineBreakMode = .byTruncatingTail
        titleLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        statusLabel.font = .systemFont(ofSize: 12)
        statusLabel.textColor = .secondaryLabelColor
        statusLabel.lineBreakMode = .byTruncatingTail
        statusLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        detailsButton.image = NSImage(systemSymbolName: "chart.bar.xaxis", accessibilityDescription: "Métricas da sessão")
        detailsButton.imagePosition = .imageLeading
        detailsButton.target = self
        detailsButton.action = #selector(showDetails)
        detailsButton.toolTip = "Mostrar métricas da sessão"
        Glass.style(detailsButton)
        settingsButton.image = NSImage(systemSymbolName: "gearshape", accessibilityDescription: "Ajustes")
        settingsButton.toolTip = "Abrir Ajustes"
        settingsButton.setAccessibilityLabel("Ajustes")
        settingsButton.target = self
        settingsButton.action = #selector(showSettings)
        Glass.style(settingsButton)

        let reconnect = NSButton(title: "Reconectar", target: self, action: #selector(reconnect))
        reconnectButton = reconnect
        reconnect.isHidden = true
        Glass.style(reconnect)
        let close = NSButton(title: "Encerrar", target: self, action: #selector(closeSession))
        Glass.style(close)

        let row = NSStackView(views: [titleLabel, statusLabel, windowActionsView,
                                      zoomControls, detailsButton, settingsButton, reconnect, close])
        row.orientation = .horizontal
        row.spacing = 8
        row.alignment = .centerY
        row.translatesAutoresizingMaskIntoConstraints = false
        bar.addSubview(row)
        detailsLabel.font = .monospacedDigitSystemFont(ofSize: 12, weight: .regular)
        detailsLabel.textColor = .secondaryLabelColor
        detailsLabel.alignment = .center
        detailsLabel.lineBreakMode = .byWordWrapping
        detailsLabel.frame = NSRect(x: 0, y: 0, width: 320, height: 52)
        let detailsController = NSViewController()
        detailsController.view = detailsLabel
        detailsPopover.contentViewController = detailsController
        detailsPopover.behavior = .transient

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
        videoView.onBoundsChange = { [weak self] in self?.scheduleReadingResize() }
        videoView.onZoomChange = { [weak self] in
            guard let self else { return }
            self.videoView.beginZoomTransition()
            self.sendWindowAction("reading", enabled: true)
        }
        windowActionsView.update(state: nil, windowId: target.id, isMonitor: target.isMonitor,
                                 autoFitEnabled: autoFitEnabled)
        windowActionsView.onAction = { [weak self] action, enabled in self?.sendWindowAction(action, enabled: enabled) }
        windowActionsView.onRequestMenus = { [weak self] in self?.connection?.requestWindowMenus() }
        windowActionsView.onInvokeMenu = { [weak self] id in self?.connection?.invokeWindowMenu(id: id) }
        windowActionsView.onMenuOpen = { [weak self] in
            self?.videoView.resetInput()
            self?.window.makeFirstResponder(nil)
        }
        windowActionsView.onMenuClose = { [weak self] in
            guard let self, self.remoteWindowState?.minimized != true,
                  !self.videoView.isHidden, self.window.isKeyWindow else { return }
            self.window.makeFirstResponder(self.videoView)
        }
    }

    func showWindow() {
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        if remoteWindowState?.minimized == true { sendWindowAction("restore") }
    }

    func start() {
        wantsConnection = true
        connect()
    }

    // MARK: - Conexão

    private func connect() {
        generation = UUID()
        let current = generation
        videoView.beginStreamTransition()

        let client = RemoteConnection(
            onState: { [weak self] message, ready in
                guard let self, self.generation == current else { return }
                self.statusLabel.stringValue = message
                self.statusLabel.toolTip = message
                if ready {
                    self.retryCount = 0
                    self.statusLabel.stringValue = "Conectado"
                    self.statusLabel.toolTip = message
                    AppMotion.setVisible(self.reconnectButton, false)
                } else {
                    self.videoView.resetInput()
                    self.connection = nil
                    self.remoteWindowState = nil
                    self.videoView.allowsWindowZoom = false
                    self.lastReadingSize = nil
                    self.windowActionsView.update(state: nil, windowId: self.target.id, isMonitor: self.target.isMonitor,
                                                  autoFitEnabled: self.autoFitEnabled)
                    // Recusa de credencial não vira reconexão: repetir dispararia
                    // o bloqueio progressivo do host no PC.
                    if message.hasPrefix(RemoteConnection.authFailurePrefix) {
                        self.wantsConnection = false
                        self.statusLabel.stringValue = message
                        AppMotion.setVisible(self.reconnectButton, false)
                        return
                    }
                    self.scheduleReconnect()
                }
            },
            onFrame: { [weak self] png, width, height, _ in
                guard let self, self.generation == current,
                      self.remoteWindowState?.minimized != true, !self.window.isMiniaturized else { return }
                self.videoView.presentPng(png, width: width, height: height)
                self.sampleFrames += 1
                self.sampleBytes += png.count
                self.updateMetrics()
            },
            onMedia: { [weak self] payload, codec, keyframe, width, height, ptsMs in
                guard let self, self.generation == current else { return }
                if codec == ProtocolV2.codecH264 {
                    // Descartar access units ao minimizar quebraria as referências do H.264.
                    self.videoView.decoder.decode(accessUnit: payload,
                                                  keyframe: keyframe,
                                                  width: width,
                                                  height: height,
                                                  ptsMs: ptsMs)
                } else if self.remoteWindowState?.minimized != true, !self.window.isMiniaturized,
                          codec == ProtocolV2.codecJpeg {
                    // Fallback do host sem encoder H.264.
                    self.videoView.presentJpeg(payload, width: width, height: height)
                } else if self.remoteWindowState?.minimized != true, !self.window.isMiniaturized,
                          codec == ProtocolV2.codecPng {
                    self.videoView.presentPng(payload, width: width, height: height)
                } else {
                    return
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
            },
            onWindowState: { [weak self] state in
                guard let self, self.generation == current, state.windowId == self.target.id else { return }
                self.remoteWindowState = state
                self.videoView.allowsWindowZoom = state.canResize && !state.minimized && !self.target.isMonitor
                self.windowActionsView.update(state: state, windowId: self.target.id, isMonitor: self.target.isMonitor,
                                              autoFitEnabled: self.autoFitEnabled)
                self.videoView.resetInput()
                self.videoView.isHidden = state.minimized
                if state.minimized {
                    self.statusLabel.stringValue = "Minimizada no Windows"
                    self.window.makeFirstResponder(nil)
                } else {
                    self.statusLabel.stringValue = "Conectado"
                    if !self.zoomControls.isShowing { self.window.makeFirstResponder(self.videoView) }
                }
                if self.restoreOnInitialWindowState {
                    self.restoreOnInitialWindowState = false
                    if state.minimized { self.connection?.sendWindowAction("restore") }
                }
                if !state.minimized { self.scheduleReadingResize(debounce: self.lastReadingSize != nil) }
            },
            onWindowCommandResult: { [weak self] action, success, message in
                guard let self, self.generation == current else { return }
                if !success {
                    if action == "reading" {
                        self.autoFitEnabled = false
                        self.videoView.allowsWindowZoom = false
                        self.readingResizeTask?.cancel()
                        self.windowActionsView.update(state: self.remoteWindowState, windowId: self.target.id,
                                                      isMonitor: self.target.isMonitor,
                                                      autoFitEnabled: self.autoFitEnabled)
                    }
                    self.statusLabel.stringValue = message ?? "O Windows recusou a ação \(action)."
                }
            },
            onWindowMenus: { [weak self] menus in
                guard let self, self.generation == current else { return }
                self.windowActionsView.showMenus(menus)
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
            AppMotion.setVisible(reconnectButton, true)
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
        metricsText = String(format: "%d × %d px\n%.0f FPS · %.2f Mbps",
                             target.width, target.height,
                             Double(sampleFrames) / elapsed,
                             Double(sampleBytes) / elapsed * 8 / 1_000_000)
        detailsLabel.stringValue = metricsText
        sampleStart = ProcessInfo.processInfo.systemUptime
        sampleFrames = 0
        sampleBytes = 0
    }

    @objc private func reconnect() {
        AppMotion.setVisible(reconnectButton, false)
        retryCount = 0
        wantsConnection = true
        connection?.disconnect()
        connection = nil
        resetRemoteWindowState()
        connect()
    }

    @objc private func closeSession() {
        wantsConnection = false
        retryTask?.cancel()
        connection?.disconnect()
        connection = nil
        resetRemoteWindowState()
        statusLabel.stringValue = "Encerrado."
        AppMotion.setVisible(reconnectButton, false)
    }

    private func resetRemoteWindowState() {
        remoteWindowState = nil
        videoView.allowsWindowZoom = false
        readingResizeTask?.cancel()
        readingResizeTask = nil
        lastReadingSize = nil
        windowActionsView.update(state: nil, windowId: target.id, isMonitor: target.isMonitor,
                                 autoFitEnabled: autoFitEnabled)
        videoView.resetInput()
    }

    @objc private func showDetails() {
        detailsLabel.stringValue = metricsText
        detailsPopover.show(relativeTo: detailsButton.bounds, of: detailsButton, preferredEdge: .maxY)
    }

    @objc private func settingsDidChange() {
        guard appliedAutoFitPreference != settings.autoFitWindows else { return }
        appliedAutoFitPreference = settings.autoFitWindows
        autoFitEnabled = settings.autoFitWindows
        sendWindowAction("reading", enabled: autoFitEnabled)
    }

    private func sendWindowAction(_ action: String, enabled: Bool? = nil) {
        guard let connection, remoteWindowState != nil, !target.isMonitor else { return }
        if action == "reading" {
            autoFitEnabled = enabled ?? !autoFitEnabled
            readingResizeTask?.cancel()
            lastReadingSize = nil
            windowActionsView.update(state: remoteWindowState, windowId: target.id,
                                     isMonitor: target.isMonitor, autoFitEnabled: autoFitEnabled)
            if autoFitEnabled { scheduleReadingResize(debounce: zoomControls.isShowing) }
            else { connection.sendWindowAction(action, enabled: false) }
            return
        }
        if action == "minimize" {
            readingResizeTask?.cancel()
        } else if ["maximize", "close"].contains(action) ||
            (action == "restore" && remoteWindowState?.minimized != true) {
            let wasAutoFitEnabled = autoFitEnabled
            autoFitEnabled = false
            readingResizeTask?.cancel()
            lastReadingSize = nil
            if wasAutoFitEnabled { connection.sendWindowAction("reading", enabled: false) }
        } else if action == "restore" {
            lastReadingSize = nil
        }
        windowActionsView.update(state: remoteWindowState, windowId: target.id,
                                 isMonitor: target.isMonitor, autoFitEnabled: autoFitEnabled)
        connection.sendWindowAction(action, enabled: enabled)
    }

    private func scheduleReadingResize(debounce: Bool = true) {
        readingResizeTask?.cancel()
        readingResizeTask = Task { [weak self] in
            if debounce { try? await Task.sleep(for: .milliseconds(120)) }
            guard !Task.isCancelled, let self, self.autoFitEnabled,
                  !self.target.isMonitor, self.remoteWindowState?.canResize == true,
                  self.remoteWindowState?.minimized != true,
                  let connection = self.connection else { return }
            let preferred = self.videoView.preferredWindowSize
            let size = (Int(preferred.width.rounded()), Int(preferred.height.rounded()))
            guard size.0 > 0, size.1 > 0 else { return }
            guard self.lastReadingSize?.0 != size.0 || self.lastReadingSize?.1 != size.1 else { return }
            self.lastReadingSize = size
            connection.sendWindowAction("reading", enabled: true, width: size.0, height: size.1)
        }
    }

    @objc private func showSettings() {
        NSApp.sendAction(NSSelectorFromString("showSettings:"), to: NSApp.delegate, from: settingsButton)
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
        zoomControls.close()
        videoView.resetInput()
        wantsConnection = false
        retryTask?.cancel()
        connection?.disconnect()
        connection = nil
        remoteWindowState = nil
        readingResizeTask?.cancel()
        readingResizeTask = nil
        lastReadingSize = nil
        windowActionsView.update(state: nil, windowId: target.id, isMonitor: target.isMonitor)
        // Remove os retângulos de cursor associados à janela encerrada.
        videoView.restoreCursor()
        onClose?()
    }
}
