import AppKit

/// Ações da janela Windows remota. Os controles da própria NSWindow continuam locais.
@MainActor
final class WindowActionsView: NSView, NSMenuDelegate {
    var onAction: ((String, Bool?) -> Void)?
    var onRequestMenus: (() -> Void)?
    var onInvokeMenu: ((String) -> Void)?
    var onMenuOpen: (() -> Void)?
    var onMenuClose: (() -> Void)?

    private let button = NSButton(title: "Janela", target: nil, action: nil)
    private var state: RemoteWindowState?
    private var autoFitEnabled = false
    private var menuSnapshot: RemoteWindowMenus?
    private var waitingForMenus = false
    private var menuWaitTask: Task<Void, Never>?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        button.image = NSImage(systemSymbolName: "macwindow", accessibilityDescription: nil)
        button.imagePosition = .imageLeading
        button.setAccessibilityLabel("Controles da janela do Windows")
        button.toolTip = "Ações na janela remota do Windows"
        button.target = self
        button.action = #selector(showActions)
        Glass.style(button)
        button.translatesAutoresizingMaskIntoConstraints = false
        addSubview(button)
        NSLayoutConstraint.activate([
            button.leadingAnchor.constraint(equalTo: leadingAnchor),
            button.trailingAnchor.constraint(equalTo: trailingAnchor),
            button.topAnchor.constraint(equalTo: topAnchor),
            button.bottomAnchor.constraint(equalTo: bottomAnchor)
        ])
        updateAvailability()
    }

    required init?(coder: NSCoder) { fatalError("A interface é construída em código; nib não é suportado.") }

    func update(state: RemoteWindowState?, windowId: Int64? = nil, isMonitor: Bool,
                autoFitEnabled: Bool = false) {
        self.state = isMonitor || (windowId != nil && state?.windowId != windowId) ? nil : state
        self.autoFitEnabled = autoFitEnabled && (self.state?.canResize == true || self.state?.reading == true)
        menuSnapshot = nil
        updateAvailability()
    }

    func showMenus(_ menus: RemoteWindowMenus) {
        menuWaitTask?.cancel()
        menuWaitTask = nil
        waitingForMenus = false
        let menu = makeMenusMenu(menus)
        menu.delegate = self
        onMenuOpen?()
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: bounds.maxY), in: self)
    }

    func makeMenusMenu(_ menus: RemoteWindowMenus) -> NSMenu {
        menuSnapshot = menus
        let menu = NSMenu(title: "Menus do aplicativo")
        menu.autoenablesItems = false
        if menus.supported {
            append(menus.items, to: menu)
            if menu.items.isEmpty { menu.addItem(disabled(menus.reason ?? "Nenhum comando disponível.")) }
        } else {
            menu.addItem(disabled(menus.reason ?? "Menus indisponíveis neste aplicativo."))
        }
        return menu
    }

    private func updateAvailability() {
        button.isEnabled = state != nil
        button.toolTip = state == nil
            ? "O host ainda não oferece controles remotos de janela."
            : "Ações na janela remota do Windows"
    }

    @objc private func showActions() {
        guard let menu = makeActionsMenu() else { return }
        menu.delegate = self
        onMenuOpen?()
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: bounds.maxY), in: self)
    }

    func makeActionsMenu() -> NSMenu? {
        guard let state else { return nil }
        let menu = NSMenu(title: "Janela do Windows")
        menu.autoenablesItems = false
        addAction("Minimizar", action: "minimize", enabled: !state.minimized, to: menu)
        addAction(state.maximized || state.minimized ? "Restaurar" : "Maximizar",
                  action: state.maximized || state.minimized ? "restore" : "maximize",
                  enabled: state.maximized || state.minimized || state.canResize, to: menu)
        menu.addItem(.separator())
        addAction("Ajustar automaticamente",
                  action: "reading", enabled: state.canResize || autoFitEnabled, to: menu,
                  checked: autoFitEnabled)
        menu.addItem(.separator())
        addAction("Fechar no Windows…", action: "close", enabled: true, to: menu)
        menu.addItem(.separator())
        let menus = NSMenuItem(title: "Menus do aplicativo…", action: #selector(loadMenus), keyEquivalent: "")
        menus.target = self
        menu.addItem(menus)
        return menu
    }

    @objc private func loadMenus() {
        waitingForMenus = true
        onMenuOpen?()
        onRequestMenus?()
        menuWaitTask?.cancel()
        menuWaitTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(8))
            guard !Task.isCancelled, let self, self.waitingForMenus else { return }
            self.waitingForMenus = false
            self.onMenuClose?()
        }
    }

    @objc private func performAction(_ sender: NSMenuItem) {
        guard let action = sender.representedObject as? String else { return }
        onAction?(action, nil)
    }

    @objc private func invokeMenu(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String, menuSnapshot?.supported == true else { return }
        onInvokeMenu?(id)
        menuSnapshot = nil
    }

    func menuDidClose(_ menu: NSMenu) {
        if !waitingForMenus { onMenuClose?() }
    }

    private func addAction(_ title: String, action: String, enabled: Bool, to menu: NSMenu,
                           checked: Bool = false) {
        let item = NSMenuItem(title: title, action: #selector(performAction), keyEquivalent: "")
        item.target = self
        item.representedObject = action
        item.isEnabled = enabled
        if checked { item.state = .on }
        menu.addItem(item)
    }

    private func append(_ items: [RemoteWindowMenuItem], to menu: NSMenu) {
        for source in items {
            if source.separator { menu.addItem(.separator()); continue }
            let item = NSMenuItem(title: source.title,
                                  action: source.children.isEmpty ? #selector(invokeMenu) : nil,
                                  keyEquivalent: "")
            item.isEnabled = source.enabled
            if source.checked { item.state = .on }
            if source.children.isEmpty {
                item.target = self
                item.representedObject = source.id
            } else {
                let submenu = NSMenu(title: source.title)
                submenu.autoenablesItems = false
                append(source.children, to: submenu)
                item.submenu = submenu
            }
            menu.addItem(item)
        }
    }

    private func disabled(_ title: String) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        item.isEnabled = false
        return item
    }
}
