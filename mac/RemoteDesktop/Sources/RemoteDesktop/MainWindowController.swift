import AppKit

/// Dados de conexão injetados pelos testes de fumaça, sem tocar no Chaves.
struct ConnectionPreset: Sendable {
    let host: String
    let port: String
    let pin: String
    let fingerprint: String?
}

/// Janela principal do Mac: conectar ao PC com IP + PIN, ver a lista de janelas
/// do PC e abrir cada uma em janela remota própria (Fases 3 e 4).
@MainActor
final class MainWindowController: NSObject, NSWindowDelegate, NSTableViewDataSource, NSTableViewDelegate {

    let window: NSWindow
    var onClose: (() -> Void)?
    var onReady: (() -> Void)?

    private let address = NSTextField(string: "")
    private let port = NSTextField(string: "9443")
    private let pin = NSSecureTextField(string: "")
    private let status = NSTextField(wrappingLabelWithString: "Informe o IP do Windows e o PIN escolhido no Windows.")
    private let button = NSButton(title: "Conectar", target: nil, action: nil)
    private let table = NSTableView()
    private let emptyLabel = NSTextField(labelWithString: "Conecte para ver as janelas deste PC.")

    private var connection: RemoteConnection?
    private var generation = UUID()
    private var wantsConnection = false
    private var retryCount = 0
    private var retryTask: Task<Void, Never>?
    private var refreshTask: Task<Void, Never>?
    private var usingDeviceToken = false
    private var windows: [RemoteWindow] = []
    private var openWindows: [Int64: RemoteWindowController] = [:]
    private let preset: ConnectionPreset?
    private let persistPairing: Bool

    init(preset: ConnectionPreset? = nil, persistPairing: Bool = true) {
        self.preset = preset
        self.persistPairing = persistPairing
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 720, height: 560),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered,
                          defer: false)
        super.init()
        window.title = "RemoteDesktop"
        window.minSize = NSSize(width: 520, height: 420)
        window.isReleasedWhenClosed = false
        window.delegate = self

        let content = NSView()
        window.contentView = content

        // Painel de vidro com os dados de conexão.
        let card = Glass.panel(cornerRadius: 20)
        card.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(card)

        address.placeholderString = "192.168.0.100"
        pin.placeholderString = "PIN escolhido no Windows"
        button.target = self
        button.action = #selector(toggle)
        Glass.style(button)

        address.setAccessibilityLabel("Endereço IP do Windows")
        port.setAccessibilityLabel("Porta")
        pin.setAccessibilityLabel("PIN escolhido no Windows")

        let addressRow = NSStackView(views: [address, port])
        let pinRow = NSStackView(views: [pin, button])
        for row in [addressRow, pinRow] {
            row.orientation = .horizontal
            row.spacing = 8
            row.alignment = .centerY
            for field in row.arrangedSubviews {
                row.setVisibilityPriority(.mustHold, for: field)
            }
        }
        let fields = NSStackView(views: [
            Glass.caption("Endereço IP do Windows · Porta"), addressRow,
            Glass.caption("PIN escolhido no Windows"), pinRow
        ])
        fields.orientation = .vertical
        fields.spacing = 8
        fields.alignment = .leading
        for field in fields.arrangedSubviews {
            fields.setVisibilityPriority(.mustHold, for: field)
        }
        fields.translatesAutoresizingMaskIntoConstraints = false
        card.addSubview(fields)

        status.font = .systemFont(ofSize: 12)
        status.textColor = .secondaryLabelColor
        status.translatesAutoresizingMaskIntoConstraints = false
        card.addSubview(status)

        // Lista de janelas do PC.
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("janela"))
        column.title = "Janelas deste PC"
        column.width = 460
        table.addTableColumn(column)
        table.dataSource = self
        table.delegate = self
        table.headerView = nil
        table.rowHeight = 26
        table.style = .inset
        table.doubleAction = #selector(openSelectedWindow)
        table.target = self
        table.action = #selector(openSelectedWindow)
        table.translatesAutoresizingMaskIntoConstraints = false

        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.documentView = table
        scroll.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(scroll)

        emptyLabel.font = .systemFont(ofSize: 13)
        emptyLabel.textColor = .tertiaryLabelColor
        emptyLabel.alignment = .center
        emptyLabel.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(emptyLabel)

        NSLayoutConstraint.activate([
            addressRow.widthAnchor.constraint(equalTo: fields.widthAnchor),
            pinRow.widthAnchor.constraint(equalTo: fields.widthAnchor),
            port.widthAnchor.constraint(equalToConstant: 64),
            address.widthAnchor.constraint(greaterThanOrEqualToConstant: 180),
            pin.widthAnchor.constraint(greaterThanOrEqualToConstant: 180),
            card.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 16),
            card.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -16),
            card.topAnchor.constraint(equalTo: content.topAnchor, constant: 16),
            fields.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 16),
            fields.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -16),
            fields.topAnchor.constraint(equalTo: card.topAnchor, constant: 16),
            status.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 16),
            status.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -16),
            status.topAnchor.constraint(equalTo: fields.bottomAnchor, constant: 10),
            status.bottomAnchor.constraint(equalTo: card.bottomAnchor, constant: -14),
            scroll.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 16),
            scroll.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -16),
            scroll.topAnchor.constraint(equalTo: card.bottomAnchor, constant: 16),
            scroll.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -16),
            emptyLabel.centerXAnchor.constraint(equalTo: scroll.centerXAnchor),
            emptyLabel.centerYAnchor.constraint(equalTo: scroll.centerYAnchor)
        ])

        prefill()
        window.center()
    }

    // MARK: - Início

    private func prefill() {
        if let preset {
            address.stringValue = preset.host
            port.stringValue = preset.port
            pin.stringValue = preset.pin
            status.stringValue = "Conectando com os dados do teste."
            return
        }
        guard let last = PairingStore.loadLast() else { return }
        address.stringValue = last.host
        port.stringValue = last.port
        if !last.deviceToken.isEmpty {
            status.stringValue = "Mac já pareado com este PC."
        }
    }

    func showWindow() {
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func startConnection() {
        if !wantsConnection { toggle() }
    }

    @objc private func toggle() {
        if wantsConnection {
            wantsConnection = false
            disconnect()
            return
        }
        wantsConnection = true
        retryCount = 0
        connect()
    }

    // MARK: - Conexão de controle

    private func connect() {
        let problems = InputValidation.problems(address: address.stringValue, port: port.stringValue)
        guard problems.isEmpty else {
            wantsConnection = false
            status.stringValue = "Confira os dados: " + problems.joined(separator: "; ")
            return
        }
        let host = normalizedHost()
        let portText = port.stringValue.trimmingCharacters(in: .whitespaces)
        guard let number = UInt16(portText), number != 0 else {
            wantsConnection = false
            status.stringValue = "Confira a porta."
            return
        }

        let creds = credentials(host: host, port: portText)
        var pinValue: String?
        if creds.token != nil {
            usingDeviceToken = true
        } else {
            usingDeviceToken = false
            guard PasswordProof.isAcceptablePin(pin.stringValue) else {
                wantsConnection = false
                status.stringValue = "Informe o PIN escolhido no Windows (mínimo de 6 caracteres)."
                return
            }
            pinValue = pin.stringValue
        }

        generation = UUID()
        let current = generation

        let client = RemoteConnection(
            onState: { [weak self] message, ready in
                guard let self, self.generation == current else { return }
                self.status.stringValue = message
                if ready {
                    self.retryCount = 0
                    self.startRefresh()
                    self.onReady?()
                } else {
                    self.connection = nil
                    self.stopRefresh()
                    self.handleFailure(message)
                }
            },
            onFrame: { _, _, _, _ in },
            onMedia: { _, _, _, _, _, _ in },
            onWindows: { [weak self] list in
                guard let self, self.generation == current else { return }
                self.updateWindows(list)
            },
            onDevicePaired: { [weak self] token, fingerprint in
                guard let self, self.generation == current else { return }
                self.savePairing(token: token, fingerprint: fingerprint, host: host, port: portText)
            },
            onRoundTrip: { _ in },
            onNotice: { [weak self] message in
                guard let self, self.generation == current else { return }
                self.status.stringValue = message
            }
        )

        connection = client
        button.title = "Desconectar"
        status.stringValue = "Conectando…"
        client.connect(host: host, port: number, pin: pinValue, deviceToken: creds.token, fingerprint: creds.fingerprint, role: "control")
    }

    /// Pede a lista de janelas periodicamente para refletir o que está aberto no PC.
    private func startRefresh() {
        stopRefresh()
        refreshTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(2))
                guard !Task.isCancelled, let self, self.connection != nil else { return }
                self.connection?.requestWindows()
            }
        }
    }

    private func stopRefresh() {
        refreshTask?.cancel()
        refreshTask = nil
    }

    private func updateWindows(_ list: [RemoteWindow]) {
        windows = list
        table.reloadData()
        emptyLabel.isHidden = !list.isEmpty
        if list.isEmpty {
            status.stringValue = "Este PC não tem janelas visíveis no momento."
        }
    }

    // MARK: - Abertura de janela remota

    @objc private func openSelectedWindow() {
        let row = table.selectedRow
        guard row >= 0, row < windows.count else { return }
        open(window: windows[row])
    }

    private func open(window target: RemoteWindow) {
        if openWindows[target.id] != nil {
            NSApp.activate(ignoringOtherApps: true)
            return
        }

        let host = normalizedHost()
        let portText = port.stringValue.trimmingCharacters(in: .whitespaces)
        guard let number = UInt16(portText) else { return }
        let creds = credentials(host: host, port: portText)

        let controller = RemoteWindowController(target: target,
                                                host: host,
                                                port: number,
                                                pin: creds.token == nil ? pin.stringValue : nil,
                                                deviceToken: creds.token,
                                                fingerprint: creds.fingerprint)
        controller.onClose = { [weak self] in
            self?.openWindows.removeValue(forKey: target.id)
        }
        openWindows[target.id] = controller
        controller.showWindow()
        controller.start()
    }

    // MARK: - NSTableViewDataSource

    func numberOfRows(in tableView: NSTableView) -> Int { windows.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard row < windows.count else { return nil }
        let item = windows[row]
        let identifier = NSUserInterfaceItemIdentifier("janela")
        let cell = tableView.makeView(withIdentifier: identifier, owner: self) as? NSTableCellView
            ?? makeCell(identifier: identifier)
        cell.textField?.stringValue = item.title.isEmpty ? item.process : item.title
        cell.textField?.toolTip = "\(item.process) — \(item.width)×\(item.height)"
        return cell
    }

    private func makeCell(identifier: NSUserInterfaceItemIdentifier) -> NSTableCellView {
        let cell = NSTableCellView()
        cell.identifier = identifier
        let text = NSTextField(labelWithString: "")
        text.lineBreakMode = .byTruncatingTail
        text.translatesAutoresizingMaskIntoConstraints = false
        cell.addSubview(text)
        cell.textField = text
        NSLayoutConstraint.activate([
            text.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 4),
            text.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -4),
            text.centerYAnchor.constraint(equalTo: cell.centerYAnchor)
        ])
        return cell
    }

    // MARK: - Pareamento

    private func credentials(host: String, port: String) -> (fingerprint: String?, token: Data?) {
        var fingerprint: String?
        var token: Data?
        if persistPairing, let record = PairingStore.load(host: host, port: port) {
            fingerprint = record.fingerprint.isEmpty ? nil : (CertificatePin.normalizedFingerprint(record.fingerprint) ?? record.fingerprint)
            token = record.deviceToken.isEmpty ? nil : Data(base64Encoded: record.deviceToken)
        }
        if fingerprint == nil, let preset, preset.host == host, preset.port == port, let raw = preset.fingerprint {
            fingerprint = CertificatePin.normalizedFingerprint(raw) ?? raw
        }
        return (fingerprint, token)
    }

    private func savePairing(token: Data, fingerprint: String, host: String, port: String) {
        guard persistPairing else { return }
        let record = HostRecord(host: host, port: port, fingerprint: fingerprint,
                                deviceToken: token.base64EncodedString(),
                                name: RemoteConnection.shortHostname)
        if !PairingStore.save(record) {
            status.stringValue = "Conectado; não foi possível salvar no Chaves do Mac."
        }
    }

    private func handleFailure(_ message: String) {
        // Certificado divergente: reconectar em loop não resolve e só polui.
        if message.hasPrefix("Certificado do host diferente") {
            wantsConnection = false
            button.title = "Conectar"
            status.stringValue = message
            return
        }
        guard message.hasPrefix(RemoteConnection.authFailurePrefix) else {
            scheduleReconnect()
            return
        }
        wantsConnection = false
        button.title = "Conectar"
        guard usingDeviceToken else { return }
        usingDeviceToken = false
        if persistPairing { dropSavedToken() }
        status.stringValue = "Pareamento revogado no Windows. Informe o PIN para reconectar."
    }

    private func dropSavedToken() {
        let host = normalizedHost()
        let portText = port.stringValue.trimmingCharacters(in: .whitespaces)
        guard let record = PairingStore.load(host: host, port: portText), !record.deviceToken.isEmpty else { return }
        let updated = HostRecord(host: record.host, port: record.port, fingerprint: record.fingerprint,
                                 deviceToken: "", name: record.name)
        _ = PairingStore.save(updated)
    }

    private func scheduleReconnect() {
        // Reconecta mesmo com o app em segundo plano: a sessão continua válida.
        guard wantsConnection, retryCount < 5 else {
            wantsConnection = false
            button.title = "Conectar"
            return
        }
        let delay = 1 << retryCount
        retryCount += 1
        button.title = "Cancelar reconexão"
        status.stringValue += " Nova tentativa em \(delay)s (\(retryCount)/5)."
        retryTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled, let self, self.wantsConnection else { return }
            self.connect()
        }
    }

    private func disconnect() {
        retryTask?.cancel(); retryTask = nil
        stopRefresh()
        generation = UUID()
        connection?.disconnect()
        connection = nil
        button.title = "Conectar"
        status.stringValue = "Desconectado."
    }

    private func normalizedHost() -> String {
        address.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            .trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
    }

    // MARK: - NSWindowDelegate

    func windowDidResignKey(_ notification: Notification) { }
    func windowDidBecomeKey(_ notification: Notification) {
        if wantsConnection && connection == nil {
            retryCount = 0
            connect()
        }
    }
    func windowWillClose(_ notification: Notification) {
        wantsConnection = false
        disconnect()
        for controller in openWindows.values { controller.window.close() }
        openWindows.removeAll()
        onClose?()
    }
}
