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
        case .app: return "Aplicativo instalado no Windows"
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

    var isApp: Bool {
        if case .app = self { return true }
        return false
    }

    var stableID: StableID {
        switch self {
        case .window(let window): .window(window.id)
        case .app(let app): .app(app.id)
        }
    }

    func hasSameContent(as other: SearchPaletteItem) -> Bool {
        switch (self, other) {
        case let (.window(a), .window(b)):
            return a.id == b.id && a.title == b.title && a.process == b.process &&
                a.width == b.width && a.height == b.height && a.monitorId == b.monitorId &&
                a.icon == b.icon
        case let (.app(a), .app(b)):
            return a.id == b.id && a.name == b.name && a.cmd == b.cmd &&
                a.icon == b.icon && a.process == b.process
        default:
            return false
        }
    }

    enum StableID: Hashable {
        case window(Int64)
        case app(String)
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
    private let backdrop = Glass.panel(cornerRadius: 16)

    private var allItems: [SearchPaletteItem] = []
    private var filteredItems: [SearchPaletteItem] = []
    private var selectedIndex: Int = 0
    private var itemRows: [PaletteRowView] = []

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        setupUI()
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    private func setupUI() {
        backdrop.translatesAutoresizingMaskIntoConstraints = false
        addSubview(backdrop, positioned: .below, relativeTo: nil)
        NSLayoutConstraint.activate([
            backdrop.leadingAnchor.constraint(equalTo: leadingAnchor),
            backdrop.trailingAnchor.constraint(equalTo: trailingAnchor),
            backdrop.topAnchor.constraint(equalTo: topAnchor),
            backdrop.bottomAnchor.constraint(equalTo: bottomAnchor)
        ])

        searchField.placeholderString = "Buscar janela ou aplicativo... (⌘K)"
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
        searchField.stringValue = ""
        selectedIndex = 0
        scrollView.contentView.scroll(to: .zero)
        update(with: windows, apps: apps, preserveState: false)
        window?.makeFirstResponder(searchField)
    }

    /// Atualiza catálogo e janelas sem fechar a paleta nem perder busca, seleção ou posição.
    func update(with windows: [RemoteWindow], apps: [RemoteApp]) {
        update(with: windows, apps: apps, preserveState: true)
    }

    private func update(with windows: [RemoteWindow], apps: [RemoteApp], preserveState: Bool) {
        var items: [SearchPaletteItem] = windows.map { .window($0) }
        items.append(contentsOf: apps.map { .app($0) })
        if preserveState && itemsEqual(allItems, items) { return }

        let selectedID = filteredItems.indices.contains(selectedIndex)
            ? filteredItems[selectedIndex].stableID : nil
        let scrollOrigin = scrollView.contentView.bounds.origin
        let oldFilteredItems = filteredItems
        allItems = items
        filteredItems = filteredItems(matching: searchField.stringValue)
        if let selectedID, let index = filteredItems.firstIndex(where: { $0.stableID == selectedID }) {
            selectedIndex = index
        } else if filteredItems.isEmpty {
            selectedIndex = 0
        } else {
            selectedIndex = min(max(selectedIndex, 0), filteredItems.count - 1)
        }
        guard !preserveState || !itemsEqual(oldFilteredItems, filteredItems) else { return }
        rebuildList()
        if preserveState {
            layoutSubtreeIfNeeded()
            scrollView.contentView.scroll(to: scrollOrigin)
            scrollView.reflectScrolledClipView(scrollView.contentView)
            selectedIndex = itemRows.isEmpty ? 0 : min(max(selectedIndex, 0), itemRows.count - 1)
            updateSelectionHighlight()
            scrollSelectedRowToVisibleIfNeeded()
        } else {
            scrollView.contentView.scroll(to: .zero)
            scrollView.reflectScrolledClipView(scrollView.contentView)
        }
    }

    private func itemsEqual(_ lhs: [SearchPaletteItem], _ rhs: [SearchPaletteItem]) -> Bool {
        lhs.count == rhs.count && zip(lhs, rhs).allSatisfy { pair in
            pair.0.hasSameContent(as: pair.1)
        }
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
                scrollSelectedRowToVisible()
            }
            return true
        }
        if commandSelector == #selector(NSResponder.moveUp(_:)) {
            if !filteredItems.isEmpty {
                selectedIndex = max(0, selectedIndex - 1)
                updateSelectionHighlight()
                scrollSelectedRowToVisible()
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
        applyFilter(preserving: nil)
    }

    private func applyFilter(preserving selectedID: SearchPaletteItem.StableID?) {
        filteredItems = filteredItems(matching: searchField.stringValue)
        if let selectedID, let index = filteredItems.firstIndex(where: { $0.stableID == selectedID }) {
            selectedIndex = index
        } else if filteredItems.isEmpty {
            selectedIndex = 0
        } else {
            selectedIndex = min(max(selectedIndex, 0), filteredItems.count - 1)
        }
        rebuildList()
    }

    private func filteredItems(matching queryText: String) -> [SearchPaletteItem] {
        let query = queryText.trimmingCharacters(in: .whitespaces).lowercased()
        if query.isEmpty {
            return allItems
        }
        return allItems.filter {
            $0.title.lowercased().contains(query) || $0.subtitle.lowercased().contains(query)
        }
    }

    private func scrollSelectedRowToVisibleIfNeeded() {
        guard itemRows.indices.contains(selectedIndex) else { return }
        layoutSubtreeIfNeeded()
        let row = itemRows[selectedIndex]
        let rowRect = row.convert(row.bounds, to: scrollView.contentView)
        guard !scrollView.contentView.bounds.contains(rowRect) else { return }
        scrollSelectedRowToVisible()
    }

    private func scrollSelectedRowToVisible() {
        guard itemRows.indices.contains(selectedIndex) else { return }
        layoutSubtreeIfNeeded()
        let row = itemRows[selectedIndex]
        _ = row.scrollToVisible(row.bounds)
        scrollView.reflectScrolledClipView(scrollView.contentView)
    }

    private func rebuildList() {
        itemRows.removeAll()
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

        var previousGroup: Int?
        for (idx, item) in filteredItems.enumerated() {
            let group = item.isApp ? 1 : 0
            if previousGroup != group {
                let heading = NSTextField(labelWithString: group == 0 ? "Abertos agora" : "Aplicativos instalados")
                heading.font = .systemFont(ofSize: 11, weight: .semibold)
                heading.textColor = .secondaryLabelColor
                stackView.addArrangedSubview(heading)
                heading.leadingAnchor.constraint(equalTo: stackView.leadingAnchor, constant: 8).isActive = true
                previousGroup = group
            }
            let row = createRow(item: item, isSelected: idx == selectedIndex)
            itemRows.append(row)
            stackView.addArrangedSubview(row)
            row.leadingAnchor.constraint(equalTo: stackView.leadingAnchor).isActive = true
            row.trailingAnchor.constraint(equalTo: stackView.trailingAnchor).isActive = true
        }
    }

    private func updateSelectionHighlight() {
        for (idx, row) in itemRows.enumerated() { row.setSelected(idx == selectedIndex) }
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
    private var isSelected = false

    init(item: SearchPaletteItem, isSelected: Bool) {
        super.init(frame: .zero)
        setup(item: item, isSelected: isSelected)
    }

    required init?(coder: NSCoder) { fatalError() }

    private func setup(item: SearchPaletteItem, isSelected: Bool) {
        wantsLayer = true
        layer?.cornerRadius = 8
        layer?.masksToBounds = true
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

        setAccessibilityElement(true)
        setAccessibilityRole(.button)
        setAccessibilityLabel(item.title)
        setAccessibilityHelp(item.subtitle)
        setSelected(isSelected)
    }

    func setSelected(_ selected: Bool) {
        isSelected = selected
        setAccessibilityValue(selected ? "Selecionado" : "Não selecionado")
        AppMotion.animate {
            self.layer?.backgroundColor = self.isSelected
                ? AppSettings.shared.accentColor.withAlphaComponent(0.24).cgColor
                : (self.isHovered ? NSColor.controlBackgroundColor.withAlphaComponent(0.3).cgColor : NSColor.clear.cgColor)
        }
    }

    private var isHovered = false
    private var tracking: NSTrackingArea?

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self, userInfo: nil)
        addTrackingArea(area)
        tracking = area
    }

    override func mouseEntered(with event: NSEvent) {
        isHovered = true
        NSCursor.pointingHand.set()
        setSelected(isSelected)
    }

    override func mouseExited(with event: NSEvent) {
        isHovered = false
        NSCursor.arrow.set()
        setSelected(isSelected)
    }

    override func mouseDown(with event: NSEvent) {
        onClick?()
    }

    override func accessibilityPerformPress() -> Bool {
        onClick?()
        return true
    }
}
