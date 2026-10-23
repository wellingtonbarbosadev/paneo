import AppKit

/// Dados de conexão injetados pelos testes de fumaça, sem tocar no Chaves.
struct ConnectionPreset: Sendable {
    let host: String
    let port: String
    let pin: String
    let fingerprint: String?
}

/// Janela principal do Mac: conexão única integrada com header de miniaturas
/// e área principal com streaming e alternância instantânea de apps do Windows.
@MainActor
final class MainWindowController: NSObject, NSWindowDelegate {

    let window: NSWindow
    var onClose: (() -> Void)?
    var onReady: (() -> Void)?

    // MARK: - Estado de Conexão e Credenciais
    private let address = NSTextField(string: "")
    private let port = NSTextField(string: "9443")
    private let pin = NSSecureTextField(string: "")
    private let status = NSTextField(wrappingLabelWithString: "Informe o IP do Windows e o PIN escolhido no Windows.")
    private let button = NSButton(title: "Conectar", target: nil, action: nil)

    private var controlConnection: RemoteConnection?
    private var viewConnection: RemoteConnection?
    private var generation = UUID()
    private var viewGeneration = UUID()
    private var wantsConnection = false
    private var retryCount = 0
    private var retryTask: Task<Void, Never>?
    private var refreshTask: Task<Void, Never>?
    private var usingDeviceToken = false
    private var windows: [RemoteWindow] = []
    private var monitors: [RemoteMonitor] = []
    private var selectedWindow: RemoteWindow?
    /// Apps/monitores destacados em janelas próprias do macOS, por id do card.
    private var detached: [Int64: RemoteWindowController] = [:]

    // MARK: - Barra recolhível
    private var barTopConstraint: NSLayoutConstraint?
    private var stageTopExpanded: NSLayoutConstraint?
    private var stageTopCollapsed: NSLayoutConstraint?
    private var isBarCollapsed = false
    private var isBarPeeking = false
    private var isBarAnimating = false
    private var barAnimationGeneration = UUID()
    private var peekHideTask: Task<Void, Never>?
    private let peekStrip = PeekStripView()
    private let preset: ConnectionPreset?
    private let persistPairing: Bool

    // MARK: - Métricas de Streaming
    private var sampleStart = ProcessInfo.processInfo.systemUptime
    private var sampleFrames = 0
    private var sampleBytes = 0

    // MARK: - Componentes Visuais
    private let rootContentView = NSView()
    private let welcomeCard: NSView
    private let workspaceView = InteractionLayoutView()
    private let thumbnailBar = WindowThumbnailBar()
    private let mainStage = NSView()
    private let emptyPlaceholder = NSStackView()
    private let emptyTitle = NSTextField(labelWithString: "Selecione um aplicativo no topo")
    private let emptyDesc = NSTextField(wrappingLabelWithString: "Clique em uma das miniaturas acima para iniciar a transmissão em 60 FPS com controle total do teclado e mouse.")
    private let videoView = RemoteVideoView()
    private let floatingBar: NSView
    private let activeAppTitleLabel = NSTextField(labelWithString: "")
    private let streamStatusLabel = NSTextField(labelWithString: "")
    private let streamMetricsLabel = NSTextField(labelWithString: "")
    private let searchPalette = QuickSearchPalette()
    private let clipboardSync = ClipboardSync()
    private var availableApps: [RemoteApp] = []
    private var lastAdaptiveQualityUpdate: TimeInterval = 0
    private var isLowQualityMode = false
    private let audioPlayback = AudioPlayback()

    init(preset: ConnectionPreset? = nil, persistPairing: Bool = true) {
        self.preset = preset
        self.persistPairing = persistPairing

        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1040, height: 680),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered,
                          defer: false)
        welcomeCard = Glass.panel(cornerRadius: 22)
        floatingBar = Glass.panel(cornerRadius: 14)
        super.init()

        window.title = "RemoteDesktop"
        window.minSize = NSSize(width: 680, height: 480)
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.tabbingMode = .disallowed
        window.collectionBehavior.insert(.fullScreenPrimary)
        window.contentView = rootContentView

        setupWelcomeView()
        setupWorkspaceView()
        workspaceView.onLayout = { [weak self] in self?.updateVideoInteractionExclusions() }
        prefill()
        showWelcome(true)
        window.center()
    }

    // MARK: - Configuração da UI de Login / Boas-vindas

    private func setupWelcomeView() {
        welcomeCard.translatesAutoresizingMaskIntoConstraints = false
        rootContentView.addSubview(welcomeCard)

        let appIcon = NSImageView()
        appIcon.image = NSImage(systemSymbolName: "display.2", accessibilityDescription: "RemoteDesktop")
        appIcon.contentTintColor = .controlAccentColor
        appIcon.imageScaling = .scaleProportionallyUpOrDown
        appIcon.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            appIcon.widthAnchor.constraint(equalToConstant: 44),
            appIcon.heightAnchor.constraint(equalToConstant: 44)
        ])

        let titleLabel = NSTextField(labelWithString: "RemoteDesktop")
        titleLabel.font = .systemFont(ofSize: 22, weight: .bold)
        titleLabel.alignment = .center

        let headerStack = NSStackView(views: [appIcon, titleLabel])
        headerStack.orientation = .vertical
        headerStack.spacing = 6
        headerStack.alignment = .centerX

        address.placeholderString = "192.168.0.100"
        pin.placeholderString = "PIN de pareamento"
        button.target = self
        button.action = #selector(toggle)
        Glass.style(button)

        address.setAccessibilityLabel("Endereço IP do Windows")
        port.setAccessibilityLabel("Porta")
        pin.setAccessibilityLabel("PIN escolhido no Windows")

        let addressRow = NSStackView(views: [address, port])
        addressRow.orientation = .horizontal
        addressRow.spacing = 8
        addressRow.alignment = .centerY

        let pinRow = NSStackView(views: [pin, button])
        pinRow.orientation = .horizontal
        pinRow.spacing = 8
        pinRow.alignment = .centerY

        for row in [addressRow, pinRow] {
            for field in row.arrangedSubviews {
                row.setVisibilityPriority(.mustHold, for: field)
            }
        }

        let fields = NSStackView(views: [
            headerStack,
            Glass.caption("Endereço IP do Windows · Porta"), addressRow,
            Glass.caption("PIN escolhido no Windows"), pinRow
        ])
        fields.orientation = .vertical
        fields.spacing = 10
        fields.alignment = .leading
        for field in fields.arrangedSubviews {
            fields.setVisibilityPriority(.mustHold, for: field)
        }
        fields.translatesAutoresizingMaskIntoConstraints = false
        welcomeCard.addSubview(fields)

        status.font = .systemFont(ofSize: 12)
        status.textColor = .secondaryLabelColor
        status.alignment = .center
        status.translatesAutoresizingMaskIntoConstraints = false
        welcomeCard.addSubview(status)

        NSLayoutConstraint.activate([
            welcomeCard.centerXAnchor.constraint(equalTo: rootContentView.centerXAnchor),
            welcomeCard.centerYAnchor.constraint(equalTo: rootContentView.centerYAnchor),
            welcomeCard.widthAnchor.constraint(equalToConstant: 440),

            headerStack.centerXAnchor.constraint(equalTo: fields.centerXAnchor),
            addressRow.widthAnchor.constraint(equalTo: fields.widthAnchor),
            pinRow.widthAnchor.constraint(equalTo: fields.widthAnchor),
            port.widthAnchor.constraint(equalToConstant: 68),
            address.widthAnchor.constraint(greaterThanOrEqualToConstant: 200),
            pin.widthAnchor.constraint(greaterThanOrEqualToConstant: 200),

            fields.leadingAnchor.constraint(equalTo: welcomeCard.leadingAnchor, constant: 24),
            fields.trailingAnchor.constraint(equalTo: welcomeCard.trailingAnchor, constant: -24),
            fields.topAnchor.constraint(equalTo: welcomeCard.topAnchor, constant: 24),

            status.leadingAnchor.constraint(equalTo: welcomeCard.leadingAnchor, constant: 20),
            status.trailingAnchor.constraint(equalTo: welcomeCard.trailingAnchor, constant: -20),
            status.topAnchor.constraint(equalTo: fields.bottomAnchor, constant: 14),
            status.bottomAnchor.constraint(equalTo: welcomeCard.bottomAnchor, constant: -20)
        ])
    }

    // MARK: - Configuração da UI de Workspace (Header + Stage)

    private func setupWorkspaceView() {
        workspaceView.translatesAutoresizingMaskIntoConstraints = false
        rootContentView.addSubview(workspaceView)

        NSLayoutConstraint.activate([
            workspaceView.leadingAnchor.constraint(equalTo: rootContentView.leadingAnchor),
            workspaceView.trailingAnchor.constraint(equalTo: rootContentView.trailingAnchor),
            workspaceView.topAnchor.constraint(equalTo: rootContentView.topAnchor),
            workspaceView.bottomAnchor.constraint(equalTo: rootContentView.bottomAnchor)
        ])

        // 1. Barra de miniaturas no topo (Header)
        workspaceView.addSubview(thumbnailBar)
        thumbnailBar.onSelectWindow = { [weak self] target in
            self?.select(window: target)
        }
        thumbnailBar.onRefresh = { [weak self] in
            self?.controlConnection?.requestWindows()
        }
        thumbnailBar.onDisconnect = { [weak self] in
            self?.disconnect()
        }
        thumbnailBar.onDetachWindow = { [weak self] target in
            self?.detach(window: target)
        }
        thumbnailBar.onCollapse = { [weak self] in
            self?.toggleBarCollapsed()
        }
        thumbnailBar.onHoverChanged = { [weak self] inside in
            self?.barHoverChanged(inside)
        }

        // 2. Área principal de conteúdo (Stage)
        mainStage.translatesAutoresizingMaskIntoConstraints = false
        workspaceView.addSubview(mainStage)

        // Placeholder de espera
        let emptyIcon = NSImageView()
        emptyIcon.image = NSImage(systemSymbolName: "cursorarrow.click.2", accessibilityDescription: nil)
        emptyIcon.contentTintColor = .tertiaryLabelColor
        emptyIcon.imageScaling = .scaleProportionallyUpOrDown
        NSLayoutConstraint.activate([
            emptyIcon.widthAnchor.constraint(equalToConstant: 52),
            emptyIcon.heightAnchor.constraint(equalToConstant: 52)
        ])

        emptyTitle.font = .systemFont(ofSize: 16, weight: .semibold)
        emptyTitle.textColor = .secondaryLabelColor
        emptyTitle.alignment = .center

        emptyDesc.font = .systemFont(ofSize: 13)
        emptyDesc.textColor = .tertiaryLabelColor
        emptyDesc.alignment = .center
        emptyDesc.preferredMaxLayoutWidth = 360

        emptyPlaceholder.orientation = .vertical
        emptyPlaceholder.spacing = 10
        emptyPlaceholder.alignment = .centerX
        emptyPlaceholder.addArrangedSubview(emptyIcon)
        emptyPlaceholder.addArrangedSubview(emptyTitle)
        emptyPlaceholder.addArrangedSubview(emptyDesc)
        emptyPlaceholder.translatesAutoresizingMaskIntoConstraints = false
        mainStage.addSubview(emptyPlaceholder)

        // View de vídeo
        videoView.translatesAutoresizingMaskIntoConstraints = false
        videoView.isHidden = true
        mainStage.addSubview(videoView)

        videoView.onThumbnail = { [weak self] cgImage in
            guard let self, let win = self.selectedWindow else { return }
            self.thumbnailBar.updateThumbnail(windowId: win.id, cgImage: cgImage)
        }

        // 3. Rodapé de métricas e status, fora da área de vídeo.
        floatingBar.translatesAutoresizingMaskIntoConstraints = false
        mainStage.addSubview(floatingBar)

        activeAppTitleLabel.font = .systemFont(ofSize: 12, weight: .semibold)
        activeAppTitleLabel.textColor = .labelColor
        activeAppTitleLabel.lineBreakMode = .byTruncatingTail

        streamStatusLabel.font = .systemFont(ofSize: 11)
        streamStatusLabel.textColor = .secondaryLabelColor

        streamMetricsLabel.font = .monospacedDigitSystemFont(ofSize: 11, weight: .regular)
        streamMetricsLabel.textColor = .tertiaryLabelColor

        let reconnectBtn = NSButton(title: "Reconectar", target: self, action: #selector(reconnectStream))
        Glass.style(reconnectBtn)

        let barRow = NSStackView(views: [activeAppTitleLabel, streamStatusLabel, streamMetricsLabel, reconnectBtn])
        barRow.orientation = .horizontal
        barRow.spacing = 10
        barRow.alignment = .centerY
        barRow.translatesAutoresizingMaskIntoConstraints = false
        floatingBar.addSubview(barRow)

        floatingBar.isHidden = true

        NSLayoutConstraint.activate([
            thumbnailBar.leadingAnchor.constraint(equalTo: workspaceView.leadingAnchor, constant: 14),
            thumbnailBar.trailingAnchor.constraint(equalTo: workspaceView.trailingAnchor, constant: -14),
            mainStage.leadingAnchor.constraint(equalTo: workspaceView.leadingAnchor),
            mainStage.trailingAnchor.constraint(equalTo: workspaceView.trailingAnchor),
            mainStage.bottomAnchor.constraint(equalTo: workspaceView.bottomAnchor),

            emptyPlaceholder.centerXAnchor.constraint(equalTo: mainStage.centerXAnchor),
            emptyPlaceholder.centerYAnchor.constraint(equalTo: mainStage.centerYAnchor),

            videoView.leadingAnchor.constraint(equalTo: mainStage.leadingAnchor),
            videoView.trailingAnchor.constraint(equalTo: mainStage.trailingAnchor),
            videoView.topAnchor.constraint(equalTo: mainStage.topAnchor),
            videoView.bottomAnchor.constraint(equalTo: floatingBar.topAnchor, constant: -8),

            floatingBar.leadingAnchor.constraint(equalTo: mainStage.leadingAnchor, constant: 16),
            floatingBar.trailingAnchor.constraint(equalTo: mainStage.trailingAnchor, constant: -16),
            floatingBar.bottomAnchor.constraint(equalTo: mainStage.bottomAnchor, constant: -14),

            barRow.leadingAnchor.constraint(equalTo: floatingBar.leadingAnchor, constant: 14),
            barRow.trailingAnchor.constraint(equalTo: floatingBar.trailingAnchor, constant: -14),
            barRow.topAnchor.constraint(equalTo: floatingBar.topAnchor, constant: 6),
            barRow.bottomAnchor.constraint(equalTo: floatingBar.bottomAnchor, constant: -6)
        ])

        // Posição da barra e do palco mudam ao recolher/espiar a lista.
        let barTop = thumbnailBar.topAnchor.constraint(equalTo: workspaceView.topAnchor, constant: 10)
        let expanded = mainStage.topAnchor.constraint(equalTo: thumbnailBar.bottomAnchor, constant: 8)
        let collapsed = mainStage.topAnchor.constraint(equalTo: workspaceView.topAnchor)
        barTop.isActive = true
        expanded.isActive = true
        barTopConstraint = barTop
        stageTopExpanded = expanded
        stageTopCollapsed = collapsed

        // Faixa fina no topo: indicador e gatilho para reaparecer a barra.
        peekStrip.translatesAutoresizingMaskIntoConstraints = false
        peekStrip.isHidden = true
        peekStrip.onEnter = { [weak self] in self?.peekBar(true) }
        workspaceView.addSubview(peekStrip, positioned: .above, relativeTo: nil)
        NSLayoutConstraint.activate([
            peekStrip.leadingAnchor.constraint(equalTo: workspaceView.leadingAnchor),
            peekStrip.trailingAnchor.constraint(equalTo: workspaceView.trailingAnchor),
            peekStrip.topAnchor.constraint(equalTo: workspaceView.topAnchor),
            peekStrip.heightAnchor.constraint(equalToConstant: 8)
        ])
        // A barra precisa ficar acima do palco ao espiar.
        workspaceView.addSubview(thumbnailBar, positioned: .above, relativeTo: mainStage)
        workspaceView.addSubview(peekStrip, positioned: .above, relativeTo: thumbnailBar)

        // Paleta de busca rápida estilo Spotlight (⌘K)
        searchPalette.translatesAutoresizingMaskIntoConstraints = false
        searchPalette.isHidden = true
        searchPalette.onSelect = { [weak self] target in
            self?.select(window: target)
        }
        searchPalette.onLaunchApp = { [weak self] app in
            self?.controlConnection?.launchApp(app.cmd)
        }
        searchPalette.onClose = { [weak self] in
            guard let self else { return }
            self.searchPalette.isHidden = true
            if !self.videoView.isHidden {
                self.window.makeFirstResponder(self.videoView)
            }
        }
        workspaceView.addSubview(searchPalette, positioned: .above, relativeTo: nil)
        NSLayoutConstraint.activate([
            searchPalette.centerXAnchor.constraint(equalTo: workspaceView.centerXAnchor),
            searchPalette.topAnchor.constraint(equalTo: workspaceView.topAnchor, constant: 40),
            searchPalette.widthAnchor.constraint(equalToConstant: 480),
            searchPalette.heightAnchor.constraint(equalToConstant: 300)
        ])

        // Gestos de trackpad: deslizar para alternar janelas
        videoView.onSwipe = { [weak self] delta in
            self?.cycleWindow(forward: delta > 0)
        }

        // Arrastar arquivos do Mac diretamente para a janela remota
        videoView.onDropFiles = { [weak self] urls in
            guard let self else { return }
            for url in urls {
                self.streamStatusLabel.stringValue = "Enviando arquivo: \(url.lastPathComponent)…"
                self.viewConnection?.sendFile(url: url)
            }
        }

        // Sincronização de clipboard com o host Windows
        clipboardSync.onSendClipboard = { [weak self] text in
            self?.viewConnection?.sendClipboard(text)
            self?.controlConnection?.sendClipboard(text)
        }
    }

    /// ⌘K: abre ou fecha a busca rápida de janelas e monitores.
    func toggleQuickSearch() {
        if searchPalette.isHidden {
            var items = monitors.map(RemoteWindow.monitor)
            items.append(contentsOf: windows)
            items.removeAll { detached[$0.id] != nil }
            searchPalette.show(with: items, apps: availableApps)
            searchPalette.isHidden = false
        } else {
            searchPalette.isHidden = true
            if !videoView.isHidden {
                window.makeFirstResponder(videoView)
            }
        }
    }

    /// ⌘` / ⌘⇧`: alternância cíclica entre janelas remotas.
    func cycleWindow(forward: Bool) {
        var items = monitors.map(RemoteWindow.monitor)
        items.append(contentsOf: windows)
        items.removeAll { detached[$0.id] != nil }
        guard !items.isEmpty else { return }
        guard let current = selectedWindow, let index = items.firstIndex(where: { $0.id == current.id }) else {
            if let first = items.first { select(window: first) }
            return
        }
        let nextIndex = forward ? (index + 1) % items.count : (index - 1 + items.count) % items.count
        select(window: items[nextIndex])
    }

    /// Qualidade adaptativa com base na latência (RTT) de ida e volta da rede.
    private func handleRoundTrip(_ rtt: Double) {
        let now = ProcessInfo.processInfo.systemUptime
        guard now - lastAdaptiveQualityUpdate > 3.0 else { return }

        if rtt > 150.0 && !isLowQualityMode {
            // Alta latência: reduz para 30 FPS e 6 Mbps para evitar atrasos e congestionamento
            isLowQualityMode = true
            lastAdaptiveQualityUpdate = now
            viewConnection?.sendQuality(fps: 30, bitrate: 6_000_000)
            streamStatusLabel.stringValue = "Qualidade adaptativa: 30 FPS (RTT \(Int(rtt)) ms)"
        } else if rtt < 60.0 && isLowQualityMode {
            // Rede estável: restaura 60 FPS e 12 Mbps
            isLowQualityMode = false
            lastAdaptiveQualityUpdate = now
            viewConnection?.sendQuality(fps: 60, bitrate: 12_000_000)
            streamStatusLabel.stringValue = "Qualidade máxima: 60 FPS"
        }
    }

    // MARK: - Recolher / espiar a lista de apps

    /// Alterna entre lista fixa no topo e lista oculta (reaparece pela borda superior).
    func toggleBarCollapsed() {
        isBarCollapsed.toggle()
        isBarPeeking = false
        peekHideTask?.cancel()
        applyBarState(animated: true)
    }

    private func peekBar(_ show: Bool) {
        guard isBarCollapsed else { return }
        peekHideTask?.cancel()
        guard isBarPeeking != show else { return }
        isBarPeeking = show
        applyBarState(animated: true)
    }

    private func barHoverChanged(_ inside: Bool) {
        guard isBarCollapsed else { return }
        peekHideTask?.cancel()
        if inside { return }
        // Pequena tolerância evita piscar ao cruzar a borda entre faixa e barra.
        peekHideTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(350))
            guard !Task.isCancelled, let self else { return }
            self.peekBar(false)
        }
    }

    private func applyBarState(animated: Bool) {
        let hiddenOffset = -(thumbnailBar.frame.height > 0 ? thumbnailBar.frame.height : 128) - 24
        if isBarCollapsed {
            stageTopExpanded?.isActive = false
            stageTopCollapsed?.isActive = true
        } else {
            stageTopCollapsed?.isActive = false
            stageTopExpanded?.isActive = true
        }
        let target: CGFloat = (!isBarCollapsed || isBarPeeking) ? 10 : hiddenOffset
        peekStrip.isHidden = !isBarCollapsed || isBarPeeking
        if animated {
            let animationGeneration = UUID()
            barAnimationGeneration = animationGeneration
            isBarAnimating = true
            updateVideoInteractionExclusions()
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.22
                context.allowsImplicitAnimation = true
                barTopConstraint?.animator().constant = target
                workspaceView.layoutSubtreeIfNeeded()
            } completionHandler: { [weak self] in
                Task { @MainActor in
                    guard let self, self.barAnimationGeneration == animationGeneration else { return }
                    self.isBarAnimating = false
                    self.updateVideoInteractionExclusions()
                }
            }
        } else {
            barAnimationGeneration = UUID()
            isBarAnimating = false
            barTopConstraint?.constant = target
            workspaceView.layoutSubtreeIfNeeded()
            updateVideoInteractionExclusions()
        }
    }

    private func updateVideoInteractionExclusions() {
        guard videoView.window != nil else { return }
        var exclusions: [CGRect] = []
        for overlay in [thumbnailBar, peekStrip] where !overlay.isHidden {
            let frameInWorkspace = workspaceView.convert(overlay.bounds, from: overlay)
            let clipped = frameInWorkspace.intersection(workspaceView.bounds)
            guard !clipped.isNull, !clipped.isEmpty else { continue }
            let frameInVideo = videoView.convert(clipped, from: workspaceView)
            let visiblePart = frameInVideo.intersection(videoView.bounds)
            if !visiblePart.isNull, !visiblePart.isEmpty { exclusions.append(visiblePart) }
        }
        if isBarAnimating {
            let height = max(160, thumbnailBar.bounds.height + 24)
            exclusions.append(CGRect(x: 0, y: 0, width: videoView.bounds.width, height: height))
        }
        videoView.setInteractionExclusions(exclusions)
    }

    private func showWelcome(_ show: Bool) {
        welcomeCard.isHidden = !show
        workspaceView.isHidden = show
        if show {
            videoView.resetInput()
            videoView.restoreCursor()
        } else if let screen = window.screen ?? NSScreen.main, !window.styleMask.contains(.fullScreen) {
            // O workspace sempre usa a maior área possível da tela.
            window.setFrame(screen.visibleFrame, display: true, animate: false)
        }
    }

    // MARK: - Início e Prefill

    private func prefill() {
        if let preset {
            address.stringValue = preset.host
            port.stringValue = preset.port
            pin.stringValue = preset.pin
            status.stringValue = "Conectando com os dados do teste."
            return
        }
        guard let last = PairingStore.loadLast() else { return }
        address.stringValue = last.host
        port.stringValue = last.port
        if !last.deviceToken.isEmpty {
            status.stringValue = "Mac já pareado com este PC."
        }
    }

    func showWindow() {
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func startConnection() {
        if !wantsConnection { toggle() }
    }

    @objc private func toggle() {
        if wantsConnection {
            wantsConnection = false
            disconnect()
            return
        }
        wantsConnection = true
        retryCount = 0
        connect()
    }

    // MARK: - Conexão de Controle (Listagem de Janelas e Pareamento)

    private func connect() {
        retryTask?.cancel()
        retryTask = nil
        guard controlConnection == nil else { return }

        let problems = InputValidation.problems(address: address.stringValue, port: port.stringValue)
        guard problems.isEmpty else {
            wantsConnection = false
            status.stringValue = "Confira os dados: " + problems.joined(separator: "; ")
            return
        }
        let host = normalizedHost()
        let portText = port.stringValue.trimmingCharacters(in: .whitespaces)
        guard let number = UInt16(portText), number != 0 else {
            wantsConnection = false
            status.stringValue = "Confira a porta."
            return
        }

        let creds = credentials(host: host, port: portText)
        var pinValue: String?
        if creds.token != nil {
            usingDeviceToken = true
        } else {
            usingDeviceToken = false
            guard PasswordProof.isAcceptablePin(pin.stringValue) else {
                wantsConnection = false
                status.stringValue = "Informe o PIN escolhido no Windows (mínimo de 6 caracteres)."
                return
            }
            pinValue = pin.stringValue
        }

        generation = UUID()
        let current = generation

        let client = RemoteConnection(
            onState: { [weak self] message, ready in
                guard let self, self.generation == current else { return }
                self.status.stringValue = message
                if ready {
                    self.retryCount = 0
                    self.showWelcome(false)
                    self.startRefresh()
                    self.onReady?()
                } else {
                    self.controlConnection = nil
                    self.stopRefresh()
                    self.closeActiveViewSession()
                    self.showWelcome(true)
                    self.handleFailure(message)
                }
            },
            onFrame: { _, _, _, _ in },
            onMedia: { _, _, _, _, _, _ in },
            onWindows: { [weak self] list in
                guard let self, self.generation == current else { return }
                self.updateWindows(list)
            },
            onDevicePaired: { [weak self] token, fingerprint in
                guard let self, self.generation == current else { return }
                self.savePairing(token: token, fingerprint: fingerprint, host: host, port: portText)
            },
            onRoundTrip: { [weak self] rtt in
                self?.handleRoundTrip(rtt)
            },
            onNotice: { [weak self] message in
                guard let self, self.generation == current else { return }
                self.status.stringValue = message
            },
            onMonitors: { [weak self] list in
                guard let self, self.generation == current else { return }
                self.monitors = list
            },
            onApps: { [weak self] apps in
                guard let self, self.generation == current else { return }
                self.availableApps = apps
            }
        )

        controlConnection = client
        client.onClipboard = { [weak self] text in
            self?.clipboardSync.applyRemoteClipboard(text)
        }
        button.title = "Desconectar"
        status.stringValue = "Conectando ao Windows…"
        client.connect(host: host, port: number, pin: pinValue, deviceToken: creds.token, fingerprint: creds.fingerprint, role: "control")
    }

    private func startRefresh() {
        stopRefresh()
        refreshTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(2))
                guard !Task.isCancelled, let self, self.controlConnection != nil else { return }
                self.controlConnection?.requestWindows()
            }
        }
    }

    private func stopRefresh() {
        refreshTask?.cancel()
        refreshTask = nil
    }

    private func updateWindows(_ list: [RemoteWindow]) {
        windows = list
        refreshBar()

        // Se a janela selecionada foi fechada no PC, encerra a transmissão e volta ao placeholder
        if let current = selectedWindow, !current.isMonitor, !list.contains(where: { $0.id == current.id }) {
            closeActiveViewSession()
            emptyTitle.stringValue = "Janela fechada no Windows"
            emptyDesc.stringValue = "O aplicativo anterior (\(current.process)) foi fechado no Windows. Selecione outro aplicativo acima."
        }
    }

    /// Cards da barra: monitores primeiro, depois as janelas, sem o que está destacado.
    private func refreshBar() {
        var items = monitors.map(RemoteWindow.monitor)
        if monitors.count > 1 { items.append(.allMonitors(count: monitors.count)) }
        items.append(contentsOf: windows)
        items.removeAll { detached[$0.id] != nil }
        thumbnailBar.setWindows(items, selectedId: selectedWindow?.id)
    }

    // MARK: - Destacar em janela do macOS

    /// Abre o app (ou monitor) numa janela própria do macOS, com sessão `view`
    /// independente. O macOS passa a cuidar de split view e posicionamento.
    private func detach(window target: RemoteWindow) {
        if target.isAllMonitors {
            detachAllMonitors()
            return
        }
        if let existing = detached[target.id] {
            existing.showWindow()
            return
        }
        let host = normalizedHost()
        let portText = port.stringValue.trimmingCharacters(in: .whitespaces)
        guard let number = UInt16(portText) else {
            status.stringValue = "Confira a porta."
            return
        }
        if selectedWindow?.id == target.id { closeActiveViewSession() }

        let creds = credentials(host: host, port: portText)
        let controller = RemoteWindowController(
            target: target, host: host, port: number,
            pin: creds.token == nil ? pin.stringValue : nil,
            deviceToken: creds.token, fingerprint: creds.fingerprint)
        let targetId = target.id
        controller.onClose = { [weak self] in
            guard let self else { return }
            self.detached[targetId] = nil
            self.refreshBar()
        }
        detached[targetId] = controller
        controller.start()
        controller.showWindow()
        if let origin = detached.values.first(where: { $0 !== controller })?.window.frame.origin {
            controller.window.setFrameOrigin(NSPoint(x: origin.x + 28, y: origin.y - 28))
        }
        refreshBar()
    }

    /// Uma janela macOS por monitor do PC, cada uma no monitor correspondente do Mac.
    private func detachAllMonitors() {
        let screens = NSScreen.screens
        for (index, monitor) in monitors.enumerated() {
            let card = RemoteWindow.monitor(monitor)
            detach(window: card)
            guard let controller = detached[card.id], !screens.isEmpty else { continue }
            let screen = screens[index % screens.count]
            controller.window.setFrame(screen.visibleFrame, display: true, animate: false)
        }
    }

    private func closeDetachedWindows() {
        for controller in Array(detached.values) { controller.window.close() }
        detached.removeAll()
    }

    // MARK: - Alternância de Aplicativos (App Switcher)

    private func select(window target: RemoteWindow) {
        if target.isAllMonitors {
            detachAllMonitors()
            return
        }
        if selectedWindow?.id == target.id, viewConnection != nil {
            return
        }

        let host = normalizedHost()
        let portText = port.stringValue.trimmingCharacters(in: .whitespaces)
        guard let number = UInt16(portText) else {
            status.stringValue = "Confira a porta."
            return
        }

        // Tira snapshot da janela anterior antes de trocar
        if let previous = selectedWindow, let snap = videoView.captureSnapshot() {
            thumbnailBar.updateThumbnail(windowId: previous.id, cgImage: snap)
        }

        // Fecha a conexão de streaming anterior
        closeActiveViewSession()

        selectedWindow = target
        thumbnailBar.setSelected(windowId: target.id)

        activeAppTitleLabel.stringValue = target.title.isEmpty ? target.process : target.title
        window.title = "\(target.title.isEmpty ? target.process : target.title) — RemoteDesktop"

        emptyPlaceholder.isHidden = true
        videoView.isHidden = false
        floatingBar.isHidden = false
        streamStatusLabel.stringValue = "Conectando…"
        streamMetricsLabel.stringValue = ""

        startViewSession(for: target, host: host, number: number)
    }

    private func startViewSession(for target: RemoteWindow, host: String, number: UInt16) {
        let portText = String(number)
        let creds = credentials(host: host, port: portText)

        viewGeneration = UUID()
        let current = viewGeneration
        videoView.setBaseRect(x: 0, y: 0, width: 0, height: 0)

        sampleStart = ProcessInfo.processInfo.systemUptime
        sampleFrames = 0
        sampleBytes = 0

        let client = RemoteConnection(
            onState: { [weak self] message, ready in
                guard let self, self.viewGeneration == current else { return }
                self.streamStatusLabel.stringValue = message
                if !ready {
                    self.videoView.resetInput()
                    self.viewConnection = nil
                }
            },
            onFrame: { [weak self] png, width, height, _ in
                guard let self, self.viewGeneration == current else { return }
                self.videoView.presentPng(png, width: width, height: height)
                self.sampleFrames += 1
                self.sampleBytes += png.count
                self.updateMetrics()
            },
            onMedia: { [weak self] payload, codec, keyframe, width, height, ptsMs in
                guard let self, self.viewGeneration == current else { return }
                if codec == ProtocolV2.codecH264 {
                    self.videoView.decoder.decode(accessUnit: payload,
                                                  keyframe: keyframe,
                                                  width: width,
                                                  height: height,
                                                  ptsMs: ptsMs)
                } else if codec == ProtocolV2.codecJpeg {
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
            onRoundTrip: { [weak self] rtt in
                self?.handleRoundTrip(rtt)
            },
            onNotice: { [weak self] message in
                guard let self, self.viewGeneration == current else { return }
                self.streamStatusLabel.stringValue = message
            },
            onViewport: { [weak self] x, y, width, height in
                guard let self, self.viewGeneration == current else { return }
                self.videoView.setBaseRect(x: x, y: y, width: width, height: height)
            }
        )

        videoView.decoder.onError = { [weak self] error in
            guard let self, self.viewGeneration == current else { return }
            self.streamStatusLabel.stringValue = "Vídeo: \(error)"
        }

        videoView.send = { [weak client] event in
            client?.send(event)
        }

        let targetId = target.id
        videoView.onThumbnail = { [weak self] cgImage in
            guard let self, self.selectedWindow?.id == targetId else { return }
            self.thumbnailBar.updateThumbnail(windowId: targetId, cgImage: cgImage)
        }

        viewConnection = client
        client.onClipboard = { [weak self] text in
            self?.clipboardSync.applyRemoteClipboard(text)
        }
        client.onAudio = { [weak self] pcmData in
            self?.audioPlayback.playPcm(data: pcmData)
        }
        client.onFileReceived = { [weak self] fileName in
            self?.streamStatusLabel.stringValue = "Arquivo recebido no Windows: \(fileName) ✓"
        }
        client.connect(host: host,
                       port: number,
                       pin: creds.token == nil ? pin.stringValue : nil,
                       deviceToken: creds.token,
                       fingerprint: creds.fingerprint,
                       role: "view",
                       windowId: target.monitorId == nil ? target.id : nil,
                       monitorId: target.monitorId)
    }

    private func closeActiveViewSession() {
        viewGeneration = UUID()
        audioPlayback.stop()
        videoView.resetInput()
        videoView.restoreCursor()
        viewConnection?.disconnect()
        viewConnection = nil
        selectedWindow = nil
        thumbnailBar.setSelected(windowId: nil)
        videoView.isHidden = true
        floatingBar.isHidden = true
        emptyPlaceholder.isHidden = false
        window.title = "RemoteDesktop"
    }

    @objc private func reconnectStream() {
        guard let win = selectedWindow else { return }
        select(window: win)
    }

    private func updateMetrics() {
        let now = ProcessInfo.processInfo.systemUptime
        let elapsed = now - sampleStart
        guard elapsed >= 1.0 else { return }
        let fps = Double(sampleFrames) / elapsed
        let mbps = (Double(sampleBytes) * 8.0) / (elapsed * 1_000_000.0)
        let w = Int(videoView.pixelSize.width)
        let h = Int(videoView.pixelSize.height)
        let sizeText = (w > 0 && h > 0) ? "\(w)×\(h) · " : ""
        streamMetricsLabel.stringValue = String(format: "%@%.1f FPS · %.2f Mbps", sizeText, fps, mbps)
        sampleStart = now
        sampleFrames = 0
        sampleBytes = 0
    }

    // MARK: - Credenciais e Pareamento

    private func credentials(host: String, port: String) -> (fingerprint: String?, token: Data?) {
        var fingerprint: String?
        var token: Data?
        if persistPairing, let record = PairingStore.load(host: host, port: port) {
            fingerprint = record.fingerprint.isEmpty ? nil : (CertificatePin.normalizedFingerprint(record.fingerprint) ?? record.fingerprint)
            token = record.deviceToken.isEmpty ? nil : Data(base64Encoded: record.deviceToken)
        }
        if fingerprint == nil, let preset, preset.host == host, preset.port == port, let raw = preset.fingerprint {
            fingerprint = CertificatePin.normalizedFingerprint(raw) ?? raw
        }
        return (fingerprint, token)
    }

    private func savePairing(token: Data, fingerprint: String, host: String, port: String) {
        guard persistPairing else { return }
        let record = HostRecord(host: host, port: port, fingerprint: fingerprint,
                                deviceToken: token.base64EncodedString(),
                                name: RemoteConnection.shortHostname)
        if !PairingStore.save(record) {
            status.stringValue = "Conectado; não foi possível salvar no Chaves do Mac."
        }
    }

    private func handleFailure(_ message: String) {
        if message.hasPrefix("Certificado do host diferente") {
            wantsConnection = false
            button.title = "Conectar"
            status.stringValue = message
            return
        }
        guard message.hasPrefix(RemoteConnection.authFailurePrefix) else {
            scheduleReconnect()
            return
        }
        wantsConnection = false
        button.title = "Conectar"
        guard usingDeviceToken else { return }
        usingDeviceToken = false
        if persistPairing { dropSavedToken() }
        status.stringValue = "Pareamento revogado no Windows. Informe o PIN para reconectar."
    }

    private func dropSavedToken() {
        let host = normalizedHost()
        let portText = port.stringValue.trimmingCharacters(in: .whitespaces)
        guard let record = PairingStore.load(host: host, port: portText), !record.deviceToken.isEmpty else { return }
        let updated = HostRecord(host: record.host, port: record.port, fingerprint: record.fingerprint,
                                 deviceToken: "", name: record.name)
        _ = PairingStore.save(updated)
    }

    private func scheduleReconnect() {
        guard wantsConnection, retryCount < 5 else {
            wantsConnection = false
            button.title = "Conectar"
            return
        }
        let delay = 1 << retryCount
        retryCount += 1
        button.title = "Cancelar reconexão"
        status.stringValue += " Nova tentativa em \(delay)s (\(retryCount)/5)."
        retryTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled, let self, self.wantsConnection else { return }
            self.connect()
        }
    }

    private func disconnect() {
        retryTask?.cancel(); retryTask = nil
        stopRefresh()
        generation = UUID()
        closeActiveViewSession()
        closeDetachedWindows()
        monitors = []
        windows = []
        thumbnailBar.reset()
        emptyTitle.stringValue = "Selecione um aplicativo no topo"
        emptyDesc.stringValue = "Clique em uma das miniaturas acima para iniciar a transmissão em 60 FPS com controle total do teclado e mouse."
        controlConnection?.disconnect()
        controlConnection = nil
        button.title = "Conectar"
        status.stringValue = "Desconectado."
        showWelcome(true)
    }

    private func normalizedHost() -> String {
        address.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            .trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
    }

    // MARK: - NSWindowDelegate

    func windowDidResignKey(_ notification: Notification) {
        videoView.resetInput()
        videoView.restoreCursor()
    }

    func windowDidBecomeKey(_ notification: Notification) {
        videoView.restoreCursor()
        updateVideoInteractionExclusions()
        if wantsConnection && controlConnection == nil {
            retryTask?.cancel()
            retryTask = nil
            retryCount = 0
            connect()
        }
    }
    func windowDidResize(_ notification: Notification) {
        updateVideoInteractionExclusions()
    }
    func windowWillClose(_ notification: Notification) {
        wantsConnection = false
        disconnect()
        onClose?()
    }
}

/// Faixa fina no topo da janela, visível só com a lista recolhida: indica que
/// a barra existe e a faz reaparecer quando o mouse encosta na borda superior.
@MainActor
final class PeekStripView: NSView {

    var onEnter: (() -> Void)?
    private var tracking: NSTrackingArea?
    private let pill = CALayer()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        pill.backgroundColor = NSColor.white.withAlphaComponent(0.35).cgColor
        pill.cornerRadius = 2
        layer?.addSublayer(pill)
        toolTip = "Passe o mouse aqui para mostrar a lista de apps"
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) não suportado.") }

    override func layout() {
        super.layout()
        pill.frame = CGRect(x: bounds.midX - 28, y: bounds.maxY - 6, width: 56, height: 4)
    }

    /// Captura a faixa para impedir que mouse/rolagem cheguem ao vídeo abaixo.
    override func hitTest(_ point: NSPoint) -> NSView? {
        guard !isHidden, let superview else { return nil }
        return bounds.contains(convert(point, from: superview)) ? self : nil
    }

    override func mouseDown(with event: NSEvent) {}
    override func rightMouseDown(with event: NSEvent) {}
    override func mouseUp(with event: NSEvent) {}
    override func rightMouseUp(with event: NSEvent) {}
    override func mouseDragged(with event: NSEvent) {}
    override func rightMouseDragged(with event: NSEvent) {}
    override func scrollWheel(with event: NSEvent) {}

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: bounds,
                                  options: [.mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect],
                                  owner: self, userInfo: nil)
        addTrackingArea(area)
        tracking = area
    }

    override func mouseEntered(with event: NSEvent) { onEnter?() }
}

@MainActor
final class InteractionLayoutView: NSView {
    var onLayout: (() -> Void)?

    override func layout() {
        super.layout()
        onLayout?()
    }
}
