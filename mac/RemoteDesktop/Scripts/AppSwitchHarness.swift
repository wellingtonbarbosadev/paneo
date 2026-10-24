import AppKit
import Foundation

/// Reproduz a troca no controller real com PNGs e viewports sintéticos.
@MainActor
@main
struct AppSwitchHarness {
    private struct Session: Decodable { let host: String; let port: String; let pin: String }
    private static var failures: [String] = []
    private static var eventTime: TimeInterval = 0

    static func main() throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let session = try JSONDecoder().decode(Session.self,
            from: Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1])))
        let suite = "AppSwitchHarness.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suite) else { fatalError("UserDefaults indisponível") }
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = AppSettings(defaults: defaults)
        settings.autoFitWindows = false
        let controller = MainWindowController(
            preset: ConnectionPreset(host: session.host, port: session.port, pin: session.pin, fingerprint: nil),
            persistPairing: false, settings: settings)
        controller.showWindow()
        controller.window.setContentSize(NSSize(width: 1100, height: 760))
        controller.window.makeKeyAndOrderFront(nil)
        app.activate(ignoringOtherApps: true)
        defer { controller.window.close() }

        guard let bar = value("thumbnailBar", in: controller) as? WindowThumbnailBar,
              let video = value("videoView", in: controller) as? RemoteVideoView,
              let pngLayer = value("pngLayer", in: video) as? CALayer,
              let cover = value("streamCover", in: video) as? NSView,
              let coverImage = value("streamCoverImage", in: video) as? CALayer,
              let workspace = value("workspaceView", in: controller) as? NSView,
              let welcome = value("welcomeCard", in: controller) as? NSView else {
            fatalError("Componentes do workspace indisponíveis")
        }
        workspace.isHidden = false
        welcome.isHidden = true
        let a = RemoteWindow(id: 4242, title: "Fixture A", process: "a.exe", width: 800, height: 600)
        let b = RemoteWindow(id: 4343, title: "Fixture B", process: "b.exe", width: 600, height: 900)
        let imageA = try makePNG(width: 800, height: 600)
        let imageB = try makePNG(width: 600, height: 900)

        bar.onSelectWindow?(a)
        video.setBaseRect(x: 0, y: 0, width: 800, height: 600)
        video.presentPng(imageA, width: 800, height: 600)
        layout(controller, video)
        pumpMainRunLoop()
        let frameA = pngLayer.frame
        check(!video.isHiddenOrHasHiddenAncestor && video.visibleRect.width > 0 && video.visibleRect.height > 0,
              "video view is visible for synthetic pointer input")
        check(cover.isHidden && !video.isTransitioning && !pngLayer.isHidden &&
              imageMatches(pngLayer, width: 800, height: 600), "A has been revealed at 800x600")

        bar.onSelectWindow?(b)
        video.setBaseRect(x: 0, y: 0, width: 600, height: 900)
        layout(controller, video)
        let bPendingFrame = coverImage.frame
        check(!cover.isHidden && video.isTransitioning && pngLayer.isHidden &&
              imageMatches(coverImage, width: 800, height: 600), "A snapshot covers the pending B stream")
        check(bPendingFrame == frameA, "A cover keeps its original geometry during B viewport")

        var inputTargets: [String] = []
        video.send = { _ in inputTargets.append("B") }
        let point = CGPoint(x: video.bounds.midX, y: video.bounds.midY)
        video.mouseDown(with: try mouseEvent(.leftMouseDown, window: controller.window, point: point, view: video))
        video.mouseUp(with: try mouseEvent(.leftMouseUp, window: controller.window, point: point, view: video))
        check(inputTargets.isEmpty, "input does not reach B while A snapshot is visible")

        // Revenir à A avant l'image B teste l'annulation rapide de la transition.
        bar.onSelectWindow?(a)
        video.setBaseRect(x: 0, y: 0, width: 800, height: 600)
        layout(controller, video)
        check(!cover.isHidden && video.isTransitioning && imageMatches(coverImage, width: 800, height: 600) &&
              coverImage.frame == frameA, "quick B-to-A keeps the visible A snapshot stable")
        video.presentPng(imageA, width: 800, height: 600)
        layout(controller, video)
        pumpMainRunLoop()
        check(cover.isHidden && !video.isTransitioning && imageMatches(pngLayer, width: 800, height: 600),
              "quick B-to-A reveals A again")

        bar.onSelectWindow?(b)
        video.setBaseRect(x: 0, y: 0, width: 600, height: 900)
        video.presentPng(imageB, width: 600, height: 900)
        layout(controller, video)
        pumpMainRunLoop()
        let frameB = pngLayer.frame
        check(cover.isHidden && !video.isTransitioning && !pngLayer.isHidden &&
              imageMatches(pngLayer, width: 600, height: 900), "B replaces the cover with its 600x900 PNG")
        check(abs(frameB.width / frameB.height - 2.0 / 3.0) < 0.01 &&
              frameB.width <= video.bounds.width + 1 && frameB.height <= video.bounds.height + 1,
              "B layer uses the 600x900 geometry")
        inputTargets.removeAll()
        video.send = { _ in inputTargets.append("B") }
        video.mouseDown(with: try mouseEvent(.leftMouseDown, window: controller.window, point: point, view: video))
        video.mouseUp(with: try mouseEvent(.leftMouseUp, window: controller.window, point: point, view: video))
        check(inputTargets == ["B", "B"], "input resumes after B is revealed")

        bar.onSelectWindow?(a)
        video.setBaseRect(x: 0, y: 0, width: 800, height: 600)
        video.presentPng(imageA, width: 800, height: 600)
        layout(controller, video)
        pumpMainRunLoop(for: 0.15)
        video.setBaseRect(x: 0, y: 0, width: 790, height: 600)
        layout(controller, video)
        pumpMainRunLoop(for: 0.16)
        check(!cover.isHidden && video.isTransitioning,
              "geometry change during reveal fade keeps the transition cover active")
        pumpMainRunLoop(for: 0.5)
        check(cover.isHidden && !video.isTransitioning,
              "geometry change reschedules the cover reveal to completion")

        let report: [String: Any] = [
            "passed": failures.isEmpty,
            "failures": failures,
            "aFrame": rect(frameA),
            "bPendingCoverFrame": rect(bPendingFrame),
            "bFrame": rect(frameB),
            "videoBounds": rect(video.bounds),
            "inputTargets": inputTargets,
            "scope": "MainWindowController + RemoteVideoView real; isolated defaults; synthetic PNG/viewport; no Keychain/PC"
        ]
        let data = try JSONSerialization.data(withJSONObject: report, options: [.sortedKeys])
        try data.write(to: URL(fileURLWithPath: CommandLine.arguments[2]))
        print("APPSWITCH_REPORT \(String(data: data, encoding: .utf8)!)")
        if !failures.isEmpty { exit(1) }
        print("PASS app switch: A → B → A → B, transition input and image geometry")
        exit(0)
    }

    private static func makePNG(width: Int, height: Int) throws -> Data {
        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
              let data = bitmap.representation(using: .png, properties: [:]) else {
            throw AppSwitchFailure.imageCreation
        }
        return data
    }

    private static func mouseEvent(_ type: NSEvent.EventType, window: NSWindow,
                                   point: CGPoint, view: NSView) throws -> NSEvent {
        eventTime += 1
        let location = view.convert(point, to: nil)
        guard let event = NSEvent.mouseEvent(with: type, location: location, modifierFlags: [],
            timestamp: eventTime, windowNumber: window.windowNumber, context: nil,
            eventNumber: 1, clickCount: 1, pressure: 1) else {
            throw AppSwitchFailure.eventCreation
        }
        return event
    }

    private static func layout(_ controller: MainWindowController, _ video: RemoteVideoView) {
        controller.window.contentView?.layoutSubtreeIfNeeded()
        (value("workspaceView", in: controller) as? NSView)?.layoutSubtreeIfNeeded()
        video.layoutSubtreeIfNeeded()
        CATransaction.flush()
    }

    private static func pumpMainRunLoop(for duration: TimeInterval = 0.5) {
        RunLoop.main.run(until: Date(timeIntervalSinceNow: duration))
    }

    private static func imageSize(_ layer: CALayer) -> (Int, Int)? {
        guard let contents = layer.contents else { return nil }
        let image = contents as! CGImage
        return (image.width, image.height)
    }

    private static func imageMatches(_ layer: CALayer, width: Int, height: Int) -> Bool {
        guard let size = imageSize(layer) else { return false }
        return size.0 == width && size.1 == height
    }

    private static func rect(_ value: CGRect) -> [Double] {
        [Double(value.origin.x), Double(value.origin.y), Double(value.width), Double(value.height)]
    }

    private static func check(_ condition: Bool, _ description: String) {
        if !condition { failures.append(description) }
    }

    private static func value(_ name: String, in object: Any) -> Any? {
        var mirror: Mirror? = Mirror(reflecting: object)
        while let current = mirror {
            if let child = current.children.first(where: { $0.label == name }) { return child.value }
            mirror = current.superclassMirror
        }
        return nil
    }
}

private enum AppSwitchFailure: Error {
    case imageCreation
    case eventCreation
}
