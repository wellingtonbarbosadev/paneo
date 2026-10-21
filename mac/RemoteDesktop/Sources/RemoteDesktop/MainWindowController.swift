import AppKit

@MainActor
final class MainWindowController: NSObject, NSWindowDelegate {
    let window: NSWindow
    var onClose: (() -> Void)?
    var onReady: (() -> Void)?
    private let address = NSTextField(string: "")
    private let port = NSTextField(string: "9443")
    private let fingerprint = NSTextField(string: "")
    private let token = NSSecureTextField(string: "")
    private let status = NSTextField(wrappingLabelWithString: "Informe os dados exibidos no host Windows.")
    private let canvas = RemoteCanvas()
    private let metrics = NSTextField(labelWithString: "Sem métricas nesta sessão.")
    private var sampleStart = ProcessInfo.processInfo.systemUptime
    private var sampleFrames = 0
    private var sampleBytes = 0
    private var roundTrip: Double?
    private let button = NSButton(title: "Conectar", target: nil, action: nil)
    private var connection: RemoteConnection?
    private var generation = UUID()
    private var wantsConnection = false
    private var retryCount = 0
    private var retryTask: Task<Void, Never>?
    private let remember = NSButton(checkboxWithTitle: "Salvar pareamento no Chaves do Mac", target: nil, action: nil)

    init(initialPairing: SavedPairing? = nil, savePairing: Bool = true) {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 900, height: 720), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        super.init()
        window.title = "RemoteDesktop — janela Windows"; window.minSize = NSSize(width: 640, height: 480)
        window.isReleasedWhenClosed = false; window.delegate = self
        let stack = NSStackView(); stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 8
        stack.translatesAutoresizingMaskIntoConstraints = false
        let content = NSView(); content.addSubview(stack); window.contentView = content
        NSLayoutConstraint.activate([stack.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 16), stack.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -16), stack.topAnchor.constraint(equalTo: content.topAnchor, constant: 16), stack.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -16)])
        for (label, field) in [("Windows (IP ou nome)", address), ("Porta", port), ("SHA-256 do certificado", fingerprint), ("Token de pareamento", token)] {
            let title = NSTextField(labelWithString: label); title.widthAnchor.constraint(equalToConstant: 170).isActive = true
            let row = NSStackView(views: [title, field]); row.orientation = .horizontal
            stack.addArrangedSubview(row); row.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
            field.setAccessibilityLabel(label)
        }
        address.placeholderString = "192.168.0.100"; fingerprint.placeholderString = "64 dígitos exibidos no Windows"
        button.target = self; button.action = #selector(toggle)
        remember.state = savePairing ? .on : .off
        stack.addArrangedSubview(remember)
        let forget = NSButton(title: "Esquecer pareamento neste Mac", target: self, action: #selector(forgetPairing))
        stack.addArrangedSubview(forget)
        stack.addArrangedSubview(button); stack.addArrangedSubview(status); stack.addArrangedSubview(metrics); stack.addArrangedSubview(canvas)
        status.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        canvas.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        canvas.heightAnchor.constraint(greaterThanOrEqualToConstant: 200).isActive = true
        canvas.setContentHuggingPriority(.init(1), for: .vertical)
        let note = NSTextField(wrappingLabelWithString: "PNG/TLS: arraste e composição de texto pelo layout do Mac. Control envia Ctrl; Command fica local. Sair da janela suspende o controle; voltar reconecta. Revogue o acesso no Windows.")
        stack.addArrangedSubview(note); note.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        canvas.send = { [weak self] event in self?.connection?.send(event) }
        if let saved = initialPairing ?? (savePairing ? PairingStore.load() : nil) {
            address.stringValue = saved.host; port.stringValue = saved.port
            fingerprint.stringValue = saved.fingerprint; token.stringValue = saved.token
        }
        window.center()
    }
    func showWindow() { window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true) }
    func startConnection() { if !wantsConnection { toggle() } }
    @objc private func toggle() {
        if wantsConnection { wantsConnection = false; disconnect(); return }
        wantsConnection = true; retryCount = 0; connect()
    }
    private func connect() {
        let problems = InputValidation.problems(address: address.stringValue, port: port.stringValue)
        guard problems.isEmpty, let number = UInt16(port.stringValue.trimmingCharacters(in: .whitespaces)),
              let pin = CertificatePin.normalizedFingerprint(fingerprint.stringValue), !token.stringValue.isEmpty else {
            wantsConnection = false; status.stringValue = "Confira endereço, porta, SHA-256 (64 dígitos) e token."; return
        }
        let saved = SavedPairing(host: address.stringValue, port: port.stringValue, fingerprint: pin, token: token.stringValue)
        generation = UUID(); let current = generation
        sampleStart = ProcessInfo.processInfo.systemUptime; sampleFrames = 0; sampleBytes = 0; roundTrip = nil
        let client = RemoteConnection(onState: { [weak self] message, ready in
            guard let self, self.generation == current else { return }
            self.status.stringValue = message
            if ready {
                self.retryCount = 0
                if self.remember.state == .on && !PairingStore.save(saved) { self.status.stringValue = "Conectado; não foi possível salvar no Chaves." }
                self.onReady?()
            } else {
                self.canvas.resetInput(); self.connection = nil
                self.scheduleReconnect()
            }
        }, onFrame: { [weak self] png, width, height, id in
            guard let self, self.generation == current, self.connection != nil else { return }
            guard self.canvas.present(png, width: width, height: height) else { self.disconnect(); self.status.stringValue = "Imagem inválida."; return }
            self.connection?.acknowledge(id)
            self.sampleFrames += 1; self.sampleBytes += png.count
            let elapsed = ProcessInfo.processInfo.systemUptime - self.sampleStart
            if elapsed >= 2 {
                let rtt = self.roundTrip.map { String(format: "%.0f ms", $0) } ?? "—"
                self.metrics.stringValue = String(format: "%.1f FPS apresentados · %.2f MB/s PNG · RTT %@ (não input-to-photon)", Double(self.sampleFrames) / elapsed, Double(self.sampleBytes) / elapsed / 1_000_000, rtt)
                self.sampleStart = ProcessInfo.processInfo.systemUptime; self.sampleFrames = 0; self.sampleBytes = 0
            }
        }, onRoundTrip: { [weak self] milliseconds in
            guard let self, self.generation == current else { return }
            self.roundTrip = milliseconds
        })
        connection = client; button.title = "Desconectar"; status.stringValue = "Conectando…"
        let host = address.stringValue.trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        client.connect(host: host, port: number, fingerprint: pin, token: token.stringValue)
    }
    private func disconnect() {
        retryTask?.cancel(); retryTask = nil
        canvas.resetInput()
        generation = UUID(); connection?.disconnect(); connection = nil
        button.title = "Conectar"; status.stringValue = "Desconectado."
    }
    private func scheduleReconnect() {
        guard wantsConnection, window.isKeyWindow, retryCount < 5 else {
            wantsConnection = false; button.title = "Conectar"; return
        }
        let delay = 1 << retryCount; retryCount += 1
        button.title = "Cancelar reconexão"
        status.stringValue += " Nova tentativa em \(delay)s (\(retryCount)/5)."
        retryTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled, let self, self.wantsConnection, self.window.isKeyWindow else { return }
            self.connect()
        }
    }
    @objc private func forgetPairing() {
        wantsConnection = false; disconnect()
        if PairingStore.forget() { token.stringValue = ""; fingerprint.stringValue = ""; status.stringValue = "Pareamento local removido. Para revogar acesso, use o host Windows." }
        else { status.stringValue = "Não foi possível remover o pareamento do Chaves." }
    }
    func windowDidResignKey(_ notification: Notification) { disconnect() }
    func windowDidBecomeKey(_ notification: Notification) { if wantsConnection && connection == nil { retryCount = 0; connect() } }
    func windowWillClose(_ notification: Notification) { wantsConnection = false; disconnect(); onClose?() }
}
