import AppKit

struct FavoriteApp: Codable, Equatable, Identifiable {
    let process: String

    var id: String { process }
    var displayName: String { process }

    init(process: String) {
        self.process = Self.normalizedProcess(process)
    }

    static func normalizedProcess(_ process: String) -> String {
        let trimmed = process.trimmingCharacters(in: .whitespacesAndNewlines)
        let processName = trimmed.lowercased()
        return processName.hasSuffix(".exe") ? String(processName.dropLast(4)) : processName
    }

    private enum CodingKeys: String, CodingKey { case process }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(process: try values.decode(String.self, forKey: .process))
    }
}

@MainActor
final class FavoriteAppStore {
    static let shared = FavoriteAppStore()

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func favorites(forHost hostKey: String) -> [FavoriteApp] {
        guard let data = defaults.data(forKey: storageKey(hostKey)),
              let favorites = try? JSONDecoder().decode([FavoriteApp].self, from: data) else { return [] }
        var seen = Set<String>()
        return favorites.filter { !$0.process.isEmpty && seen.insert($0.process).inserted }
    }

    func save(_ favorites: [FavoriteApp], forHost hostKey: String) {
        var seen = Set<String>()
        let favorites = favorites.filter { !$0.process.isEmpty && seen.insert($0.process).inserted }
        guard let data = try? JSONEncoder().encode(favorites) else { return }
        defaults.set(data, forKey: storageKey(hostKey))
    }

    private func storageKey(_ hostKey: String) -> String {
        "paneo.favoriteApps.\(hostKey)"
    }
}

@MainActor
final class FavoriteAppCardView: NSView, NSDraggingSource {
    var onActivate: (() -> Void)?
    var onRemove: (() -> Void)?
    var onMove: ((String, Bool) -> Void)?
    var isVerticalOrder = false
    var isSelected = false { didSet { updateAppearance() } }

    private let favorite: FavoriteApp
    private let iconView = NSImageView()
    private let nameLabel = NSTextField(labelWithString: "")
    private var dragStart: NSPoint?
    private var didStartDrag = false

    init(favorite: FavoriteApp, icon: NSImage?, size: NSSize) {
        self.favorite = favorite
        super.init(frame: NSRect(origin: .zero, size: size))
        wantsLayer = true
        layer?.cornerRadius = 11
        registerForDraggedTypes([.paneoFavorite])

        iconView.image = icon ?? NSImage(systemSymbolName: "macwindow", accessibilityDescription: favorite.displayName)
        iconView.imageScaling = .scaleProportionallyUpOrDown
        iconView.translatesAutoresizingMaskIntoConstraints = false
        nameLabel.stringValue = favorite.displayName
        nameLabel.font = .systemFont(ofSize: 10, weight: .medium)
        nameLabel.textColor = .secondaryLabelColor
        nameLabel.alignment = .center
        nameLabel.lineBreakMode = .byTruncatingTail
        nameLabel.translatesAutoresizingMaskIntoConstraints = false
        nameLabel.isHidden = size.height <= 60
        addSubview(iconView)
        addSubview(nameLabel)

        NSLayoutConstraint.activate([
            widthAnchor.constraint(equalToConstant: size.width),
            heightAnchor.constraint(equalToConstant: size.height),
            iconView.centerXAnchor.constraint(equalTo: centerXAnchor),
            iconView.topAnchor.constraint(equalTo: topAnchor, constant: 8),
            iconView.widthAnchor.constraint(equalToConstant: min(42, size.width - 16)),
            iconView.heightAnchor.constraint(equalTo: iconView.widthAnchor),
            nameLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 4),
            nameLabel.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -4),
            nameLabel.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -5)
        ])
        setAccessibilityElement(true)
        setAccessibilityRole(.button)
        setAccessibilityLabel("Favorito: \(favorite.displayName)")
        setAccessibilityHelp("Pressione Return para abrir. Arraste para reordenar.")
        updateAppearance()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) não suportado.") }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        updateAppearance()
    }

    override var acceptsFirstResponder: Bool { true }

    override func hitTest(_ point: NSPoint) -> NSView? {
        super.hitTest(point) == nil ? nil : self
    }

    override func accessibilityPerformPress() -> Bool {
        onActivate?()
        return true
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 36 || event.keyCode == 76 { onActivate?() }
        else { super.keyDown(with: event) }
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        dragStart = convert(event.locationInWindow, from: nil)
        didStartDrag = false
    }

    override func mouseDragged(with event: NSEvent) {
        guard let start = dragStart, !didStartDrag else { return }
        let point = convert(event.locationInWindow, from: nil)
        guard hypot(point.x - start.x, point.y - start.y) > 4 else { return }
        didStartDrag = true
        let item = NSPasteboardItem()
        item.setString(favorite.process, forType: .paneoFavorite)
        let draggingItem = NSDraggingItem(pasteboardWriter: item)
        draggingItem.setDraggingFrame(bounds, contents: iconView.image)
        beginDraggingSession(with: [draggingItem], event: event, source: self)
    }

    override func mouseUp(with event: NSEvent) {
        if !didStartDrag, bounds.contains(convert(event.locationInWindow, from: nil)) { onActivate?() }
        dragStart = nil
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        let menu = NSMenu()
        let item = NSMenuItem(title: "Remover dos Favoritos", action: #selector(removeFavorite), keyEquivalent: "")
        item.target = self
        menu.addItem(item)
        return menu
    }

    @objc private func removeFavorite() { onRemove?() }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        sender.draggingPasteboard.availableType(from: [.paneoFavorite]) == nil ? [] : .move
    }

    override func prepareForDragOperation(_ sender: NSDraggingInfo) -> Bool {
        sender.draggingPasteboard.availableType(from: [.paneoFavorite]) != nil
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        guard let process = sender.draggingPasteboard.string(forType: .paneoFavorite), process != favorite.process else { return false }
        let point = convert(sender.draggingLocation, from: nil)
        let after = isVerticalOrder ? point.y < bounds.midY : point.x > bounds.midX
        onMove?(process, after)
        return true
    }

    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation { .move }
    func ignoreModifierKeys(for session: NSDraggingSession) -> Bool { true }

    private func updateAppearance() {
        let accent = AppSettings.shared.accentColor
        layer?.borderColor = (isSelected ? accent : NSColor.separatorColor.withAlphaComponent(0.3)).cgColor
        layer?.borderWidth = isSelected ? 2.5 : 1
        layer?.backgroundColor = (isSelected ? accent.withAlphaComponent(0.2) : NSColor.controlBackgroundColor.withAlphaComponent(0.12)).cgColor
    }
}

extension NSPasteboard.PasteboardType {
    static let paneoFavorite = NSPasteboard.PasteboardType("com.paneo.favorite-app")
}
