import AppKit

@MainActor
@main
struct SettingsHarness {
    static func main() throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)

        let suite = "SettingsHarness.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suite) else { fatalError("isolated defaults unavailable") }
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = AppSettings(defaults: defaults)
        let controller = SettingsViewController(settings: settings)
        let window = NSWindow(contentRect: controller.view.frame,
                              styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = controller.view
        window.makeKeyAndOrderFront(nil)
        verifyMotion(in: window)

        let buttons = value("choiceButtons", in: controller) as? [String: [NSButton]] ?? [:]
        let preview = value("preview", in: controller) as? NSView
        let scroll = value("settingsScrollView", in: controller) as? NSScrollView
        guard let preview, let scroll, let document = scroll.documentView else { fatalError("settings UI is incomplete") }

        check(settings.barPosition == .top && settings.barDensity == .compact && settings.appearance == .system,
              "initial settings use existing defaults")
        let autoFitButton = value("autoFitWindowsButton", in: controller) as? NSButton
        check(settings.autoFitWindows && autoFitButton?.state == .on,
              "automatic window fitting defaults to enabled")
        check(autoFitButton?.accessibilityLabel() == "Ajustar janelas automaticamente" &&
              autoFitButton?.accessibilityHelp()?.contains("espaço disponível para vídeo") == true,
              "automatic window fitting explains its behavior accessibly")
        check(document.isFlipped, "scroll document uses top-down coordinates")

        click("position", value: BarPosition.right.rawValue, buttons: buttons)
        click("density", value: BarDensity.icons.rawValue, buttons: buttons)
        click("appearance", value: AppAppearance.dark.rawValue, buttons: buttons)
        click("accent", value: AppAccent.custom.rawValue, buttons: buttons)
        autoFitButton?.performClick(nil)
        let accent = NSColor(srgbRed: 0.18, green: 0.42, blue: 0.76, alpha: 1)
        let colorWell = value("accentWell", in: controller) as? NSColorWell
        colorWell?.color = accent
        if let colorWell, let action = colorWell.action {
            _ = NSApp.sendAction(action, to: colorWell.target, from: colorWell)
        }

        check(settings.barPosition == .right && settings.barDensity == .icons &&
              settings.appearance == .dark && settings.accent == .custom,
              "choice controls update the injected preferences")
        check(!settings.autoFitWindows && autoFitButton?.state == .off,
              "automatic window fitting checkbox updates its preference")
        check(settings.accentColor.redComponent > 0.17 && settings.accentColor.redComponent < 0.19,
              "custom accent saves through NSColorWell action")
        for (key, expected) in [("position", "right"), ("density", "icons"),
                                ("appearance", "dark"), ("accent", "custom")] {
            let selected = buttons[key]?.filter { $0.state == .on } ?? []
            check(selected.count == 1 && selected[0].identifier?.rawValue == expected,
                  "\(key) displays exactly one selected option")
            check(selected[0].accessibilityLabel() != nil && selected[0].accessibilityHelp() != nil,
                  "\(key) choice has accessibility label and hint")
            check(selected[0].image != nil && selected[0].isBordered == false &&
                  selected[0].layer?.cornerRadius == 10 && (selected[0].layer?.borderWidth ?? 0) >= 1,
                  "\(key) choice renders as an icon card with a selected border")
        }

        let storedSettings = AppSettings(defaults: defaults)
        check(storedSettings.barPosition == .right && storedSettings.barDensity == .icons &&
              storedSettings.appearance == .dark && storedSettings.accent == .custom,
              "settings survive a new AppSettings instance in isolated defaults")
        check(!storedSettings.autoFitWindows, "automatic window fitting survives a new AppSettings instance")
        autoFitButton?.performClick(nil)
        check(settings.autoFitWindows && autoFitButton?.state == .on,
              "automatic window fitting can be enabled immediately")

        let sizes = [NSSize(width: 920, height: 650), NSSize(width: 760, height: 600), NSSize(width: 632, height: 390),
                     NSSize(width: 1040, height: 680), NSSize(width: 680, height: 480)]
        for size in sizes {
            window.setContentSize(size)
            window.contentView?.layoutSubtreeIfNeeded()
            scroll.layoutSubtreeIfNeeded()
            document.layoutSubtreeIfNeeded()
            preview.layoutSubtreeIfNeeded()
            if size.width == 920 {
                check(document.frame.height <= scroll.contentView.bounds.height + 1,
                      "standard-size document fits without vertical scrolling")
            }
            check(abs(document.frame.width - scroll.contentView.bounds.width) < 1,
                  "document tracks viewport width at \(Int(size.width))×\(Int(size.height)): \(document.frame.width) vs \(scroll.contentView.bounds.width)")
            checkNoClipping(controller, buttons: buttons, preview: preview, scroll: scroll, size: size)
        }

        for position in BarPosition.allCases {
            click("position", value: position.rawValue, buttons: buttons)
            for density in BarDensity.allCases {
                click("density", value: density.rawValue, buttons: buttons)
                preview.layoutSubtreeIfNeeded()
                verifyPreview(preview, position: position, density: density, accent: settings.accentColor)
            }
        }

        var closed = false
        controller.onClose = { closed = true }
        (value("closeButton", in: controller) as? NSButton)?.performClick(nil)
        check(closed, "close button dismisses settings")
        let close = value("closeButton", in: controller) as! NSButton
        check(close.frame.minX >= 20 && close.frame.maxX <= controller.view.bounds.maxX - 20 &&
              close.frame.maxY <= controller.view.bounds.maxY - 20,
              "Fechar mantém margem confortável e fica fora da rolagem")
        window.close()
        print("PASS: escolhas, prefs isoladas, ajuste automático, preview 4×3, opções visíveis e AppMotion em 632×390, 680×480, 760×600, 920×650 e 1040×680; Fechar")
    }

    private static func click(_ key: String, value: String, buttons: [String: [NSButton]]) {
        guard let button = buttons[key]?.first(where: { $0.identifier?.rawValue == value }) else {
            fatalError("missing \(key) choice: \(value)")
        }
        button.performClick(nil)
    }

    private static func checkNoClipping(_ controller: SettingsViewController,
                                        buttons: [String: [NSButton]], preview: NSView,
                                        scroll: NSScrollView, size: NSSize) {
        let width = Int(size.width)
        let height = Int(size.height)
        let allButtons = buttons.values.flatMap { $0 }
        check(allButtons.allSatisfy { $0.image != nil }, "every choice has a visible system symbol")
        check(allButtons.allSatisfy { $0.frame.height >= 68 && $0.frame.height <= 72 },
              "choice cards reserve room for icon and title at \(width)×\(height)")
        guard let document = scroll.documentView else { fatalError("scroll document unavailable") }
        for button in allButtons {
            let rect = button.convert(button.bounds, to: document)
            check(document.bounds.contains(rect), "choice remains inside scroll document at \(width)×\(height)")
        }
        if width == 920 {
            for button in allButtons {
                let rect = button.convert(button.bounds, to: scroll.contentView)
                check(scroll.contentView.bounds.contains(rect), "all choices visible without scrolling at standard size")
            }
        } else if document.frame.height > scroll.contentView.bounds.height {
            guard let lastButton = buttons["density"]?.last else { fatalError("last density option unavailable") }
            lastButton.scrollToVisible(lastButton.bounds)
            scroll.reflectScrolledClipView(scroll.contentView)
            scroll.layoutSubtreeIfNeeded()
            let rect = lastButton.convert(lastButton.bounds, to: scroll.contentView)
            check(scroll.contentView.bounds.intersects(rect), "minimum-size scroll reaches the last option")
        }
        let previewRect = preview.convert(preview.bounds, to: controller.view)
        check(previewRect.width > 250, "preview retains a useful width at \(width)×\(height)")
    }

    private static func verifyPreview(_ preview: NSView, position: BarPosition,
                                      density: BarDensity, accent: NSColor) {
        guard let frame = value("frameView", in: preview) as? NSView,
              let bar = value("barView", in: preview) as? NSView,
              let stage = value("stageView", in: preview) as? NSView,
              let cards = value("cards", in: preview) as? NSStackView else { fatalError("preview views unavailable") }
        preview.layoutSubtreeIfNeeded()
        frame.layoutSubtreeIfNeeded()
        let barRect = frame.convert(bar.bounds, from: bar)
        let stageRect = frame.convert(stage.bounds, from: stage)
        check(frame.bounds.contains(barRect) && frame.bounds.contains(stageRect) && !barRect.intersects(stageRect),
              "preview keeps bar and stage in frame for \(position)/\(density)")
        switch position {
        case .top: check(barRect.maxY > stageRect.maxY, "preview places bar at top")
        case .bottom: check(barRect.minY < stageRect.minY, "preview places bar at bottom")
        case .left: check(barRect.minX < stageRect.minX, "preview places bar at left")
        case .right: check(barRect.maxX > stageRect.maxX, "preview places bar at right")
        }
        check(cards.orientation == (position.isVertical ? .vertical : .horizontal),
              "preview follows bar orientation")
        check(cards.arrangedSubviews.count == 3, "preview shows three app cards")
        if let card = cards.arrangedSubviews.first, let color = card.layer?.backgroundColor,
           let actual = NSColor(cgColor: color)?.usingColorSpace(.deviceRGB) {
            check(abs(actual.redComponent - accent.redComponent) < 0.02,
                  "preview card uses selected accent")
        }
    }

    private static func verifyMotion(in window: NSWindow) {
        let expectedDuration: TimeInterval = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion ? 0 : 0.18
        check(abs(AppMotion.duration - expectedDuration) < 0.001, "motion duration follows Reduce Motion")
        let view = NSView(frame: NSRect(x: 0, y: 0, width: 40, height: 40))
        window.contentView?.addSubview(view)
        AppMotion.setVisible(view, false)
        AppMotion.setVisible(view, true)
        RunLoop.main.run(until: Date().addingTimeInterval(0.25))
        check(!view.isHidden && abs(view.alphaValue - 1) < 0.01,
              "fast re-show wins over a pending hide animation")
        AppMotion.setVisible(view, false)
        RunLoop.main.run(until: Date().addingTimeInterval(0.25))
        check(view.isHidden, "completed hide leaves the view hidden")
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
        guard condition() else {
            fputs("FAIL: \(message)\n", stderr)
            exit(1)
        }
    }
}
