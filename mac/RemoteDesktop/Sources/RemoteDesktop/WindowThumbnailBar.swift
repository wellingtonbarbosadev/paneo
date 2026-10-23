import AppKit

/// Card individual de miniatura para uma janela aberta no Windows.
@MainActor
final class ThumbnailCardView: NSView {

    let target: RemoteWindow
    var onClick: (() -> Void)?
    var onDetach: (() -> Void)?

    var isSelected: Bool = false {
        didSet { updateAppearance() }
    }

    var thumbnailImage: NSImage? {
        didSet {
            previewView.image = thumbnailImage
            previewView.isHidden = thumbnailImage == nil
            placeholderView.isHidden = thumbnailImage != nil
        }
    }

    private let previewView = NSImageView()
    private let placeholderView = NSStackView()
    private let titleLabel = NSTextField(labelWithString: "")
    private let processLabel = NSTextField(labelWithString: "")
    private var isHovered: Bool = false
    private var tracking: NSTrackingArea?
    private let detachButton = NSButton()
    private let badgeView = NSTextField(labelWithString: "")
    var onToggleFavorite: (() -> Void)?
    var isFavorite: Bool = false {
        didSet { updateAppearance() }
    }

    init(target: RemoteWindow) {
        self.target = target
        super.init(frame: NSRect(x: 0, y: 0, width: 160, height: 96))
        wantsLayer = true
        layer?.cornerRadius = 12
        layer?.masksToBounds = true

        toolTip = "\(target.process) — \(target.title.isEmpty ? target.process : target.title)\n\(target.width)×\(target.height)"

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
        let bottomBar = NSView()
        bottomBar.wantsLayer = true
        bottomBar.layer?.backgroundColor = NSColor.black.withAlphaComponent(0.65).cgColor
        bottomBar.translatesAutoresizingMaskIntoConstraints = false
        addSubview(bottomBar)

        titleLabel.stringValue = target.title.isEmpty ? target.process : target.title
        titleLabel.font = .systemFont(ofSize: 11, weight: .semibold)
        titleLabel.textColor = .white
        titleLabel.lineBreakMode = .byTruncatingTail
        titleLabel.translatesAutoresizingMaskIntoConstraints = false
        bottomBar.addSubview(titleLabel)

        // Botão "Destacar": abre o app numa janela própria do macOS.
        detachButton.image = NSImage(systemSymbolName: "arrow.up.forward.app",
                                     accessibilityDescription: "Destacar em janela")
        detachButton.isBordered = false
        detachButton.contentTintColor = .white
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
        } else {
            badgeView.isHidden = true
        }
        addSubview(badgeView, positioned: .above, relativeTo: nil)

        NSLayoutConstraint.activate([
            widthAnchor.constraint(equalToConstant: 160),
            heightAnchor.constraint(equalToConstant: 96),

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
            bottomBar.heightAnchor.constraint(equalToConstant: 26),

            titleLabel.leadingAnchor.constraint(equalTo: bottomBar.leadingAnchor, constant: 8),
            titleLabel.trailingAnchor.constraint(equalTo: bottomBar.trailingAnchor, constant: -8),
            titleLabel.centerYAnchor.constraint(equalTo: bottomBar.centerYAnchor)
        ])

        updateAppearance()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) não suportado.") }

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
        detachButton.isHidden = false
        NSCursor.pointingHand.set()
        updateAppearance()
    }

    override func mouseExited(with event: NSEvent) {
        isHovered = false
        detachButton.isHidden = true
        NSCursor.arrow.set()
        updateAppearance()
    }

    override func mouseUp(with event: NSEvent) {
        let location = convert(event.locationInWindow, from: nil)
        if bounds.contains(location) {
            onClick?()
        }
    }

    private func updateAppearance() {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        if isSelected {
            layer?.borderColor = NSColor.controlAccentColor.cgColor
            layer?.borderWidth = 2.5
            layer?.backgroundColor = NSColor.controlAccentColor.withAlphaComponent(0.2).cgColor
        } else if isHovered {
            layer?.borderColor = NSColor.labelColor.withAlphaComponent(0.5).cgColor
            layer?.borderWidth = 1.5
            layer?.backgroundColor = NSColor.controlBackgroundColor.withAlphaComponent(0.25).cgColor
        } else {
            layer?.borderColor = NSColor.separatorColor.withAlphaComponent(0.3).cgColor
            layer?.borderWidth = 1.0
            layer?.backgroundColor = NSColor.controlBackgroundColor.withAlphaComponent(0.12).cgColor
        }
        CATransaction.commit()
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

/// Barra horizontal com efeito translúcido (Header) exibindo as miniaturas dos apps do Windows.
@MainActor
final class WindowThumbnailBar: NSView {

    var onSelectWindow: ((RemoteWindow) -> Void)?
    var onRefresh: (() -> Void)?
    var onDisconnect: (() -> Void)?
    var onDetachWindow: ((RemoteWindow) -> Void)?
    var onCollapse: (() -> Void)?
    /// Mouse entrou/saiu da barra (usado para recolher de novo depois de espiar).
    var onHoverChanged: ((Bool) -> Void)?

    private var hoverTracking: NSTrackingArea?
    private let collapseButton = NSButton()
    private let panel: NSView
    private let titleBadge = NSTextField(labelWithString: "Janelas")
    private let countBadge = NSTextField(labelWithString: "0")
    private let scrollView = NSScrollView()
    private let cardsStack = NSStackView()
    private let refreshButton = NSButton()
    private let disconnectButton = NSButton(title: "Desconectar", target: nil, action: nil)

    private var cardViews: [Int64: ThumbnailCardView] = [:]
    private var cachedThumbnails: [Int64: NSImage] = [:]
    private var currentWindows: [RemoteWindow] = []
    private var selectedId: Int64?

    override init(frame frameRect: NSRect) {
        panel = Glass.panel(cornerRadius: 16)
        super.init(frame: frameRect)
        translatesAutoresizingMaskIntoConstraints = false

        panel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(panel)

        // Título e Contador à esquerda
        titleBadge.font = .systemFont(ofSize: 12, weight: .bold)
        titleBadge.textColor = .labelColor
        countBadge.font = .monospacedDigitSystemFont(ofSize: 11, weight: .semibold)
        countBadge.textColor = .secondaryLabelColor

        let badgeStack = NSStackView(views: [titleBadge, countBadge])
        badgeStack.orientation = .vertical
        badgeStack.spacing = 2
        badgeStack.alignment = .leading
        badgeStack.translatesAutoresizingMaskIntoConstraints = false
        panel.addSubview(badgeStack)

        // Scroll horizontal para os cards
        cardsStack.orientation = .horizontal
        cardsStack.spacing = 10
        cardsStack.alignment = .centerY
        cardsStack.translatesAutoresizingMaskIntoConstraints = false

        scrollView.drawsBackground = false
        scrollView.hasVerticalScroller = false
        scrollView.hasHorizontalScroller = true
        scrollView.autohidesScrollers = true
        scrollView.scrollerStyle = .overlay
        scrollView.horizontalScrollElasticity = .allowed
        scrollView.verticalScrollElasticity = .none
        scrollView.documentView = cardsStack
        scrollView.translatesAutoresizingMaskIntoConstraints = false
        panel.addSubview(scrollView)

        // Botões de ação à direita
        refreshButton.image = NSImage(systemSymbolName: "arrow.clockwise", accessibilityDescription: "Atualizar janelas")
        refreshButton.toolTip = "Atualizar lista de janelas (Automático a cada 2s)"
        refreshButton.target = self
        refreshButton.action = #selector(didClickRefresh)
        Glass.style(refreshButton)

        disconnectButton.target = self
        disconnectButton.action = #selector(didClickDisconnect)
        Glass.style(disconnectButton)

        collapseButton.image = NSImage(systemSymbolName: "chevron.up", accessibilityDescription: "Recolher lista")
        collapseButton.toolTip = "Recolher a lista de apps (⌘⇧B). Passe o mouse no topo para reaparecer."
        collapseButton.target = self
        collapseButton.action = #selector(didClickCollapse)
        Glass.style(collapseButton)

        let actionsStack = NSStackView(views: [collapseButton, refreshButton, disconnectButton])
        actionsStack.orientation = .horizontal
        actionsStack.spacing = 8
        actionsStack.alignment = .centerY
        actionsStack.translatesAutoresizingMaskIntoConstraints = false
        panel.addSubview(actionsStack)

        NSLayoutConstraint.activate([
            heightAnchor.constraint(equalToConstant: 128),

            panel.leadingAnchor.constraint(equalTo: leadingAnchor),
            panel.trailingAnchor.constraint(equalTo: trailingAnchor),
            panel.topAnchor.constraint(equalTo: topAnchor),
            panel.bottomAnchor.constraint(equalTo: bottomAnchor),

            badgeStack.leadingAnchor.constraint(equalTo: panel.leadingAnchor, constant: 14),
            badgeStack.centerYAnchor.constraint(equalTo: panel.centerYAnchor),

            scrollView.leadingAnchor.constraint(equalTo: badgeStack.trailingAnchor, constant: 12),
            scrollView.trailingAnchor.constraint(equalTo: actionsStack.leadingAnchor, constant: -12),
            scrollView.topAnchor.constraint(equalTo: panel.topAnchor, constant: 6),
            scrollView.bottomAnchor.constraint(equalTo: panel.bottomAnchor, constant: -6),

            cardsStack.topAnchor.constraint(equalTo: scrollView.topAnchor, constant: 2),
            cardsStack.leadingAnchor.constraint(equalTo: scrollView.leadingAnchor),
            cardsStack.heightAnchor.constraint(equalToConstant: 96),

            actionsStack.trailingAnchor.constraint(equalTo: panel.trailingAnchor, constant: -14),
            actionsStack.centerYAnchor.constraint(equalTo: panel.centerYAnchor)
        ])
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) não suportado.") }

    func setWindows(_ windows: [RemoteWindow], selectedId: Int64?) {
        self.selectedId = selectedId
        countBadge.stringValue = "\(windows.count) aberta\(windows.count == 1 ? "" : "s")"

        let newIds = windows.map(\.id)
        let oldIds = currentWindows.map(\.id)

        // Se a lista de janelas for idêntica, apenas atualiza a seleção sem recriar views
        if newIds == oldIds {
            setSelected(windowId: selectedId)
            return
        }

        self.currentWindows = windows

        // Remove do cache janelas que não existem mais no PC
        let activeSet = Set(newIds)
        cachedThumbnails = cachedThumbnails.filter { activeSet.contains($0.key) }

        // Reconstrução dos cards mantendo as miniaturas cacheadas
        for card in cardViews.values {
            card.removeFromSuperview()
        }
        cardViews.removeAll()

        for window in windows {
            let card = ThumbnailCardView(target: window)
            card.isSelected = (window.id == selectedId)
            if let cached = cachedThumbnails[window.id] {
                card.thumbnailImage = cached
            }
            card.onClick = { [weak self] in
                self?.onSelectWindow?(window)
            }
            card.onDetach = { [weak self] in
                self?.onDetachWindow?(window)
            }
            cardViews[window.id] = card
            cardsStack.addArrangedSubview(card)
        }
    }

    func reset() {
        currentWindows.removeAll()
        cachedThumbnails.removeAll()
        selectedId = nil
        countBadge.stringValue = "0"
        for card in cardViews.values {
            card.removeFromSuperview()
        }
        cardViews.removeAll()
    }

    func setSelected(windowId: Int64?) {
        self.selectedId = windowId
        for (id, card) in cardViews {
            card.isSelected = (id == windowId)
        }
    }

    func updateThumbnail(windowId: Int64, cgImage: CGImage) {
        let nsImage = NSImage(cgImage: cgImage, size: NSSize(width: cgImage.width, height: cgImage.height))
        cachedThumbnails[windowId] = nsImage
        if let card = cardViews[windowId] {
            card.thumbnailImage = nsImage
        }
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let hoverTracking { removeTrackingArea(hoverTracking) }
        let area = NSTrackingArea(rect: bounds,
                                  options: [.mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect],
                                  owner: self, userInfo: nil)
        addTrackingArea(area)
        hoverTracking = area
    }

    override func mouseEntered(with event: NSEvent) { onHoverChanged?(true) }
    override func mouseExited(with event: NSEvent) { onHoverChanged?(false) }

    @objc private func didClickCollapse() { onCollapse?() }

    @objc private func didClickRefresh() {
        onRefresh?()
    }

    @objc private func didClickDisconnect() {
        onDisconnect?()
    }
}
