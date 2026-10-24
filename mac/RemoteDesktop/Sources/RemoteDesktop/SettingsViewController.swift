import AppKit

@MainActor
final class SettingsViewController: NSViewController {
    var onClose: (() -> Void)?
    private let closeButton = NSButton(title: "Fechar", target: nil, action: nil)
    private let columns = NSStackView()
    private var columnWidth: NSLayoutConstraint!
    private var previewWidth: NSLayoutConstraint!
    private var isNarrow = false
    private let settings: AppSettings
    private let preview = SettingsPreviewView()
    private let settingsScrollView = NSScrollView()
    private let appearanceChoices = NSStackView()
    private let positionChoices = NSStackView()
    private let densityChoices = NSStackView()
    private let accentChoices = NSStackView()
    private let accentWell = NSColorWell(frame: .zero)
    private let autoFitWindowsButton = NSButton(checkboxWithTitle: "Ajustar janelas automaticamente", target: nil, action: nil)
    private var choiceButtons: [String: [NSButton]] = [:]

    init(settings: AppSettings = .shared) {
        self.settings = settings
        super.init(nibName: nil, bundle: nil)
        NSColorPanel.shared.showsAlpha = false

        let content = NSView(frame: NSRect(x: 0, y: 0, width: 920, height: 650))
        let scroll = settingsScrollView
        scroll.drawsBackground = false
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(scroll)

        let stack = SettingsDocumentStackView()
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 14
        stack.translatesAutoresizingMaskIntoConstraints = false
        scroll.documentView = stack

        closeButton.target = self
        closeButton.action = #selector(closeSettings(_:))
        closeButton.keyEquivalent = "\u{1b}"
        closeButton.setAccessibilityLabel("Fechar ajustes")
        Glass.style(closeButton)

        let heading = NSStackView(views: [
            Self.title("Ajustes do Paneo", size: 22),
            Self.subtitle("Tema, cor e organização das janelas remotas.")
        ])
        heading.orientation = .vertical
        heading.alignment = .leading
        heading.spacing = 5
        heading.translatesAutoresizingMaskIntoConstraints = false
        closeButton.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(heading)
        content.addSubview(closeButton)

        preview.translatesAutoresizingMaskIntoConstraints = false
        preview.heightAnchor.constraint(equalToConstant: 238).isActive = true
        let previewSection = Self.section(title: "Prévia", content: preview)

        let appearanceContent = NSStackView()
        appearanceContent.orientation = .vertical
        appearanceContent.alignment = .leading
        appearanceContent.spacing = 8
        appearanceChoices.orientation = .horizontal
        appearanceChoices.spacing = 10
        accentChoices.orientation = .horizontal
        accentChoices.spacing = 10
        appearanceContent.addArrangedSubview(Self.field("Tema", appearanceChoices))
        appearanceContent.addArrangedSubview(Self.field("Cor de destaque", accentChoices))

        let colorRow = NSStackView(views: [Self.caption("Cor personalizada"), accentWell])
        colorRow.orientation = .horizontal
        colorRow.alignment = .centerY
        colorRow.spacing = 10
        appearanceContent.addArrangedSubview(colorRow)

        let barContent = NSStackView()
        barContent.orientation = .vertical
        barContent.alignment = .leading
        barContent.spacing = 6
        positionChoices.orientation = .horizontal
        positionChoices.spacing = 10
        densityChoices.orientation = .horizontal
        densityChoices.spacing = 10
        barContent.addArrangedSubview(Self.field("Posição", positionChoices))
        barContent.addArrangedSubview(Self.field("Densidade", densityChoices))
        autoFitWindowsButton.target = self
        autoFitWindowsButton.action = #selector(autoFitWindowsChanged(_:))
        autoFitWindowsButton.setAccessibilityHelp("Redimensiona as janelas remotas para acompanhar o espaço disponível para vídeo.")
        autoFitWindowsButton.toolTip = "Redimensiona as janelas remotas para acompanhar o espaço disponível para vídeo."
        barContent.addArrangedSubview(autoFitWindowsButton)

        let appearanceSection = Self.section(title: "Aparência", content: appearanceContent)
        let barSection = Self.section(title: "Barra e janelas", content: barContent)
        let settingsColumn = NSStackView(views: [appearanceSection, barSection])
        settingsColumn.orientation = .vertical
        settingsColumn.alignment = .leading
        settingsColumn.spacing = 6
        settingsColumn.translatesAutoresizingMaskIntoConstraints = false
        columns.addArrangedSubview(settingsColumn)
        columns.addArrangedSubview(previewSection)
        columns.orientation = .horizontal
        columns.alignment = .top
        columns.spacing = 16
        columns.translatesAutoresizingMaskIntoConstraints = false
        stack.addArrangedSubview(columns)

        columnWidth = settingsColumn.widthAnchor.constraint(equalToConstant: 400)
        previewWidth = previewSection.widthAnchor.constraint(equalTo: columns.widthAnchor, constant: -416)
        NSLayoutConstraint.activate([
            heading.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 24),
            heading.topAnchor.constraint(equalTo: content.topAnchor, constant: 24),
            heading.trailingAnchor.constraint(lessThanOrEqualTo: closeButton.leadingAnchor, constant: -20),
            closeButton.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -24),
            closeButton.centerYAnchor.constraint(equalTo: heading.centerYAnchor),
            closeButton.widthAnchor.constraint(greaterThanOrEqualToConstant: 76),
            closeButton.heightAnchor.constraint(equalToConstant: 32),
            scroll.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            scroll.topAnchor.constraint(equalTo: heading.bottomAnchor, constant: 16),
            scroll.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -12),
            stack.leadingAnchor.constraint(equalTo: scroll.contentView.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: scroll.contentView.trailingAnchor),
            stack.topAnchor.constraint(equalTo: scroll.contentView.topAnchor),
            columns.widthAnchor.constraint(equalTo: scroll.contentView.widthAnchor, constant: -48),
            columns.leadingAnchor.constraint(equalTo: stack.leadingAnchor, constant: 24),
            columnWidth,
            appearanceSection.widthAnchor.constraint(equalTo: settingsColumn.widthAnchor),
            barSection.widthAnchor.constraint(equalTo: settingsColumn.widthAnchor),
            previewWidth,
            preview.widthAnchor.constraint(equalTo: previewSection.widthAnchor, constant: -32)
        ])

        view = content
        stack.onAppearanceChange = { [weak self] in
            self?.refreshChoices()
            self?.updatePreview()
        }

        addChoices()
        accentWell.color = settings.accentColor
        accentWell.isEnabled = settings.accent == .custom
        accentWell.setAccessibilityLabel("Escolher cor de destaque personalizada")
        accentWell.target = self
        accentWell.action = #selector(colorChanged(_:))
        NotificationCenter.default.addObserver(self, selector: #selector(settingsDidChange), name: AppSettings.didChange, object: settings)
        autoFitWindowsButton.state = settings.autoFitWindows ? .on : .off
        refreshChoices()
        updatePreview()
    }

    required init?(coder: NSCoder) { nil }

    override func viewDidLayout() {
        super.viewDidLayout()
        let narrow = view.bounds.width < 780
        guard narrow != isNarrow else { return }
        isNarrow = narrow
        let settingsColumn = columns.arrangedSubviews[0]
        NSLayoutConstraint.deactivate([columnWidth, previewWidth])
        columns.orientation = narrow ? .vertical : .horizontal
        columns.alignment = narrow ? .leading : .top
        columnWidth = narrow
            ? settingsColumn.widthAnchor.constraint(equalTo: columns.widthAnchor)
            : settingsColumn.widthAnchor.constraint(equalToConstant: 400)
        previewWidth.constant = narrow ? 0 : -416
        NSLayoutConstraint.activate([columnWidth, previewWidth])
    }

    @objc private func closeSettings(_ sender: Any?) {
        accentWell.deactivate()
        onClose?()
    }

    private func addChoices() {
        addChoiceGroup(to: appearanceChoices, key: "appearance", values: [
            ("Sistema", "circle.lefthalf.filled", AppAppearance.system.rawValue),
            ("Claro", "sun.max", AppAppearance.light.rawValue),
            ("Escuro", "moon", AppAppearance.dark.rawValue)
        ])
        addChoiceGroup(to: accentChoices, key: "accent", values: [
            ("Sistema", "paintpalette", AppAccent.system.rawValue),
            ("Personalizada", "eyedropper", AppAccent.custom.rawValue)
        ])
        addChoiceGroup(to: positionChoices, key: "position", values: [
            ("Topo", "arrow.up.to.line", BarPosition.top.rawValue),
            ("Baixo", "arrow.down.to.line", BarPosition.bottom.rawValue),
            ("Esquerda", "arrow.left.to.line", BarPosition.left.rawValue),
            ("Direita", "arrow.right.to.line", BarPosition.right.rawValue)
        ])
        addChoiceGroup(to: densityChoices, key: "density", values: [
            ("Grande", "rectangle.grid.1x2", BarDensity.large.rawValue),
            ("Compacta", "rectangle.grid.2x2", BarDensity.compact.rawValue),
            ("Ícones", "square.grid.2x2", BarDensity.icons.rawValue)
        ])
    }

    private func addChoiceGroup(to stack: NSStackView, key: String, values: [(String, String, String)]) {
        let buttons = values.enumerated().map { index, value in
            let button = SettingsChoiceButton(title: value.0, target: self, action: #selector(choiceChanged(_:)))
            button.setButtonType(.toggle)
            button.isBordered = false
            button.bezelStyle = .regularSquare
            button.controlSize = .small
            button.wantsLayer = true
            button.translatesAutoresizingMaskIntoConstraints = false
            button.layer?.cornerRadius = 10
            button.layer?.borderWidth = 1
            button.image = NSImage(systemSymbolName: value.1, accessibilityDescription: value.0)
            button.imagePosition = .imageAbove
            button.imageHugsTitle = true
            button.image?.size = NSSize(width: 18, height: 18)
            button.tag = index
            button.identifier = NSUserInterfaceItemIdentifier(value.2)
            button.setAccessibilityLabel(value.0)
            button.setAccessibilityHelp("Aplicar \(value.0.lowercased())")
            button.widthAnchor.constraint(equalToConstant: key == "position" ? 84 : 96).isActive = true
            button.heightAnchor.constraint(equalToConstant: 68).isActive = true
            stack.addArrangedSubview(button)
            return button
        }
        choiceButtons[key] = buttons
    }

    @objc private func choiceChanged(_ sender: NSButton) {
        guard let raw = sender.identifier?.rawValue else { return }
        let key = choiceButtons.first(where: { $0.value.contains(sender) })?.key
        switch key {
        case "appearance": if let value = AppAppearance(rawValue: raw) { settings.appearance = value }
        case "accent": if let value = AppAccent(rawValue: raw) { settings.accent = value }
        case "position": if let value = BarPosition(rawValue: raw) { settings.barPosition = value }
        case "density": if let value = BarDensity(rawValue: raw) { settings.barDensity = value }
        default: break
        }
        refreshChoices()
        updatePreview()
    }

    @objc private func colorChanged(_ sender: NSColorWell) {
        settings.accentColor = sender.color
        updatePreview()
    }

    @objc private func autoFitWindowsChanged(_ sender: NSButton) {
        settings.autoFitWindows = sender.state == .on
    }

    @objc private func settingsDidChange() {
        if let window = view.window { settings.applyAppearance(to: window) }
        accentWell.color = settings.accentColor
        accentWell.isEnabled = settings.accent == .custom
        autoFitWindowsButton.state = settings.autoFitWindows ? .on : .off
        refreshChoices()
        updatePreview()
    }

    private func refreshChoices() {
        view.effectiveAppearance.performAsCurrentDrawingAppearance {
            let selected: [String: String] = [
                "appearance": settings.appearance.rawValue,
                "accent": settings.accent.rawValue,
                "position": settings.barPosition.rawValue,
                "density": settings.barDensity.rawValue
            ]
            for (key, buttons) in choiceButtons {
                for button in buttons {
                    let isSelected = button.identifier?.rawValue == selected[key]
                    button.state = isSelected ? .on : .off
                    button.contentTintColor = isSelected ? settings.accentColor : .secondaryLabelColor
                    button.layer?.backgroundColor = (isSelected
                        ? settings.accentColor.withAlphaComponent(0.2)
                        : NSColor.controlBackgroundColor.withAlphaComponent(0.22)).cgColor
                    button.layer?.borderColor = (isSelected
                        ? settings.accentColor.cgColor
                        : NSColor.separatorColor.withAlphaComponent(0.45).cgColor)
                    button.layer?.borderWidth = isSelected ? 1.5 : 1
                    AppMotion.animate { button.animator().alphaValue = isSelected ? 1 : 0.94 }
                }
            }
        }
    }

    private func updatePreview() {
        AppMotion.animate { [weak self] in
            guard let self else { return }
            self.preview.update(position: self.settings.barPosition,
                                density: self.settings.barDensity,
                                appearance: self.settings.appearance,
                                accent: self.settings.accentColor)
        }
    }

    private static func section(title: String, content: NSView) -> NSView {
        let panel = Glass.panel(cornerRadius: 18)
        let stack = NSStackView(views: [self.title(title, size: 16), content])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 8
        stack.translatesAutoresizingMaskIntoConstraints = false
        panel.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: panel.leadingAnchor, constant: 14),
            stack.trailingAnchor.constraint(equalTo: panel.trailingAnchor, constant: -14),
            stack.topAnchor.constraint(equalTo: panel.topAnchor, constant: 14),
            stack.bottomAnchor.constraint(equalTo: panel.bottomAnchor, constant: -14),
            content.widthAnchor.constraint(equalTo: stack.widthAnchor)
        ])
        return panel
    }

    private static func field(_ title: String, _ control: NSView) -> NSStackView {
        let stack = NSStackView(views: [caption(title), control])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 6
        return stack
    }

    private static func title(_ text: String, size: CGFloat = 25) -> NSTextField {
        let label = NSTextField(labelWithString: text)
        label.font = .systemFont(ofSize: size, weight: .bold)
        label.textColor = .labelColor
        return label
    }

    private static func subtitle(_ text: String) -> NSTextField {
        let label = NSTextField(wrappingLabelWithString: text)
        label.font = .systemFont(ofSize: 13)
        label.textColor = .secondaryLabelColor
        return label
    }

    private static func caption(_ text: String) -> NSTextField {
        let label = NSTextField(labelWithString: text)
        label.font = .systemFont(ofSize: 12, weight: .semibold)
        label.textColor = .secondaryLabelColor
        return label
    }
}

@MainActor
private final class SettingsDocumentStackView: NSStackView {
    override var isFlipped: Bool { true }
    var onAppearanceChange: (() -> Void)?

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        onAppearanceChange?()
    }
}

@MainActor
private final class SettingsChoiceButton: NSButton {
    override var intrinsicContentSize: NSSize {
        NSSize(width: NSView.noIntrinsicMetric, height: 68)
    }
}

@MainActor
private final class SettingsPreviewView: NSView {
    private let frameView = NSView()
    private let stageView = NSView()
    private let barView = NSView()
    private let cards = NSStackView()
    private var layoutConstraints: [NSLayoutConstraint] = []

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.cornerRadius = 14
        frameView.wantsLayer = true
        frameView.layer?.cornerRadius = 10
        stageView.wantsLayer = true
        stageView.layer?.cornerRadius = 7
        barView.wantsLayer = true
        barView.layer?.cornerRadius = 7
        cards.alignment = .centerY
        cards.spacing = 7
        cards.translatesAutoresizingMaskIntoConstraints = false
        barView.addSubview(cards)
        frameView.addSubview(stageView)
        frameView.addSubview(barView)
        addSubview(frameView)
        frameView.translatesAutoresizingMaskIntoConstraints = false
        stageView.translatesAutoresizingMaskIntoConstraints = false
        barView.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            frameView.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 16),
            frameView.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -16),
            frameView.topAnchor.constraint(equalTo: topAnchor, constant: 14),
            frameView.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -14),
            cards.centerXAnchor.constraint(equalTo: barView.centerXAnchor),
            cards.centerYAnchor.constraint(equalTo: barView.centerYAnchor)
        ])
    }

    required init?(coder: NSCoder) { nil }

    func update(position: BarPosition, density: BarDensity, appearance: AppAppearance, accent: NSColor) {
        let vertical = position.isVertical
        let isDark = appearance == .dark || (appearance == .system && effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua)
        let background = isDark ? NSColor(calibratedWhite: 0.16, alpha: 1) : NSColor(calibratedWhite: 0.94, alpha: 1)
        let stage = isDark ? NSColor(calibratedWhite: 0.1, alpha: 1) : NSColor.white
        layer?.backgroundColor = background.cgColor
        frameView.layer?.backgroundColor = (isDark ? NSColor(calibratedWhite: 0.22, alpha: 1) : NSColor(calibratedWhite: 0.88, alpha: 1)).cgColor
        stageView.layer?.backgroundColor = stage.cgColor
        barView.layer?.backgroundColor = (isDark ? NSColor(calibratedWhite: 0.3, alpha: 1) : NSColor(calibratedWhite: 0.78, alpha: 1)).cgColor
        NSLayoutConstraint.deactivate(layoutConstraints)
        for view in cards.arrangedSubviews { cards.removeArrangedSubview(view); view.removeFromSuperview() }
        cards.orientation = vertical ? .vertical : .horizontal
        cards.alignment = vertical ? .centerX : .centerY

        let size: NSSize = switch density {
        case .large: NSSize(width: 54, height: 38)
        case .compact: NSSize(width: 42, height: 30)
        case .icons: NSSize(width: 24, height: 24)
        }
        for index in 0..<3 {
            let card = NSView()
            card.wantsLayer = true
            card.layer?.cornerRadius = density == .icons ? 6 : 4
            card.layer?.backgroundColor = accent.withAlphaComponent(index == 0 ? 0.45 : 0.18).cgColor
            card.layer?.borderColor = (index == 0 ? accent : NSColor.separatorColor).cgColor
            card.layer?.borderWidth = index == 0 ? 1.5 : 0.7
            card.translatesAutoresizingMaskIntoConstraints = false
            card.widthAnchor.constraint(equalToConstant: size.width).isActive = true
            card.heightAnchor.constraint(equalToConstant: size.height).isActive = true
            cards.addArrangedSubview(card)
        }

        let thickness: CGFloat = density == .icons ? 46 : (density == .compact ? 58 : 70)
        if vertical {
            layoutConstraints = [barView.widthAnchor.constraint(equalToConstant: thickness),
                                 barView.topAnchor.constraint(equalTo: frameView.topAnchor, constant: 8),
                                 barView.bottomAnchor.constraint(equalTo: frameView.bottomAnchor, constant: -8),
                                 stageView.topAnchor.constraint(equalTo: frameView.topAnchor, constant: 8),
                                 stageView.bottomAnchor.constraint(equalTo: frameView.bottomAnchor, constant: -8)]
            if position == .left {
                layoutConstraints += [barView.leadingAnchor.constraint(equalTo: frameView.leadingAnchor, constant: 8),
                                      stageView.leadingAnchor.constraint(equalTo: barView.trailingAnchor, constant: 8),
                                      stageView.trailingAnchor.constraint(equalTo: frameView.trailingAnchor, constant: -8)]
            } else {
                layoutConstraints += [barView.trailingAnchor.constraint(equalTo: frameView.trailingAnchor, constant: -8),
                                      stageView.leadingAnchor.constraint(equalTo: frameView.leadingAnchor, constant: 8),
                                      stageView.trailingAnchor.constraint(equalTo: barView.leadingAnchor, constant: -8)]
            }
        } else {
            layoutConstraints = [barView.leadingAnchor.constraint(equalTo: frameView.leadingAnchor, constant: 8),
                                 barView.trailingAnchor.constraint(equalTo: frameView.trailingAnchor, constant: -8),
                                 barView.heightAnchor.constraint(equalToConstant: thickness),
                                 stageView.leadingAnchor.constraint(equalTo: frameView.leadingAnchor, constant: 8),
                                 stageView.trailingAnchor.constraint(equalTo: frameView.trailingAnchor, constant: -8)]
            if position == .top {
                layoutConstraints += [barView.topAnchor.constraint(equalTo: frameView.topAnchor, constant: 8),
                                      stageView.topAnchor.constraint(equalTo: barView.bottomAnchor, constant: 8),
                                      stageView.bottomAnchor.constraint(equalTo: frameView.bottomAnchor, constant: -8)]
            } else {
                layoutConstraints += [stageView.topAnchor.constraint(equalTo: frameView.topAnchor, constant: 8),
                                      stageView.bottomAnchor.constraint(equalTo: barView.topAnchor, constant: -8),
                                      barView.bottomAnchor.constraint(equalTo: frameView.bottomAnchor, constant: -8)]
            }
        }
        NSLayoutConstraint.activate(layoutConstraints)
        needsLayout = true
    }
}
