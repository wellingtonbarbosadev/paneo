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
    private let titlebarTitle = TitlebarTitleField(labelWithString: "Paneo")
    private let titlebarAccessory = NSTitlebarAccessoryViewController()
    var onClose: (() -> Void)?
    var onReady: (() -> Void)?

    // MARK: - Estado de Conexão e Credenciais
    private let address = NSTextField(string: "")
    private let port = NSTextField(string: "9443")
    private let pin = NSSecureTextField(string: "")
    private let status = NSTextField(wrappingLabelWithString: "Informe o IP do Windows e o PIN escolhido no Windows.")
    private let nearbyHostsStack = NSStackView()
    private let nearbyHostsSection = NSStackView()
    private let hostDiscovery = HostDiscovery()
    private var currentDiscoveredHosts: [DiscoveredHost] = []
    private let button = NSButton(title: "Conectar", target: nil, action: nil)

    private var controlConnection: RemoteConnection?
    private var viewConnection: RemoteConnection?
    private var generation = UUID()
    private var viewGeneration = UUID()
    private var wantsConnection = false
    private var retryCount = 0
    private var retryTask: Task<Void, Never>?
    private var streamRetryTask: Task<Void, Never>?
    private var streamRetryCount = 0
    private var pendingReconnectTarget: RemoteWindow?
    private var refreshTask: Task<Void, Never>?
    private var usingDeviceToken = false
    private var windows: [RemoteWindow] = []
    private var monitors: [RemoteMonitor] = []
    private var selectedWindow: RemoteWindow?
    /// Apps/monitores destacados em janelas próprias do macOS, por id do card.
    private var detached: [Int64: RemoteWindowController] = [:]

    // MARK: - Barra recolhível
    private var barEdgeConstraint: NSLayoutConstraint?
    private var barLayoutConstraints: [NSLayoutConstraint] = []
    private var stageExpandedConstraint: NSLayoutConstraint?
    private var stageCollapsedConstraint: NSLayoutConstraint?
    private var isBarCollapsed = false
    private var isBarPeeking = false
    private var isBarAnimating = false
    private var barAnimationGeneration = UUID()
    private var peekHideTask: Task<Void, Never>?
    private let peekStrip = PeekStripView()
    private let preset: ConnectionPreset?
    private let persistPairing: Bool
    private let settings: AppSettings
    private var settingsController: SettingsViewController?
    private var settingsSheet: NSWindow?
    private var isShowingSettings: Bool { settingsSheet?.sheetParent != nil }
    private var appliedBarLayout: (position: BarPosition, density: BarDensity)?

    // MARK: - Métricas de Streaming
    private var sampleStart = ProcessInfo.processInfo.systemUptime
    private var sampleFrames = 0
    private var sampleBytes = 0

    // MARK: - Componentes Visuais
    private let rootContentView = NSView()
    private let welcomeCard: NSView
    private let welcomeIcon = NSImageView()
    private let workspaceView = InteractionLayoutView()
    private let thumbnailBar = WindowThumbnailBar()
    private let mainStage = NSView()
    private let emptyPlaceholder = NSStackView()
    private let emptyTitle = NSTextField(labelWithString: "Selecione um aplicativo na barra")
    private let emptyDesc = NSTextField(wrappingLabelWithString: "Escolha uma janela ou um favorito para começar a trabalhar no Windows.")
    private let videoView = RemoteVideoView()
    private let floatingBar: NSView
    private let activeAppTitleLabel = NSTextField(labelWithString: "")
    private let streamStatusLabel = NSTextField(labelWithString: "")
    private let streamMetricsLabel = NSTextField(labelWithString: "")
    private let reconnectButton = NSButton(title: "Reconectar", target: nil, action: nil)
    private let detailsPopover = NSPopover()
    private let streamControlsPopover = NSPopover()
    private let windowActionsView = WindowActionsView(frame: .zero)
    private let settingsDock = Glass.panel(cornerRadius: 12)
    private let searchPalette = QuickSearchPalette()
    private var isSearchOpen = false
    private let clipboardSync = ClipboardSync()
    private var availableApps: [RemoteApp] = []
    private var pendingFavoriteLaunch: (process: String, expires: TimeInterval)?
    private var lastAdaptiveQualityUpdate: TimeInterval = 0
    private var isLowQualityMode = false
    private var streamPreferences = StreamPreferences()
    private var streamSupportsScaling: Bool?
    private var remoteWindowState: RemoteWindowState?
    private var restoreOnInitialWindowState = false
    private var readingResizeTask: Task<Void, Never>?
    private var lastReadingSize: (Int, Int)?
    private var autoFitEnabled: Bool
    private var appliedAutoFitPreference: Bool
    private let audioPlayback = AudioPlayback()

    init(preset: ConnectionPreset? = nil, persistPairing: Bool = true, settings: AppSettings = .shared) {
        self.preset = preset
        self.persistPairing = persistPairing
        self.settings = settings
        autoFitEnabled = settings.autoFitWindows
        appliedAutoFitPreference = settings.autoFitWindows

        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1040, height: 680),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered,
                          defer: false)
        welcomeCard = Glass.panel(cornerRadius: 22)
        floatingBar = Glass.panel(cornerRadius: 14)
        super.init()

        setWindowTitle("Paneo")
        window.minSize = NSSize(width: 680, height: 480)
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.tabbingMode = .disallowed
        window.collectionBehavior.insert(.fullScreenPrimary)
        window.contentView = rootContentView
        titlebarTitle.font = .systemFont(ofSize: 13, weight: .semibold)
        titlebarTitle.textColor = .labelColor
        titlebarTitle.lineBreakMode = .byTruncatingTail
        titlebarTitle.translatesAutoresizingMaskIntoConstraints = false
        titlebarAccessory.layoutAttribute = .left
        titlebarAccessory.view = NSView(frame: NSRect(x: 0, y: 0, width: 860, height: 28))
        titlebarAccessory.view.addSubview(titlebarTitle)
        NSLayoutConstraint.activate([
            titlebarTitle.leadingAnchor.constraint(equalTo: titlebarAccessory.view.leadingAnchor, constant: 12),
            titlebarTitle.trailingAnchor.constraint(equalTo: titlebarAccessory.view.trailingAnchor, constant: -12),
            titlebarTitle.centerYAnchor.constraint(equalTo: titlebarAccessory.view.centerYAnchor)
        ])
        window.titleVisibility = .hidden
        window.addTitlebarAccessoryViewController(titlebarAccessory)

        setupWelcomeView()
        setupWorkspaceView()
        setupSettingsDock()
        NotificationCenter.default.addObserver(self, selector: #selector(settingsDidChange),
                                               name: AppSettings.didChange, object: settings)
        applySettings()
        workspaceView.onLayout = { [weak self] in self?.updateVideoInteractionExclusions() }
        prefill()
        showWelcome(true)
        window.center()
    }

    private func setWindowTitle(_ title: String) {
        window.title = title
        titlebarTitle.stringValue = title
        titlebarTitle.toolTip = title
    }

    // MARK: - Configuração da UI de Login / Boas-vindas

    private func setupWelcomeView() {
        welcomeCard.translatesAutoresizingMaskIntoConstraints = false
        rootContentView.addSubview(welcomeCard)

        let appIcon = welcomeIcon
        appIcon.image = NSImage(named: NSImage.applicationIconName)
            ?? NSImage(systemSymbolName: "display.2", accessibilityDescription: "Paneo")
        appIcon.setAccessibilityLabel("Paneo")
        appIcon.contentTintColor = .controlAccentColor
        appIcon.imageScaling = .scaleProportionallyUpOrDown
        appIcon.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            appIcon.widthAnchor.constraint(equalToConstant: 64),
            appIcon.heightAnchor.constraint(equalToConstant: 64)
        ])

        let titleLabel = NSTextField(labelWithString: "Paneo")
        titleLabel.font = .systemFont(ofSize: 28, weight: .bold)
        titleLabel.alignment = .center

        let subtitle = NSTextField(labelWithString: "Seus aplicativos Windows, no Mac.")
        subtitle.font = .systemFont(ofSize: 13)
        subtitle.textColor = .secondaryLabelColor
        let headerStack = NSStackView(views: [appIcon, titleLabel, subtitle])
        headerStack.orientation = .vertical
        headerStack.spacing = 6
        headerStack.alignment = .centerX

        address.placeholderString = "192.168.0.100"
        pin.placeholderString = "PIN de pareamento"
        button.target = self
        button.action = #selector(toggle)
        Glass.style(button)
        button.controlSize = .large
        button.keyEquivalent = "\r"
        for field in [address, port, pin] {
            field.controlSize = .large
            field.font = .systemFont(ofSize: 14)
        }

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

        let nearbyTitle = Glass.caption("PCS PRÓXIMOS")
        nearbyHostsStack.orientation = .vertical
        nearbyHostsStack.alignment = .leading
        nearbyHostsStack.spacing = 4
        nearbyHostsStack.translatesAutoresizingMaskIntoConstraints = false
        nearbyHostsStack.isHidden = true
        nearbyHostsSection.addArrangedSubview(nearbyTitle)
        nearbyHostsSection.addArrangedSubview(nearbyHostsStack)
        nearbyHostsSection.orientation = .vertical
        nearbyHostsSection.alignment = .leading
        nearbyHostsSection.spacing = 4
        nearbyHostsSection.translatesAutoresizingMaskIntoConstraints = false
        nearbyHostsSection.isHidden = true
        welcomeCard.addSubview(nearbyHostsSection)

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
            nearbyHostsSection.leadingAnchor.constraint(equalTo: fields.leadingAnchor),
            nearbyHostsSection.trailingAnchor.constraint(equalTo: fields.trailingAnchor),
            nearbyHostsSection.topAnchor.constraint(equalTo: fields.bottomAnchor, constant: 10),
            status.topAnchor.constraint(equalTo: nearbyHostsSection.bottomAnchor, constant: 10),
            status.bottomAnchor.constraint(equalTo: welcomeCard.bottomAnchor, constant: -20)
        ])
        hostDiscovery.onHostsChanged = { [weak self] hosts in self?.showNearbyHosts(hosts) }
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
        thumbnailBar.onActivateFavorite = { [weak self] favorite, source in
            self?.activateFavorite(favorite, from: source)
        }
        thumbnailBar.onOpenApps = { [weak self] in self?.toggleQuickSearch() }
        thumbnailBar.onShowQuality = { [weak self] source in self?.showStreamControls(from: source) }
        windowActionsView.onAction = { [weak self] action, enabled in self?.sendWindowAction(action, enabled: enabled) }
        videoView.onBoundsChange = { [weak self] in self?.scheduleReadingResize() }
        videoView.onZoomChange = { [weak self] in self?.sendWindowAction("reading", enabled: true) }
        windowActionsView.onRequestMenus = { [weak self] in self?.viewConnection?.requestWindowMenus() }
        windowActionsView.onInvokeMenu = { [weak self] id in self?.viewConnection?.invokeWindowMenu(id: id) }
        windowActionsView.onMenuOpen = { [weak self] in
            self?.videoView.resetInput()
            self?.window.makeFirstResponder(nil)
        }
        windowActionsView.onMenuClose = { [weak self] in
            guard let self, self.remoteWindowState?.minimized != true,
                  !self.videoView.isHidden, self.window.isKeyWindow else { return }
            self.window.makeFirstResponder(self.videoView)
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

        activeAppTitleLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        streamStatusLabel.lineBreakMode = .byTruncatingTail
        streamStatusLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        reconnectButton.target = self
        reconnectButton.action = #selector(reconnectStream)
        reconnectButton.isHidden = true
        Glass.style(reconnectButton)
        let detailsButton = NSButton(title: "Detalhes", target: self, action: #selector(showStreamDetails(_:)))
        detailsButton.setAccessibilityLabel("Detalhes da transmissão")
        Glass.style(detailsButton)
        let details = NSViewController()
        details.view = NSView(frame: NSRect(x: 0, y: 0, width: 310, height: 80))
        let detailsTitle = Glass.caption("TRANSMISSÃO")
        let detailsStack = NSStackView(views: [detailsTitle, streamMetricsLabel])
        detailsStack.orientation = .vertical
        detailsStack.alignment = .leading
        detailsStack.spacing = 10
        detailsStack.translatesAutoresizingMaskIntoConstraints = false
        details.view.addSubview(detailsStack)
        NSLayoutConstraint.activate([
            detailsStack.leadingAnchor.constraint(equalTo: details.view.leadingAnchor, constant: 18),
            detailsStack.trailingAnchor.constraint(lessThanOrEqualTo: details.view.trailingAnchor, constant: -18),
            detailsStack.centerYAnchor.constraint(equalTo: details.view.centerYAnchor)
        ])
        detailsPopover.contentViewController = details
        detailsPopover.behavior = .transient
        let controls = StreamControlsViewController()
        controls.onChange = { [weak self] preferences in self?.updateStreamPreferences(preferences) }
        streamControlsPopover.contentViewController = controls
        streamControlsPopover.behavior = .transient
        let barRow = NSStackView(views: [activeAppTitleLabel, streamStatusLabel, reconnectButton,
                                         windowActionsView, detailsButton])
        barRow.orientation = .horizontal
        barRow.spacing = 10
        barRow.alignment = .centerY
        barRow.translatesAutoresizingMaskIntoConstraints = false
        floatingBar.addSubview(barRow)

        floatingBar.isHidden = true

        NSLayoutConstraint.activate([
            emptyPlaceholder.centerXAnchor.constraint(equalTo: mainStage.centerXAnchor),
            emptyPlaceholder.centerYAnchor.constraint(equalTo: mainStage.centerYAnchor),

            videoView.leadingAnchor.constraint(equalTo: mainStage.leadingAnchor),
            videoView.trailingAnchor.constraint(equalTo: mainStage.trailingAnchor),
            videoView.topAnchor.constraint(equalTo: mainStage.topAnchor),
            videoView.bottomAnchor.constraint(equalTo: floatingBar.topAnchor, constant: -8),

            floatingBar.leadingAnchor.constraint(equalTo: mainStage.leadingAnchor, constant: 16),
            floatingBar.trailingAnchor.constraint(equalTo: mainStage.trailingAnchor, constant: -64),
            floatingBar.bottomAnchor.constraint(equalTo: mainStage.bottomAnchor, constant: -14),

            barRow.leadingAnchor.constraint(equalTo: floatingBar.leadingAnchor, constant: 14),
            barRow.trailingAnchor.constraint(equalTo: floatingBar.trailingAnchor, constant: -14),
            barRow.topAnchor.constraint(equalTo: floatingBar.topAnchor, constant: 6),
            barRow.bottomAnchor.constraint(equalTo: floatingBar.bottomAnchor, constant: -6)
        ])

        // A faixa acompanha a borda escolhida para a barra recolhida.
        peekStrip.translatesAutoresizingMaskIntoConstraints = false
        peekStrip.isHidden = true
        peekStrip.onEnter = { [weak self] in self?.peekBar(true) }
        workspaceView.addSubview(peekStrip, positioned: .above, relativeTo: nil)
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
            self?.launchApp(app)
        }
        searchPalette.onClose = { [weak self] in
            guard let self else { return }
            self.isSearchOpen = false
            AppMotion.setVisible(self.searchPalette, false) { [weak self] in self?.updateVideoInteractionExclusions() }
            self.updateVideoInteractionExclusions()
            if !self.isShowingSettings && !self.videoView.isHidden {
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

    private func setupSettingsDock() {
        settingsDock.translatesAutoresizingMaskIntoConstraints = false
        rootContentView.addSubview(settingsDock)
        let gear = NSButton(image: NSImage(systemSymbolName: "gearshape", accessibilityDescription: "Ajustes do Paneo")!,
                            target: self, action: #selector(openSettings(_:)))
        gear.isBordered = false
        gear.toolTip = "Ajustes do Paneo (⌘,)"
        gear.setAccessibilityLabel("Ajustes do Paneo")
        gear.translatesAutoresizingMaskIntoConstraints = false
        settingsDock.addSubview(gear)
        NSLayoutConstraint.activate([
            settingsDock.trailingAnchor.constraint(equalTo: rootContentView.trailingAnchor, constant: -14),
            settingsDock.bottomAnchor.constraint(equalTo: rootContentView.bottomAnchor, constant: -12),
            settingsDock.widthAnchor.constraint(equalToConstant: 36),
            settingsDock.heightAnchor.constraint(equalToConstant: 36),
            gear.leadingAnchor.constraint(equalTo: settingsDock.leadingAnchor),
            gear.trailingAnchor.constraint(equalTo: settingsDock.trailingAnchor),
            gear.topAnchor.constraint(equalTo: settingsDock.topAnchor),
            gear.bottomAnchor.constraint(equalTo: settingsDock.bottomAnchor)
        ])
    }

    @objc private func openSettings(_ sender: Any?) {
        showSettings()
    }

    func showSettings() {
        guard !isShowingSettings else { showWindow(); return }
        if isSearchOpen { toggleQuickSearch() }
        detailsPopover.close()
        streamControlsPopover.close()
        peekHideTask?.cancel()
        videoView.resetInput()
        videoView.restoreCursor()
        window.makeFirstResponder(nil)
        let controller = SettingsViewController(settings: settings)
        controller.onClose = { [weak self] in self?.closeSettings() }
        settingsController = controller
        let size = NSSize(width: min(920, window.frame.width - 48),
                          height: min(650, window.contentLayoutRect.height - 48))
        let sheet = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                             styleMask: [.titled], backing: .buffered, defer: false)
        sheet.isReleasedWhenClosed = false
        sheet.tabbingMode = .disallowed
        sheet.contentViewController = controller
        sheet.setContentSize(size)
        settings.applyAppearance(to: sheet)
        settingsSheet = sheet
        showWindow()
        window.beginSheet(sheet) { [weak self] _ in
            guard let self else { return }
            self.settingsSheet = nil
            self.settingsController = nil
            self.updateVideoInteractionExclusions()
            self.window.makeFirstResponder(!self.workspaceView.isHidden && !self.videoView.isHidden
                                           ? self.videoView : self.address)
        }
    }

    private func closeSettings() {
        guard let sheet = settingsSheet else { return }
        window.endSheet(sheet)
    }

    @objc private func showStreamDetails(_ sender: NSButton) {
        detailsPopover.show(relativeTo: sender.bounds, of: sender, preferredEdge: .maxY)
    }

    private func showStreamControls(from sender: NSView) {
        (streamControlsPopover.contentViewController as? StreamControlsViewController)?
            .configure(streamPreferences, supportsScaling: streamSupportsScaling)
        streamControlsPopover.show(relativeTo: sender.bounds, of: sender, preferredEdge: .maxY)
    }

    private func sendWindowAction(_ action: String, enabled: Bool? = nil) {
        guard let connection = viewConnection, remoteWindowState != nil,
              selectedWindow?.isMonitor != true else { return }
        if action == "reading" {
            autoFitEnabled = enabled ?? !autoFitEnabled
            readingResizeTask?.cancel()
            lastReadingSize = nil
            windowActionsView.update(state: remoteWindowState, windowId: selectedWindow?.id,
                                     isMonitor: false, autoFitEnabled: autoFitEnabled)
            if autoFitEnabled { scheduleReadingResize(debounce: false) }
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
        windowActionsView.update(state: remoteWindowState, windowId: selectedWindow?.id,
                                 isMonitor: false, autoFitEnabled: autoFitEnabled)
        connection.sendWindowAction(action, enabled: enabled)
    }

    private func scheduleReadingResize(debounce: Bool = true) {
        readingResizeTask?.cancel()
        readingResizeTask = Task { [weak self] in
            if debounce { try? await Task.sleep(for: .milliseconds(120)) }
            guard !Task.isCancelled, let self, self.autoFitEnabled,
                  self.selectedWindow?.isMonitor != true,
                  self.remoteWindowState?.windowId == self.selectedWindow?.id,
                  self.remoteWindowState?.canResize == true,
                  self.remoteWindowState?.minimized != true,
                  let connection = self.viewConnection else { return }
            let preferred = self.videoView.preferredWindowSize
            let size = (Int(preferred.width.rounded()), Int(preferred.height.rounded()))
            guard size.0 > 0, size.1 > 0 else { return }
            guard self.lastReadingSize?.0 != size.0 || self.lastReadingSize?.1 != size.1 else { return }
            self.lastReadingSize = size
            connection.sendWindowAction("reading", enabled: true, width: size.0, height: size.1)
        }
    }

    private func applyRemoteWindowState(_ state: RemoteWindowState, target: RemoteWindow) {
        videoView.allowsWindowZoom = state.canResize && !state.minimized && !target.isMonitor
        if let index = windows.firstIndex(where: { $0.id == state.windowId }) {
            windows[index].minimized = state.minimized
            refreshBar()
        }
        if selectedWindow?.id == state.windowId { selectedWindow?.minimized = state.minimized }
        windowActionsView.update(state: state, windowId: target.id, isMonitor: target.isMonitor,
                                 autoFitEnabled: autoFitEnabled)
        videoView.resetInput()
        let paused = state.minimized
        videoView.isHidden = paused
        emptyPlaceholder.isHidden = !paused
        if paused {
            emptyTitle.stringValue = "Minimizada no Windows"
            emptyDesc.stringValue = "Selecione este cartão ou use Restaurar para retomar o vídeo."
        }
        if paused { window.makeFirstResponder(nil) }
        else if selectedWindow?.id == state.windowId && !isShowingSettings {
            emptyTitle.stringValue = "Selecione um aplicativo na barra"
            emptyDesc.stringValue = "Escolha uma janela ou um favorito para começar a trabalhar no Windows."
            window.makeFirstResponder(videoView)
        }
    }

    private func updateStreamPreferences(_ preferences: StreamPreferences) {
        streamPreferences = preferences
        viewConnection?.sendQuality(fps: preferences.fps, bitrate: preferences.bitrate,
                                    maxDimension: preferences.maxDimension)
        streamStatusLabel.stringValue = "\(preferences.fps) FPS · \(preferences.maxDimension == 0 ? "Nativa" : "até \(preferences.maxDimension)p")"
    }

    private func launchApp(_ app: RemoteApp) {
        guard controlConnection != nil else { return }
        pendingFavoriteLaunch = (FavoriteApp.normalizedProcess(app.process ?? app.name),
                                 ProcessInfo.processInfo.systemUptime + 20)
        controlConnection?.launchApp(app.cmd)
        streamStatusLabel.stringValue = "Abrindo \(app.name)…"
        emptyTitle.stringValue = "Abrindo \(app.name)…"
        emptyDesc.stringValue = "Aguardando a janela aparecer no Windows."
    }

    /// ⌘K: abre ou fecha a busca rápida de janelas e monitores.
    func toggleQuickSearch() {
        guard !isShowingSettings, !workspaceView.isHidden else { return }
        isSearchOpen.toggle()
        if isSearchOpen {
            AppMotion.setVisible(searchPalette, true)
            searchPalette.show(with: searchableWindows(), apps: availableApps)
        } else {
            AppMotion.setVisible(searchPalette, false) { [weak self] in self?.updateVideoInteractionExclusions() }
            if !videoView.isHidden {
                if !isShowingSettings { window.makeFirstResponder(videoView) }
            }
        }
        updateVideoInteractionExclusions()
    }

    /// ⌘` / ⌘⇧`: alternância cíclica entre janelas remotas.
    func cycleWindow(forward: Bool) {
        guard !isShowingSettings else { return }
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
        guard streamPreferences.quality == .automatic else { return }
        let now = ProcessInfo.processInfo.systemUptime
        guard now - lastAdaptiveQualityUpdate > 3.0 else { return }

        if rtt > 150.0 && !isLowQualityMode {
            // Alta latência: reduz para 30 FPS e 6 Mbps para evitar atrasos e congestionamento
            isLowQualityMode = true
            lastAdaptiveQualityUpdate = now
            viewConnection?.sendQuality(fps: 30, bitrate: 6_000_000,
                                        maxDimension: streamPreferences.maxDimension)
            streamStatusLabel.stringValue = "Qualidade adaptativa: 30 FPS (RTT \(Int(rtt)) ms)"
        } else if rtt < 60.0 && isLowQualityMode {
            // Rede estável: restaura 60 FPS e 12 Mbps
            isLowQualityMode = false
            lastAdaptiveQualityUpdate = now
            viewConnection?.sendQuality(fps: streamPreferences.fps, bitrate: streamPreferences.bitrate,
                                        maxDimension: streamPreferences.maxDimension)
            streamStatusLabel.stringValue = "Qualidade máxima: 60 FPS"
        }
    }

    // MARK: - Recolher / espiar a lista de apps

    @objc private func settingsDidChange(_ notification: Notification) {
        if appliedAutoFitPreference != settings.autoFitWindows {
            appliedAutoFitPreference = settings.autoFitWindows
            autoFitEnabled = settings.autoFitWindows
            sendWindowAction("reading", enabled: autoFitEnabled)
        }
        applySettings()
    }

    private func applySettings() {
        settings.applyAppearance(to: window)
        if let settingsSheet { settings.applyAppearance(to: settingsSheet) }
        for controller in detached.values { settings.applyAppearance(to: controller.window) }
        welcomeIcon.contentTintColor = settings.accentColor
        button.contentTintColor = settings.accentColor
        activeAppTitleLabel.textColor = .labelColor
        if appliedBarLayout?.position != settings.barPosition || appliedBarLayout?.density != settings.barDensity {
            thumbnailBar.configure(position: settings.barPosition, density: settings.barDensity)
            configureBarLayout()
            appliedBarLayout = (settings.barPosition, settings.barDensity)
        }
    }

    private func configureBarLayout() {
        peekHideTask?.cancel()
        isBarPeeking = false
        stageExpandedConstraint?.isActive = false
        stageCollapsedConstraint?.isActive = false
        NSLayoutConstraint.deactivate(barLayoutConstraints)
        let thickness = thumbnailBar.preferredThickness
        let position = settings.barPosition
        peekStrip.position = position
        var constraints: [NSLayoutConstraint] = []
        // Reserva o canto da engrenagem nas barras direita e inferior.
        if position.isVertical {
            constraints += [
                thumbnailBar.widthAnchor.constraint(equalToConstant: thickness),
                thumbnailBar.topAnchor.constraint(equalTo: workspaceView.topAnchor, constant: 10),
                thumbnailBar.bottomAnchor.constraint(equalTo: workspaceView.bottomAnchor, constant: position == .right ? -60 : -10),
                mainStage.topAnchor.constraint(equalTo: workspaceView.topAnchor),
                mainStage.bottomAnchor.constraint(equalTo: workspaceView.bottomAnchor),
                peekStrip.topAnchor.constraint(equalTo: workspaceView.topAnchor),
                peekStrip.bottomAnchor.constraint(equalTo: workspaceView.bottomAnchor),
                peekStrip.widthAnchor.constraint(equalToConstant: 8)
            ]
        } else {
            constraints += [
                thumbnailBar.heightAnchor.constraint(equalToConstant: thickness),
                thumbnailBar.leadingAnchor.constraint(equalTo: workspaceView.leadingAnchor, constant: 14),
                thumbnailBar.trailingAnchor.constraint(equalTo: workspaceView.trailingAnchor, constant: position == .bottom ? -64 : -14),
                mainStage.leadingAnchor.constraint(equalTo: workspaceView.leadingAnchor),
                mainStage.trailingAnchor.constraint(equalTo: workspaceView.trailingAnchor),
                peekStrip.leadingAnchor.constraint(equalTo: workspaceView.leadingAnchor),
                peekStrip.trailingAnchor.constraint(equalTo: workspaceView.trailingAnchor),
                peekStrip.heightAnchor.constraint(equalToConstant: 8)
            ]
        }
        switch position {
        case .top:
            barEdgeConstraint = thumbnailBar.topAnchor.constraint(equalTo: workspaceView.topAnchor, constant: 10)
            stageExpandedConstraint = mainStage.topAnchor.constraint(equalTo: thumbnailBar.bottomAnchor, constant: 8)
            stageCollapsedConstraint = mainStage.topAnchor.constraint(equalTo: workspaceView.topAnchor)
            constraints += [mainStage.bottomAnchor.constraint(equalTo: workspaceView.bottomAnchor),
                            peekStrip.topAnchor.constraint(equalTo: workspaceView.topAnchor)]
        case .bottom:
            barEdgeConstraint = thumbnailBar.bottomAnchor.constraint(equalTo: workspaceView.bottomAnchor, constant: -10)
            stageExpandedConstraint = mainStage.bottomAnchor.constraint(equalTo: thumbnailBar.topAnchor, constant: -8)
            stageCollapsedConstraint = mainStage.bottomAnchor.constraint(equalTo: workspaceView.bottomAnchor)
            constraints += [mainStage.topAnchor.constraint(equalTo: workspaceView.topAnchor),
                            peekStrip.bottomAnchor.constraint(equalTo: workspaceView.bottomAnchor)]
        case .left:
            barEdgeConstraint = thumbnailBar.leadingAnchor.constraint(equalTo: workspaceView.leadingAnchor, constant: 10)
            stageExpandedConstraint = mainStage.leadingAnchor.constraint(equalTo: thumbnailBar.trailingAnchor, constant: 8)
            stageCollapsedConstraint = mainStage.leadingAnchor.constraint(equalTo: workspaceView.leadingAnchor)
            constraints += [mainStage.trailingAnchor.constraint(equalTo: workspaceView.trailingAnchor),
                            peekStrip.leadingAnchor.constraint(equalTo: workspaceView.leadingAnchor)]
        case .right:
            barEdgeConstraint = thumbnailBar.trailingAnchor.constraint(equalTo: workspaceView.trailingAnchor, constant: -10)
            stageExpandedConstraint = mainStage.trailingAnchor.constraint(equalTo: thumbnailBar.leadingAnchor, constant: -8)
            stageCollapsedConstraint = mainStage.trailingAnchor.constraint(equalTo: workspaceView.trailingAnchor)
            constraints += [mainStage.leadingAnchor.constraint(equalTo: workspaceView.leadingAnchor),
                            peekStrip.trailingAnchor.constraint(equalTo: workspaceView.trailingAnchor)]
        }
        if let barEdgeConstraint { constraints.append(barEdgeConstraint) }
        barLayoutConstraints = constraints
        NSLayoutConstraint.activate(constraints)
        applyBarState(animated: false)
    }

    /// Alterna entre lista fixa e lista oculta, usando a borda escolhida.
    func toggleBarCollapsed() {
        guard !isShowingSettings else { return }
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
        let reversed = settings.barPosition == .bottom || settings.barPosition == .right
        let direction: CGFloat = reversed ? -1 : 1
        let hiddenOffset = -direction * (thumbnailBar.preferredThickness + 24)
        if isBarCollapsed {
            stageExpandedConstraint?.isActive = false
            stageCollapsedConstraint?.isActive = true
        } else {
            stageCollapsedConstraint?.isActive = false
            stageExpandedConstraint?.isActive = true
        }
        let target: CGFloat = (!isBarCollapsed || isBarPeeking) ? direction * 10 : hiddenOffset
        peekStrip.isHidden = !isBarCollapsed || isBarPeeking
        if animated && AppMotion.duration > 0 {
            let animationGeneration = UUID()
            barAnimationGeneration = animationGeneration
            isBarAnimating = true
            updateVideoInteractionExclusions()
            NSAnimationContext.runAnimationGroup { context in
                context.duration = AppMotion.duration
                context.allowsImplicitAnimation = true
                barEdgeConstraint?.animator().constant = target
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
            barEdgeConstraint?.constant = target
            workspaceView.layoutSubtreeIfNeeded()
            updateVideoInteractionExclusions()
        }
    }

    private func updateVideoInteractionExclusions() {
        guard videoView.window != nil else { return }
        var exclusions: [CGRect] = []
        for overlay in [thumbnailBar, peekStrip, settingsDock, searchPalette] where !overlay.isHidden {
            let frameInWorkspace = workspaceView.convert(overlay.bounds, from: overlay)
            let clipped = frameInWorkspace.intersection(workspaceView.bounds)
            guard !clipped.isNull, !clipped.isEmpty else { continue }
            let frameInVideo = videoView.convert(clipped, from: workspaceView)
            let visiblePart = frameInVideo.intersection(videoView.bounds)
            if !visiblePart.isNull, !visiblePart.isEmpty { exclusions.append(visiblePart) }
        }
        if isBarAnimating {
            // Exclui todo o percurso da barra enquanto seu frame está animando.
            let extent = thumbnailBar.preferredThickness + 24
            let bounds = workspaceView.bounds
            let path: CGRect
            switch settings.barPosition {
            case .top: path = CGRect(x: 0, y: bounds.maxY - extent, width: bounds.width, height: extent)
            case .bottom: path = CGRect(x: 0, y: 0, width: bounds.width, height: extent)
            case .left: path = CGRect(x: 0, y: 0, width: extent, height: bounds.height)
            case .right: path = CGRect(x: bounds.maxX - extent, y: 0, width: extent, height: bounds.height)
            }
            let clipped = videoView.convert(path, from: workspaceView).intersection(videoView.bounds)
            if !clipped.isNull, !clipped.isEmpty { exclusions.append(clipped) }
        }
        videoView.setInteractionExclusions(exclusions)
    }

    private func showWelcome(_ show: Bool) {
        AppMotion.setVisible(welcomeCard, show)
        AppMotion.setVisible(workspaceView, !show)
        if show {
            hostDiscovery.start()
            isSearchOpen = false
            AppMotion.setVisible(searchPalette, false) { [weak self] in self?.updateVideoInteractionExclusions() }
            detailsPopover.close()
            streamControlsPopover.close()
            videoView.resetInput()
            videoView.restoreCursor()
        } else {
            hostDiscovery.stop()
            if let screen = window.screen ?? NSScreen.main, !window.styleMask.contains(.fullScreen) {
            // O workspace sempre usa a maior área possível da tela.
                window.setFrame(screen.visibleFrame, display: true, animate: AppMotion.duration > 0)
            }
        }
    }

    private func showNearbyHosts(_ hosts: [DiscoveredHost]) {
        currentDiscoveredHosts = hosts
        for view in nearbyHostsStack.arrangedSubviews {
            nearbyHostsStack.removeArrangedSubview(view)
            view.removeFromSuperview()
        }
        nearbyHostsSection.isHidden = hosts.isEmpty
        for host in hosts {
            let button = NSButton(title: "\(host.name)  ·  \(host.addresses.first(where: { !$0.contains(":") }) ?? host.addresses.first ?? host.hostname)",
                                  target: self, action: #selector(selectNearbyHost(_:)))
            button.identifier = NSUserInterfaceItemIdentifier(host.id)
            button.toolTip = "Preencher endereço e porta. O pareamento ainda exige o PIN do Windows."
            button.setAccessibilityLabel("\(host.name), computador próximo. Preencher endereço, sem conectar.")
            button.alignment = .left
            button.lineBreakMode = .byTruncatingTail
            Glass.style(button)
            nearbyHostsStack.addArrangedSubview(button)
            button.widthAnchor.constraint(equalTo: nearbyHostsStack.widthAnchor).isActive = true
        }
    }

    @objc private func selectNearbyHost(_ sender: NSButton) {
        guard let host = currentDiscoveredHosts.first(where: { $0.id == sender.identifier?.rawValue }) else { return }
        let addressValue = host.addresses.first(where: { !$0.contains(":") }) ?? host.addresses.first ?? host.hostname
        address.stringValue = addressValue.contains(":") ? "[\(addressValue)]" : addressValue
        port.stringValue = String(host.port)
        pin.becomeFirstResponder()
        status.stringValue = "Endereço preenchido. Confirme o PIN escolhido no Windows para parear."
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

        thumbnailBar.setHostKey("\(host.lowercased()):\(number)")

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
                    let target = self.selectedWindow ?? self.pendingReconnectTarget
                    self.pendingReconnectTarget = RemoteConnectionFailure.isTransient(message) ? target : nil
                    self.pendingFavoriteLaunch = nil
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
                self.restoreSelectedTargetIfAvailable()
                self.refreshQuickSearchContents()
            },
            onApps: { [weak self] apps in
                guard let self, self.generation == current else { return }
                self.availableApps = apps
                self.refreshQuickSearchContents()
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
        if let selected = selectedWindow, let latest = list.first(where: { $0.id == selected.id }) {
            selectedWindow?.minimized = latest.minimized
        }
        refreshBar()
        restoreSelectedTargetIfAvailable()

        if let pending = pendingFavoriteLaunch {
            if ProcessInfo.processInfo.systemUptime > pending.expires {
                pendingFavoriteLaunch = nil
                streamStatusLabel.stringValue = "O aplicativo ainda não abriu no Windows."
                emptyTitle.stringValue = "A janela ainda não apareceu"
                emptyDesc.stringValue = "Confira o aplicativo no Windows ou tente abri-lo novamente pelos favoritos."
            } else if let target = list.first(where: {
                FavoriteApp.normalizedProcess($0.process) == pending.process ||
                FavoriteApp.normalizedProcess($0.title) == pending.process
            }) {
                pendingFavoriteLaunch = nil
                activateFavoriteWindow(target)
            }
        }

        // Se a janela selecionada foi fechada no PC, encerra a transmissão e volta ao placeholder
        if let current = selectedWindow, !current.isMonitor, !list.contains(where: { $0.id == current.id }) {
            closeActiveViewSession()
            emptyTitle.stringValue = "Janela fechada no Windows"
            emptyDesc.stringValue = "O aplicativo anterior (\(current.process)) foi fechado no Windows. Selecione outro aplicativo na barra."
        }
        refreshQuickSearchContents()
    }

    private func searchableWindows() -> [RemoteWindow] {
        var items = monitors.map(RemoteWindow.monitor)
        items.append(contentsOf: windows)
        return items.filter { detached[$0.id] == nil }
    }

    private func refreshQuickSearchContents() {
        guard isSearchOpen else { return }
        searchPalette.update(with: searchableWindows(), apps: availableApps)
    }

    /// Cards da barra: monitores primeiro, depois as janelas, sem o que está destacado.
    private func refreshBar() {
        var items = monitors.map(RemoteWindow.monitor)
        if monitors.count > 1 { items.append(.allMonitors(count: monitors.count)) }
        items.append(contentsOf: windows)
        items.removeAll { detached[$0.id] != nil }
        thumbnailBar.setWindows(items, selectedId: selectedWindow?.id)
    }

    private func activateFavorite(_ favorite: FavoriteApp, from source: NSView) {
        pendingFavoriteLaunch = nil
        let matches = windows.filter { FavoriteApp.normalizedProcess($0.process) == favorite.process }
        if matches.count == 1, let target = matches.first {
            activateFavoriteWindow(target)
        } else if matches.count > 1 {
            let menu = NSMenu(title: favorite.displayName)
            for target in matches {
                let item = NSMenuItem(title: target.title.isEmpty ? target.process : target.title,
                                      action: #selector(selectFavoriteWindow(_:)), keyEquivalent: "")
                item.target = self
                item.representedObject = target
                item.state = selectedWindow?.id == target.id ? .on : .off
                menu.addItem(item)
            }
            menu.popUp(positioning: nil, at: NSPoint(x: 0, y: source.bounds.maxY), in: source)
        } else if let app = availableApps.first(where: {
            FavoriteApp.normalizedProcess($0.process ?? $0.name) == favorite.process ||
            ($0.id == "edge" && favorite.process == "msedge")
        }), controlConnection != nil {
            launchApp(app)
        } else {
            let alert = NSAlert()
            alert.messageText = "Aplicativo fechado no Windows"
            alert.informativeText = "O lançador atual ainda não oferece suporte para abrir \(favorite.displayName). Abra o aplicativo no PC; ele continuará salvo nos favoritos."
            alert.addButton(withTitle: "OK")
            alert.beginSheetModal(for: window)
        }
    }

    @objc private func selectFavoriteWindow(_ sender: NSMenuItem) {
        guard let target = sender.representedObject as? RemoteWindow,
              windows.contains(where: { $0.id == target.id }) else { return }
        activateFavoriteWindow(target)
    }

    private func activateFavoriteWindow(_ target: RemoteWindow) {
        if let existing = detached[target.id] {
            existing.showWindow()
        } else {
            select(window: target)
            if !isShowingSettings { window.makeFirstResponder(videoView) }
        }
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
            deviceToken: creds.token, fingerprint: creds.fingerprint, settings: settings)
        settings.applyAppearance(to: controller.window)
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
        let preserveZoom = selectedWindow?.id == target.id || pendingReconnectTarget?.id == target.id
        pendingFavoriteLaunch = nil
        pendingReconnectTarget = nil
        cancelStreamRetry()
        streamRetryCount = 0
        if target.isAllMonitors {
            detachAllMonitors()
            return
        }
        if selectedWindow?.id == target.id, viewConnection != nil {
            if remoteWindowState?.minimized == true { sendWindowAction("restore", enabled: nil) }
            if !isShowingSettings { window.makeFirstResponder(videoView) }
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
        if !preserveZoom { videoView.resetWindowZoom() }

        selectedWindow = target
        remoteWindowState = nil
        restoreOnInitialWindowState = target.minimized
        autoFitEnabled = settings.autoFitWindows
        lastReadingSize = nil
        readingResizeTask?.cancel()
        windowActionsView.update(state: nil, windowId: target.id, isMonitor: target.isMonitor,
                                 autoFitEnabled: autoFitEnabled)
        streamSupportsScaling = nil
        thumbnailBar.setSelected(windowId: target.id)

        activeAppTitleLabel.stringValue = target.title.isEmpty ? target.process : target.title
        setWindowTitle("\(target.title.isEmpty ? target.process : target.title) — Paneo")

        emptyPlaceholder.isHidden = true
        videoView.isHidden = false
        AppMotion.setVisible(floatingBar, true)
        reconnectButton.isHidden = true
        if !isShowingSettings { window.makeFirstResponder(videoView) }
        streamStatusLabel.stringValue = "Conectando…"
        streamMetricsLabel.stringValue = "Aguardando métricas…"

        startViewSession(for: target, host: host, number: number)
    }

    private func startViewSession(for target: RemoteWindow, host: String, number: UInt16) {
        let portText = String(number)
        let creds = credentials(host: host, port: portText)

        viewGeneration = UUID()
        let current = viewGeneration
        videoView.beginStreamTransition()

        sampleStart = ProcessInfo.processInfo.systemUptime
        sampleFrames = 0
        sampleBytes = 0

        let client = RemoteConnection(
            onState: { [weak self] message, ready in
                guard let self, self.viewGeneration == current else { return }
                self.streamStatusLabel.stringValue = ready ? "Conectado" : message
                self.streamStatusLabel.toolTip = message
                if ready {
                    self.streamRetryTask?.cancel()
                    self.streamRetryTask = nil
                    self.viewConnection?.sendQuality(fps: self.streamPreferences.fps,
                                                    bitrate: self.streamPreferences.bitrate,
                                                    maxDimension: self.streamPreferences.maxDimension)
                }
                AppMotion.setVisible(self.reconnectButton, !ready)
                if !ready {
                    self.videoView.resetInput()
                    self.viewConnection = nil
                    self.remoteWindowState = nil
                    self.videoView.allowsWindowZoom = false
                    self.lastReadingSize = nil
                    self.readingResizeTask?.cancel()
                    self.windowActionsView.update(state: nil, windowId: target.id, isMonitor: target.isMonitor,
                                                  autoFitEnabled: self.autoFitEnabled)
                    if message.hasPrefix(RemoteConnection.authFailurePrefix) {
                        self.wantsConnection = false
                        self.pendingFavoriteLaunch = nil
                        self.cancelStreamRetry()
                        self.streamRetryCount = 0
                        self.stopRefresh()
                        self.generation = UUID()
                        self.controlConnection?.disconnect()
                        self.controlConnection = nil
                        self.pendingReconnectTarget = nil
                        self.showWelcome(true)
                        self.handleFailure(message)
                    } else if RemoteConnectionFailure.isTransient(message) {
                        self.scheduleStreamReconnect(for: target)
                    }
                }
            },
            onFrame: { [weak self] png, width, height, _ in
                guard let self, self.viewGeneration == current,
                      self.remoteWindowState?.minimized != true, !self.window.isMiniaturized else { return }
                self.streamRetryCount = 0
                self.videoView.presentPng(png, width: width, height: height)
                self.sampleFrames += 1
                self.sampleBytes += png.count
                self.updateMetrics()
            },
            onMedia: { [weak self] payload, codec, keyframe, width, height, ptsMs in
                guard let self, self.viewGeneration == current else { return }
                self.streamRetryCount = 0
                if codec == ProtocolV2.codecH264 {
                    // Descartar access units ao minimizar quebraria as referências do H.264.
                    self.videoView.decoder.decode(accessUnit: payload,
                                                  keyframe: keyframe,
                                                  width: width,
                                                  height: height,
                                                  ptsMs: ptsMs)
                } else if self.remoteWindowState?.minimized != true, !self.window.isMiniaturized,
                          codec == ProtocolV2.codecJpeg {
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
            },
            onVideoCapabilities: { [weak self] supportsScaling in
                guard let self, self.viewGeneration == current else { return }
                self.streamSupportsScaling = supportsScaling
                (self.streamControlsPopover.contentViewController as? StreamControlsViewController)?
                    .configure(self.streamPreferences, supportsScaling: supportsScaling)
            },
            onWindowState: { [weak self] state in
                guard let self, self.viewGeneration == current, state.windowId == target.id else { return }
                self.remoteWindowState = state
                self.applyRemoteWindowState(state, target: target)
                if self.restoreOnInitialWindowState {
                    self.restoreOnInitialWindowState = false
                    if state.minimized { self.viewConnection?.sendWindowAction("restore") }
                }
                if !state.minimized { self.scheduleReadingResize(debounce: self.lastReadingSize != nil) }
            },
            onWindowCommandResult: { [weak self] action, success, message in
                guard let self, self.viewGeneration == current else { return }
                if !success {
                    if action == "reading" {
                        self.autoFitEnabled = false
                        self.videoView.allowsWindowZoom = false
                        self.readingResizeTask?.cancel()
                        self.windowActionsView.update(state: self.remoteWindowState, windowId: target.id,
                                                      isMonitor: target.isMonitor,
                                                      autoFitEnabled: self.autoFitEnabled)
                    }
                    self.streamStatusLabel.stringValue = message ?? "O Windows recusou a ação \(action)."
                }
            },
            onWindowMenus: { [weak self] menus in
                guard let self, self.viewGeneration == current else { return }
                self.windowActionsView.showMenus(menus)
            }
        )

        videoView.decoder.onError = { [weak self] error in
            guard let self, self.viewGeneration == current else { return }
            self.streamStatusLabel.stringValue = "Vídeo: \(error)"
            AppMotion.setVisible(self.reconnectButton, true)
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
        client.onFileReceived = { [weak self] (fileName: String) in
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
        cancelStreamRetry()
        detailsPopover.close()
        streamControlsPopover.close()
        viewGeneration = UUID()
        audioPlayback.stop()
        videoView.resetInput()
        videoView.restoreCursor()
        viewConnection?.disconnect()
        viewConnection = nil
        remoteWindowState = nil
        videoView.allowsWindowZoom = false
        restoreOnInitialWindowState = false
        readingResizeTask?.cancel()
        readingResizeTask = nil
        lastReadingSize = nil
        windowActionsView.update(state: nil, windowId: selectedWindow?.id,
                                 isMonitor: selectedWindow?.isMonitor == true,
                                 autoFitEnabled: autoFitEnabled)
        selectedWindow = nil
        streamSupportsScaling = nil
        thumbnailBar.setSelected(windowId: nil)
        videoView.isHidden = true
        floatingBar.isHidden = true
        emptyPlaceholder.isHidden = false
        setWindowTitle("Paneo")
    }

    @objc private func reconnectStream() {
        guard let win = selectedWindow else { return }
        select(window: win)
    }

    private func scheduleStreamReconnect(for target: RemoteWindow) {
        guard streamRetryCount < 5 else {
            streamStatusLabel.stringValue = "Não foi possível retomar a transmissão. Tente Reconectar."
            reconnectButton.isHidden = false
            return
        }
        let delay = 1 << streamRetryCount
        streamRetryCount += 1
        streamRetryTask?.cancel()
        streamRetryTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled, let self,
                  self.selectedWindow?.id == target.id, self.viewConnection == nil,
                  self.controlConnection != nil else { return }
            self.streamStatusLabel.stringValue = "Retomando transmissão…"
            guard let port = UInt16(self.port.stringValue.trimmingCharacters(in: .whitespaces)) else { return }
            self.startViewSession(for: target, host: self.normalizedHost(), number: port)
        }
    }

    private func cancelStreamRetry() {
        streamRetryTask?.cancel()
        streamRetryTask = nil
    }

    private func restoreSelectedTargetIfAvailable() {
        guard let previous = pendingReconnectTarget else { return }
        guard let restored = RemoteWindowRecovery.target(previous, windows: windows, monitors: monitors) else { return }
        pendingReconnectTarget = nil
        select(window: restored)
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
            pendingReconnectTarget = nil
            button.title = "Conectar"
            status.stringValue = message
            return
        }
        guard message.hasPrefix(RemoteConnection.authFailurePrefix) else {
            guard RemoteConnectionFailure.isTransient(message) else {
                wantsConnection = false
                pendingReconnectTarget = nil
                button.title = "Conectar"
                return
            }
            scheduleReconnect()
            return
        }
        wantsConnection = false
        pendingReconnectTarget = nil
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
        pendingFavoriteLaunch = nil
        pendingReconnectTarget = nil
        cancelStreamRetry()
        streamRetryCount = 0
        availableApps = []
        retryTask?.cancel(); retryTask = nil
        stopRefresh()
        generation = UUID()
        closeActiveViewSession()
        closeDetachedWindows()
        monitors = []
        windows = []
        thumbnailBar.reset()
        emptyTitle.stringValue = "Selecione um aplicativo na barra"
        emptyDesc.stringValue = "Escolha uma janela ou um favorito para começar a trabalhar no Windows."
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
        titlebarAccessory.view.setFrameSize(NSSize(width: window.frame.width - 180, height: 28))
        updateVideoInteractionExclusions()
    }
    func windowWillClose(_ notification: Notification) {
        closeSettings()
        wantsConnection = false
        disconnect()
        onClose?()
    }
}

@MainActor
private final class TitlebarTitleField: NSTextField {
    override var allowsVibrancy: Bool { false }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        (window?.effectiveAppearance ?? effectiveAppearance).performAsCurrentDrawingAppearance {
            textColor = NSColor.labelColor.usingColorSpace(.deviceRGB)
        }
    }
}

/// Faixa fina no topo da janela, visível só com a lista recolhida: indica que
/// a barra existe e a faz reaparecer quando o mouse encosta na borda superior.
@MainActor
final class PeekStripView: NSView {

    var onEnter: (() -> Void)?
    var position: BarPosition = .top { didSet { needsLayout = true } }
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
        switch position {
        case .top: pill.frame = CGRect(x: bounds.midX - 28, y: bounds.maxY - 6, width: 56, height: 4)
        case .bottom: pill.frame = CGRect(x: bounds.midX - 28, y: 2, width: 56, height: 4)
        case .left: pill.frame = CGRect(x: 2, y: bounds.midY - 28, width: 4, height: 56)
        case .right: pill.frame = CGRect(x: bounds.maxX - 6, y: bounds.midY - 28, width: 4, height: 56)
        }
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
