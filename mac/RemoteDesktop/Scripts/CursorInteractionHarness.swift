import AppKit

@MainActor
@main
struct CursorInteractionHarness {
    private static var eventTime: TimeInterval = 0

    static func main() throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)

        let window = NSWindow(contentRect: NSRect(x: 80, y: 80, width: 240, height: 240),
                              styleMask: [], backing: .buffered, defer: false)
        let container = NSView(frame: CGRect(x: 0, y: 0, width: 240, height: 240))
        let video = RemoteVideoView(frame: NSRect(x: 0, y: 0, width: 200, height: 200))
        window.contentView = container
        container.addSubview(video)
        window.makeKeyAndOrderFront(nil)
        app.activate(ignoringOtherApps: true)
        video.presentPng(try makePNG(width: 16, height: 9), width: 16, height: 9)
        video.layoutSubtreeIfNeeded()

        let frame = CGRect(x: 0, y: 43.75, width: 200, height: 112.5)
        let peekRect = CGRect(x: 0, y: 43.75, width: 200, height: 8)
        let barRect = CGRect(x: 0, y: 60, width: 200, height: 30)
        let overlays = [peekRect, barRect]
        video.setInteractionExclusions(overlays)
        check(video.cursorShouldHide(at: CGPoint(x: 100, y: 100)), "cursor transparent over visible image")
        check(!video.cursorShouldHide(at: CGPoint(x: 100, y: 20)), "cursor visible in aspect-fit margin")
        check(!video.cursorShouldHide(at: CGPoint(x: 100, y: 47)), "cursor visible over peek overlay")
        check(!video.cursorShouldHide(at: CGPoint(x: 100, y: 70)), "cursor visible over thumbnail bar")
        let cursorRects = RemoteVideoInteraction.cursorRects(frameRect: frame, bounds: video.bounds,
                                                             visibleRect: video.visibleRect,
                                                             exclusions: overlays)
        check(overlays.allSatisfy { overlay in cursorRects.allSatisfy { !$0.intersects(overlay) } },
              "peek and thumbnail bar excluded from transparent cursor rects")

        var events: [String] = []
        video.send = { event in
            switch event {
            case .pointer(let action, _, _, _): events.append(action.rawValue)
            case .scroll: events.append("scroll")
            default: events.append("other")
            }
        }
        video.mouseMoved(with: try mouseEvent(.mouseMoved, window: window, point: CGPoint(x: 100, y: 20), view: video))
        video.mouseMoved(with: try mouseEvent(.mouseMoved, window: window, point: CGPoint(x: 100, y: 47), view: video))
        video.mouseMoved(with: try mouseEvent(.mouseMoved, window: window, point: CGPoint(x: 100, y: 70), view: video))
        check(events.isEmpty, "no remote hover in margins, peek or thumbnail bar")
        video.mouseMoved(with: try mouseEvent(.mouseMoved, window: window, point: CGPoint(x: 100, y: 100), view: video))
        check(events == ["hover"], "remote hover resumes over the visible image")
        events.removeAll()

        video.mouseDown(with: try mouseEvent(.leftMouseDown, window: window, point: CGPoint(x: 100, y: 20), view: video))
        video.mouseDown(with: try mouseEvent(.leftMouseDown, window: window, point: CGPoint(x: 100, y: 70), view: video))
        let scrollOverPeekIsRemote = RemoteVideoInteraction.contains(CGPoint(x: 100, y: 47),
                                                                      in: frame, bounds: video.bounds,
                                                                      visibleRect: video.visibleRect,
                                                                      exclusions: overlays)
        check(events.isEmpty && !scrollOverPeekIsRemote,
              "no remote click or scroll in margins, peek or thumbnail bar")

        video.mouseDown(with: try mouseEvent(.leftMouseDown, window: window, point: CGPoint(x: 100, y: 100), view: video))
        video.mouseDragged(with: try mouseEvent(.leftMouseDragged, window: window, point: CGPoint(x: 100, y: 47), view: video))
        check(events == ["down"], "drag does not cross the peek overlay remotely")
        check(video.hasPendingReleaseMonitor, "pressed pointer installs a temporary release monitor")

        let peek = PeekStripView(frame: CGRect(x: 0, y: 43.75, width: 200, height: 8))
        video.addSubview(peek, positioned: .above, relativeTo: nil)
        peek.isHidden = false
        check(peek.hitTest(CGPoint(x: 100, y: 47)) === peek, "peek view captures clicks over the video")
        let release = try mouseEvent(.leftMouseUp, window: window, point: CGPoint(x: 100, y: 47), view: video)
        check(video.releasePointerIfNeeded(release, in: window), "overlay mouse-up reaches the release handler")
        check(events.suffix(1) == ["up"], "mouse-up is released when pointer ends over overlay")
        check(!video.hasPendingReleaseMonitor, "release removes the temporary monitor")

        video.mouseDown(with: try mouseEvent(.leftMouseDown, window: window, point: CGPoint(x: 100, y: 100), view: video))
        video.resetInput()
        check(events.suffix(2) == ["down", "up"], "focus cleanup releases a pressed remote button")

        peek.removeFromSuperview()
        video.setInteractionExclusions([])
        video.presentPng(try makePNG(width: 9, height: 16), width: 9, height: 16)
        video.layoutSubtreeIfNeeded()
        check(!video.cursorShouldHide(at: CGPoint(x: 20, y: 100)), "portrait: left margin keeps local cursor")
        check(!video.cursorShouldHide(at: CGPoint(x: 180, y: 100)), "portrait: right margin keeps local cursor")
        check(video.cursorShouldHide(at: CGPoint(x: 100, y: 100)), "portrait: image uses remote cursor")
        events.removeAll()
        video.mouseDown(with: try mouseEvent(.leftMouseDown, window: window, point: CGPoint(x: 20, y: 100), view: video))
        video.mouseMoved(with: try mouseEvent(.mouseMoved, window: window, point: CGPoint(x: 180, y: 100), view: video))
        check(events.isEmpty, "portrait margins do not send input")
        video.setFrameSize(CGSize(width: 240, height: 200))
        video.layoutSubtreeIfNeeded()
        check(!video.cursorShouldHide(at: CGPoint(x: 50, y: 100)), "resize recomputes portrait margin")
        check(video.cursorShouldHide(at: CGPoint(x: 120, y: 100)), "resize preserves image interaction")
        window.close()
        print("PASS: cursor regions, aspect-fit margins, overlay routing, mouse-up and input cleanup")
    }

    private static func check(_ condition: @autoclosure () -> Bool, _ label: String) {
        guard condition() else {
            fputs("FAIL: \(label)\n", stderr)
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

    private static func mouseEvent(_ type: NSEvent.EventType, window: NSWindow, point: CGPoint,
                                   view: NSView) throws -> NSEvent {
        eventTime += 1
        let windowPoint = view.convert(point, to: nil)
        guard let event = NSEvent.mouseEvent(with: type, location: windowPoint, modifierFlags: [],
                                             timestamp: eventTime,
                                             windowNumber: window.windowNumber, context: nil,
                                             eventNumber: 1, clickCount: 1, pressure: 1) else {
            throw HarnessError.eventCreation
        }
        return event
    }

}

private enum HarnessError: Error {
    case imageCreation
    case eventCreation
}
