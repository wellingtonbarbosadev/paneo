import Foundation
import Network
import Security

/// Cliente do protocolo v2 (docs/PROTOCOL.md): TLS com pin SHA-256, handshake
/// por PIN/token de dispositivo e enquadramento binário sem ACK.
///
/// Todo o estado mutável vive em `queue` (serial); todos os callbacks de
/// apresentação são `@MainActor`. O PBKDF2 do PIN (600 mil iterações) roda na
/// `queue`, nunca na thread principal.
final class RemoteConnection: @unchecked Sendable {

    /// Prefixo das recusas de autenticação em `onState(message, false)`. A UI
    /// usa este prefixo para distinguir "o host recusou a credencial" de uma
    /// falha de rede (que deve reconectar com backoff).
    static let authFailurePrefix = "Autenticação recusada"

    /// Hostname curto (sem domínio), usado no `hello` e no nome do dispositivo.
    static var shortHostname: String {
        let full = ProcessInfo.processInfo.hostName
        let short = full.split(separator: ".").first.map(String.init) ?? full
        return short.isEmpty ? "Mac" : short
    }

    private let queue = DispatchQueue(label: "RemoteDesktop.transport")
    private var connection: NWConnection?
    private var reader = FrameReader()
    private var closed = false
    private var authenticated = false
    private var expectedFingerprint: String?
    private var peerFingerprint: String?
    private var pin: String?
    private var deviceToken: Data?
    private var lastSequence: UInt32 = 0
    private var unsupportedCodec: UInt8?
    private var outgoing: [Data] = []
    private var writing = false
    private var heartbeat: DispatchSourceTimer?
    private var lastReceived = Date()
    private var pingStarted: UInt64?

    private let onState: @MainActor @Sendable (String, Bool) -> Void
    private let onFrame: @MainActor @Sendable (Data, Int, Int, UInt32) -> Void
    private let onMedia: @MainActor @Sendable (Data, UInt8, Bool, Int, Int, UInt32) -> Void
    private let onWindows: @MainActor @Sendable ([RemoteWindow]) -> Void
    private let onMonitors: @MainActor @Sendable ([RemoteMonitor]) -> Void
    private let onApps: @MainActor @Sendable ([RemoteApp]) -> Void
    private let onDevicePaired: @MainActor @Sendable (Data, String) -> Void
    private let onRoundTrip: @MainActor @Sendable (Double) -> Void
    private let onNotice: @MainActor @Sendable (String) -> Void
    private let onVideo: @MainActor @Sendable (String, Int, Int) -> Void
    private let onViewport: @MainActor @Sendable (Int, Int, Int, Int) -> Void
    var onClipboard: (@MainActor @Sendable (String) -> Void)?
    var onFileReceived: (@MainActor @Sendable (String) -> Void)?
    var onAudio: (@MainActor @Sendable (Data) -> Void)?

    init(onState: @escaping @MainActor @Sendable (String, Bool) -> Void,
         onFrame: @escaping @MainActor @Sendable (Data, Int, Int, UInt32) -> Void,
         onMedia: @escaping @MainActor @Sendable (Data, UInt8, Bool, Int, Int, UInt32) -> Void = { _, _, _, _, _, _ in },
         onWindows: @escaping @MainActor @Sendable ([RemoteWindow]) -> Void,
         onDevicePaired: @escaping @MainActor @Sendable (Data, String) -> Void,
         onRoundTrip: @escaping @MainActor @Sendable (Double) -> Void,
         onNotice: @escaping @MainActor @Sendable (String) -> Void,
         onVideo: @escaping @MainActor @Sendable (String, Int, Int) -> Void = { _, _, _ in },
         onViewport: @escaping @MainActor @Sendable (Int, Int, Int, Int) -> Void = { _, _, _, _ in },
         onMonitors: @escaping @MainActor @Sendable ([RemoteMonitor]) -> Void = { _ in },
         onApps: @escaping @MainActor @Sendable ([RemoteApp]) -> Void = { _ in }) {
        self.onApps = onApps
        self.onMonitors = onMonitors
        self.onState = onState
        self.onFrame = onFrame
        self.onMedia = onMedia
        self.onWindows = onWindows
        self.onDevicePaired = onDevicePaired
        self.onRoundTrip = onRoundTrip
        self.onNotice = onNotice
        self.onVideo = onVideo
        self.onViewport = onViewport
    }

    // MARK: - Ciclo de vida

    func connect(host: String,
                 port: UInt16,
                 pin: String?,
                 deviceToken: Data?,
                 fingerprint: String?,
                 role: String = "view",
                 windowId: Int64? = nil,
                 monitorId: Int64? = nil) {
        queue.async { [self] in
            guard !closed, connection == nil else { return }

            var savedToken: Data?
            if let deviceToken {
                guard deviceToken.count == ProtocolV2.tokenBytes else {
                    fail("Token de pareamento inválido.")
                    return
                }
                savedToken = deviceToken
            } else {
                guard let pin, PasswordProof.isAcceptablePin(pin) else {
                    fail("Informe o PIN escolhido no Windows.")
                    return
                }
                self.pin = pin
            }

            var expected: String?
            if let fingerprint {
                guard let hex = CertificatePin.normalizedFingerprint(fingerprint) else {
                    fail("Fingerprint do certificado inválido.")
                    return
                }
                expected = hex
            }

            self.deviceToken = savedToken
            expectedFingerprint = expected
            lastReceived = Date()

            guard let nwPort = NWEndpoint.Port(rawValue: port), port != 0 else {
                fail("Porta inválida.")
                return
            }

            let tls = NWProtocolTLS.Options()
            sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
            // Primeira conexão (sem fingerprint salvo): aceita o certificado e
            // registra o hash para o pareamento; havendo fingerprint, o pin é
            // obrigatório. Não existe trust-all permanente.
            sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { [weak self] _, trust, complete in
                guard let self else { complete(false); return }
                let value = sec_trust_copy_ref(trust).takeRetainedValue()
                if let expected = self.expectedFingerprint {
                    complete(CertificatePin.matches(trust: value, expectedSHA256Hex: expected))
                } else {
                    self.peerFingerprint = Self.leafFingerprint(value)
                    complete(true)
                }
            }, queue)

            let nw = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: NWParameters(tls: tls))
            connection = nw
            nw.stateUpdateHandler = { [weak self] state in
                guard let self, !self.closed else { return }
                switch state {
                case .ready:
                    self.lastReceived = Date()
                    self.enqueue(Self.hello(role: role, windowId: windowId, monitorId: monitorId))
                    self.receive()
                case .failed:
                    // Com fingerprint configurado, a falha de TLS quase sempre
                    // é certificado divergente: reconectar em loop não ajuda
                    // e só polui a interface.
                    if self.expectedFingerprint != nil {
                        self.fail("Certificado do host diferente do esperado. Revogue o pareamento no Windows e pareia de novo.")
                    } else {
                        self.fail("Falha na conexão TLS. Confira endereço e porta.")
                    }
                case .cancelled:
                    self.fail("Conexão encerrada.")
                default:
                    break
                }
            }
            nw.start(queue: queue)
            // O PBKDF2 do PIN roda 600 mil iterações (≈3 s neste Mac) e o
            // handshake TLS compete pelo mesmo plano de fundo: 10 s deixavam
            // a autenticação por PIN expirar em máquinas lentas.
            queue.asyncAfter(deadline: .now() + 30) { [weak self] in
                guard let self, !self.authenticated, !self.closed else { return }
                self.fail("Tempo esgotado ao conectar ou autenticar.")
            }
        }
    }

    /// Pede a lista de janelas do PC (sessão de controle).
    func requestWindows() {
        queue.async { [self] in
            guard !closed, authenticated else { return }
            enqueue(OutgoingMessage.list())
        }
    }

    /// Envia texto do clipboard local para o host Windows.
    func sendClipboard(_ text: String) {
        queue.async { [self] in
            guard !closed, authenticated else { return }
            enqueue(OutgoingMessage.clipboard(text))
        }
    }

    /// Lança um aplicativo no host Windows.
    func launchApp(_ app: String) {
        queue.async { [self] in
            guard !closed, authenticated else { return }
            enqueue(OutgoingMessage.launch(app: app))
        }
    }

    /// Ajusta cadência e bitrate adaptativos.
    func sendQuality(fps: Int, bitrate: Int) {
        queue.async { [self] in
            guard !closed, authenticated else { return }
            enqueue(OutgoingMessage.quality(fps: fps, bitrate: bitrate))
        }
    }

    /// Envia arquivo arrastado do Finder em pedaços para o host Windows.
    func sendFile(url: URL) {
        queue.async { [self] in
            guard !closed, authenticated else { return }
            guard let data = try? Data(contentsOf: url) else { return }
            let fileName = url.lastPathComponent
            let chunkSize = 4096
            var offset = 0
            let total = data.count

            if total == 0 {
                enqueue(OutgoingMessage.fileChunk(name: fileName, data: "", isFirst: true, isLast: true))
                return
            }

            while offset < total {
                let nextOffset = min(offset + chunkSize, total)
                let chunk = data.subdata(in: offset..<nextOffset)
                let isFirst = (offset == 0)
                let isLast = (nextOffset == total)
                enqueue(OutgoingMessage.fileChunk(name: fileName, data: chunk.base64EncodedString(), isFirst: isFirst, isLast: isLast))
                offset = nextOffset
            }
        }
    }

    func disconnect() {
        queue.async { [self] in fail("Desconectado.") }
    }

    func send(_ event: RemoteInputEvent) {
        queue.async { [self] in
            guard !closed, authenticated else { return }
            switch event {
            case let .click(x, y, button):
                enqueue(OutgoingMessage.click(x: x, y: y, button: button))
            case let .text(text):
                let value = TextLimiter.truncateUTF16(text)
                if !value.isEmpty { enqueue(OutgoingMessage.text(value)) }
            case let .key(key, ctrl, alt, shift):
                enqueue(OutgoingMessage.key(key, ctrl: ctrl, alt: alt, shift: shift))
            case let .scroll(x, y, delta):
                enqueue(OutgoingMessage.scroll(x: x, y: y, delta: max(-1200, min(1200, delta))))
            case let .pointer(action, x, y, button):
                enqueue(OutgoingMessage.pointer(action: action, x: x, y: y, button: button))
            case .needKeyframe:
                enqueue(OutgoingMessage.requestKeyframe())
            }
        }
    }

    // MARK: - Hello

    /// `{"type":"hello","version":2,"role":"view","windowId":null,...}`.
    /// Montado aqui (e não via `OutgoingMessage.hello`) porque o encode
    /// sintético de `Int64?` omitiria a chave e o contrato exige `null`.
    private static func hello(role: String, windowId: Int64?, monitorId: Int64? = nil) -> Data? {
        let payload: [String: Any] = [
            "type": "hello",
            "version": ProtocolV2.version,
            "role": role,
            "windowId": windowId ?? NSNull(),
            "monitorId": monitorId ?? NSNull(),
            "clientName": "Mac de \(shortHostname)"
        ]
        guard let json = try? JSONSerialization.data(withJSONObject: payload) else { return nil }
        return FrameCodec.control(json)
    }

    private static func leafFingerprint(_ trust: SecTrust) -> String? {
        guard let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
              let leaf = chain.first
        else { return nil }
        return CertificatePin.sha256Hex(ofDER: SecCertificateCopyData(leaf) as Data)
    }

    // MARK: - Saída

    private func enqueue(_ framed: Data?) {
        guard !closed, let framed else { return }
        guard outgoing.count < 64 else {
            Task { @MainActor [onNotice] in onNotice("Conexão lenta; comando descartado.") }
            return
        }
        outgoing.append(framed)
        writeNext()
    }

    private func writeNext() {
        guard !closed, !writing, !outgoing.isEmpty, let connection else { return }
        writing = true
        let data = outgoing.removeFirst()
        connection.send(content: data, completion: .contentProcessed { [weak self] error in
            guard let self, !self.closed else { return }
            self.writing = false
            if error != nil { self.fail("Falha no envio; comandos descartados.") }
            else { self.writeNext() }
        })
    }

    // MARK: - Entrada

    private func receive() {
        guard !closed, let connection else { return }
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, ended, error in
            guard let self, !self.closed else { return }
            if let data, !data.isEmpty {
                self.lastReceived = Date()
                do {
                    for frame in try self.reader.append(data, maxControlBytes: ProtocolV2.maxControlHostToClient) {
                        try self.handle(frame)
                    }
                } catch {
                    self.failOnRead(error)
                    return
                }
            }
            if error != nil || ended {
                self.fail("O host encerrou a conexão.")
            } else {
                self.receive()
            }
        }
    }

    private func failOnRead(_ error: Error) {
        switch error {
        case let protocolError as ProtocolError:
            switch protocolError {
            case .malformed, .zeroLength, .tooLarge:
                fail("Mensagem inválida ou incompatível recebida do host.")
            case let .unsupported(detail):
                fail(detail)
            }
        default:
            fail("Falha ao ler a resposta do host.")
        }
    }

    private func handle(_ frame: IncomingFrame) throws {
        if frame.isControl {
            try handleControl(HostMessage.decode(frame.payload))
            return
        }

        // Mídia: só depois do `ready`; `seq` repetido ou antigo é descartado.
        guard authenticated else { throw ProtocolError.malformed }
        if frame.codec == ProtocolV2.codecPcm {
            let payload = frame.payload
            Task { @MainActor [onAudio] in onAudio?(payload) }
            return
        }
        if frame.codec == ProtocolV2.codecPng
            || frame.codec == ProtocolV2.codecH264
            || frame.codec == ProtocolV2.codecJpeg {
            guard frame.sequence > lastSequence else { return }
            lastSequence = frame.sequence
            unsupportedCodec = nil
            let payload = frame.payload
            let width = Int(frame.width)
            let height = Int(frame.height)
            let sequence = frame.sequence
            let codec = frame.codec
            let keyframe = frame.isKeyframe
            let ptsMs = frame.ptsMs
            Task { @MainActor [onMedia, onFrame] in
                onMedia(payload, codec, keyframe, width, height, ptsMs)
                if codec == ProtocolV2.codecPng { onFrame(payload, width, height, sequence) }
            }
        } else if unsupportedCodec != frame.codec {
            // Aviso único por codec; a sessão continua.
            unsupportedCodec = frame.codec
            Task { @MainActor [onNotice] in onNotice("codec não suportado nesta versão") }
        }
    }

    private func handleControl(_ message: HostMessage) throws {
        switch message {
        case let .challenge(salt, nonce):
            guard !authenticated else { throw ProtocolError.malformed }
            answer(salt: salt, nonce: nonce)
        case let .ready(_, _, token):
            guard !authenticated else { throw ProtocolError.malformed }
            becomeAuthenticated(readyToken: token)
        case let .authFailed(message, retryAfter):
            var text = "\(Self.authFailurePrefix): \(message)"
            if retryAfter > 0 { text += " Aguarde \(retryAfter) s." }
            fail(text)
        case let .windows(list, monitors, apps):
            guard authenticated else { throw ProtocolError.malformed }
            Task { @MainActor [onWindows, onMonitors, onApps] in
                onMonitors(monitors)
                onApps(apps)
                onWindows(list)
            }
        case let .inputRejected(reason):
            Task { @MainActor [onNotice] in onNotice("Windows recusou a entrada: \(reason)") }
        case let .error(reason):
            // Durante o handshake é fatal; depois, apenas informativo (o host
            // normalmente fecha em seguida).
            if authenticated {
                Task { @MainActor [onNotice] in onNotice("Host Windows informou: \(reason)") }
            } else {
                fail(reason)
            }
        case .pong:
            guard let start = pingStarted else { return }
            pingStarted = nil
            let milliseconds = Double(DispatchTime.now().uptimeNanoseconds - start) / 1_000_000
            Task { @MainActor [onRoundTrip] in onRoundTrip(milliseconds) }
        case let .video(codec, width, height):
            // O host anuncia a geometria antes do primeiro quadro: usar isso
            // para ajustar a janela evita esperar o primeiro quadro chegar.
            Task { @MainActor [onVideo] in onVideo(codec, width, height) }
        case let .viewport(x, y, width, height):
            Task { @MainActor [onViewport] in onViewport(x, y, width, height) }
        case let .clipboard(text):
            Task { @MainActor [onClipboard] in onClipboard?(text) }
        case let .fileReceived(name):
            Task { @MainActor [onFileReceived] in onFileReceived?(name) }
        }
    }

    private func answer(salt: Data, nonce: Data) {
        if let deviceToken {
            let proof = PasswordProof.deviceProof(token: deviceToken, nonce: nonce)
            enqueue(OutgoingMessage.auth(mode: "device", proof: proof))
        } else if let pin {
            // PBKDF2-HMAC-SHA256 com 600 mil iterações: executa na queue.
            let key = PasswordProof.deriveKey(pin: pin, salt: salt)
            enqueue(OutgoingMessage.auth(mode: "pin", proof: PasswordProof.pinProof(key: key, nonce: nonce)))
        } else {
            fail("Informe o PIN escolhido no Windows.")
        }
    }

    private func becomeAuthenticated(readyToken: Data?) {
        authenticated = true
        startHeartbeat()
        Task { @MainActor [onState] in onState("Conectado. Clique na imagem para controlar.", true) }
        // Primeira conexão: reporta token + fingerprint para o Chaves; a partir
        // da próxima, o pinning passa a ser obrigatório.
        if expectedFingerprint == nil, let peer = peerFingerprint, let saved = readyToken ?? deviceToken {
            Task { @MainActor [onDevicePaired] in onDevicePaired(saved, peer) }
        }
    }

    private func startHeartbeat() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 2, repeating: 2)
        timer.setEventHandler { [weak self] in
            guard let self, !self.closed else { return }
            if Date().timeIntervalSince(self.lastReceived) > 10 {
                self.fail("Host sem resposta.")
                return
            }
            if self.pingStarted == nil {
                self.pingStarted = DispatchTime.now().uptimeNanoseconds
                self.enqueue(OutgoingMessage.ping())
            }
        }
        heartbeat = timer
        timer.resume()
    }

    private func fail(_ message: String) {
        guard !closed else { return }
        closed = true
        authenticated = false
        outgoing.removeAll()
        reader = FrameReader()
        heartbeat?.cancel()
        heartbeat = nil
        connection?.stateUpdateHandler = nil
        connection?.cancel()
        connection = nil
        Task { @MainActor [onState] in onState(message, false) }
    }
}
