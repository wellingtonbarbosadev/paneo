import AppKit
import ImageIO

@MainActor
final class RemoteCanvas: NSView, @preconcurrency NSTextInputClient {
    var send: ((RemoteInputEvent) -> Void)?
    var image: NSImage?
    var pixelSize = NSSize.zero
    private var pressed: RemoteMouseButton?
    private var lastPoint = NSPoint.zero
    private var lastMove: TimeInterval = 0
    private var marked = NSAttributedString(string: "")
    private var selection = NSRange(location: 0, length: 0)
    override var acceptsFirstResponder: Bool { true }
    override var isFlipped: Bool { true }
    override func resignFirstResponder() -> Bool { resetInput(); return super.resignFirstResponder() }
    func present(_ data: Data, width: Int, height: Int) -> Bool {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let w = properties[kCGImagePropertyPixelWidth] as? Int,
              let h = properties[kCGImagePropertyPixelHeight] as? Int,
              w == width, h == height,
              let cg = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return false }
        pixelSize = NSSize(width: w, height: h)
        image = NSImage(cgImage: cg, size: pixelSize)
        needsDisplay = true; displayIfNeeded()
        return true
    }
    var imageRect: NSRect {
        guard pixelSize.width > 0, pixelSize.height > 0 else { return .zero }
        let scale = min(bounds.width / pixelSize.width, bounds.height / pixelSize.height)
        let size = NSSize(width: pixelSize.width * scale, height: pixelSize.height * scale)
        return NSRect(x: (bounds.width - size.width) / 2, y: (bounds.height - size.height) / 2, width: size.width, height: size.height)
    }
    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.setFill(); bounds.fill()
        image?.draw(in: imageRect, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        if hasMarkedText() {
            let area = NSRect(x: imageRect.minX + 8, y: imageRect.maxY - 30, width: max(0, imageRect.width - 16), height: 26)
            NSColor.black.withAlphaComponent(0.85).setFill(); area.fill()
            (marked.string as NSString).draw(in: area, withAttributes: [.foregroundColor: NSColor.yellow, .font: NSFont.systemFont(ofSize: 16), .underlineStyle: NSUnderlineStyle.single.rawValue])
        }
    }
    private func point(_ event: NSEvent) -> NSPoint? {
        let point = convert(event.locationInWindow, from: nil), rect = imageRect
        guard rect.width > 0, rect.height > 0, rect.contains(point) else { return nil }
        return NSPoint(x: (point.x - rect.minX) / rect.width, y: (point.y - rect.minY) / rect.height)
    }
    override func mouseDown(with event: NSEvent) {
        beginPointer(event, button: .left)
    }
    override func rightMouseDown(with event: NSEvent) {
        beginPointer(event, button: .right)
    }
    private func beginPointer(_ event: NSEvent, button: RemoteMouseButton) {
        guard pressed == nil, let p = point(event) else { return }
        window?.makeFirstResponder(self); pressed = button; lastPoint = p
        send?(.pointer(action: .down, x: p.x, y: p.y, button: button))
    }
    override func mouseDragged(with event: NSEvent) { movePointer(event) }
    override func rightMouseDragged(with event: NSEvent) { movePointer(event) }
    private func movePointer(_ event: NSEvent) {
        guard let button = pressed, let p = point(event), event.timestamp - lastMove >= 1.0 / 60 else { return }
        lastPoint = p; lastMove = event.timestamp
        send?(.pointer(action: .move, x: p.x, y: p.y, button: button))
    }
    override func mouseUp(with event: NSEvent) { endPointer(event) }
    override func rightMouseUp(with event: NSEvent) { endPointer(event) }
    private func endPointer(_ event: NSEvent) {
        guard let button = pressed else { return }
        if let p = point(event) {
            lastPoint = p
            send?(.pointer(action: .move, x: p.x, y: p.y, button: button))
        }
        send?(.pointer(action: .up, x: lastPoint.x, y: lastPoint.y, button: button)); pressed = nil
    }
    func resetInput() {
        if let button = pressed { send?(.pointer(action: .up, x: lastPoint.x, y: lastPoint.y, button: button)) }
        pressed = nil; clearMarkedText(); inputContext?.discardMarkedText()
    }
    override func scrollWheel(with event: NSEvent) {
        guard let p = point(event) else { return }
        let delta = Int(max(-1200, min(1200, event.scrollingDeltaY * (event.hasPreciseScrollingDeltas ? 10 : 120))))
        if delta != 0 { send?(.scroll(x: p.x, y: p.y, delta: delta)) }
    }
    override func keyDown(with event: NSEvent) {
        let flags = event.modifierFlags
        if flags.contains(.command) { return }
        if hasMarkedText() { interpretKeyEvents([event]); return }
        let ctrl = flags.contains(.control), alt = flags.contains(.option), shift = flags.contains(.shift)
        let keys: [UInt16: RemoteKey] = [36: .enter, 76: .enter, 51: .backspace, 48: .tab, 53: .escape, 123: .left, 124: .right, 125: .down, 126: .up, 117: .delete, 115: .home, 119: .end, 116: .pageup, 121: .pagedown, 114: .insert, 122: .f1, 120: .f2, 99: .f3, 118: .f4, 96: .f5, 97: .f6, 98: .f7, 100: .f8, 101: .f9, 109: .f10, 103: .f11, 111: .f12]
        if let key = keys[event.keyCode] { send?(.key(key, ctrl: ctrl, alt: alt, shift: shift)); return }
        if ctrl, let text = event.charactersIgnoringModifiers?.lowercased(), let key = RemoteKey(rawValue: text) {
            send?(.key(key, ctrl: true, alt: alt, shift: shift)); return
        }
        if !ctrl { interpretKeyEvents([event]) }
    }
    func insertText(_ string: Any, replacementRange: NSRange) {
        let text = (string as? NSAttributedString)?.string ?? (string as? String ?? "")
        clearMarkedText()
        if !text.isEmpty { send?(.text(text)) }
    }
    func setMarkedText(_ string: Any, selectedRange: NSRange, replacementRange: NSRange) {
        marked = (string as? NSAttributedString) ?? NSAttributedString(string: string as? String ?? "")
        selection = selectedRange
        needsDisplay = true
    }
    private func clearMarkedText() { marked = NSAttributedString(string: ""); selection = NSRange(location: 0, length: 0); needsDisplay = true }
    func unmarkText() {
        let text = marked.string; clearMarkedText()
        if !text.isEmpty { send?(.text(text)) }
    }
    func selectedRange() -> NSRange { selection }
    func markedRange() -> NSRange { NSRange(location: marked.length == 0 ? NSNotFound : 0, length: marked.length) }
    func hasMarkedText() -> Bool { marked.length > 0 }
    func validAttributesForMarkedText() -> [NSAttributedString.Key] { [] }
    func attributedSubstring(forProposedRange range: NSRange, actualRange: NSRangePointer?) -> NSAttributedString? {
        guard range.location != NSNotFound, range.location <= marked.length, range.length <= marked.length - range.location else { return nil }
        actualRange?.pointee = range
        return marked.attributedSubstring(from: range)
    }
    func characterIndex(for point: NSPoint) -> Int { NSNotFound }
    func firstRect(forCharacterRange range: NSRange, actualRange: NSRangePointer?) -> NSRect {
        window?.convertToScreen(convert(NSRect(x: imageRect.minX, y: imageRect.maxY - 20, width: 1, height: 20), to: nil)) ?? .zero
    }
    override func doCommand(by selector: Selector) {
        if selector == #selector(NSResponder.cancelOperation(_:)) { clearMarkedText(); inputContext?.discardMarkedText() }
    }
}
