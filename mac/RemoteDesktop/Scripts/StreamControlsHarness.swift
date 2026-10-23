import AppKit

struct RemoteWindow {
    let id: Int64
    let title: String
    let process: String
    let width: Int
    let height: Int
    let monitorId: Int64?
    let icon: String?
    var isMonitor: Bool { monitorId != nil }

    init(id: Int64 = 1, title: String, process: String, isMonitor: Bool = false,
         width: Int = 0, height: Int = 0, monitorId: Int64? = nil, icon: String?) {
        self.id = id
        self.title = title
        self.process = process
        self.width = width
        self.height = height
        self.monitorId = isMonitor ? (monitorId ?? 1) : monitorId
        self.icon = icon
    }

    static func monitor(_ monitor: RemoteMonitor) -> RemoteWindow {
        RemoteWindow(id: -monitor.id, title: monitor.name, process: "Tela inteira",
                     width: monitor.width, height: monitor.height, monitorId: monitor.id, icon: nil)
    }
}

struct RemoteMonitor {
    let id: Int64
    let name: String
    let width: Int
    let height: Int
    let x: Int
    let y: Int
    let primary: Bool
}

struct RemoteApp {
    let id: String
    let name: String
    let cmd: String
    let icon: String?
    let process: String?
}

@MainActor
@main
struct StreamControlsHarness {
    static func main() throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let controller = StreamControlsViewController()
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 360, height: 240),
                              styleMask: [.titled], backing: .buffered, defer: false)
        window.contentView = controller.view
        window.makeKeyAndOrderFront(nil)
        var changed: [StreamPreferences] = []
        controller.onChange = { changed.append($0) }
        controller.configure(StreamPreferences(), supportsScaling: false)
        window.contentView?.layoutSubtreeIfNeeded()

        for (name, count) in [("quality", 4), ("resolution", 3), ("fps", 2)] {
            guard let popup = value(name, in: controller) as? NSPopUpButton else { fatalError("missing \(name) control") }
            check(popup.numberOfItems == count && popup.accessibilityLabel() != nil && !popup.title.isEmpty,
                  "\(name) choices are accessible and display a value")
            for index in 0..<count {
                popup.selectItem(at: index)
                guard let action = popup.action, let target = popup.target else { fatalError("\(name) action unavailable") }
                _ = app.sendAction(action, to: target, from: popup)
            }
        }
        check(changed.count == 9, "every quality, resolution and FPS choice invokes the callback")
        check(changed[0].quality == .automatic && changed[1].quality == .low &&
              changed[2].quality == .balanced && changed[3].quality == .high,
              "quality selections map to the matching preference")
        check(changed[4].resolution == .native && changed[5].resolution == .p1080 &&
              changed[6].resolution == .p720, "resolution selections map to the matching preference")
        check(changed[7].fps == 30 && changed[8].fps == 60, "FPS selections map to the matching preference")
        guard let capability = value("capability", in: controller) as? NSTextField else { fatalError("capability label unavailable") }
        controller.configure(StreamPreferences(quality: .automatic, fps: 60, resolution: .p720), supportsScaling: false)
        check(capability.stringValue.contains("não oferece"), "unsupported scaling is explained")
        controller.configure(StreamPreferences(quality: .automatic, fps: 60, resolution: .p720), supportsScaling: true)
        check(capability.stringValue.isEmpty, "supported scaling clears the limitation message")
        check(RemoteConnectionFailure.isTransient("Host sem resposta.") &&
              !RemoteConnectionFailure.isTransient("Autenticação recusada: token inválido") &&
              !RemoteConnectionFailure.isTransient("Certificado do host diferente do esperado.") &&
              !RemoteConnectionFailure.isTransient("Mensagem inválida ou incompatível recebida do host."),
              "reconnect policy retries network loss but stops for auth, pin and protocol failures")
        let oldWindow = RemoteWindow(id: 17, title: "Documento", process: "notepad.exe", icon: nil)
        let replacement = RemoteWindow(id: 23, title: "Documento", process: "NOTEPAD.EXE", icon: nil)
        check(RemoteWindowRecovery.target(oldWindow, windows: [replacement], monitors: [])?.id == 23 &&
              RemoteWindowRecovery.target(oldWindow, windows: [], monitors: []) == nil,
              "restore finds the same app/window after a host restart and skips a closed target")
        let oldMonitor = RemoteWindow(id: -8, title: "Monitor", process: "Tela inteira", isMonitor: true,
                                      monitorId: 8, icon: nil)
        check(RemoteWindowRecovery.target(oldMonitor, windows: [],
                                          monitors: [RemoteMonitor(id: 8, name: "Monitor", width: 1920, height: 1080,
                                                                   x: 0, y: 0, primary: true)])?.monitorId == 8,
              "restore maps monitor selections from the fresh monitor list")
        let screenshot = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 360, pixelsHigh: 240,
                                          bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                          isPlanar: false, colorSpaceName: .deviceRGB,
                                          bytesPerRow: 0, bitsPerPixel: 0)!
        window.contentView?.cacheDisplay(in: window.contentView!.bounds, to: screenshot)
        try screenshot.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "/tmp/paneo-stream-controls.png"))
        window.close()

        let palette = QuickSearchPalette(frame: NSRect(x: 0, y: 0, width: 480, height: 300))
        let paletteWindow = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 680, height: 480),
                                     styleMask: [.titled, .resizable], backing: .buffered, defer: false)
        paletteWindow.contentView = palette
        paletteWindow.makeKeyAndOrderFront(nil)
        palette.show(with: [], apps: [])
        guard let allItems = value("allItems", in: palette) as? [SearchPaletteItem],
              let filteredItems = value("filteredItems", in: palette) as? [SearchPaletteItem],
              let searchField = value("searchField", in: palette) as? NSSearchField,
              let scrollView = value("scrollView", in: palette) as? NSScrollView else { fatalError("palette controls unavailable") }
        check(allItems.isEmpty && filteredItems.isEmpty,
              "Apps can open before the asynchronous catalog arrives")
        searchField.stringValue = "Installed app"
        palette.controlTextDidChange(Notification(name: NSControl.textDidChangeNotification, object: searchField))
        let apps = (0..<250).map { index in
            RemoteApp(id: "app-\(index)",
                      name: index == 249 ? "Target Long Browser App Name 249" : "Installed app \(index)",
                      cmd: "opaque-launch-id-\(index)", icon: nil, process: nil)
        }
        palette.update(with: [], apps: apps)
        let filteredAfterSearch = value("filteredItems", in: palette) as? [SearchPaletteItem] ?? []
        check(searchField.stringValue == "Installed app" && filteredAfterSearch.count == 249,
              "late catalog refresh preserves the query and populates matching apps")
        check(scrollView.hasVerticalScroller, "long app catalog remains scrollable")

        for _ in 0..<25 {
            _ = palette.control(searchField, textView: NSTextView(),
                                doCommandBy: #selector(NSResponder.moveDown(_:)))
        }
        guard let selectedIndex = value("selectedIndex", in: palette) as? Int,
              let rows = value("itemRows", in: palette) as? [NSView] else {
            fatalError("palette selection state unavailable")
        }
        let selectedBefore = filteredAfterSearch[selectedIndex].stableID
        let visibleRect = rows[selectedIndex].convert(rows[selectedIndex].bounds, to: scrollView.contentView)
        check(scrollView.contentView.bounds.intersects(visibleRect),
              "keyboard navigation scrolls the selected result into view")
        let scrollBefore = scrollView.contentView.bounds.origin

        let expandedApps = apps + [RemoteApp(id: "app-250", name: "Installed app 250",
                                              cmd: "opaque-launch-id-250", icon: nil, process: nil)]
        palette.update(with: [], apps: expandedApps)
        let updatedItems = value("filteredItems", in: palette) as? [SearchPaletteItem] ?? []
        let selectedAfterIndex = value("selectedIndex", in: palette) as? Int ?? -1
        let scrollAfter = scrollView.contentView.bounds.origin
        check(searchField.stringValue == "Installed app" && updatedItems.count == 250 &&
              updatedItems[selectedAfterIndex].stableID == selectedBefore,
              "catalog growth preserves the query and selected app identity")
        check(abs(scrollAfter.y - scrollBefore.y) < 1,
              "catalog growth preserves the current scroll position")

        let rowsBeforeUnchangedUpdate = value("itemRows", in: palette) as? [NSView] ?? []
        palette.update(with: [], apps: expandedApps)
        let rowsAfterUnchangedUpdate = value("itemRows", in: palette) as? [NSView] ?? []
        check(rowsBeforeUnchangedUpdate.count == rowsAfterUnchangedUpdate.count &&
              zip(rowsBeforeUnchangedUpdate, rowsAfterUnchangedUpdate).allSatisfy { pair in pair.0 === pair.1 },
              "identical catalog snapshots do not rebuild the result rows")

        palette.update(with: [RemoteWindow(title: "Uma janela fora da busca", process: "other.exe", icon: nil)],
                       apps: expandedApps)
        let rowsAfterWindowUpdate = value("itemRows", in: palette) as? [NSView] ?? []
        check(rowsAfterWindowUpdate.count == rowsBeforeUnchangedUpdate.count &&
              zip(rowsBeforeUnchangedUpdate, rowsAfterWindowUpdate).allSatisfy { pair in pair.0 === pair.1 } &&
              (value("selectedIndex", in: palette) as? Int).map {
                  (value("filteredItems", in: palette) as? [SearchPaletteItem])?[$0].stableID == selectedBefore
              } == true,
              "irrelevant window updates preserve visible rows and app selection")
        paletteWindow.setFrame(NSRect(x: 0, y: 0, width: 680, height: 480), display: true)
        paletteWindow.contentView?.layoutSubtreeIfNeeded()
        check(palette.frame.width <= 680 && palette.frame.height <= 480, "search palette fits the minimum window")
        paletteWindow.close()
        print("PASS: controles, limites, política de reconexão e busca ao vivo de 250 apps")
    }

    private static func value(_ name: String, in object: Any) -> Any? {
        var mirror: Mirror? = Mirror(reflecting: object)
        while let current = mirror {
            if let child = current.children.first(where: { $0.label == name }) { return child.value }
            mirror = current.superclassMirror
        }
        return nil
    }

    private static func check(_ condition: @autoclosure () -> Bool, _ message: String) {
        guard condition() else { fputs("FAIL: \(message)\n", stderr); exit(1) }
    }
}
