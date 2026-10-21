import AppKit

/// Área de vídeo H.264: camada de apresentação do VideoToolbox, entrada remota
/// (clique, arraste, rolagem, teclado) e hover (movimento do ponteiro do PC).
@MainActor
final class RemoteVideoView: NSView, @preconcurrency NSTextInputClient {

    /// Eventos de entrada enviada ao host.
    var send: ((RemoteInputEvent) -> Void)?

    let decoder = H264Decoder()
    /// Fallback quando o Windows não tem encoder H.264.
    let jpegDecoder = JpegDecoder()
    private let pngLayer = CALayer()
    private(set) var pixelSize = CGSize.zero

    private var pressed: RemoteMouseButton?
    private var lastPoint = NSPoint.zero
    private var lastMove: TimeInterval = 0
    private var lastHover = NSPoint.zero
    private var marked = NSAttributedString(string: "")
    private var selection = NSRange(location: 0, length: 0)
    private var tracking: NSTrackingArea?
    private var cursorHidden = false

    override var acceptsFirstResponder: Bool { true }
    override var isFlipped: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func resignFirstResponder() -> Bool {
        resetInput()
        return super.resignFirstResponder()
    }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        pngLayer.contentsGravity = .resizeAspect
        pngLayer.isHidden = true
        jpegDecoder.displayLayer.isHidden = true
        layer?.addSublayer(pngLayer)
        layer?.addSublayer(decoder.displayLayer)
        layer?.addSublayer(jpegDecoder.displayLayer)
        decoder.onNeedKeyframe = { [weak self] in self?.send?(.needKeyframe) }
        decoder.onResolution = { [weak self] width, height in
            guard let self else { return }
            self.pngLayer.isHidden = true
            self.jpegDecoder.displayLayer.isHidden = true
            self.decoder.displayLayer.isHidden = false
            self.pixelSize = CGSize(width: width, height: height)
            self.needsLayout = true
            self.onResolution?(width, height)
        }
        decoder.onDecoded = { [weak self] _ in
            guard let self else { return }
            if self.pngLayer.isHidden == false {
                CATransaction.begin()
                CATransaction.setDisableActions(true)
                self.pngLayer.isHidden = true
                self.decoder.displayLayer.isHidden = false
                CATransaction.commit()
            }
        }
    }

    required init?(coder: NSCoder) {
        fatalError("A interface é construída em código; nib não é suportado.")
    }

    /// Notifica mudança de resolução do vídeo.
    var onResolution: ((Int, Int) -> Void)?

    func presentPng(_ data: Data, width: Int, height: Int) {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let cg = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        pngLayer.contents = cg
        pngLayer.isHidden = false
        decoder.displayLayer.isHidden = true
        jpegDecoder.displayLayer.isHidden = true
        CATransaction.commit()
        if pixelSize.width != CGFloat(width) || pixelSize.height != CGFloat(height) {
            pixelSize = CGSize(width: width, height: height)
            needsLayout = true
            onResolution?(width, height)
        }
    }

    /// Fallback JPEG: usado quando o Windows não tem encoder H.264.
    func presentJpeg(_ data: Data, width: Int, height: Int) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        pngLayer.isHidden = true
        decoder.displayLayer.isHidden = true
        jpegDecoder.displayLayer.isHidden = false
        CATransaction.commit()
        jpegDecoder.decode(data)
        if pixelSize.width != CGFloat(width) || pixelSize.height != CGFloat(height) {
            pixelSize = CGSize(width: width, height: height)
            needsLayout = true
            onResolution?(width, height)
        }
    }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        decoder.displayLayer.frame = bounds
        pngLayer.frame = bounds
        jpegDecoder.displayLayer.frame = bounds
        CATransaction.commit()
    }

    // MARK: - Hover

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: bounds,
                                  options: [.mouseMoved, .mouseEnteredAndExited, .inVisibleRect, .activeInKeyWindow],
                                  owner: self,
                                  userInfo: nil)
        addTrackingArea(area)
        tracking = area
        window?.acceptsMouseMovedEvents = true
    }

    override func mouseMoved(with event: NSEvent) {
        guard let point = point(for: event) else { return }
        let now = event.timestamp
        guard now - lastMove >= 1.0 / 60 else { return }
        // Coordenadas repetidas não são reenviadas.
        if abs(point.x - lastHover.x) < 0.0005, abs(point.y - lastHover.y) < 0.0005 { return }
        lastMove = now
        lastHover = point
        send?(.pointer(action: .hover, x: point.x, y: point.y, button: .left))
    }

    override func mouseEntered(with event: NSEvent) { hideCursor(true) }
    override func mouseExited(with event: NSEvent) { hideCursor(false) }

    /// Restaura o cursor do sistema: sem isso ele fica invisível no resto do
    /// app quando a janela remota fecha com o ponteiro dentro da imagem.
    func restoreCursor() {
        hideCursor(false)
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if window == nil { restoreCursor() }
    }

    private func hideCursor(_ hidden: Bool) {
        guard hidden != cursorHidden else { return }
        cursorHidden = hidden
        if hidden { NSCursor.hide() } else { NSCursor.unhide() }
    }

    // MARK: - Coordenadas

    private func point(for event: NSEvent) -> NSPoint? {
        let location = convert(event.locationInWindow, from: nil)
        guard bounds.contains(location) else { return nil }
        return NSPoint(x: location.x / max(bounds.width, 1), y: location.y / max(bounds.height, 1))
    }

    // MARK: - Ponteiro

    override func mouseDown(with event: NSEvent) { beginPointer(event, button: .left) }
    override func rightMouseDown(with event: NSEvent) { beginPointer(event, button: .right) }

    private func beginPointer(_ event: NSEvent, button: RemoteMouseButton) {
        guard pressed == nil, let point = point(for: event) else { return }
        window?.makeFirstResponder(self)
        pressed = button
        lastPoint = point
        send?(.pointer(action: .down, x: point.x, y: point.y, button: button))
    }

    override func mouseDragged(with event: NSEvent) { movePointer(event) }
    override func rightMouseDragged(with event: NSEvent) { movePointer(event) }

    private func movePointer(_ event: NSEvent) {
        guard let button = pressed, let point = point(for: event), event.timestamp - lastMove >= 1.0 / 60 else { return }
        lastPoint = point
        lastMove = event.timestamp
        send?(.pointer(action: .move, x: point.x, y: point.y, button: button))
    }

    override func mouseUp(with event: NSEvent) { endPointer(event) }
    override func rightMouseUp(with event: NSEvent) { endPointer(event) }

    private func endPointer(_ event: NSEvent) {
        guard let button = pressed else { return }
        if let point = point(for: event) { lastPoint = point }
        send?(.pointer(action: .up, x: lastPoint.x, y: lastPoint.y, button: button))
        pressed = nil
    }

    func resetInput() {
        if let button = pressed {
            send?(.pointer(action: .up, x: lastPoint.x, y: lastPoint.y, button: button))
        }
        pressed = nil
        clearMarkedText()
        inputContext?.discardMarkedText()
    }

    // MARK: - Rolagem

    override func scrollWheel(with event: NSEvent) {
        guard let point = point(for: event) else { return }
        let delta = Int(max(-1200, min(1200, event.scrollingDeltaY * (event.hasPreciseScrollingDeltas ? 10 : 120))))
        if delta != 0 { send?(.scroll(x: point.x, y: point.y, delta: delta)) }
    }

    // MARK: - Teclado

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
    }

    private func clearMarkedText() {
        marked = NSAttributedString(string: "")
        selection = NSRange(location: 0, length: 0)
    }

    func unmarkText() {
        let text = marked.string
        clearMarkedText()
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
        window?.convertToScreen(convert(bounds, to: nil)) ?? .zero
    }

    override func doCommand(by selector: Selector) {
        if selector == #selector(NSResponder.cancelOperation(_:)) {
            clearMarkedText()
            inputContext?.discardMarkedText()
        }
    }
}
