import AppKit
import ImageIO

@MainActor
@main
struct ZoomSliderHarness {
    private static var timestamp: TimeInterval = 0
    private static var appDelegate: HarnessAppDelegate?
    private static var harnessWindows: [NSWindow] = []
    private static var artifacts = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)

    static func main() {
        let app = NSApplication.shared
        app.setActivationPolicy(.regular)
        appDelegate = HarnessAppDelegate()
        app.delegate = appDelegate
        app.activate(ignoringOtherApps: true)
        Task { @MainActor in
            do {
                try verifySliderAndShortcutSync()
                try verifyControllerLayouts()
                try await verifyZoomTransitions()
                print("PASS: slider, sincronização, layouts e transições; PNGs em artifacts/zoom-slider")
                harnessWindows.forEach { $0.close() }
                app.stop(nil)
            } catch {
                fputs("FAIL: \(error)\n", stderr)
                app.stop(nil)
                exit(1)
            }
            let wakeEvent = NSEvent.otherEvent(with: .applicationDefined, location: .zero,
                                               modifierFlags: [], timestamp: 0, windowNumber: 0,
                                               context: nil, subtype: 0, data1: 0, data2: 0)
            if let wakeEvent { app.postEvent(wakeEvent, atStart: true) }
        }
        app.run()
    }

    private static func verifySliderAndShortcutSync() throws {
        let (window, video) = makeVideo(size: NSSize(width: 360, height: 240))
        video.allowsWindowZoom = true
        video.onZoomChange = {}
        var remoteKeys = 0
        video.send = { if case .key = $0 { remoteKeys += 1 } }
        let controls = ZoomControlsView(videoView: video)
        controls.frame = NSRect(x: 20, y: 20, width: 36, height: 28)
        window.contentView?.addSubview(controls)
        layout(window)

        let button: NSButton = mirrorValue("button", in: controls)!
        let slider: NSSlider = mirrorValue("slider", in: controls)!
        let percentage: NSTextField = mirrorValue("percentage", in: controls)!
        let popover: NSPopover = mirrorValue("popover", in: controls)!
        check(button.isEnabled && close(button.bounds.width, 36), "botão de zoom tem 36 pt e fica habilitado")
        button.performClick(nil)
        check(controls.isShowing && slider.isContinuous, "botão abre popover com slider contínuo")
        NSApp.activate(ignoringOtherApps: true)
        popover.contentViewController?.view.window?.makeKeyAndOrderFront(nil)
        popover.contentViewController?.view.window?.makeFirstResponder(slider)
        check(slider.window?.firstResponder === slider, "slider recebe foco ao abrir popover")
        if let popoverWindow = popover.contentViewController?.view.window {
            popoverWindow.contentView?.layoutSubtreeIfNeeded()
            saveView(popover.contentViewController!.view, as: "popover.png")
        }

        slider.doubleValue = 173
        guard let action = slider.action, let target = slider.target else {
            throw HarnessFailure.assertion("slider tem ação e target")
        }
        _ = NSApp.sendAction(action, to: target, from: slider)
        check(close(video.zoomScale, 1.7) && close(slider.doubleValue, 170) &&
              percentage.stringValue == "170%", "slider quantiza em 10% e atualiza percentual")

        window.makeFirstResponder(video)
        video.keyDown(with: keyEvent("+", modifiers: [.command, .shift], window: window))
        check(close(video.zoomScale, 1.8) && close(slider.doubleValue, 180) &&
              percentage.stringValue == "180%", "atalho sincroniza slider e percentual aberto")
        video.keyDown(with: keyEvent("-", modifiers: .command, window: window))
        check(close(video.zoomScale, 1.7) && close(slider.doubleValue, 170) && remoteKeys == 0,
              "Cmd− sincroniza UI e não envia tecla ao host")

        guard let reset = findView(NSButton.self, in: popover.contentViewController!.view,
                                   where: { $0.title == "100%" }) else {
            throw HarnessFailure.assertion("botão de restaurar 100% aparece no popover")
        }
        let panel = popover.contentViewController!.view
        layout(panel.window ?? window)
        panel.layoutSubtreeIfNeeded()
        check(!reset.isHidden && !reset.frame.isEmpty && panel.bounds.contains(reset.frame),
              "reset 100% tem frame visível dentro do popover: \(reset.frame)")
        reset.performClick(nil)
        check(close(video.zoomScale, 1) && percentage.stringValue == "100%" &&
              close(slider.doubleValue, 100), "reset restaura escala e UI para 100%")

        if !controls.isShowing { button.performClick(nil) }
        check(controls.isShowing, "popover reabre para interação de teclado")
        guard let sliderWindow = slider.window else { throw HarnessFailure.assertion("slider pertence à janela do popover") }
        print("INFO teclado não validado: appActive=\(NSApp.isActive), popoverKey=\(sliderWindow.isKeyWindow), sliderResponder=\(sliderWindow.firstResponder === slider)")

        video.allowsWindowZoom = false
        video.onZoomStateChange?()
        check(!button.isEnabled && !slider.isEnabled,
              "capability fixa/minimizada/monitor/host legado desabilita controles")
        video.keyDown(with: keyEvent("0", modifiers: .command, window: window))
        check(remoteKeys == 0, "Cmd0 continua local mesmo sem capability")

        controls.close()
    }

    private static func verifyControllerLayouts() throws {
        for (name, position) in [("main-top", BarPosition.top), ("main-side", .left)] {
            let suite = "ZoomSliderHarness.\(UUID().uuidString)"
            guard let defaults = UserDefaults(suiteName: suite) else {
                throw HarnessFailure.assertion("UserDefaults suite disponível")
            }
            defer { defaults.removePersistentDomain(forName: suite) }
            defaults.set(position.rawValue, forKey: "settings.barPosition")
            defaults.set(BarDensity.compact.rawValue, forKey: "settings.barDensity")
            defaults.set(false, forKey: "settings.autoFitWindows")
            let preset = ConnectionPreset(host: "", port: "9443", pin: "", fingerprint: nil)
            let controller = MainWindowController(preset: preset, persistPairing: false,
                                                  settings: AppSettings(defaults: defaults))
            controller.showWindow()
            controller.window.setContentSize(NSSize(width: 680, height: 480))
            guard let workspace: NSView = mirrorValue("workspaceView", in: controller),
                  let welcome: NSView = mirrorValue("welcomeCard", in: controller),
                  let floating: NSView = mirrorValue("floatingBar", in: controller),
                  let reconnect: NSButton = mirrorValue("reconnectButton", in: controller),
                  let video: RemoteVideoView = mirrorValue("videoView", in: controller),
                  let actions: WindowActionsView = mirrorValue("windowActionsView", in: controller),
                  let controls = findView(ZoomControlsView.self, in: controller.window.contentView!) else {
                throw HarnessFailure.assertion("workspace principal contém controle de zoom e barra")
            }
            workspace.isHidden = false
            welcome.isHidden = true
            floating.isHidden = false
            reconnect.isHidden = false
            video.isHidden = false
            video.allowsWindowZoom = true
            video.presentPng(makePNG(width: 800, height: 600), width: 800, height: 600)
            controls.refresh()
            layout(controller.window)
            try verifyControlPlacement(controls, actions: actions, reconnect: reconnect,
                                       content: controller.window.contentView!, label: name)
            saveWindow(controller.window, as: "\(name).png")
            controller.window.close()
        }

        let target = RemoteWindow(id: 7123, title: "Janela destacada", process: "app.exe",
                                  width: 800, height: 600, icon: nil, monitorId: nil, minimized: false)
        let detachedSuite = "ZoomSliderDetached.\(UUID().uuidString)"
        guard let detachedDefaults = UserDefaults(suiteName: detachedSuite) else {
            throw HarnessFailure.assertion("UserDefaults suite destacada disponível")
        }
        defer { detachedDefaults.removePersistentDomain(forName: detachedSuite) }
        detachedDefaults.set(false, forKey: "settings.autoFitWindows")
        let detached = RemoteWindowController(target: target, host: "127.0.0.1", port: 9443,
                                              pin: nil, deviceToken: nil, fingerprint: nil,
                                              settings: AppSettings(defaults: detachedDefaults))
        detached.window.setContentSize(NSSize(width: 480, height: 320))
        detached.showWindow()
        guard let video: RemoteVideoView = mirrorValue("videoView", in: detached),
              let actions: WindowActionsView = mirrorValue("windowActionsView", in: detached),
              let reconnect: NSButton = mirrorValue("reconnectButton", in: detached),
              let controls = findView(ZoomControlsView.self, in: detached.window.contentView!) else {
            throw HarnessFailure.assertion("janela destacada contém controle de zoom e barra")
        }
        reconnect.isHidden = false
        video.allowsWindowZoom = true
        video.presentPng(makePNG(width: 800, height: 600), width: 800, height: 600)
        controls.refresh()
        layout(detached.window)
        try verifyControlPlacement(controls, actions: actions, reconnect: reconnect,
                                   content: detached.window.contentView!, label: "detached")
        saveWindow(detached.window, as: "detached.png")
        detached.window.close()
    }

    private static func verifyControlPlacement(_ controls: ZoomControlsView, actions: WindowActionsView,
                                               reconnect: NSButton, content: NSView, label: String) throws {
        let button: NSButton = mirrorValue("button", in: controls)!
        let controlRect = controls.convert(controls.bounds, to: content)
        let buttonRect = button.convert(button.bounds, to: content)
        let contentRect = content.bounds
        check(controls.isDescendant(of: content) && !controlRect.isEmpty &&
              contentRect.insetBy(dx: -0.5, dy: -0.5).contains(controlRect),
              "\(label): botão não é cortado pelo limite da janela")
        check(button.isEnabled, "\(label): botão disponível com vídeo e capability")

        let reconnectRect = reconnect.convert(reconnect.bounds, to: content)
        check(reconnect.isHidden || !controlRect.intersects(reconnectRect),
              "\(label): zoom não cobre Reconectar")
        let actionRect = actions.convert(actions.bounds, to: content)
        if !controls.isDescendant(of: actions) {
            check(!controlRect.intersects(actionRect), "\(label): zoom não cobre os controles da janela")
        } else if let parent = controls.superview {
            let zoomInParent = controls.convert(controls.bounds, to: parent)
            for sibling in parent.subviews where sibling !== controls && !sibling.isHidden && !sibling.bounds.isEmpty {
                let siblingRect = sibling.convert(sibling.bounds, to: parent)
                check(!zoomInParent.intersects(siblingRect), "\(label): zoom não cobre botão vizinho")
            }
        }
    }

    private static func verifyZoomTransitions() async throws {
        let (window, video) = makeVideo(size: NSSize(width: 320, height: 220))
        video.allowsWindowZoom = true
        video.onZoomChange = {}
        var sent: [RemoteInputEvent] = []
        video.send = { sent.append($0) }
        guard video.captureSnapshot() != nil else {
            throw HarnessFailure.assertion("quadro existente permite snapshot")
        }
        let initialFrame = video.decoder.displayLayer.frame

        guard AppMotion.duration > 0 else {
            video.beginZoomTransition()
            check(!video.isTransitioning, "Reduce Motion não inicia fade de zoom")
            return
        }
        let cover: CALayer = mirrorValue("streamCoverImage", in: video)!
        video.beginZoomTransition()
        check(video.isTransitioning && cover.contents != nil && cover.frame == initialFrame,
              "transição de zoom cobre o snapshot na geometria antiga")
        check(video.canZoom, "canZoom permanece ativo para slider contínuo durante fade")
        video.mouseDown(with: mouseEvent(.leftMouseDown, window: window,
                                         point: CGPoint(x: 160, y: 110), view: video))
        check(sent.isEmpty, "input remoto fica bloqueado durante transição de zoom")
        video.zoomIn(nil)
        check(close(video.zoomScale, 1.1), "slider pode atualizar zoom durante transição")
        video.presentPng(makePNG(width: 260, height: 160), width: 260, height: 160)
        layout(window)
        check(video.decoder.displayLayer.frame != initialFrame, "novo quadro atualiza geometria sob o snapshot")
        try await waitForTransition(video, "fade conclui e libera input após novo PNG")

        video.beginZoomTransition()
        check(video.isTransitioning, "nova transição começa após o fade anterior")
        video.presentPng(makePNG(width: 180, height: 120), width: 180, height: 120)
        try await pump(150)
        video.beginZoomTransition()
        check(video.isTransitioning, "segunda transição invalida completion anterior")
        video.presentPng(makePNG(width: 220, height: 140), width: 220, height: 140)
        try await pump(180)
        check(video.isTransitioning, "completion do fade anterior não revela a transição mais recente")
        try await waitForTransition(video, "a transição mais recente conclui seu próprio fade")

        video.beginZoomTransition()
        check(video.isTransitioning, "transição sem quadro começa")
        video.cancelZoomTransition()
        try await pump(250)
        check(!video.isTransitioning, "cancel sem novo quadro remove cover e libera input")

        video.beginZoomTransition()
        try await waitForTransition(video, "timeout sem quadro novo libera a transição")

        video.beginZoomTransition()
        check(video.isTransitioning, "zoom começa antes da troca de stream")
        video.beginStreamTransition()
        check(video.isTransitioning && !video.canZoom,
              "troca normal de stream cancela zoom e mantém entrada/zoom bloqueados")
        let scale = video.zoomScale
        video.zoomIn(nil)
        check(close(video.zoomScale, scale), "zoom fica bloqueado durante troca de stream")
        video.presentPng(makePNG(width: 240, height: 150), width: 240, height: 150)
        try await waitForTransition(video, "primeiro quadro do stream novo encerra a cobertura")
        video.mouseDown(with: mouseEvent(.leftMouseDown, window: window,
                                         point: CGPoint(x: 160, y: 110), view: video))
        check(sent.contains { if case .pointer(.down, _, _, _) = $0 { return true }; return false },
              "input remoto volta após transição de stream")
        video.resetInput()

        saveWindow(window, as: "transition.png")
    }

    private static func makeVideo(size: NSSize) -> (NSWindow, RemoteVideoView) {
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                              styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        let video = RemoteVideoView(frame: NSRect(origin: .zero, size: size))
        window.contentView = video
        window.makeKeyAndOrderFront(nil)
        harnessWindows.append(window)
        video.presentPng(makePNG(width: 160, height: 100), width: 160, height: 100)
        layout(window)
        return (window, video)
    }

    private static func layout(_ window: NSWindow) {
        window.contentView?.needsLayout = true
        window.contentView?.layoutSubtreeIfNeeded()
    }

    private static func saveWindow(_ window: NSWindow, as name: String) {
        guard let content = window.contentView, !content.bounds.isEmpty,
              let bitmap = content.bitmapImageRepForCachingDisplay(in: content.bounds) else { return }
        content.cacheDisplay(in: content.bounds, to: bitmap)
        guard let data = bitmap.representation(using: .png, properties: [:]) else { return }
        try? data.write(to: artifacts.appendingPathComponent(name), options: .atomic)
    }

    private static func saveView(_ view: NSView, as name: String) {
        guard !view.bounds.isEmpty,
              let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        guard let data = bitmap.representation(using: .png, properties: [:]) else { return }
        try? data.write(to: artifacts.appendingPathComponent(name), options: .atomic)
    }

    private static func findView<T: NSView>(_ type: T.Type, in root: NSView,
                                            where predicate: (T) -> Bool = { _ in true }) -> T? {
        if let found = root as? T, predicate(found) { return found }
        for child in root.subviews {
            if let found = findView(type, in: child, where: predicate) { return found }
        }
        return nil
    }

    private static func mirrorValue<T>(_ name: String, in object: Any) -> T? {
        var mirror: Mirror? = Mirror(reflecting: object)
        while let current = mirror {
            for child in current.children where child.label == name {
                if let value = child.value as? T { return value }
                if let nested: T = mirrorValue(name, in: child.value) { return nested }
            }
            mirror = current.superclassMirror
        }
        return nil
    }

    private static func keyEvent(_ characters: String, modifiers: NSEvent.ModifierFlags,
                                 window: NSWindow, keyCode: UInt16 = 24) -> NSEvent {
        timestamp += 1
        return NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: modifiers,
                                timestamp: timestamp, windowNumber: window.windowNumber, context: nil,
                                characters: characters, charactersIgnoringModifiers: characters,
                                isARepeat: false, keyCode: keyCode)!
    }

    private static func mouseEvent(_ type: NSEvent.EventType, window: NSWindow,
                                   point: CGPoint, view: NSView) -> NSEvent {
        timestamp += 1
        let location = view.convert(point, to: nil)
        return NSEvent.mouseEvent(with: type, location: location, modifierFlags: [], timestamp: timestamp,
                                  windowNumber: window.windowNumber, context: nil,
                                  eventNumber: 1, clickCount: 1, pressure: 1)!
    }

    private static func makePNG(width: Int, height: Int) -> Data {
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height,
                                      bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                      isPlanar: false, colorSpaceName: .deviceRGB,
                                      bytesPerRow: 0, bitsPerPixel: 0)!
        return bitmap.representation(using: .png, properties: [:])!
    }

    private static func close(_ lhs: CGFloat, _ rhs: CGFloat) -> Bool { abs(lhs - rhs) < 0.01 }

    private static func check(_ condition: @autoclosure () -> Bool, _ label: String) {
        guard condition() else {
            fputs("FAIL: \(label)\n", stderr)
            exit(1)
        }
    }

    private static func pump(_ milliseconds: Int) async throws {
        try await Task.sleep(for: .milliseconds(milliseconds))
        for _ in 0..<10 { await Task.yield() }
    }

    private static func waitForTransition(_ video: RemoteVideoView, _ label: String) async throws {
        let deadline = Date().addingTimeInterval(3)
        while video.isTransitioning && Date() < deadline { try await pump(20) }
        check(!video.isTransitioning, label)
    }

}

private enum HarnessFailure: Error {
    case assertion(String)
}

@MainActor
private final class HarnessAppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
}
