import AppKit

/// Área de vídeo H.264: camada de apresentação do VideoToolbox, entrada remota
/// (clique, arraste, rolagem, teclado) e hover (movimento do ponteiro do PC).
@MainActor
final class RemoteVideoView: NSView, @preconcurrency NSTextInputClient {

    /// Eventos de entrada enviada ao host.
    var send: ((RemoteInputEvent) -> Void)?
    var onDropFiles: (([URL]) -> Void)?
    var onBoundsChange: (() -> Void)?

    let decoder = H264Decoder()
    /// Fallback quando o Windows não tem encoder H.264.
    let jpegDecoder = JpegDecoder()
    private let pngLayer = CALayer()
    private var lastPngImage: CGImage?
    private(set) var pixelSize = CGSize.zero
    /// Retângulo da janela autorizada dentro do quadro (pixels do quadro).
    /// Quando um popup expande o quadro, a escala da janela fica estável e o
    /// popup aparece além da borda dela.
    private var baseRect: CGRect?
    /// Área da view ocupada pelo quadro inteiro (view flipped, y para baixo).
    private var frameRect = CGRect.zero

    private var pressed: RemoteMouseButton?
    private var lastPoint = NSPoint.zero
    private var lastMove: TimeInterval = 0
    private var lastHover = NSPoint.zero
    private var marked = NSAttributedString(string: "")
    private var selection = NSRange(location: 0, length: 0)
    private var tracking: NSTrackingArea?
    private var interactionExclusions: [CGRect] = []
    private var releaseMonitor: Any?

    override var acceptsFirstResponder: Bool { true }
    override var isFlipped: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func setFrameSize(_ newSize: NSSize) {
        let changed = frame.size != newSize
        super.setFrameSize(newSize)
        if changed { onBoundsChange?() }
    }
    override func setBoundsSize(_ newSize: NSSize) {
        let changed = bounds.size != newSize
        super.setBoundsSize(newSize)
        if changed { onBoundsChange?() }
    }
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
        decoder.onThumbnail = { [weak self] cg in self?.onThumbnail?(cg) }
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
        registerForDraggedTypes([.fileURL])
    }

    override func draggingEntered(_ sender: any NSDraggingInfo) -> NSDragOperation {
        if sender.draggingPasteboard.canReadObject(forClasses: [NSURL.self], options: nil) {
            return .copy
        }
        return []
    }

    override func draggingUpdated(_ sender: any NSDraggingInfo) -> NSDragOperation {
        return .copy
    }

    override func performDragOperation(_ sender: any NSDraggingInfo) -> Bool {
        guard let urls = sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: nil) as? [URL], !urls.isEmpty else {
            return false
        }
        onDropFiles?(urls)
        return true
    }

    required init?(coder: NSCoder) {
        fatalError("A interface é construída em código; nib não é suportado.")
    }

    /// Notifica mudança de resolução do vídeo.
    var onResolution: ((Int, Int) -> Void)?
    /// Notifica miniatura gerada para a barra do header.
    var onThumbnail: ((CGImage) -> Void)?

    func captureSnapshot() -> CGImage? {
        if !pngLayer.isHidden, let last = lastPngImage {
            return last
        }
        return decoder.captureSnapshot()
    }

    func presentPng(_ data: Data, width: Int, height: Int) {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let cg = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return }
        lastPngImage = cg
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        pngLayer.contents = cg
        pngLayer.isHidden = false
        decoder.displayLayer.isHidden = true
        jpegDecoder.displayLayer.isHidden = true
        CATransaction.commit()
        onThumbnail?(cg)
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

    /// Define onde a janela fica dentro do quadro (mensagem `viewport` do host).
    func setBaseRect(x: Int, y: Int, width: Int, height: Int) {
        baseRect = width > 0 && height > 0
            ? CGRect(x: x, y: y, width: width, height: height) : nil
        needsLayout = true
        window?.invalidateCursorRects(for: self)
    }

    /// Calcula onde o quadro inteiro é desenhado: a janela autorizada ocupa
    /// o maior espaço possível (aspecto preservado) e o resto do quadro
    /// (popups) fica ao redor, sem alterar a escala.
    private func updateFrameRect() {
        guard pixelSize.width > 0, pixelSize.height > 0, bounds.width > 0, bounds.height > 0 else {
            frameRect = bounds
            return
        }
        let base = baseRect ?? CGRect(origin: .zero, size: pixelSize)
        let scale = min(bounds.width / base.width, bounds.height / base.height)
        let originX = (bounds.width - base.width * scale) / 2 - base.minX * scale
        let originY = (bounds.height - base.height * scale) / 2 - base.minY * scale
        frameRect = CGRect(x: originX, y: originY,
                           width: pixelSize.width * scale, height: pixelSize.height * scale)
    }

    override func layout() {
        super.layout()
        updateFrameRect()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        decoder.displayLayer.frame = frameRect
        pngLayer.frame = frameRect
        jpegDecoder.displayLayer.frame = frameRect
        layer?.masksToBounds = true
        CATransaction.commit()
        window?.invalidateCursorRects(for: self)
    }

    override func resetCursorRects() {
        super.resetCursorRects()
        guard let window, window.isKeyWindow, !isHiddenOrHasHiddenAncestor,
              pixelSize.width > 0, pixelSize.height > 0 else { return }
        for rect in RemoteVideoInteraction.cursorRects(frameRect: frameRect,
                                                        bounds: bounds,
                                                        visibleRect: visibleRect,
                                                        exclusions: interactionExclusions) {
            addCursorRect(rect, cursor: RemoteVideoInteraction.hiddenCursor)
        }
    }

    func setInteractionExclusions(_ rects: [CGRect]) {
        interactionExclusions = rects
        window?.invalidateCursorRects(for: self)
    }

    /// Política geométrica usada para conferir a região de interação nos testes.
    func cursorShouldHide(at point: CGPoint) -> Bool {
        guard !isHiddenOrHasHiddenAncestor, pixelSize.width > 0, pixelSize.height > 0 else { return false }
        return RemoteVideoInteraction.contains(point, in: frameRect, bounds: bounds,
                                               visibleRect: visibleRect,
                                               exclusions: interactionExclusions)
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

    /// Recalcula a região do cursor após mudanças de foco ou de janela.
    func restoreCursor() {
        window?.invalidateCursorRects(for: self)
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if window == nil {
            removeReleaseMonitor()
            resetInput()
        } else {
            restoreCursor()
        }
    }

    // MARK: - Coordenadas

    private func point(for event: NSEvent) -> NSPoint? {
        let location = convert(event.locationInWindow, from: nil)
        guard RemoteVideoInteraction.contains(location, in: frameRect, bounds: bounds,
                                               visibleRect: visibleRect,
                                               exclusions: interactionExclusions),
              frameRect.width > 0, frameRect.height > 0 else { return nil }
        // Coordenadas relativas ao quadro inteiro (o host mapeia o mesmo retângulo).
        let x = (location.x - frameRect.minX) / frameRect.width
        let y = (location.y - frameRect.minY) / frameRect.height
        guard x >= 0, x < 1, y >= 0, y < 1 else { return nil }
        return NSPoint(x: x, y: y)
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
        installReleaseMonitor()
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
        removeReleaseMonitor()
    }

    func resetInput() {
        removeReleaseMonitor()
        if let button = pressed {
            send?(.pointer(action: .up, x: lastPoint.x, y: lastPoint.y, button: button))
        }
        pressed = nil
        clearMarkedText()
        inputContext?.discardMarkedText()
    }

    private func installReleaseMonitor() {
        removeReleaseMonitor()
        guard let window else { return }
        let monitoredWindowNumber = window.windowNumber
        releaseMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseUp, .rightMouseUp]) { [weak self] event in
            guard let self, event.window?.windowNumber == monitoredWindowNumber else { return event }
            _ = self.releasePointerIfNeeded(event)
            return event
        }
    }

    @discardableResult
    func releasePointerIfNeeded(_ event: NSEvent, in window: NSWindow? = nil) -> Bool {
        if let window, event.window?.windowNumber != window.windowNumber { return false }
        guard (pressed == .left && event.type == .leftMouseUp)
                || (pressed == .right && event.type == .rightMouseUp) else { return false }
        endPointer(event)
        return true
    }

    var hasPendingReleaseMonitor: Bool { releaseMonitor != nil }

    private func removeReleaseMonitor() {
        if let releaseMonitor {
            NSEvent.removeMonitor(releaseMonitor)
            self.releaseMonitor = nil
        }
    }

    // MARK: - Rolagem

    override func scrollWheel(with event: NSEvent) {
        guard let point = point(for: event) else { return }
        let delta = Int(max(-1200, min(1200, event.scrollingDeltaY * (event.hasPreciseScrollingDeltas ? 10 : 120))))
        if delta != 0 { send?(.scroll(x: point.x, y: point.y, delta: delta)) }
    }

    // MARK: - Teclado e Gestos

    var onSwipe: ((CGFloat) -> Void)?

    override func swipe(with event: NSEvent) {
        if event.deltaX != 0 {
            onSwipe?(event.deltaX)
        } else {
            super.swipe(with: event)
        }
    }

    override func keyDown(with event: NSEvent) {
        let flags = event.modifierFlags

        // Atalhos com Command (⌘):
        if flags.contains(.command) {
            let char = event.charactersIgnoringModifiers?.lowercased() ?? ""

            // Deixa atalhos reservados do app macOS passarem para o menu:
            // ⌘Q (quit), ⌘W (close), ⌘M (minimize), ⌘H (hide), ⌘K (busca rápida), ⌘` (alternar janela)
            if char == "q" || char == "w" || char == "m" || char == "h" || char == "k" || char == "," || char == "`" || event.keyCode == 50 {
                super.keyDown(with: event)
                return
            }
            if flags.contains(.shift) && char == "b" {
                super.keyDown(with: event)
                return
            }
            if flags.contains(.control) && char == "f" {
                super.keyDown(with: event)
                return
            }

            // Tradução ⌘ -> Ctrl para o Windows (⌘C -> Ctrl+C, ⌘V -> Ctrl+V, ⌘A -> Ctrl+A, etc.)
            let alt = flags.contains(.option)
            let shift = flags.contains(.shift)

            // Redo: ⌘⇧Z -> Ctrl+Y
            if char == "z" && shift {
                send?(.key(.y, ctrl: true, alt: alt, shift: false))
                return
            }

            if let key = RemoteKey(rawValue: char) {
                send?(.key(key, ctrl: true, alt: alt, shift: shift))
                return
            }

            let specialKeys: [UInt16: RemoteKey] = [
                36: .enter, 76: .enter, 51: .backspace, 48: .tab, 53: .escape,
                123: .left, 124: .right, 125: .down, 126: .up,
                117: .delete, 115: .home, 119: .end
            ]
            if let key = specialKeys[event.keyCode] {
                send?(.key(key, ctrl: true, alt: alt, shift: shift))
                return
            }
            return
        }

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
