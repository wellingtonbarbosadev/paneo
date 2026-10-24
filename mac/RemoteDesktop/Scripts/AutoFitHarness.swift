import AppKit
import Foundation

/// Exercita o auto-fit do controlador real contra fixture TLS local.
@MainActor
@main
struct AutoFitHarness {
    private struct Session: Decodable {
        let host: String
        let port: String
        let pin: String
    }

    static func main() {
        let application = NSApplication.shared
        application.setActivationPolicy(.accessory)
        Task { @MainActor in
            do {
                try await runChecks()
                exit(0)
            } catch {
                fputs("FAIL: \(error)\n", stderr)
                exit(1)
            }
        }
        application.run()
    }

    private static func runChecks() async throws {
        let session = try JSONDecoder().decode(Session.self,
            from: Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1])))
        let suite = "AutoFitHarness.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suite) else { fatalError("UserDefaults indisponível") }
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = AppSettings(defaults: defaults)
        settings.autoFitWindows = false
        let controller = MainWindowController(
            preset: ConnectionPreset(host: session.host, port: session.port, pin: session.pin, fingerprint: nil),
            persistPairing: false, settings: settings)
        controller.showWindow()
        controller.startConnection()

        guard let bar = value("thumbnailBar", in: controller) as? WindowThumbnailBar,
              let video = value("videoView", in: controller) as? RemoteVideoView,
              let actions = value("windowActionsView", in: controller) as? WindowActionsView else {
            fatalError("Componentes do workspace indisponíveis")
        }

        try await waitUntil("fixture window listed") {
            (value("windows", in: controller) as? [RemoteWindow])?.contains(where: { $0.id == 4242 }) == true
        }
        guard let target = (value("windows", in: controller) as? [RemoteWindow])?.first(where: { $0.id == 4242 }) else {
            fatalError("Fixture window ausente")
        }

        bar.onSelectWindow?(target)
        try await waitUntil("resizable view state received") {
            guard let state = value("remoteWindowState", in: controller) as? RemoteWindowState else { return false }
            return state.windowId == target.id && state.canResize && !state.minimized
        }

        video.presentPng(makePNG(width: 800, height: 600), width: 800, height: 600)
        layout(controller, video)
        try await Task.sleep(for: .milliseconds(450))
        try check(value("autoFitEnabled", in: controller) as? Bool == false,
                  "main controller starts with saved auto-fit disabled")
        settings.autoFitWindows = true
        try await Task.sleep(for: .milliseconds(450))
        let initial = size(video)
        try check(initial.0 > 0 && initial.1 > 0, "initial video bounds are positive")

        controller.window.setContentSize(NSSize(width: 1220, height: 820))
        layout(controller, video)
        try await Task.sleep(for: .milliseconds(450))
        let resized = size(video)
        try check(resized != initial, "window resize changes video bounds")
        try await Task.sleep(for: .milliseconds(400))

        guard let toggle = actions.makeActionsMenu()?.items.first(where: {
            $0.representedObject as? String == "reading" && $0.state == .on
        }) else {
            throw HarnessFailure.assertion("auto-fit menu item is enabled")
        }
        NSApp.sendAction(toggle.action!, to: toggle.target, from: toggle)
        controller.window.setContentSize(NSSize(width: 1320, height: 880))
        layout(controller, video)
        try await Task.sleep(for: .milliseconds(450))
        let afterDisable = size(video)
        guard let enable = actions.makeActionsMenu()?.items.first(where: { $0.title == "Ajustar automaticamente" }) else {
            throw HarnessFailure.assertion("auto-fit can be enabled again")
        }
        NSApp.sendAction(enable.action!, to: enable.target, from: enable)
        try await Task.sleep(for: .milliseconds(450))
        let enabledAfterResize = size(video)

        settings.barPosition = .bottom
        settings.barDensity = .large
        layout(controller, video)
        try await Task.sleep(for: .milliseconds(450))
        let barChanged = size(video)
        try check(barChanged != enabledAfterResize, "bar layout change updates video bounds")

        try await waitUntil("fixed state applied") {
            guard let state = value("remoteWindowState", in: controller) as? RemoteWindowState else { return false }
            return !state.canResize && !state.reading
        }
        guard let fixedFit = actions.makeActionsMenu()?.items.first(where: { $0.title == "Ajustar automaticamente" }) else {
            throw HarnessFailure.assertion("fixed window offers the disabled auto-fit action")
        }
        try check(!fixedFit.isEnabled, "fixed window disables auto-fit menu action")
        controller.window.setContentSize(NSSize(width: 1270, height: 850))
        layout(controller, video)
        try await Task.sleep(for: .milliseconds(450))
        let fixedBounds = size(video)
        video.zoomIn(nil)
        try await Task.sleep(for: .milliseconds(350))
        try check(video.zoomScale == 1, "janela fixa mantém zoom desabilitado")

        actions.onAction?("reading", nil)
        try await waitUntil("resizable state restored by fixture") {
            guard let state = value("remoteWindowState", in: controller) as? RemoteWindowState else { return false }
            return state.canResize && !state.reading
        }
        guard let reEnable = actions.makeActionsMenu()?.items.first(where: { $0.title == "Ajustar automaticamente" }) else {
            throw HarnessFailure.assertion("auto-fit can be enabled after fixed state")
        }
        NSApp.sendAction(reEnable.action!, to: reEnable.target, from: reEnable)
        try await Task.sleep(for: .milliseconds(450))
        let reEnabled = size(video)

        let monitor = RemoteWindow.monitor(RemoteMonitor(id: 17, name: "Fixture monitor", width: 1280,
                                                         height: 720, x: 0, y: 0, primary: true))
        let remoteMonitor = RemoteWindowController(target: monitor, host: session.host, port: UInt16(session.port)!,
                                                   pin: session.pin, deviceToken: nil, fingerprint: nil, settings: settings)
        remoteMonitor.start()
        remoteMonitor.showWindow()
        try await Task.sleep(for: .seconds(4))
        guard let monitorVideo = value("videoView", in: remoteMonitor) as? RemoteVideoView else {
            throw HarnessFailure.assertion("monitor controller video view exists")
        }
        monitorVideo.presentPng(makePNG(width: 1280, height: 720), width: 1280, height: 720)
        monitorVideo.layoutSubtreeIfNeeded()
        monitorVideo.zoomIn(nil)
        try check(monitorVideo.zoomScale == 1, "monitor mantém zoom local desabilitado")
        remoteMonitor.window.setContentSize(NSSize(width: 1180, height: 760))
        remoteMonitor.window.contentView?.layoutSubtreeIfNeeded()
        monitorVideo.layoutSubtreeIfNeeded()
        try await Task.sleep(for: .milliseconds(350))

        let detachedTarget = RemoteWindow(id: 4344, title: "Detached fixture", process: "app.exe",
                                          width: 800, height: 600)
        let detached = RemoteWindowController(target: detachedTarget, host: session.host, port: UInt16(session.port)!,
                                              pin: session.pin, deviceToken: nil, fingerprint: nil, settings: settings)
        detached.start()
        detached.showWindow()
        guard let detachedVideo = value("videoView", in: detached) as? RemoteVideoView else {
            throw HarnessFailure.assertion("detached window controller video view exists")
        }
        try await waitUntil("detached resizable window state received") {
            guard let state = value("remoteWindowState", in: detached) as? RemoteWindowState else { return false }
            return state.windowId == detachedTarget.id && state.canResize && !state.minimized
        }
        detachedVideo.presentPng(makePNG(width: 800, height: 600), width: 800, height: 600)
        try await Task.sleep(for: .milliseconds(450))
        let detachedInitial = size(detachedVideo)
        detached.window.setContentSize(NSSize(width: 1180, height: 760))
        detached.window.contentView?.layoutSubtreeIfNeeded()
        detachedVideo.layoutSubtreeIfNeeded()
        try await Task.sleep(for: .milliseconds(450))
        let detachedResized = size(detachedVideo)

        let legacy = RemoteWindowController(target: RemoteWindow(id: 4343, title: "Legacy", process: "old.exe",
                                                                  width: 800, height: 600),
                                            host: session.host, port: UInt16(session.port)!, pin: session.pin,
                                            deviceToken: nil, fingerprint: nil, settings: settings)
        legacy.start()
        legacy.showWindow()
        try await Task.sleep(for: .seconds(4))
        let legacyVideo = value("videoView", in: legacy) as! RemoteVideoView
        legacyVideo.presentPng(makePNG(width: 800, height: 600), width: 800, height: 600)
        legacyVideo.zoomIn(nil)
        try check(legacyVideo.zoomScale == 1, "host legado mantém zoom local desabilitado")
        legacy.window.setContentSize(NSSize(width: 1180, height: 760))
        legacy.window.contentView?.layoutSubtreeIfNeeded()
        (value("videoView", in: legacy) as? RemoteVideoView)?.layoutSubtreeIfNeeded()
        try await Task.sleep(for: .milliseconds(450))

        layout(controller, video)
        try await Task.sleep(for: .milliseconds(450))
        let beforeMaximize = size(video)

        settings.autoFitWindows = false
        controller.window.setContentSize(NSSize(width: 1250, height: 830))
        detached.window.setContentSize(NSSize(width: 1100, height: 720))
        layout(controller, video)
        detached.window.contentView?.layoutSubtreeIfNeeded()
        try await Task.sleep(for: .milliseconds(450))
        try check(value("autoFitEnabled", in: controller) as? Bool == false &&
                  value("autoFitEnabled", in: detached) as? Bool == false,
                  "preference disables both open controllers immediately")
        settings.appearance = .dark
        try check(value("autoFitEnabled", in: controller) as? Bool == false &&
                  value("autoFitEnabled", in: detached) as? Bool == false,
                  "appearance change preserves disabled auto-fit")
        let disabledDetached = RemoteWindowController(target: detachedTarget, host: session.host,
            port: UInt16(session.port)!, pin: session.pin, deviceToken: nil, fingerprint: nil, settings: settings)
        try check(value("autoFitEnabled", in: disabledDetached) as? Bool == false,
                  "new detached controller uses saved disabled preference")
        disabledDetached.window.close()
        settings.autoFitWindows = true
        try await Task.sleep(for: .milliseconds(450))
        let preferenceMain = size(video)
        let preferenceDetached = size(detachedVideo)

        let detachedZoomBounds = size(detachedVideo)
        let zoomControls = descendants(of: detached.window.contentView!).compactMap { $0 as? ZoomControlsView }.first!
        let zoomButton = value("button", in: zoomControls) as! NSButton
        let zoomSlider = value("slider", in: zoomControls) as! NSSlider
        let zoomPercentage = value("percentage", in: zoomControls) as! NSTextField
        let zoomPopover = value("popover", in: zoomControls) as! NSPopover
        detached.showWindow()
        zoomButton.performClick(nil)
        try await waitUntil("detached zoom slider popover opens") { zoomControls.isShowing }
        guard let popoverWindow = zoomPopover.contentViewController?.view.window else {
            throw HarnessFailure.assertion("zoom slider popover has a window")
        }
        try check(zoomSlider.isEnabled && popoverWindow.firstResponder === zoomSlider,
                  "slider is enabled and focused when popover opens")
        for value in [233.0, 167.0, 110.0] {
            zoomSlider.doubleValue = value
            _ = zoomSlider.sendAction(zoomSlider.action!, to: zoomSlider.target)
        }
        try await Task.sleep(for: .milliseconds(450))
        try await waitUntil("fixture sends a window state while zoom popover is open") {
            (value("remoteWindowState", in: detached) as? RemoteWindowState)?.maximized == true
        }
        let sliderFocusSurvivedState = popoverWindow.firstResponder === zoomSlider
        try check(sliderFocusSurvivedState, "window state does not steal slider focus")
        let detachedZoomIn = preferredSize(detachedVideo)
        let sliderScale = detachedVideo.zoomScale
        let sliderPercent = zoomPercentage.stringValue
        try check(sliderScale == 1.1 && sliderPercent == "110%" && zoomSlider.doubleValue == 110,
                  "três movimentos rápidos do slider terminam em 110%")
        zoomControls.close()
        try await waitUntil("popover closes before zoom shortcuts") { !zoomControls.isShowing }
        detachedVideo.zoomOut(nil)
        try await Task.sleep(for: .milliseconds(400))
        let detachedZoomOut = preferredSize(detachedVideo)
        detachedVideo.zoomOut(nil)
        try await Task.sleep(for: .milliseconds(400))
        let detachedZoomWide = preferredSize(detachedVideo)
        detachedVideo.zoomIn(nil)
        try await Task.sleep(for: .milliseconds(400))
        let detachedZoomBack = preferredSize(detachedVideo)
        detachedVideo.zoomIn(nil)
        try await Task.sleep(for: .milliseconds(400))
        detachedVideo.resetZoom(nil)
        try await Task.sleep(for: .milliseconds(400))
        let detachedZoomReset = preferredSize(detachedVideo)
        try check(detachedZoomIn.0 < detachedZoomBounds.0 && detachedZoomIn.1 < detachedZoomBounds.1 &&
                  detachedZoomOut == detachedZoomBounds && detachedZoomWide.0 > detachedZoomBounds.0 &&
                  detachedZoomWide.1 > detachedZoomBounds.1 && detachedZoomBack == detachedZoomBounds &&
                  detachedZoomReset == detachedZoomBounds,
                  "zoom destacado mostra mais e menos conteúdo e restaura bounds em 1×")

        guard let maximize = actions.makeActionsMenu()?.items.first(where: { $0.representedObject as? String == "maximize" }) else {
            throw HarnessFailure.assertion("maximize menu item is available")
        }
        NSApp.sendAction(maximize.action!, to: maximize.target, from: maximize)
        try await Task.sleep(for: .milliseconds(450))
        let final = size(video)
        settings.barDensity = .compact
        layout(controller, video)
        try await Task.sleep(for: .milliseconds(450))
        try check(value("autoFitEnabled", in: controller) as? Bool == false,
                  "unrelated preference preserves per-window manual opt-out")

        let mainZoomBounds = size(video)
        video.zoomIn(nil)
        try await Task.sleep(for: .milliseconds(400))
        let mainZoomIn = preferredSize(video)
        try check(value("autoFitEnabled", in: controller) as? Bool == true,
                  "zoom explícito reativa auto-fit após maximize/manual opt-out")
        video.zoomOut(nil)
        try await Task.sleep(for: .milliseconds(400))
        let mainZoomOut = preferredSize(video)
        video.zoomOut(nil)
        try await Task.sleep(for: .milliseconds(400))
        let mainZoomWide = preferredSize(video)
        video.zoomIn(nil)
        try await Task.sleep(for: .milliseconds(400))
        let mainZoomBack = preferredSize(video)
        video.zoomIn(nil)
        try await Task.sleep(for: .milliseconds(400))
        let mainZoomResizeBefore = preferredSize(video)
        let zoomScaleDuringResize = video.zoomScale
        controller.window.setContentSize(NSSize(width: 1370, height: 880))
        layout(controller, video)
        try await Task.sleep(for: .milliseconds(450))
        let mainZoomResizeBounds = size(video)
        let mainZoomResize = preferredSize(video)
        try check(video.zoomScale == zoomScaleDuringResize && zoomScaleDuringResize == 1.1 &&
                  mainZoomResize.0 < mainZoomResizeBounds.0 && mainZoomResize.1 < mainZoomResizeBounds.1,
                  "resize Mac preserva o fator de zoom principal")
        video.resetZoom(nil)
        try await Task.sleep(for: .milliseconds(400))
        let mainZoomReset = preferredSize(video)
        try check(mainZoomIn.0 < mainZoomBounds.0 && mainZoomIn.1 < mainZoomBounds.1 &&
                  mainZoomOut == mainZoomBounds && mainZoomWide.0 > mainZoomBounds.0 &&
                  mainZoomWide.1 > mainZoomBounds.1 && mainZoomBack == mainZoomBounds &&
                  mainZoomResizeBefore == mainZoomIn && mainZoomReset == mainZoomResizeBounds,
                  "zoom principal mostra mais e menos conteúdo e restaura bounds em 1×")

        actions.onAction?("minimize", nil)
        try await waitUntil("fixture confirms main window minimized") {
            (value("remoteWindowState", in: controller) as? RemoteWindowState)?.minimized == true
        }
        video.zoomIn(nil)
        try await Task.sleep(for: .milliseconds(350))
        try check(video.zoomScale == 1, "janela minimizada mantém zoom desabilitado")
        actions.onAction?("restore", nil)
        try await waitUntil("fixture restores main window") {
            guard let state = value("remoteWindowState", in: controller) as? RemoteWindowState else { return false }
            return !state.minimized
        }

        let windows = value("windows", in: controller) as? [RemoteWindow] ?? []
        guard let nextTarget = windows.first(where: { $0.id == 4243 }) else {
            throw HarnessFailure.assertion("second fixture window is listed")
        }
        bar.onSelectWindow?(nextTarget)
        try await waitUntil("new fixture window selected") {
            (value("remoteWindowState", in: controller) as? RemoteWindowState)?.windowId == nextTarget.id
        }
        try check(video.zoomScale == 1, "nova seleção reseta escala local")

        let report: [String: Any] = [
            "passed": true,
            "initial": [initial.0, initial.1],
            "resized": [resized.0, resized.1],
            "afterDisable": [afterDisable.0, afterDisable.1],
            "enabledAfterResize": [enabledAfterResize.0, enabledAfterResize.1],
            "barChanged": [barChanged.0, barChanged.1],
            "reEnabled": [reEnabled.0, reEnabled.1],
            "final": [final.0, final.1],
            "fixedBounds": [fixedBounds.0, fixedBounds.1],
            "detachedInitial": [detachedInitial.0, detachedInitial.1],
            "detachedResized": [detachedResized.0, detachedResized.1],
            "beforeMaximize": [beforeMaximize.0, beforeMaximize.1],
            "preferenceMain": [preferenceMain.0, preferenceMain.1],
            "preferenceDetached": [preferenceDetached.0, preferenceDetached.1],
            "zoomMain": ["bounds": [mainZoomBounds.0, mainZoomBounds.1],
                         "in": [mainZoomIn.0, mainZoomIn.1],
                         "out": [mainZoomOut.0, mainZoomOut.1],
                         "wide": [mainZoomWide.0, mainZoomWide.1],
                         "back": [mainZoomBack.0, mainZoomBack.1],
                         "resizeBefore": [mainZoomResizeBefore.0, mainZoomResizeBefore.1],
                         "resizeBounds": [mainZoomResizeBounds.0, mainZoomResizeBounds.1],
                         "resize": [mainZoomResize.0, mainZoomResize.1],
                         "reset": [mainZoomReset.0, mainZoomReset.1],
                         "commands": [[mainZoomIn.0, mainZoomIn.1], [mainZoomOut.0, mainZoomOut.1],
                                      [mainZoomWide.0, mainZoomWide.1], [mainZoomBack.0, mainZoomBack.1],
                                      [mainZoomResizeBefore.0, mainZoomResizeBefore.1],
                                      [mainZoomResize.0, mainZoomResize.1],
                                      [mainZoomReset.0, mainZoomReset.1]]],
            "zoomDetached": ["bounds": [detachedZoomBounds.0, detachedZoomBounds.1],
                             "in": [detachedZoomIn.0, detachedZoomIn.1],
                             "out": [detachedZoomOut.0, detachedZoomOut.1],
                             "wide": [detachedZoomWide.0, detachedZoomWide.1],
                             "back": [detachedZoomBack.0, detachedZoomBack.1],
                             "reset": [detachedZoomReset.0, detachedZoomReset.1],
                             "commands": [[detachedZoomIn.0, detachedZoomIn.1],
                                          [detachedZoomOut.0, detachedZoomOut.1],
                                          [detachedZoomWide.0, detachedZoomWide.1],
                                          [detachedZoomBack.0, detachedZoomBack.1],
                                          [detachedZoomIn.0, detachedZoomIn.1],
                                          [detachedZoomReset.0, detachedZoomReset.1]]],
            "sliderBatch": ["moves": 3, "scale": sliderScale, "percentage": sliderPercent,
                            "focusSurvivedState": sliderFocusSurvivedState],
            "zoomSelectionReset": ["windowId": nextTarget.id, "scale": video.zoomScale],
            "scope": "MainWindowController real + TLS fixture; no Keychain or remote PC"
        ]
        try writeReport(report)
        print("PASS auto-fit controller integration: \(initial) -> \(resized); opt-out and explicit maximize exercised")
        controller.window.close()
        remoteMonitor.window.close()
        detached.window.close()
        legacy.window.close()
    }

    private static func layout(_ controller: MainWindowController, _ video: RemoteVideoView) {
        controller.window.contentView?.layoutSubtreeIfNeeded()
        (value("workspaceView", in: controller) as? NSView)?.layoutSubtreeIfNeeded()
        video.layoutSubtreeIfNeeded()
    }

    private static func size(_ view: NSView) -> (Int, Int) {
        (Int(view.bounds.width.rounded()), Int(view.bounds.height.rounded()))
    }

    private static func preferredSize(_ view: RemoteVideoView) -> (Int, Int) {
        let size = view.preferredWindowSize
        return (Int(size.width.rounded()), Int(size.height.rounded()))
    }

    private static func makePNG(width: Int, height: Int) -> Data {
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height,
                                      bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                      isPlanar: false, colorSpaceName: .deviceRGB,
                                      bytesPerRow: 0, bitsPerPixel: 0)!
        return bitmap.representation(using: .png, properties: [:])!
    }

    private static func waitUntil(_ label: String, _ predicate: () -> Bool) async throws {
        for _ in 0..<180 {
            if predicate() { return }
            try await Task.sleep(for: .milliseconds(100))
        }
        throw HarnessFailure.timeout(label)
    }

    private static func check(_ condition: Bool, _ description: String) throws {
        guard condition else { throw HarnessFailure.assertion(description) }
    }

    private static func writeReport(_ report: [String: Any]) throws {
        let data = try JSONSerialization.data(withJSONObject: report, options: [.sortedKeys])
        try data.write(to: URL(fileURLWithPath: CommandLine.arguments[2]))
    }

    private static func value(_ name: String, in object: Any) -> Any? {
        var mirror: Mirror? = Mirror(reflecting: object)
        while let current = mirror {
            if let child = current.children.first(where: { $0.label == name }) { return child.value }
            mirror = current.superclassMirror
        }
        return nil
    }

    private static func descendants(of view: NSView) -> [NSView] {
        [view] + view.subviews.flatMap { descendants(of: $0) }
    }
}

private enum HarnessFailure: Error, CustomStringConvertible {
    case timeout(String)
    case assertion(String)
    var description: String {
        switch self {
        case let .timeout(label): "Timed out: \(label)"
        case let .assertion(label): "Assertion failed: \(label)"
        }
    }
}
