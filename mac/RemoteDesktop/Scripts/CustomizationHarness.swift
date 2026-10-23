import AppKit

@MainActor
@main
struct CustomizationHarness {
    static func main() throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)

        for position in [BarPosition.top, .bottom, .left, .right] {
            for density in [BarDensity.large, .compact, .icons] {
                for size in [NSSize(width: 680, height: 480), NSSize(width: 1040, height: 680)] {
                    try verifyLayout(position: position, density: density, size: size)
                }
            }
        }
        try verifySettingsNavigation()
        try verifyHeaderContrast()
        try verifySettings()
        try verifyFavorites()
        try verifyDetachedFooter()
        print("PASS: layout/cursor em 24 combinações; ajustes acessíveis, busca, rodapés e preferências isoladas")
    }

    private static func verifySettingsNavigation() throws {
        let suiteName = "CustomizationHarness.navigation.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suiteName) else { throw HarnessError.defaults }
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let settings = AppSettings(defaults: defaults)
        let preset = ConnectionPreset(host: "", port: "", pin: "", fingerprint: nil)
        let controller = MainWindowController(preset: preset, persistPairing: false, settings: settings)
        controller.showWindow()
        let window = controller.window
        let originalContent = window.contentView
        let originalFrame = window.frame
        guard let video = value("videoView", in: controller) as? RemoteVideoView,
              let workspace = value("workspaceView", in: controller) as? NSView,
              let welcome = value("welcomeCard", in: controller) as? NSView,
              let address = value("address", in: controller) as? NSTextField,
              let dock = value("settingsDock", in: controller) as? NSView,
              let gear = descendants(dock).compactMap({ $0 as? NSButton }).first else { throw HarnessError.missingView }
        address.stringValue = "unsaved-host"
        _ = NSColorPanel.shared
        gear.performClick(nil)
        guard let settingsPage = value("settingsController", in: controller) as? SettingsViewController,
              let close = value("closeButton", in: settingsPage) as? NSButton else { throw HarnessError.missingView }
        check(window.contentView === originalContent && window.frame == originalFrame,
              "popup preserva conteúdo e tamanho da janela principal")
        guard let sheet = window.attachedSheet else {
            check(false, "engrenagem abre ajustes em popup modal")
            return
        }
        check(sheet.contentView === settingsPage.view && sheet.sheetParent === window,
              "ajustes são uma sheet vinculada à janela principal")
        check(sheet.frame.width <= 920 && sheet.frame.height < window.frame.height,
              "popup tem tamanho limitado e deixa a janela principal visível")
        controller.showSettings()
        check(window.attachedSheet === sheet, "reabrir ajustes reutiliza o popup")
        close.performClick(nil)
        RunLoop.current.run(until: Date().addingTimeInterval(0.3))
        check(window.attachedSheet == nil && window.contentView === originalContent &&
              address.stringValue == "unsaved-host", "Fechar preserva formulário e conteúdo original")

        welcome.isHidden = true
        workspace.isHidden = false
        video.isHidden = false
        window.makeFirstResponder(video)
        controller.toggleQuickSearch()
        controller.showSettings()
        check(video.window === window && window.firstResponder !== video,
              "popup retira foco remoto e preserva vídeo e sessão")
        controller.toggleQuickSearch()
        controller.toggleBarCollapsed()
        controller.cycleWindow(forward: true)
        check(value("isSearchOpen", in: controller) as? Bool == false &&
              value("isBarCollapsed", in: controller) as? Bool == false,
              "atalhos do workspace ficam inativos durante ajustes")
        var remoteEvents = 0
        video.send = { _ in remoteEvents += 1 }
        guard let key = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [],
                                        timestamp: 0, windowNumber: window.windowNumber, context: nil,
                                        characters: "a", charactersIgnoringModifiers: "a", isARepeat: false, keyCode: 0),
              let escape = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [],
                                           timestamp: 0, windowNumber: window.windowNumber, context: nil,
                                           characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}",
                                           isARepeat: false, keyCode: 53) else { throw HarnessError.missingView }
        window.attachedSheet?.sendEvent(key)
        check(remoteEvents == 0, "teclado nos ajustes não envia entrada remota")
        settings.barPosition = .right
        settings.barDensity = .icons
        check(window.attachedSheet?.performKeyEquivalent(with: escape) == true, "Esc fecha popup de ajustes")
        RunLoop.current.run(until: Date().addingTimeInterval(0.3))
        check(window.contentView === originalContent && video.window === window && window.firstResponder === video,
              "Fechar restaura workspace e foco do vídeo")
        check(settings.barPosition == .right && settings.barDensity == .icons,
              "preferências editadas permanecem ao retornar")
        for size in [NSSize(width: 1600, height: 1000), NSSize(width: 680, height: 480)] {
            window.setContentSize(size)
            controller.showSettings()
            guard let popup = window.attachedSheet else { throw HarnessError.missingView }
            check(popup.frame.width <= 920 && popup.frame.width < window.frame.width &&
                  popup.frame.height < window.frame.height, "popup limitado em \(size): popup \(popup.frame.size), janela \(window.frame.size)")
            guard let page = value("settingsController", in: controller) as? SettingsViewController,
                  let dismiss = value("closeButton", in: page) as? NSButton else { throw HarnessError.missingView }
            dismiss.performClick(nil)
            RunLoop.current.run(until: Date().addingTimeInterval(0.3))
        }
        window.close()
    }

    private static func verifyHeaderContrast() throws {
        let controller = MainWindowController(preset: ConnectionPreset(host: "", port: "", pin: "", fingerprint: nil),
                                               persistPairing: false)
        controller.showWindow()
        let window = controller.window
        guard let bar = value("thumbnailBar", in: controller) as? WindowThumbnailBar,
              let title = value("titlebarTitle", in: controller) as? NSTextField else { throw HarnessError.missingView }
        bar.setWindows([RemoteWindow(id: 1, title: "Explorador de Arquivos", process: "explorer.exe",
                                     width: 1920, height: 1080)], selectedId: nil)
        guard let card = (value("cardViews", in: bar) as? [Int64: ThumbnailCardView])?[1],
              let bottom = value("bottomBar", in: card) as? NSView,
              let label = value("titleLabel", in: card) as? NSTextField else { throw HarnessError.missingView }
        for name in [NSAppearance.Name.aqua, .darkAqua, .aqua] {
            window.appearance = NSAppearance(named: name)
            window.contentView?.layoutSubtreeIfNeeded()
            card.isSelected = false
            guard let background = bottom.layer?.backgroundColor,
                  let rgb = NSColor(cgColor: background)?.usingColorSpace(.deviceRGB) else { throw HarnessError.missingView }
            let dark = name == .darkAqua
            check(dark ? rgb.redComponent < 0.3 : rgb.redComponent > 0.7,
                  "faixa da miniatura resolve fundo no tema \(name.rawValue)")
            window.effectiveAppearance.performAsCurrentDrawingAppearance {
                let text = label.textColor!.usingColorSpace(.deviceRGB)!
                check(dark ? text.redComponent > 0.8 : text.redComponent < 0.2,
                      "título da miniatura contrasta no tema \(name.rawValue)")
                let header = title.textColor!.usingColorSpace(.deviceRGB)!
                check(dark ? header.redComponent > 0.8 : header.redComponent < 0.2,
                      "título da janela usa cor legível no tema \(name.rawValue)")
            }
        }
        check(window.titleVisibility == .hidden && title.stringValue == window.title,
              "título acessível da janela acompanha o header visível")
        window.close()
    }

    private static func verifySettings() throws {
        let suiteName = "CustomizationHarness.settings.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suiteName) else { throw HarnessError.defaults }
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let settings = AppSettings(defaults: defaults)
        check(settings.barPosition == .top && settings.barDensity == .compact &&
              settings.appearance == .system && settings.accent == .system,
              "valores iniciais das preferências")
        defaults.set("invalid", forKey: "settings.barPosition")
        defaults.set("invalid", forKey: "settings.barDensity")
        check(settings.barPosition == .top && settings.barDensity == .compact,
              "fallback de preferências inválidas")

        settings.barPosition = .right
        settings.barDensity = .icons
        settings.appearance = .dark
        settings.accent = .custom
        settings.accentColor = NSColor(srgbRed: 0.2, green: 0.4, blue: 0.6, alpha: 1)
        check(settings.barPosition == .right && settings.barDensity == .icons &&
              settings.appearance == .dark && settings.accent == .custom &&
              settings.accentColor.redComponent > 0.19 && settings.accentColor.redComponent < 0.21,
              "persistência de posição, densidade, aparência e cor customizada")
        let window = NSWindow(contentRect: .zero, styleMask: [], backing: .buffered, defer: false)
        settings.applyAppearance(to: window)
        check(window.appearance?.name == .darkAqua, "aparência aplicada à janela")
        window.close()
    }

    private static func verifyFavorites() throws {
        let suiteName = "CustomizationHarness.favorites.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suiteName) else { throw HarnessError.defaults }
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let store = FavoriteAppStore(defaults: defaults)
        let first = FavoriteApp(process: " Notepad.EXE ")
        let second = FavoriteApp(process: "calc.exe")
        check(first.process == "notepad" && first == FavoriteApp(process: "notepad"),
              "normalização e igualdade de favorito")
        store.save([second, first], forHost: "host-a")
        store.save([first], forHost: "host-b")
        check(store.favorites(forHost: "host-a").map(\.process) == ["calc", "notepad"],
              "persistência de ordem dos favoritos por host")
        check(store.favorites(forHost: "host-b").map(\.process) == ["notepad"],
              "isolamento de favoritos entre hosts")
    }

    private static func verifyLayout(position: BarPosition, density: BarDensity, size: NSSize) throws {
        let suiteName = "CustomizationHarness.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suiteName) else {
            throw HarnessError.defaults
        }
        defer { defaults.removePersistentDomain(forName: suiteName) }

        let settings = AppSettings(defaults: defaults)
        settings.barPosition = position
        settings.barDensity = density
        let preset = ConnectionPreset(host: "", port: "", pin: "", fingerprint: nil)
        let controller = MainWindowController(preset: preset, persistPairing: false, settings: settings)
        controller.window.setFrame(NSRect(origin: .zero, size: size), display: false)
        controller.showWindow()

        guard let welcome = value("welcomeCard", in: controller) as? NSView,
              let workspace = value("workspaceView", in: controller) as? NSView,
              let bar = value("thumbnailBar", in: controller) as? WindowThumbnailBar,
              let stage = value("mainStage", in: controller) as? NSView,
              let video = value("videoView", in: controller) as? RemoteVideoView,
              let footer = value("floatingBar", in: controller) as? NSView,
              let peek = value("peekStrip", in: controller) as? PeekStripView else {
            throw HarnessError.missingView
        }

        welcome.isHidden = true
        workspace.isHidden = false
        video.isHidden = false
        footer.isHidden = false
        let png = try makePNG(width: 16, height: 9)
        video.presentPng(png, width: 16, height: 9)
        controller.window.contentView?.layoutSubtreeIfNeeded()
        workspace.layoutSubtreeIfNeeded()

        let workspaceRect = workspace.bounds
        let barRect = workspace.convert(bar.bounds, from: bar)
        let stageRect = workspace.convert(stage.bounds, from: stage)
        let footerRect = workspace.convert(footer.bounds, from: footer)
        let videoRect = workspace.convert(video.bounds, from: video)
        check(workspaceRect.contains(barRect), label(position, density, size, "barra expandida dentro do workspace"))
        check(workspaceRect.contains(stageRect), label(position, density, size, "stage dentro do workspace"))
        check(!barRect.intersects(stageRect), label(position, density, size, "stage separado da barra expandida"))
        check(!videoRect.intersects(footerRect), label(position, density, size, "vídeo separado do rodapé"))

        if position == .top && density == .compact && size.width == 680 {
            guard let dock = value("settingsDock", in: controller) as? NSView,
                  let palette = value("searchPalette", in: controller) as? NSView else {
                throw HarnessError.missingView
            }
            let dockRect = workspace.convert(dock.bounds, from: dock)
            check(!dock.isHidden && workspace.bounds.contains(dockRect) && !dockRect.intersects(footerRect),
                  "engrenagem sempre visível sem sobrepor o rodapé")
            controller.toggleQuickSearch()
            settleAnimation(workspace: workspace)
            check(!palette.isHidden, "busca abre no workspace")
            checkCursorExcludes(palette, workspace: workspace, video: video, label: "busca não envia entrada ao vídeo")
            controller.toggleQuickSearch()
            settleAnimation(workspace: workspace)
            check(palette.isHidden, "busca fecha após transição")
            let oldPaletteRect = video.convert(palette.bounds, from: palette).intersection(video.bounds)
            check(!interactionExclusions(in: video).contains { $0.intersects(oldPaletteRect) },
                  "fechar busca remove exclusões de entrada da paleta")
        }

        if position == .left && size.width == 680 {
            try verifySidebarContent(bar, defaults: defaults, workspace: workspace, density: density)
        }

        if position == .top && density == .large && size.width == 680 {
            settings.barPosition = .left
            settings.barDensity = .icons
            workspace.layoutSubtreeIfNeeded()
            check(bar.preferredThickness == 84,
                  label(position, density, size, "notificação atualiza posição e densidade"))
            settings.barPosition = position
            settings.barDensity = density
            workspace.layoutSubtreeIfNeeded()
        }

        // The controller's own toggle and peek callback exercise its layout/exclusion path.
        controller.toggleBarCollapsed()
        settleAnimation(workspace: workspace)
        workspace.layoutSubtreeIfNeeded()
        let collapsedStage = workspace.convert(stage.bounds, from: stage)
        check(sameRect(collapsedStage, workspace.bounds),
              label(position, density, size, "stage ocupa workspace recolhido"))
        check(!workspace.convert(video.bounds, from: video)
              .intersects(workspace.convert(footer.bounds, from: footer)),
              label(position, density, size, "vídeo separado do rodapé recolhido"))
        check(!peek.isHidden, label(position, density, size, "faixa peek visível recolhida"))
        checkCursorExcludes(peek, workspace: workspace, video: video,
                            label: label(position, density, size, "cursor excluído na faixa peek"))

        peek.onEnter?()
        settleAnimation(workspace: workspace)
        workspace.layoutSubtreeIfNeeded()
        check(peek.isHidden, label(position, density, size, "barra aparece ao espiar"))
        check(sameRect(workspace.convert(stage.bounds, from: stage), workspace.bounds),
              label(position, density, size, "stage ocupa workspace durante peek"))
        checkCursorExcludes(bar, workspace: workspace, video: video,
                            label: label(position, density, size, "cursor excluído na barra em peek"))

        controller.toggleBarCollapsed()
        settleAnimation(workspace: workspace)
        controller.window.close()
    }

    private static func verifyDetachedFooter() throws {
        let target = RemoteWindow(id: 1, title: String(repeating: "Título comprido ", count: 10),
                                  process: "fixture", width: 1920, height: 1080)
        let controller = RemoteWindowController(target: target, host: "127.0.0.1", port: 9443,
                                                pin: nil, deviceToken: nil, fingerprint: nil)
        controller.window.setContentSize(NSSize(width: 480, height: 320))
        controller.showWindow()
        guard let content = controller.window.contentView,
              let reconnect = value("reconnectButton", in: controller) as? NSButton else {
            throw HarnessError.missingView
        }
        reconnect.isHidden = false
        content.layoutSubtreeIfNeeded()
        for button in descendants(content).compactMap({ $0 as? NSButton }) where !button.isHidden {
            check(content.bounds.contains(content.convert(button.bounds, from: button)),
                  "ações da janela destacada cabem em 480×320")
        }
        controller.window.close()
    }

    private static func descendants(_ view: NSView) -> [NSView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }

    private static func verifySidebarContent(_ bar: WindowThumbnailBar, defaults: UserDefaults,
                                             workspace: NSView, density: BarDensity) throws {
        let store = FavoriteAppStore(defaults: defaults)
        bar.favoriteStore = store
        let hostKey = "harness-\(UUID().uuidString)"
        store.save([FavoriteApp(process: " Browser.EXE "), FavoriteApp(process: ""),
                    FavoriteApp(process: "calc.exe"), FavoriteApp(process: "browser")], forHost: hostKey)
        bar.setHostKey(hostKey)
        let windows = (0..<12).map { index in
            RemoteWindow(id: Int64(1000 + index),
                         title: "Título remoto muito comprido para verificar recorte e elipse \(index)",
                         process: index < 2 ? "browser.exe" : "app\(index).exe",
                         width: 1920, height: 1080)
        }
        bar.setWindows(windows, selectedId: windows[3].id)
        workspace.layoutSubtreeIfNeeded()
        bar.layoutSubtreeIfNeeded()

        let cardViews = value("cardViews", in: bar) as? [Int64: ThumbnailCardView] ?? [:]
        let favoriteViews = value("favoriteViews", in: bar) as? [String: FavoriteAppCardView] ?? [:]
        let stack = value("cardsStack", in: bar) as? NSStackView
        let scroll = value("scrollView", in: bar) as? NSScrollView
        let panel = value("panel", in: bar) as? NSView
        let actions = value("actionsStack", in: bar) as? NSStackView
        guard let stack, let scroll, let panel, let actions else { throw HarnessError.missingView }

        check(cardViews.count == 12 && cardViews[windows[0].id] != nil && cardViews[windows[1].id] != nil,
              "12 cards preservam duas janelas do mesmo processo")
        let countBadge = value("countBadge", in: bar) as? NSTextField
        check(countBadge?.stringValue.hasPrefix("12") == true, "contador reflete a lista recebida")
        check(store.favorites(forHost: hostKey).map(\.process) == ["browser", "calc"],
              "favoritos removem nomes vazios e duplicados")
        check(favoriteViews.count == 2, "barra renderiza favoritos deduplicados")
        check(cardViews[windows[3].id]?.isSelected == true, "setSelected preservado")

        let cardSize: NSSize
        switch density {
        case .large: cardSize = NSSize(width: 160, height: 96)
        case .compact: cardSize = NSSize(width: 116, height: 76)
        case .icons: cardSize = NSSize(width: 58, height: 58)
        }
        for card in cardViews.values {
            check(abs(card.frame.width - cardSize.width) < 1 && abs(card.frame.height - cardSize.height) < 1,
                  "cards não comprimidos em \(density)")
            let title = value("titleLabel", in: card) as? NSTextField
            if density == .icons {
                check(title?.isHidden == true, "modo ícones oculta títulos em 84 pt")
            } else {
                check(title?.lineBreakMode == .byTruncatingTail && title?.isHidden == false,
                      "título longo usa reticências sem escapar do card")
                if let title, let bottomBar = value("bottomBar", in: card) as? NSView {
                    let titleRect = bottomBar.convert(title.frame, to: card)
                    check(card.bounds.contains(titleRect), "título permanece recortado dentro do card")
                }
            }
        }

        let barRect = panel.convert(panel.bounds, to: bar)
        let actionsRect = actions.convert(actions.bounds, to: bar)
        let scrollRect = scroll.convert(scroll.bounds, to: bar)
        check(barRect.contains(actionsRect) && barRect.contains(scrollRect) && !actionsRect.intersects(scrollRect),
              "ações e rolagem cabem na barra lateral")
        check(scroll.hasVerticalScroller, "barra lateral usa rolagem vertical")
        let documentHeight = stack.bounds.height
        let contentHeight = stack.arrangedSubviews.reduce(CGFloat.zero) { $0 + $1.frame.height }
            + CGFloat(max(0, stack.arrangedSubviews.count - 1)) * stack.spacing
        check(documentHeight + 1 >= contentHeight && documentHeight > scroll.contentView.bounds.height,
              "documento de rolagem cobre cards e favoritos sem comprimir")

        let png = try makePNG(width: 16, height: 9)
        guard let image = NSBitmapImageRep(data: png)?.cgImage else { throw HarnessError.imageCreation }
        bar.updateThumbnail(windowId: windows[3].id, cgImage: image)
        check(cardViews[windows[3].id]?.thumbnailImage != nil, "updateThumbnail preservado")
        if density == .icons, let card = cardViews[windows[3].id],
           let preview = value("previewView", in: card) as? NSView {
            check(preview.isHidden, "modo ícones permanece sem miniatura após receber quadro")
        }

        if let card = cardViews[windows[0].id] {
            var presses = 0
            card.onClick = { presses += 1 }
            check(card.acceptsFirstResponder && card.accessibilityPerformPress(), "card acessível por teclado e VoiceOver")
            check(presses == 1, "ação de acessibilidade ativa o card")
        }

        // Exercita o callback usado por FavoriteAppCardView após um drop no card de destino.
        favoriteViews["calc"]?.onMove?("browser", true)
        check(store.favorites(forHost: hostKey).map(\.process) == ["calc", "browser"],
              "reordenação pelo callback real before/after do card")
        let reordered = value("favoriteViews", in: bar) as? [String: FavoriteAppCardView] ?? [:]
        reordered["calc"]?.onMove?("browser", false)
        check(store.favorites(forHost: hostKey).map(\.process) == ["browser", "calc"],
              "arraste para antes do primeiro favorito")
        bar.reset()
        check((value("cardViews", in: bar) as? [Int64: ThumbnailCardView])?.isEmpty == true,
              "reset remove cards")
    }

    private static func settleAnimation(workspace: NSView) {
        // Drive AppKit's animation completion so exclusions match the final layout.
        RunLoop.current.run(until: Date().addingTimeInterval(0.28))
        workspace.layoutSubtreeIfNeeded()
    }

    private static func checkCursorExcludes(_ overlay: NSView, workspace: NSView,
                                            video: RemoteVideoView, label: String) {
        let overlayInVideo = video.convert(workspace.convert(overlay.bounds, from: overlay), from: workspace)
        let frame = value("frameRect", in: video) as? CGRect ?? video.bounds
        let exclusions = interactionExclusions(in: video)
        let overlap = overlayInVideo.intersection(video.bounds)
        if !overlap.isNull && !overlap.isEmpty {
            check(exclusions.contains(where: { $0.intersects(overlap) }), "\(label): exclusão instalada")
        }
        let cursorRects = RemoteVideoInteraction.cursorRects(frameRect: frame, bounds: video.bounds,
                                                              visibleRect: video.visibleRect,
                                                              exclusions: exclusions)
        check(!cursorRects.contains(where: { $0.intersects(overlayInVideo) }), label)
    }

    private static func sameRect(_ first: CGRect, _ second: CGRect) -> Bool {
        abs(first.minX - second.minX) < 1 && abs(first.minY - second.minY) < 1 &&
            abs(first.width - second.width) < 1 && abs(first.height - second.height) < 1
    }

    private static func interactionExclusions(in view: RemoteVideoView) -> [CGRect] {
        value("interactionExclusions", in: view) as? [CGRect] ?? []
    }

    private static func value(_ name: String, in object: Any) -> Any? {
        var mirror: Mirror? = Mirror(reflecting: object)
        while let current = mirror {
            if let child = current.children.first(where: { $0.label == name }) { return child.value }
            mirror = current.superclassMirror
        }
        return nil
    }

    private static func label(_ position: BarPosition, _ density: BarDensity,
                              _ size: NSSize, _ check: String) -> String {
        "\(position) / \(density) / \(Int(size.width))×\(Int(size.height)): \(check)"
    }

    private static func check(_ condition: @autoclosure () -> Bool, _ message: String) {
        guard condition() else {
            fputs("FAIL: \(message)\n", stderr)
            exit(1)
        }
    }

    private static func makePNG(width: Int, height: Int) throws -> Data {
        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height,
                                            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                            isPlanar: false, colorSpaceName: .deviceRGB,
                                            bytesPerRow: 0, bitsPerPixel: 0),
              let data = bitmap.representation(using: .png, properties: [:]) else {
            throw HarnessError.imageCreation
        }
        return data
    }
}

private enum HarnessError: Error {
    case defaults
    case missingView
    case imageCreation
}
