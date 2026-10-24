import AppKit

/// Card individual de miniatura para uma janela aberta no Windows.
@MainActor
final class ThumbnailCardView: NSView {

    private(set) var target: RemoteWindow
    var onClick: (() -> Void)?
    var onDetach: (() -> Void)?

    var isSelected: Bool = false {
        didSet { updateAppearance() }
    }

    var thumbnailImage: NSImage? {
        didSet { updateThumbnailVisibility() }
    }

    private var density: BarDensity = .large

    private func updateThumbnailVisibility() {
        previewView.image = thumbnailImage
        let showPreview = density != .icons && thumbnailImage != nil
        previewView.isHidden = !showPreview
        placeholderView.isHidden = showPreview
    }

    private let previewView = NSImageView()
    private let placeholderView = NSStackView()
    private let titleLabel = NSTextField(labelWithString: "")
    private let processLabel = NSTextField(labelWithString: "")
    private let bottomBar = NSView()
    private var bottomBarHeight: NSLayoutConstraint!
    private var isHovered: Bool = false
    private var isPointerInside = false
    private var tracking: NSTrackingArea?
    private let detachButton = NSButton()
    private let badgeView = NSTextField(labelWithString: "")
    private var widthConstraint: NSLayoutConstraint!
    private var heightConstraint: NSLayoutConstraint!
    var onToggleFavorite: (() -> Void)?
    var isFavorite: Bool = false {
        didSet {
            setAccessibilityLabel("\(target.title.isEmpty ? target.process : target.title), \(target.process)\(isFavorite ? ", favorito" : "")")
            updateAppearance()
        }
    }

    init(target: RemoteWindow, size: NSSize = NSSize(width: 160, height: 96)) {
        self.target = target
        super.init(frame: NSRect(origin: .zero, size: size))
        wantsLayer = true
        layer?.cornerRadius = 12
        layer?.masksToBounds = true

        toolTip = "\(target.process) — \(target.title.isEmpty ? target.process : target.title)\n\(target.width)×\(target.height)"
        setAccessibilityElement(true)
        setAccessibilityRole(.button)
        setAccessibilityLabel("\(target.title.isEmpty ? target.process : target.title), \(target.process)\(target.minimized ? ", minimizada no Windows" : "")")
        setAccessibilityHelp("Pressione Return para selecionar esta janela.")

        // Visual da prévia (imagem capturada)
        previewView.imageScaling = .scaleProportionallyUpOrDown
        previewView.translatesAutoresizingMaskIntoConstraints = false
        previewView.isHidden = true
        addSubview(previewView)

        // Visual do placeholder quando ainda não há captura de vídeo
        let iconView = Self.makeAppIconView(for: target)

        processLabel.stringValue = target.process
        processLabel.font = .systemFont(ofSize: 10, weight: .medium)
        processLabel.textColor = .secondaryLabelColor
        processLabel.alignment = .center
        processLabel.lineBreakMode = .byTruncatingTail

        placeholderView.orientation = .vertical
        placeholderView.spacing = 4
        placeholderView.alignment = .centerX
        placeholderView.addArrangedSubview(iconView)
        placeholderView.addArrangedSubview(processLabel)
        placeholderView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(placeholderView)

        // Faixa de título no rodapé do card
        bottomBar.wantsLayer = true
        bottomBar.layer?.backgroundColor = NSColor.windowBackgroundColor.withAlphaComponent(0.78).cgColor
        bottomBar.translatesAutoresizingMaskIntoConstraints = false
        addSubview(bottomBar)

        titleLabel.stringValue = target.title.isEmpty ? target.process : target.title
        titleLabel.font = .systemFont(ofSize: 11, weight: .semibold)
        titleLabel.textColor = .labelColor
        titleLabel.lineBreakMode = .byTruncatingTail
        titleLabel.translatesAutoresizingMaskIntoConstraints = false
        bottomBar.addSubview(titleLabel)

        // Botão "Destacar": abre o app numa janela própria do macOS.
        detachButton.image = NSImage(systemSymbolName: "arrow.up.forward.app",
                                     accessibilityDescription: "Destacar em janela")
        detachButton.isBordered = false
        detachButton.contentTintColor = .labelColor
        detachButton.toolTip = target.isAllMonitors
            ? "Abrir cada monitor em uma janela do Mac"
            : "Destacar em janela separada do macOS"
        detachButton.target = self
        detachButton.action = #selector(didClickDetach)
        detachButton.isHidden = true
        detachButton.wantsLayer = true
        detachButton.layer?.cornerRadius = 11
        detachButton.layer?.backgroundColor = NSColor.black.withAlphaComponent(0.55).cgColor
        detachButton.translatesAutoresizingMaskIntoConstraints = false
        addSubview(detachButton)

        // Badge de notificação ou alteração de documento
        badgeView.font = .systemFont(ofSize: 10, weight: .bold)
        badgeView.textColor = .white
        badgeView.alignment = .center
        badgeView.wantsLayer = true
        badgeView.layer?.cornerRadius = 8
        badgeView.layer?.backgroundColor = NSColor.systemRed.cgColor
        badgeView.layer?.masksToBounds = true
        badgeView.translatesAutoresizingMaskIntoConstraints = false
        if let badge = target.badge {
            badgeView.stringValue = badge == "•" ? "  •  " : " \(badge) "
            badgeView.isHidden = false
        } else if target.minimized {
            badgeView.stringValue = " Ⅱ "
            badgeView.layer?.backgroundColor = NSColor.secondaryLabelColor.cgColor
            badgeView.isHidden = false
        } else {
            badgeView.isHidden = true
        }
        addSubview(badgeView, positioned: .above, relativeTo: nil)

        widthConstraint = widthAnchor.constraint(equalToConstant: size.width)
        heightConstraint = heightAnchor.constraint(equalToConstant: size.height)
        bottomBarHeight = bottomBar.heightAnchor.constraint(equalToConstant: 26)
        NSLayoutConstraint.activate([
            widthConstraint,
            heightConstraint,

            badgeView.topAnchor.constraint(equalTo: topAnchor, constant: 5),
            badgeView.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 6),
            badgeView.heightAnchor.constraint(equalToConstant: 16),
            badgeView.widthAnchor.constraint(greaterThanOrEqualToConstant: 16),

            detachButton.topAnchor.constraint(equalTo: topAnchor, constant: 5),
            detachButton.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -5),
            detachButton.widthAnchor.constraint(equalToConstant: 22),
            detachButton.heightAnchor.constraint(equalToConstant: 22),

            previewView.leadingAnchor.constraint(equalTo: leadingAnchor),
            previewView.trailingAnchor.constraint(equalTo: trailingAnchor),
            previewView.topAnchor.constraint(equalTo: topAnchor),
            previewView.bottomAnchor.constraint(equalTo: bottomBar.topAnchor),

            placeholderView.centerXAnchor.constraint(equalTo: centerXAnchor),
            placeholderView.centerYAnchor.constraint(equalTo: previewView.centerYAnchor),
            placeholderView.leadingAnchor.constraint(greaterThanOrEqualTo: leadingAnchor, constant: 8),
            placeholderView.trailingAnchor.constraint(lessThanOrEqualTo: trailingAnchor, constant: -8),

            bottomBar.leadingAnchor.constraint(equalTo: leadingAnchor),
            bottomBar.trailingAnchor.constraint(equalTo: trailingAnchor),
            bottomBar.bottomAnchor.constraint(equalTo: bottomAnchor),
            bottomBarHeight,

            titleLabel.leadingAnchor.constraint(equalTo: bottomBar.leadingAnchor, constant: 8),
            titleLabel.trailingAnchor.constraint(equalTo: bottomBar.trailingAnchor, constant: -8),
            titleLabel.centerYAnchor.constraint(equalTo: bottomBar.centerYAnchor)
        ])

        updateAppearance()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) não suportado.") }

    override var acceptsFirstResponder: Bool { true }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 36 || event.keyCode == 76 { onClick?() }
        else { super.keyDown(with: event) }
    }

    override func accessibilityPerformPress() -> Bool {
        onClick?()
        return true
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: bounds,
                                  options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                  owner: self,
                                  userInfo: nil)
        addTrackingArea(area)
        tracking = area
    }

    @objc private func didClickDetach() { onDetach?() }
    @objc private func didClickToggleFavorite() { onToggleFavorite?() }

    func update(target: RemoteWindow) {
        self.target = target
        titleLabel.stringValue = "\(target.minimized ? "Ⅱ " : "")\(target.title.isEmpty ? target.process : target.title)"
        processLabel.stringValue = target.process
        setAccessibilityLabel("\(target.title.isEmpty ? target.process : target.title), \(target.process)\(target.minimized ? ", minimizada no Windows" : "")\(isFavorite ? ", favorito" : "")")
        toolTip = "\(target.process) — \(target.title.isEmpty ? target.process : target.title)\n\(target.width)×\(target.height)\(target.minimized ? " · Minimizada no Windows" : "")"
        if let badge = target.badge {
            badgeView.stringValue = badge == "•" ? "  •  " : " \(badge) "
            badgeView.layer?.backgroundColor = NSColor.systemRed.cgColor
            badgeView.isHidden = false
        } else if target.minimized {
            badgeView.stringValue = " Ⅱ "
            badgeView.layer?.backgroundColor = NSColor.secondaryLabelColor.cgColor
            badgeView.isHidden = false
        } else {
            badgeView.isHidden = true
        }
        detachButton.toolTip = target.isAllMonitors
            ? "Abrir cada monitor em uma janela do Mac"
            : "Destacar em janela separada do macOS"
    }

    func configure(density: BarDensity) {
        self.density = density
        let size: NSSize
        switch density {
        case .large: size = NSSize(width: 160, height: 96)
        case .compact: size = NSSize(width: 116, height: 76)
        case .icons: size = NSSize(width: 58, height: 58)
        }
        widthConstraint.constant = size.width
        heightConstraint.constant = size.height
        bottomBarHeight.constant = density == .icons ? 0 : 26
        titleLabel.isHidden = density == .icons
        bottomBar.isHidden = density == .icons
        processLabel.isHidden = density != .large
        placeholderView.spacing = density == .large ? 4 : 2
        updateThumbnailVisibility()
    }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        updateAppearance()
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        let menu = NSMenu()
        let item = NSMenuItem(title: target.isAllMonitors ? "Abrir monitores em janelas" : "Destacar em janela",
                              action: #selector(didClickDetach), keyEquivalent: "")
        item.target = self
        menu.addItem(item)
        if !target.isAllMonitors && !target.isMonitor {
            menu.addItem(.separator())
            let favItem = NSMenuItem(title: isFavorite ? "Remover dos Favoritos" : "Fixar nos Favoritos",
                                     action: #selector(didClickToggleFavorite), keyEquivalent: "")
            favItem.target = self
            menu.addItem(favItem)
        }
        return menu
    }

    override func mouseEntered(with event: NSEvent) {
        isHovered = true
        isPointerInside = true
        AppMotion.setVisible(detachButton, true)
        NSCursor.pointingHand.set()
        updateAppearance()
    }

    override func mouseExited(with event: NSEvent) {
        isHovered = false
        isPointerInside = false
        AppMotion.setVisible(detachButton, false)
        NSCursor.arrow.set()
        updateAppearance()
    }

    override func mouseUp(with event: NSEvent) {
        let location = convert(event.locationInWindow, from: nil)
        if bounds.contains(location) {
            window?.makeFirstResponder(self)
            onClick?()
        }
    }

    private func updateAppearance() {
        let accent = AppSettings.shared.accentColor
        effectiveAppearance.performAsCurrentDrawingAppearance {
            titleLabel.textColor = .labelColor
            processLabel.textColor = .secondaryLabelColor
            detachButton.contentTintColor = .labelColor
            bottomBar.layer?.backgroundColor = NSColor.windowBackgroundColor.withAlphaComponent(0.78).cgColor
            AppMotion.animate {
                if self.isSelected {
                    self.layer?.borderColor = accent.cgColor
                    self.layer?.borderWidth = 2.5
                    self.layer?.backgroundColor = accent.withAlphaComponent(0.22).cgColor
                } else if self.isHovered {
                    self.layer?.borderColor = NSColor.labelColor.withAlphaComponent(0.42).cgColor
                    self.layer?.borderWidth = 1.5
                    self.layer?.backgroundColor = NSColor.controlBackgroundColor.withAlphaComponent(0.32).cgColor
                } else {
                    self.layer?.borderColor = NSColor.separatorColor.withAlphaComponent(0.34).cgColor
                    self.layer?.borderWidth = 1
                    self.layer?.backgroundColor = NSColor.controlBackgroundColor.withAlphaComponent(0.18).cgColor
                }
                self.layer?.transform = CATransform3DMakeScale(self.isPointerInside ? 1.015 : 1, self.isPointerInside ? 1.015 : 1, 1)
            }
        }
    }

    private static func makeAppIconView(for target: RemoteWindow) -> NSView {
        // 1. Ícone nativo enviado pelo Windows Host
        if let iconBase64 = target.icon,
           let data = Data(base64Encoded: iconBase64),
           let nsImage = NSImage(data: data) {
            let iv = NSImageView()
            iv.image = nsImage
            iv.imageScaling = .scaleProportionallyUpOrDown
            iv.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([
                iv.widthAnchor.constraint(equalToConstant: 38),
                iv.heightAnchor.constraint(equalToConstant: 38)
            ])
            return iv
        }

        // 1b. Tela inteira: ícone de monitor, sem ícone de aplicativo.
        if target.isMonitor {
            let container = NSView()
            container.wantsLayer = true
            container.layer?.cornerRadius = 9
            container.layer?.backgroundColor = NSColor(srgbRed: 88/255, green: 86/255, blue: 214/255, alpha: 1).cgColor
            container.translatesAutoresizingMaskIntoConstraints = false
            let symbol = NSImageView()
            symbol.image = NSImage(systemSymbolName: target.isAllMonitors ? "display.2" : "display",
                                   accessibilityDescription: target.title)
            symbol.contentTintColor = .white
            symbol.imageScaling = .scaleProportionallyUpOrDown
            symbol.translatesAutoresizingMaskIntoConstraints = false
            container.addSubview(symbol)
            NSLayoutConstraint.activate([
                container.widthAnchor.constraint(equalToConstant: 38),
                container.heightAnchor.constraint(equalToConstant: 38),
                symbol.centerXAnchor.constraint(equalTo: container.centerXAnchor),
                symbol.centerYAnchor.constraint(equalTo: container.centerYAnchor),
                symbol.widthAnchor.constraint(equalToConstant: 24),
                symbol.heightAnchor.constraint(equalToConstant: 24)
            ])
            return container
        }

        // 2. Fallback moderno: App Squircle estilizado com cor vibrante e SF Symbol correspondente
        let container = NSView()
        container.wantsLayer = true
        container.layer?.cornerRadius = 9
        container.layer?.masksToBounds = true
        container.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            container.widthAnchor.constraint(equalToConstant: 38),
            container.heightAnchor.constraint(equalToConstant: 38)
        ])

        let bgColor: NSColor
        let symbolName: String
        let proc = target.process.lowercased()

        if proc.contains("chatgpt") {
            bgColor = NSColor(srgbRed: 16/255, green: 163/255, blue: 127/255, alpha: 1.0)
            symbolName = "sparkles"
        } else if proc.contains("discord") {
            bgColor = NSColor(srgbRed: 88/255, green: 101/255, blue: 242/255, alpha: 1.0)
            symbolName = "bubble.left.and.bubble.right.fill"
        } else if proc.contains("cua") || proc.contains("cursor") || proc.contains("driver") {
            bgColor = NSColor(srgbRed: 255/255, green: 149/255, blue: 0/255, alpha: 1.0)
            symbolName = "cursorarrow.rays"
        } else if proc.contains("explorer") || proc.contains("program manager") {
            bgColor = NSColor(srgbRed: 0/255, green: 120/255, blue: 215/255, alpha: 1.0)
            symbolName = "folder.fill"
        } else if proc.contains("code") || proc.contains("studio") {
            bgColor = NSColor(srgbRed: 0/255, green: 122/255, blue: 204/255, alpha: 1.0)
            symbolName = "chevron.left.forwardslash.chevron.right"
        } else if proc.contains("chrome") || proc.contains("edge") || proc.contains("firefox") || proc.contains("browser") {
            bgColor = NSColor(srgbRed: 52/255, green: 168/255, blue: 83/255, alpha: 1.0)
            symbolName = "globe"
        } else if proc.contains("terminal") || proc.contains("cmd") || proc.contains("powershell") {
            bgColor = NSColor(srgbRed: 30/255, green: 30/255, blue: 30/255, alpha: 1.0)
            symbolName = "terminal.fill"
        } else if proc.contains("notepad") {
            bgColor = NSColor(srgbRed: 59/255, green: 130/255, blue: 246/255, alpha: 1.0)
            symbolName = "note.text"
        } else {
            let hash = abs(target.process.hashValue)
            let hue = CGFloat(hash % 360) / 360.0
            bgColor = NSColor(calibratedHue: hue, saturation: 0.65, brightness: 0.75, alpha: 1.0)
            symbolName = "macwindow"
        }

        container.layer?.backgroundColor = bgColor.cgColor

        let symbol = NSImageView()
        symbol.image = NSImage(systemSymbolName: symbolName, accessibilityDescription: target.process)
        symbol.contentTintColor = .white
        symbol.imageScaling = .scaleProportionallyUpOrDown
        symbol.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(symbol)

        NSLayoutConstraint.activate([
            symbol.centerXAnchor.constraint(equalTo: container.centerXAnchor),
            symbol.centerYAnchor.constraint(equalTo: container.centerYAnchor),
            symbol.widthAnchor.constraint(equalToConstant: 22),
            symbol.heightAnchor.constraint(equalToConstant: 22)
        ])

        return container
    }
}

/// Barra configurável exibindo favoritos e miniaturas dos apps do Windows.
@MainActor
final class WindowThumbnailBar: NSView {
    var onSelectWindow: ((RemoteWindow) -> Void)?
    var onRefresh: (() -> Void)?
    var onDisconnect: (() -> Void)?
    var onDetachWindow: ((RemoteWindow) -> Void)?
    var onCollapse: (() -> Void)?
    var onHoverChanged: ((Bool) -> Void)?
    var onActivateFavorite: ((FavoriteApp, NSView) -> Void)?
    var onOpenApps: (() -> Void)?
    var onShowQuality: ((NSView) -> Void)?

    var favoriteStore = FavoriteAppStore.shared
    private var hostKey = "default"
    private var favorites: [FavoriteApp] = []
    private var favoriteViews: [String: FavoriteAppCardView] = [:]
    private var position: BarPosition = .top
    private var density: BarDensity = .large
    private var hoverTracking: NSTrackingArea?
    private let panel: NSView
    private let titleBadge = NSTextField(labelWithString: "Janelas")
    private let countBadge = NSTextField(labelWithString: "0")
    private let badgeStack: NSStackView
    private let scrollView = NSScrollView()
    private let cardsStack = NSStackView()
    private let refreshButton = NSButton()
    private let appsButton = NSButton(title: "Apps", target: nil, action: nil)
    private let qualityButton = NSButton(title: "Vídeo", target: nil, action: nil)
    private let disconnectButton = NSButton(title: "Desconectar", target: nil, action: nil)
    private let collapseButton = NSButton()
    private let actionsStack: NSStackView
    private var layoutConstraints: [NSLayoutConstraint] = []
    private var cardViews: [Int64: ThumbnailCardView] = [:]
    private var cachedThumbnails: [Int64: NSImage] = [:]
    private var currentWindows: [RemoteWindow] = []
    private var selectedId: Int64?
    private var favoritePresentation: [String: String] = [:]

    var preferredThickness: CGFloat {
        switch (position, density) {
        case (.top, .large), (.bottom, .large): 128
        case (.top, .compact), (.bottom, .compact): 108
        case (.top, .icons), (.bottom, .icons): 82
        case (.left, .large), (.right, .large): 188
        case (.left, .compact), (.right, .compact): 144
        case (.left, .icons), (.right, .icons): 84
        }
    }

    override init(frame frameRect: NSRect) {
        panel = Glass.panel(cornerRadius: 16)
        badgeStack = NSStackView(views: [titleBadge, countBadge])
        actionsStack = NSStackView(views: [collapseButton, refreshButton, appsButton, qualityButton, disconnectButton])
        super.init(frame: frameRect)
        translatesAutoresizingMaskIntoConstraints = false
        panel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(panel)
        titleBadge.font = .systemFont(ofSize: 12, weight: .bold)
        titleBadge.textColor = .labelColor
        countBadge.font = .monospacedDigitSystemFont(ofSize: 11, weight: .semibold)
        countBadge.textColor = .secondaryLabelColor
        badgeStack.spacing = 2
        badgeStack.translatesAutoresizingMaskIntoConstraints = false
        panel.addSubview(badgeStack)

        cardsStack.orientation = .horizontal
        cardsStack.spacing = 10
        cardsStack.alignment = .centerY
        cardsStack.translatesAutoresizingMaskIntoConstraints = false
        scrollView.drawsBackground = false
        scrollView.hasVerticalScroller = false
        scrollView.hasHorizontalScroller = true
        scrollView.autohidesScrollers = true
        scrollView.scrollerStyle = .overlay
        scrollView.documentView = cardsStack
        scrollView.translatesAutoresizingMaskIntoConstraints = false
        panel.addSubview(scrollView)

        refreshButton.image = NSImage(systemSymbolName: "arrow.clockwise", accessibilityDescription: "Atualizar janelas")
        refreshButton.toolTip = "Atualizar lista de janelas (Automático a cada 2s)"
        refreshButton.target = self
        refreshButton.action = #selector(didClickRefresh)
        Glass.style(refreshButton)
        appsButton.image = NSImage(systemSymbolName: "square.grid.2x2", accessibilityDescription: "Aplicativos")
        appsButton.setAccessibilityLabel("Aplicativos abertos e instalados no Windows")
        appsButton.target = self
        appsButton.action = #selector(didClickApps)
        Glass.style(appsButton)
        qualityButton.image = NSImage(systemSymbolName: "slider.horizontal.3", accessibilityDescription: "Qualidade do vídeo")
        qualityButton.setAccessibilityLabel("Qualidade, resolução e quadros por segundo")
        qualityButton.target = self
        qualityButton.action = #selector(didClickQuality)
        Glass.style(qualityButton)
        disconnectButton.target = self
        disconnectButton.action = #selector(didClickDisconnect)
        Glass.style(disconnectButton)
        collapseButton.image = NSImage(systemSymbolName: "chevron.up", accessibilityDescription: "Recolher lista")
        collapseButton.toolTip = "Recolher a lista de apps (⌘⇧B). Passe o mouse na borda para reaparecer."
        collapseButton.target = self
        collapseButton.action = #selector(didClickCollapse)
        Glass.style(collapseButton)
        actionsStack.spacing = 8
        actionsStack.translatesAutoresizingMaskIntoConstraints = false
        panel.addSubview(actionsStack)
        NotificationCenter.default.addObserver(self, selector: #selector(refreshAppearance),
                                               name: AppSettings.didChange, object: AppSettings.shared)
        configure(position: .top, density: .large)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) não suportado.") }

    func configure(position: BarPosition, density: BarDensity) {
        self.position = position
        self.density = density
        NSLayoutConstraint.deactivate(layoutConstraints)
        let vertical = position.isVertical
        cardsStack.orientation = vertical ? .vertical : .horizontal
        cardsStack.alignment = vertical ? .centerX : .centerY
        cardsStack.spacing = vertical || density == .large ? 10 : 7
        badgeStack.orientation = vertical ? .horizontal : .vertical
        badgeStack.alignment = vertical ? .centerY : .leading
        actionsStack.orientation = vertical ? .vertical : .horizontal
        actionsStack.alignment = vertical ? .centerX : .centerY
        scrollView.hasVerticalScroller = vertical
        scrollView.hasHorizontalScroller = !vertical
        scrollView.horizontalScrollElasticity = vertical ? .none : .allowed
        scrollView.verticalScrollElasticity = vertical ? .allowed : .none
        let collapseSymbol: String
        switch position {
        case .top: collapseSymbol = "chevron.up"
        case .bottom: collapseSymbol = "chevron.down"
        case .left: collapseSymbol = "chevron.left"
        case .right: collapseSymbol = "chevron.right"
        }
        collapseButton.image = NSImage(systemSymbolName: collapseSymbol, accessibilityDescription: "Recolher lista")
        let compactSidebar = vertical && density == .icons
        titleBadge.isHidden = compactSidebar
        updateWindowCount()
        disconnectButton.title = compactSidebar ? "" : "Desconectar"
        disconnectButton.image = compactSidebar ? NSImage(systemSymbolName: "network.slash", accessibilityDescription: "Desconectar") : nil
        appsButton.title = compactSidebar ? "" : "Apps"
        qualityButton.title = compactSidebar ? "" : "Vídeo"
        appsButton.imagePosition = compactSidebar ? .imageOnly : .imageLeading
        qualityButton.imagePosition = compactSidebar ? .imageOnly : .imageLeading
        let edges = [panel.leadingAnchor.constraint(equalTo: leadingAnchor), panel.trailingAnchor.constraint(equalTo: trailingAnchor), panel.topAnchor.constraint(equalTo: topAnchor), panel.bottomAnchor.constraint(equalTo: bottomAnchor)]
        let content: [NSLayoutConstraint]
        if vertical {
            content = [
                badgeStack.leadingAnchor.constraint(equalTo: panel.leadingAnchor, constant: 12),
                badgeStack.trailingAnchor.constraint(lessThanOrEqualTo: panel.trailingAnchor, constant: -12),
                badgeStack.topAnchor.constraint(equalTo: panel.topAnchor, constant: 14),
                scrollView.leadingAnchor.constraint(equalTo: panel.leadingAnchor, constant: 6),
                scrollView.trailingAnchor.constraint(equalTo: panel.trailingAnchor, constant: -6),
                scrollView.topAnchor.constraint(equalTo: badgeStack.bottomAnchor, constant: 8),
                scrollView.bottomAnchor.constraint(equalTo: actionsStack.topAnchor, constant: -16),
                cardsStack.topAnchor.constraint(equalTo: scrollView.topAnchor, constant: 2),
                cardsStack.centerXAnchor.constraint(equalTo: scrollView.centerXAnchor),
                cardsStack.widthAnchor.constraint(equalTo: scrollView.widthAnchor, constant: -8),
                actionsStack.leadingAnchor.constraint(equalTo: panel.leadingAnchor, constant: 12),
                actionsStack.trailingAnchor.constraint(equalTo: panel.trailingAnchor, constant: -12),
                actionsStack.bottomAnchor.constraint(equalTo: panel.bottomAnchor, constant: -14)
            ] + actionsStack.arrangedSubviews.flatMap { button in
                [button.heightAnchor.constraint(equalToConstant: 32),
                 button.widthAnchor.constraint(equalTo: actionsStack.widthAnchor)]
            }
        } else {
            content = [
                badgeStack.leadingAnchor.constraint(equalTo: panel.leadingAnchor, constant: 14),
                badgeStack.centerYAnchor.constraint(equalTo: panel.centerYAnchor),
                scrollView.leadingAnchor.constraint(equalTo: badgeStack.trailingAnchor, constant: 12),
                scrollView.trailingAnchor.constraint(equalTo: actionsStack.leadingAnchor, constant: -12),
                scrollView.topAnchor.constraint(equalTo: panel.topAnchor, constant: 6),
                scrollView.bottomAnchor.constraint(equalTo: panel.bottomAnchor, constant: -6),
                cardsStack.topAnchor.constraint(equalTo: scrollView.topAnchor, constant: 2),
                cardsStack.leadingAnchor.constraint(equalTo: scrollView.leadingAnchor),
                cardsStack.heightAnchor.constraint(equalTo: scrollView.heightAnchor, constant: -4),
                actionsStack.trailingAnchor.constraint(equalTo: panel.trailingAnchor, constant: -14),
                actionsStack.centerYAnchor.constraint(equalTo: panel.centerYAnchor)
            ]
        }
        layoutConstraints = edges + content
        NSLayoutConstraint.activate(layoutConstraints)
        for card in cardViews.values { card.configure(density: density) }
        removeFavoriteViews()
        favoritePresentation.removeAll()
        renderFavorites()
    }

    func setHostKey(_ key: String) {
        hostKey = key
        favorites = favoriteStore.favorites(forHost: key)
        for window in currentWindows { cardViews[window.id]?.isFavorite = favorites.contains(FavoriteApp(process: window.process)) }
        removeFavoriteViews()
        favoritePresentation.removeAll()
        renderFavorites()
    }

    func setWindows(_ windows: [RemoteWindow], selectedId: Int64?) {
        self.selectedId = selectedId
        let newIds = windows.map(\.id)
        let oldIds = currentWindows.map(\.id)
        currentWindows = windows
        updateWindowCount()
        let activeSet = Set(newIds)
        cachedThumbnails = cachedThumbnails.filter { activeSet.contains($0.key) }

        if newIds != oldIds {
            for card in cardViews.values { cardsStack.removeArrangedSubview(card); card.removeFromSuperview() }
            cardViews.removeAll()
            for window in windows {
                let card = ThumbnailCardView(target: window, size: cardSize)
                cardViews[window.id] = card
                cardsStack.addArrangedSubview(card)
            }
        }
        for window in windows {
            guard let card = cardViews[window.id] else { continue }
            card.update(target: window)
            card.configure(density: density)
            card.isSelected = window.id == selectedId
            card.isFavorite = favorites.contains(FavoriteApp(process: window.process))
            card.thumbnailImage = density == .icons ? nil : cachedThumbnails[window.id]
            card.onClick = { [weak self] in self?.onSelectWindow?(window) }
            card.onDetach = { [weak self] in self?.onDetachWindow?(window) }
            card.onToggleFavorite = { [weak self] in self?.toggleFavorite(for: window) }
        }
        let presentation = Dictionary(uniqueKeysWithValues: favorites.map { favorite in
            let window = windows.first { FavoriteApp(process: $0.process) == favorite }
            return (favorite.process, window?.icon ?? "closed")
        })
        if presentation != favoritePresentation {
            removeFavoriteViews()
            favoritePresentation = presentation
        }
        renderFavorites()
    }

    func reset() {
        currentWindows.removeAll()
        cachedThumbnails.removeAll()
        selectedId = nil
        updateWindowCount()
        for card in cardViews.values { cardsStack.removeArrangedSubview(card); card.removeFromSuperview() }
        cardViews.removeAll()
        removeFavoriteViews()
        favoritePresentation = Dictionary(uniqueKeysWithValues: favorites.map { ($0.process, "closed") })
        renderFavorites()
    }

    func setSelected(windowId: Int64?) {
        selectedId = windowId
        for (id, card) in cardViews { card.isSelected = id == windowId }
        renderFavorites()
    }

    func updateThumbnail(windowId: Int64, cgImage: CGImage) {
        let image = NSImage(cgImage: cgImage, size: NSSize(width: cgImage.width, height: cgImage.height))
        cachedThumbnails[windowId] = image
        cardViews[windowId]?.thumbnailImage = image
        renderFavorites()
    }

    private var cardSize: NSSize {
        switch density {
        case .large: NSSize(width: 160, height: 96)
        case .compact: NSSize(width: 116, height: 76)
        case .icons: NSSize(width: 58, height: 58)
        }
    }

    private var favoriteCardSize: NSSize {
        switch density {
        case .large: NSSize(width: 92, height: 96)
        case .compact: NSSize(width: 76, height: 76)
        case .icons: NSSize(width: 58, height: 58)
        }
    }

    private func updateWindowCount() {
        countBadge.stringValue = position.isVertical && density == .icons
            ? "\(currentWindows.count)"
            : "\(currentWindows.count) aberta\(currentWindows.count == 1 ? "" : "s")"
    }

    private func toggleFavorite(for window: RemoteWindow) {
        guard !window.isMonitor else { return }
        let favorite = FavoriteApp(process: window.process)
        if let index = favorites.firstIndex(of: favorite) { favorites.remove(at: index) }
        else { favorites.append(favorite) }
        favoriteStore.save(favorites, forHost: hostKey)
        for item in currentWindows { cardViews[item.id]?.isFavorite = favorites.contains(FavoriteApp(process: item.process)) }
        renderFavorites()
    }

    private func removeFavorite(_ favorite: FavoriteApp) {
        favorites.removeAll { $0 == favorite }
        favoriteStore.save(favorites, forHost: hostKey)
        for window in currentWindows { cardViews[window.id]?.isFavorite = favorites.contains(FavoriteApp(process: window.process)) }
        removeFavoriteViews()
        favoritePresentation.removeAll()
        renderFavorites()
    }

    private func moveFavorite(_ process: String, relativeTo target: String, after: Bool) {
        guard let source = favorites.firstIndex(where: { $0.process == process }),
              let destination = favorites.firstIndex(where: { $0.process == target }) else { return }
        let favorite = favorites.remove(at: source)
        let adjustedDestination = destination - (source < destination ? 1 : 0)
        favorites.insert(favorite, at: adjustedDestination + (after ? 1 : 0))
        favoriteStore.save(favorites, forHost: hostKey)
        removeFavoriteViews()
        renderFavorites()
    }

    private func removeFavoriteViews() {
        for view in favoriteViews.values { cardsStack.removeArrangedSubview(view); view.removeFromSuperview() }
        favoriteViews.removeAll()
    }

    private func renderFavorites() {
        let wanted = Set(favorites.map(\.process))
        for process in Set(favoriteViews.keys).subtracting(wanted) {
            if let view = favoriteViews.removeValue(forKey: process) {
                cardsStack.removeArrangedSubview(view)
                view.removeFromSuperview()
            }
        }
        for (index, favorite) in favorites.enumerated() {
            let matching = currentWindows.first { FavoriteApp(process: $0.process) == favorite }
            let icon = matching.flatMap { window in
                window.icon.flatMap { encoded in Data(base64Encoded: encoded).flatMap { NSImage(data: $0) } }
            }
            let view = favoriteViews[favorite.process] ?? FavoriteAppCardView(favorite: favorite, icon: icon, size: favoriteCardSize)
            if favoriteViews[favorite.process] == nil {
                favoriteViews[favorite.process] = view
                cardsStack.insertArrangedSubview(view, at: index)
            }
            view.isSelected = selectedId.flatMap { id in currentWindows.first(where: { $0.id == id }) }
                .map { FavoriteApp(process: $0.process) == favorite } ?? false
            view.onActivate = { [weak self, weak view] in
                guard let self, let view else { return }
                self.onActivateFavorite?(favorite, view)
            }
            view.onRemove = { [weak self] in self?.removeFavorite(favorite) }
            view.isVerticalOrder = position.isVertical
            view.onMove = { [weak self] process, after in self?.moveFavorite(process, relativeTo: favorite.process, after: after) }
        }
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let hoverTracking { removeTrackingArea(hoverTracking) }
        let area = NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect], owner: self, userInfo: nil)
        addTrackingArea(area)
        hoverTracking = area
    }

    override func mouseEntered(with event: NSEvent) { onHoverChanged?(true) }
    override func mouseExited(with event: NSEvent) { onHoverChanged?(false) }
    @objc private func refreshAppearance() {
        for (id, card) in cardViews { card.isSelected = id == selectedId }
        for (process, view) in favoriteViews {
            view.isSelected = selectedId.flatMap { id in currentWindows.first(where: { $0.id == id }) }
                .map { FavoriteApp(process: $0.process).process == process } ?? false
        }
    }
    @objc private func didClickCollapse() { onCollapse?() }
    @objc private func didClickRefresh() { onRefresh?() }
    @objc private func didClickDisconnect() { onDisconnect?() }
    @objc private func didClickApps() { onOpenApps?() }
    @objc private func didClickQuality() { onShowQuality?(qualityButton) }
}
