import AppKit

@main
@MainActor
struct MacInputSmoke {
    static func main() {
        _ = NSApplication.shared
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 400, height: 300), styleMask: [.titled], backing: .buffered, defer: false)
        let canvas = RemoteCanvas(frame: NSRect(x: 0, y: 0, width: 400, height: 300))
        window.contentView = canvas
        canvas.pixelSize = NSSize(width: 800, height: 600)
        var events: [RemoteInputEvent] = []
        canvas.send = { events.append($0) }
        func event(_ type: NSEvent.EventType, x: Double, y: Double, time: Double) -> NSEvent {
            NSEvent.mouseEvent(with: type, location: NSPoint(x: x, y: y), modifierFlags: [], timestamp: time,
                               windowNumber: window.windowNumber, context: nil, eventNumber: 1, clickCount: 1, pressure: 1)!
        }
        canvas.mouseDown(with: event(.leftMouseDown, x: 100, y: 150, time: 1))
        canvas.mouseDragged(with: event(.leftMouseDragged, x: 300, y: 150, time: 2))
        canvas.mouseUp(with: event(.leftMouseUp, x: 450, y: 150, time: 3))
        precondition(events.count == 3)
        for (index, expected) in [RemotePointerAction.down, .move, .up].enumerated() {
            guard case let .pointer(action, x, y, button) = events[index] else { fatalError("Evento inesperado") }
            precondition(action == expected && button == .left && y == 0.5)
            precondition(x == (index == 0 ? 0.25 : 0.75))
        }
        events.removeAll()
        canvas.setMarkedText("a\u{0303}", selectedRange: NSRange(location: 2, length: 0), replacementRange: NSRange(location: NSNotFound, length: 0))
        precondition(canvas.hasMarkedText() && events.isEmpty)
        canvas.insertText("ação, açúcar, bênção, avó, à, ü, ç", replacementRange: NSRange(location: NSNotFound, length: 0))
        precondition(!canvas.hasMarkedText() && events.count == 1)
        guard case let .text(text) = events[0] else { fatalError("Commit ausente") }
        precondition(text == "ação, açúcar, bênção, avó, à, ü, ç")
        canvas.mouseDown(with: event(.leftMouseDown, x: 100, y: 150, time: 4))
        canvas.resetInput()
        guard case let .pointer(action, _, _, _) = events.last else { fatalError("Liberação ausente") }
        precondition(action == .up)
        print("PASS AppKit: coordenadas/arraste, soltar fora do canvas, commit Unicode e reset. Não simula layout físico/dead keys.")
    }
}
