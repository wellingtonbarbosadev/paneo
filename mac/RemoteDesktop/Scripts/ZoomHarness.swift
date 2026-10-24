import AppKit
import ImageIO

/// Valida o redimensionamento lógico da janela remota e o input que continua local.
@MainActor
@main
struct ZoomHarness {
    private static var timestamp: TimeInterval = 0

    static func main() throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        app.activate(ignoringOtherApps: true)

        try verifyWindowZoomAndFittedFrames()
        try verifyInputAndOptionScroll()
        try verifyRepeatedJpegFrames()
        try verifyCapabilityAndMenuRouting()
        print("PASS: zoom lógico, vídeo encaixado, input, capability e menu")
    }

    private static func verifyWindowZoomAndFittedFrames() throws {
        let (window, video) = makeVideo(size: NSSize(width: 300, height: 200),
                                       image: NSSize(width: 100, height: 100))
        defer { window.close() }
        var changes = 0
        video.onZoomChange = { changes += 1 }
        let layer = video.decoder.displayLayer
        let originalFrame = layer.frame

        checkFitted(video, label: "quadro inicial fica inteiro no vídeo")
        check(close(video.zoomScale, 1), "zoom começa em 1×")
        check(close(video.preferredWindowSize.width, 300) &&
              close(video.preferredWindowSize.height, 200), "tamanho preferido começa igual aos bounds")
        video.zoomIn(nil)
        check(close(video.zoomScale, 1) && changes == 0,
              "capability desligada mantém atalhos sem efeito")

        video.allowsWindowZoom = true
        video.onZoomChange = nil
        video.zoomIn(nil)
        check(close(video.zoomScale, 1), "sem callback de controller zoom permanece indisponível")
        video.onZoomChange = { changes += 1 }
        video.zoomIn(nil)
        check(close(video.zoomScale, 1.1) && changes == 1, "zoom-in notifica uma vez")
        video.setZoom(.nan)
        video.setZoom(.infinity)
        video.setZoom(-CGFloat.infinity)
        check(close(video.zoomScale, 1.1) && changes == 1,
              "valores não finitos são ignorados sem callback")
        check(close(video.preferredWindowSize.width, 300 / 1.1) &&
              close(video.preferredWindowSize.height, 200 / 1.1),
              "aumentar zoom pede janela proporcionalmente menor")
        check(layer.frame == originalFrame && video.bounds.size == NSSize(width: 300, height: 200),
              "zoom lógico não escala a camada nem muda os bounds locais")

        video.zoomOut(nil)
        check(close(video.zoomScale, 1) && changes == 2 &&
              close(video.preferredWindowSize.width, 300) && close(video.preferredWindowSize.height, 200),
              "1 → 1,1 → 1 restaura as dimensões preferidas")

        for _ in 0..<40 { video.zoomOut(nil) }
        check(close(video.zoomScale, 0.5) &&
              close(video.preferredWindowSize.width, 600) &&
              close(video.preferredWindowSize.height, 400),
              "limite inferior pede janela maior para mostrar mais conteúdo")
        for _ in 0..<40 { video.zoomIn(nil) }
        check(close(video.zoomScale, 3) &&
              close(video.preferredWindowSize.width, 100) &&
              close(video.preferredWindowSize.height, 200 / 3),
              "limite superior pede janela menor para mostrar menos conteúdo")

        video.resetZoom(nil)
        check(close(video.zoomScale, 1) && changes > 2,
              "Restaurar Zoom volta a 1 e notifica o controller")
        check(layer.frame == originalFrame, "restaurar zoom não altera o quadro atual")

        // O controller aplica o tamanho pedido no host; cada novo quadro continua inteiro no vídeo.
        video.presentPng(makePNG(width: 272, height: 182), width: 272, height: 182)
        layout(video)
        checkFitted(video, label: "quadro após janela remota menor")
        video.presentPng(makePNG(width: 600, height: 400), width: 600, height: 400)
        layout(video)
        checkFitted(video, label: "quadro após janela remota maior")
    }

    private static func verifyInputAndOptionScroll() throws {
        let (window, video) = makeVideo(size: NSSize(width: 300, height: 200),
                                       image: NSSize(width: 100, height: 100))
        defer { window.close() }
        video.allowsWindowZoom = true
        video.onZoomChange = {}
        var sent: [RemoteInputEvent] = []
        video.send = { sent.append($0) }

        let frame = video.decoder.displayLayer.frame
        video.zoomIn(nil)
        check(video.decoder.displayLayer.frame == frame, "mudança de zoom aguarda o host, sem crop local")
        video.mouseMoved(with: mouseEvent(.mouseMoved, window: window,
                                          point: CGPoint(x: 150, y: 100), view: video))
        guard case .pointer(.hover, let hoverX, let hoverY, _) = sent.last else {
            throw HarnessFailure.assertion("hover remoto continua ativo")
        }
        check(close(hoverX, 0.5) && close(hoverY, 0.5), "hover mantém coordenadas do quadro inteiro")
        sent.removeAll()

        video.mouseDown(with: mouseEvent(.leftMouseDown, window: window,
                                         point: CGPoint(x: 150, y: 100), view: video))
        guard case .pointer(.down, let clickX, let clickY, _) = sent.last else {
            throw HarnessFailure.assertion("clique remoto continua ativo")
        }
        check(close(clickX, 0.5) && close(clickY, 0.5), "clique usa quadro inteiro sem crop")
        video.resetInput()

        video.setInteractionExclusions([CGRect(x: 100, y: 75, width: 100, height: 50)])
        check(!video.cursorShouldHide(at: CGPoint(x: 150, y: 100)), "overlay local mantém cursor visível")
        sent.removeAll()
        video.mouseDown(with: mouseEvent(.leftMouseDown, window: window,
                                         point: CGPoint(x: 150, y: 100), view: video))
        check(sent.isEmpty, "clique sobre overlay não chega ao host")
        video.setInteractionExclusions([])

        video.scrollWheel(with: scrollEvent(deltaX: 0, deltaY: 30, modifiers: [.option],
                                            window: window, view: video,
                                            point: CGPoint(x: 150, y: 100)))
        check(sent.contains { if case .scroll = $0 { return true }; return false },
              "Option-scroll segue para o Windows como rolagem normal")
        check(video.decoder.displayLayer.frame == frame, "rolagem não desloca nem recorta o quadro")
    }

    private static func verifyRepeatedJpegFrames() throws {
        let (window, video) = makeVideo(size: NSSize(width: 300, height: 200),
                                       image: NSSize(width: 100, height: 100))
        defer { window.close() }
        video.allowsWindowZoom = true
        video.onZoomChange = {}
        let jpeg = makeJPEG(width: 100, height: 100)
        video.presentJpeg(jpeg, width: 100, height: 100)
        layout(video)
        let originalFrame = video.jpegDecoder.displayLayer.frame
        video.zoomIn(nil)
        video.zoomIn(nil)
        check(video.jpegDecoder.displayLayer.frame == originalFrame,
              "zoom JPEG aguarda novo quadro do host")
        video.presentJpeg(jpeg, width: 100, height: 100)
        check(video.jpegDecoder.displayLayer.frame == originalFrame,
              "quadro JPEG repetido preserva encaixe integral")
        video.presentJpeg(makeJPEG(width: 240, height: 100), width: 240, height: 100)
        layout(video)
        checkFitted(video, label: "quadro JPEG com nova dimensão segue encaixado")
    }

    private static func verifyCapabilityAndMenuRouting() throws {
        let (windowA, videoA) = makeVideo(size: NSSize(width: 300, height: 200),
                                         image: NSSize(width: 100, height: 100))
        let (windowB, videoB) = makeVideo(size: NSSize(width: 400, height: 240),
                                         image: NSSize(width: 100, height: 100))
        defer { windowA.close(); windowB.close() }
        var changesA = 0
        var changesB = 0
        videoA.allowsWindowZoom = true
        videoA.onZoomChange = { changesA += 1 }
        videoB.allowsWindowZoom = true
        videoB.onZoomChange = { changesB += 1 }

        let menu = AppMenus.makeMainMenu()
        NSApp.mainMenu = menu
        let viewMenu = menu.items.first { $0.submenu?.title == "Visualizar" }?.submenu
        guard let viewMenu,
              let increase = viewMenu.items.first(where: { $0.title == "Aumentar Zoom" }),
              let decrease = viewMenu.items.first(where: { $0.title == "Diminuir Zoom" }),
              let reset = viewMenu.items.first(where: { $0.title == "Restaurar Zoom" }) else {
            throw HarnessFailure.assertion("menu Visualizar tem os três comandos de zoom")
        }
        check(increase.keyEquivalent == "+" && decrease.keyEquivalent == "-" && reset.keyEquivalent == "0",
              "menu mantém Cmd+, Cmd− e Cmd0")
        check(increase.keyEquivalentModifierMask.contains(.command) &&
              decrease.keyEquivalentModifierMask.contains(.command) &&
              reset.keyEquivalentModifierMask.contains(.command), "atalhos seguem locais ao Mac")
        check(increase.target == nil, "ações usam responder chain")

        // Janela A representa host antigo, janela fixa ou monitor: capability ausente.
        videoA.resetWindowZoom()
        check(!videoA.allowsWindowZoom && close(videoA.zoomScale, 1) && changesA == 0,
              "reset silencioso desliga capability ao selecionar outro alvo")
        check(!videoA.validateMenuItem(increase) && !videoA.validateMenuItem(decrease) &&
              !videoA.validateMenuItem(reset), "menu desabilita zoom sem capability do host")

        windowB.makeKeyAndOrderFront(nil)
        windowB.makeFirstResponder(videoB)
        check(videoB.validateMenuItem(increase) && videoB.validateMenuItem(decrease) &&
              videoB.validateMenuItem(reset), "menu habilita zoom só no host redimensionável")
        check(windowB.firstResponder?.tryToPerform(increase.action!, with: increase) == true,
              "ação do menu segue responder da janela ativa")
        check(close(videoB.zoomScale, 1.1) && changesB == 1 && close(videoA.zoomScale, 1),
              "menu atualiza somente a janela ativa")
        windowA.makeKeyAndOrderFront(nil)
        windowA.makeFirstResponder(videoA)
        _ = windowA.firstResponder?.tryToPerform(increase.action!, with: increase)
        check(close(videoA.zoomScale, 1), "responder sem capability não altera zoom")

        for _ in 0..<25 { videoB.zoomIn(nil) }
        check(close(videoB.zoomScale, 3) && !videoB.validateMenuItem(increase) &&
              videoB.validateMenuItem(decrease), "limite máximo desabilita aumentar")
        for _ in 0..<25 { videoB.zoomOut(nil) }
        check(close(videoB.zoomScale, 0.5) && videoB.validateMenuItem(increase) &&
              !videoB.validateMenuItem(decrease), "limite mínimo desabilita diminuir")
        videoB.resetZoom(nil)
        check(windowB.firstResponder?.tryToPerform(decrease.action!, with: decrease) == true &&
              windowB.firstResponder?.tryToPerform(reset.action!, with: reset) == true,
              "ações de diminuir e restaurar seguem responder da janela B")
        check(close(videoB.zoomScale, 1) && close(videoA.zoomScale, 1),
              "janelas destacadas mantêm escala independente")

        var remoteEvents = 0
        videoA.send = { _ in remoteEvents += 1 }
        videoB.send = { _ in remoteEvents += 1 }
        videoA.keyDown(with: keyEvent("+", modifiers: [.command, .shift], window: windowA))
        videoA.keyDown(with: keyEvent("=", modifiers: .command, window: windowA))
        videoA.keyDown(with: keyEvent("-", modifiers: .command, window: windowA))
        videoA.keyDown(with: keyEvent("0", modifiers: .command, window: windowA))
        check(remoteEvents == 0 && close(videoA.zoomScale, 1),
              "Cmd+, Cmd− e Cmd0 continuam locais quando zoom está indisponível")

        windowB.makeKeyAndOrderFront(nil)
        windowB.makeFirstResponder(videoB)
        videoB.keyDown(with: keyEvent("+", modifiers: [.command, .shift], window: windowB))
        check(close(videoB.zoomScale, 1.1) && remoteEvents == 0,
              "Cmd+ altera zoom lógico e não vaza Ctrl+ ao Windows")
        videoB.keyDown(with: keyEvent("-", modifiers: .command, window: windowB))
        videoB.keyDown(with: keyEvent("0", modifiers: .command, window: windowB))
        check(close(videoB.zoomScale, 1) && remoteEvents == 0,
              "Cmd− e Cmd0 restauram zoom sem enviar teclas remotas")
        videoB.keyDown(with: keyEvent("=", modifiers: .command, window: windowB))
        check(close(videoB.zoomScale, 1.1) && remoteEvents == 0, "Cmd= continua sendo alias local de Cmd+")
        videoB.keyDown(with: keyEvent("0", modifiers: .command, window: windowB))
        check(videoB.performKeyEquivalent(with: keyEvent("+", modifiers: [.command, .shift], window: windowB)) &&
              close(videoB.zoomScale, 1.1), "performKeyEquivalent encaminha Cmd+ ao responder ativo")

        var pointerActions: [RemotePointerAction] = []
        videoB.send = { event in
            if case .pointer(let action, _, _, _) = event { pointerActions.append(action) }
        }
        videoB.mouseDown(with: mouseEvent(.leftMouseDown, window: windowB,
                                          point: CGPoint(x: videoB.bounds.midX, y: videoB.bounds.midY), view: videoB))
        videoB.resetZoom(nil)
        check(pointerActions.suffix(2).elementsEqual([.down, .up]),
              "zoom libera botão remoto pressionado antes de pedir resize")

        videoA.isHidden = true
        check(!videoA.validateMenuItem(increase) && !videoA.validateMenuItem(decrease) &&
              !videoA.validateMenuItem(reset), "menu desabilita zoom em view oculta")
    }

    private static func checkFitted(_ video: RemoteVideoView, label: String) {
        let frame = video.decoder.displayLayer.frame
        check(frame.width <= video.bounds.width + 0.01 && frame.height <= video.bounds.height + 0.01 &&
              frame.minX >= -0.01 && frame.minY >= -0.01 &&
              frame.maxX <= video.bounds.maxX + 0.01 && frame.maxY <= video.bounds.maxY + 0.01,
              label)
    }

    private static func makeVideo(size: NSSize, image: NSSize) -> (NSWindow, RemoteVideoView) {
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                              styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        let video = RemoteVideoView(frame: NSRect(origin: .zero, size: size))
        window.contentView = video
        window.makeKeyAndOrderFront(nil)
        video.presentPng(makePNG(width: Int(image.width), height: Int(image.height)),
                         width: Int(image.width), height: Int(image.height))
        layout(video)
        return (window, video)
    }

    private static func layout(_ view: NSView) {
        view.needsLayout = true
        view.layoutSubtreeIfNeeded()
    }

    private static func mouseEvent(_ type: NSEvent.EventType, window: NSWindow,
                                   point: CGPoint, view: NSView) -> NSEvent {
        timestamp += 1
        let location = view.convert(point, to: nil)
        return NSEvent.mouseEvent(with: type, location: location, modifierFlags: [], timestamp: timestamp,
                                  windowNumber: window.windowNumber, context: nil,
                                  eventNumber: 1, clickCount: 1, pressure: 1)!
    }

    private static func scrollEvent(deltaX: Int32, deltaY: Int32,
                                    modifiers: NSEvent.ModifierFlags,
                                    window: NSWindow, view: NSView, point: CGPoint) -> NSEvent {
        let event = CGEvent(scrollWheelEvent2Source: nil, units: .pixel, wheelCount: 2,
                            wheel1: deltaY, wheel2: deltaX, wheel3: 0)!
        event.flags = modifiers.contains(.option) ? .maskAlternate : []
        let screenPoint = window.convertPoint(toScreen: view.convert(point, to: nil))
        let mainDisplayHeight = CGFloat(CGDisplayBounds(CGMainDisplayID()).height)
        event.location = CGPoint(x: screenPoint.x, y: mainDisplayHeight - screenPoint.y)
        return NSEvent(cgEvent: event)!
    }

    private static func keyEvent(_ characters: String, modifiers: NSEvent.ModifierFlags,
                                 window: NSWindow) -> NSEvent {
        timestamp += 1
        return NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: modifiers,
                                timestamp: timestamp, windowNumber: window.windowNumber, context: nil,
                                characters: characters, charactersIgnoringModifiers: characters,
                                isARepeat: false, keyCode: 24)!
    }

    private static func makePNG(width: Int, height: Int) -> Data {
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height,
                                      bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                      isPlanar: false, colorSpaceName: .deviceRGB,
                                      bytesPerRow: 0, bitsPerPixel: 0)!
        return bitmap.representation(using: .png, properties: [:])!
    }

    private static func makeJPEG(width: Int, height: Int) -> Data {
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height,
                                      bitsPerSample: 8, samplesPerPixel: 3, hasAlpha: false,
                                      isPlanar: false, colorSpaceName: .deviceRGB,
                                      bytesPerRow: 0, bitsPerPixel: 0)!
        return bitmap.representation(using: .jpeg, properties: [.compressionFactor: 0.8])!
    }

    private static func close(_ lhs: CGFloat, _ rhs: CGFloat) -> Bool { abs(lhs - rhs) < 0.01 }

    private static func check(_ condition: @autoclosure () -> Bool, _ label: String) {
        guard condition() else {
            fputs("FAIL: \(label)\n", stderr)
            exit(1)
        }
    }
}

/// O menu é compilado no harness; os comandos sem vídeo só precisam existir
/// para satisfazer seus selectors.
@MainActor
final class AppDelegate: NSObject {
    @objc func showSettings(_ sender: Any?) {}
    @objc func newConnection(_ sender: Any?) {}
    @objc func openQuickSearch(_ sender: Any?) {}
    @objc func cycleNextWindow(_ sender: Any?) {}
    @objc func cyclePreviousWindow(_ sender: Any?) {}
    @objc func toggleAppBar(_ sender: Any?) {}
}

private enum HarnessFailure: Error {
    case assertion(String)
}
