import AppKit

enum SearchPaletteItem {
    case window(RemoteWindow)
    case app(RemoteApp)

    var title: String {
        switch self {
        case .window(let win): return win.title.isEmpty ? win.process : win.title
        case .app(let app): return "Abrir \(app.name)"
        }
    }

    var subtitle: String {
        switch self {
        case .window(let win): return win.isMonitor ? "Tela do Windows" : win.process
        case .app(let app): return "Novo aplicativo Windows (\(app.cmd))"
        }
    }

    var icon: String? {
        switch self {
        case .window(let win): return win.icon
        case .app(let app): return app.icon
        }
    }

    var isMonitor: Bool {
        switch self {
        case .window(let win): return win.isMonitor
        case .app: return false
        }
    }
}

/// Paleta de busca rápida estilo Spotlight (⌘K) para alternar janelas, monitores e lançar aplicativos.
@MainActor
final class QuickSearchPalette: NSView, NSSearchFieldDelegate {

    var onSelect: ((RemoteWindow) -> Void)?
    var onLaunchApp: ((RemoteApp) -> Void)?
    var onClose: (() -> Void)?

    private let searchField = NSSearchField()
    private let scrollView = NSScrollView()
    private let stackView = NSStackView()

    private var allItems: [SearchPaletteItem] = []
    private var filteredItems: [SearchPaletteItem] = []
    private var selectedIndex: Int = 0

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        setupUI()
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    private func setupUI() {
        wantsLayer = true
        layer?.cornerRadius = 16
        layer?.masksToBounds = false
        layer?.backgroundColor = NSColor(calibratedWhite: 0.12, alpha: 0.92).cgColor
        layer?.borderWidth = 1
        layer?.borderColor = NSColor(calibratedWhite: 0.3, alpha: 0.6).cgColor
        layer?.shadowColor = NSColor.black.cgColor
        layer?.shadowOpacity = 0.45
        layer?.shadowOffset = CGSize(width: 0, height: -6)
        layer?.shadowRadius = 18

        searchField.placeholderString = "Buscar janela ou lançar aplicativo... (⌘K)"
        searchField.font = .systemFont(ofSize: 15, weight: .medium)
        searchField.focusRingType = .none
        searchField.isBordered = false
        searchField.backgroundColor = .clear
        searchField.delegate = self
        searchField.translatesAutoresizingMaskIntoConstraints = false
        addSubview(searchField)

        let separator = NSBox()
        separator.boxType = .separator
        separator.translatesAutoresizingMaskIntoConstraints = false
        addSubview(separator)

        scrollView.hasVerticalScroller = true
        scrollView.drawsBackground = false
        scrollView.translatesAutoresizingMaskIntoConstraints = false

        stackView.orientation = .vertical
        stackView.alignment = .leading
        stackView.spacing = 4
        stackView.translatesAutoresizingMaskIntoConstraints = false
        scrollView.documentView = stackView

        addSubview(scrollView)

        NSLayoutConstraint.activate([
            searchField.topAnchor.constraint(equalTo: topAnchor, constant: 14),
            searchField.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 16),
            searchField.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -16),
            searchField.heightAnchor.constraint(equalToConstant: 28),

            separator.topAnchor.constraint(equalTo: searchField.bottomAnchor, constant: 12),
            separator.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 12),
            separator.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -12),
            separator.heightAnchor.constraint(equalToConstant: 1),

            scrollView.topAnchor.constraint(equalTo: separator.bottomAnchor, constant: 8),
            scrollView.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 8),
            scrollView.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -8),
            scrollView.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -8),

            stackView.leadingAnchor.constraint(equalTo: scrollView.contentView.leadingAnchor),
            stackView.trailingAnchor.constraint(equalTo: scrollView.contentView.trailingAnchor),
            stackView.topAnchor.constraint(equalTo: scrollView.contentView.topAnchor)
        ])
    }

    func show(with windows: [RemoteWindow], apps: [RemoteApp] = []) {
        var items: [SearchPaletteItem] = windows.map { .window($0) }
        items.append(contentsOf: apps.map { .app($0) })
        allItems = items
        searchField.stringValue = ""
        selectedIndex = 0
        applyFilter()
        window?.makeFirstResponder(searchField)
    }

    func controlTextDidChange(_ obj: Notification) {
        selectedIndex = 0
        applyFilter()
    }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        if commandSelector == #selector(NSResponder.cancelOperation(_:)) {
            onClose?()
            return true
        }
        if commandSelector == #selector(NSResponder.moveDown(_:)) {
            if !filteredItems.isEmpty {
                selectedIndex = min(filteredItems.count - 1, selectedIndex + 1)
                updateSelectionHighlight()
            }
            return true
        }
        if commandSelector == #selector(NSResponder.moveUp(_:)) {
            if !filteredItems.isEmpty {
                selectedIndex = max(0, selectedIndex - 1)
                updateSelectionHighlight()
            }
            return true
        }
        if commandSelector == #selector(NSResponder.insertNewline(_:)) {
            if selectedIndex >= 0 && selectedIndex < filteredItems.count {
                let chosen = filteredItems[selectedIndex]
                switch chosen {
                case .window(let win):
                    onSelect?(win)
                case .app(let app):
                    onLaunchApp?(app)
                }
                onClose?()
            }
            return true
        }
        return false
    }

    private func applyFilter() {
        let query = searchField.stringValue.trimmingCharacters(in: .whitespaces).lowercased()
        if query.isEmpty {
            filteredItems = allItems
        } else {
            filteredItems = allItems.filter {
                $0.title.lowercased().contains(query) || $0.subtitle.lowercased().contains(query)
            }
        }
        rebuildList()
    }

    private func rebuildList() {
        for sub in stackView.arrangedSubviews {
            stackView.removeArrangedSubview(sub)
            sub.removeFromSuperview()
        }

        if filteredItems.isEmpty {
            let emptyLabel = NSTextField(labelWithString: "Nenhum aplicativo ou janela encontrado")
            emptyLabel.textColor = .secondaryLabelColor
            emptyLabel.font = .systemFont(ofSize: 13)
            stackView.addArrangedSubview(emptyLabel)
            return
        }

        for (idx, item) in filteredItems.enumerated() {
            let row = createRow(item: item, isSelected: idx == selectedIndex)
            stackView.addArrangedSubview(row)
            row.leadingAnchor.constraint(equalTo: stackView.leadingAnchor).isActive = true
            row.trailingAnchor.constraint(equalTo: stackView.trailingAnchor).isActive = true
        }
    }

    private func updateSelectionHighlight() {
        for (idx, view) in stackView.arrangedSubviews.enumerated() {
            if let row = view as? PaletteRowView {
                row.setSelected(idx == selectedIndex)
            }
        }
    }

    private func createRow(item: SearchPaletteItem, isSelected: Bool) -> PaletteRowView {
        let row = PaletteRowView(item: item, isSelected: isSelected)
        row.onClick = { [weak self] in
            switch item {
            case .window(let win):
                self?.onSelect?(win)
            case .app(let app):
                self?.onLaunchApp?(app)
            }
            self?.onClose?()
        }
        return row
    }
}

@MainActor
private final class PaletteRowView: NSView {
    var onClick: (() -> Void)?
    private let titleLabel = NSTextField(labelWithString: "")
    private let subtitleLabel = NSTextField(labelWithString: "")
    private let iconImageView = NSImageView()

    init(item: SearchPaletteItem, isSelected: Bool) {
        super.init(frame: .zero)
        setup(item: item, isSelected: isSelected)
    }

    required init?(coder: NSCoder) { fatalError() }

    private func setup(item: SearchPaletteItem, isSelected: Bool) {
        wantsLayer = true
        layer?.cornerRadius = 8
        translatesAutoresizingMaskIntoConstraints = false
        heightAnchor.constraint(equalToConstant: 40).isActive = true

        iconImageView.imageScaling = .scaleProportionallyUpOrDown
        iconImageView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(iconImageView)

        if let base64 = item.icon, let data = Data(base64Encoded: base64), let img = NSImage(data: data) {
            iconImageView.image = img
        } else if item.isMonitor {
            iconImageView.image = NSImage(systemSymbolName: "display", accessibilityDescription: nil)
        } else {
            switch item {
            case .window:
                iconImageView.image = NSImage(systemSymbolName: "app.window.checkmark", accessibilityDescription: nil)
            case .app:
                iconImageView.image = NSImage(systemSymbolName: "plus.square.fill", accessibilityDescription: nil)
            }
        }

        titleLabel.stringValue = item.title
        titleLabel.font = .systemFont(ofSize: 13, weight: .medium)
        titleLabel.lineBreakMode = .byTruncatingTail
        titleLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(titleLabel)

        subtitleLabel.stringValue = item.subtitle
        subtitleLabel.font = .systemFont(ofSize: 11)
        subtitleLabel.textColor = .secondaryLabelColor
        subtitleLabel.lineBreakMode = .byTruncatingTail
        subtitleLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(subtitleLabel)

        NSLayoutConstraint.activate([
            iconImageView.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 10),
            iconImageView.centerYAnchor.constraint(equalTo: centerYAnchor),
            iconImageView.widthAnchor.constraint(equalToConstant: 24),
            iconImageView.heightAnchor.constraint(equalToConstant: 24),

            titleLabel.leadingAnchor.constraint(equalTo: iconImageView.trailingAnchor, constant: 10),
            titleLabel.topAnchor.constraint(equalTo: topAnchor, constant: 4),
            titleLabel.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -10),

            subtitleLabel.leadingAnchor.constraint(equalTo: titleLabel.leadingAnchor),
            subtitleLabel.topAnchor.constraint(equalTo: titleLabel.bottomAnchor, constant: 2),
            subtitleLabel.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -10)
        ])

        setSelected(isSelected)
    }

    func setSelected(_ selected: Bool) {
        if selected {
            layer?.backgroundColor = NSColor.controlAccentColor.withAlphaComponent(0.35).cgColor
        } else {
            layer?.backgroundColor = .clear
        }
    }

    override func mouseDown(with event: NSEvent) {
        onClick?()
    }
}
