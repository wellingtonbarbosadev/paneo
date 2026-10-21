import Foundation
import Network
import Security

/// Todo o estado mutável é isolado em queue. Callbacks de apresentação são MainActor.
final class RemoteConnection: @unchecked Sendable {
    private let queue = DispatchQueue(label: "RemoteDesktop.transport")
    private var connection: NWConnection?
    private var decoder = FrameDecoder()
    private var closed = false
    private var authenticated = false
    private var pendingFrame: Int?
    private var lastFrame = 0
    private var outgoing: [Data] = []
    private var writing = false
    private var heartbeat: DispatchSourceTimer?
    private var lastReceived = Date()
    private var pingStarted: UInt64?
    private let onRoundTrip: @MainActor @Sendable (Double) -> Void
    private let onState: @MainActor @Sendable (String, Bool) -> Void
    private let onFrame: @MainActor @Sendable (Data, Int, Int, Int) -> Void

    init(onState: @escaping @MainActor @Sendable (String, Bool) -> Void,
          onFrame: @escaping @MainActor @Sendable (Data, Int, Int, Int) -> Void,
          onRoundTrip: @escaping @MainActor @Sendable (Double) -> Void = { _ in }) {
        self.onState = onState; self.onFrame = onFrame; self.onRoundTrip = onRoundTrip
    }
    func connect(host: String, port: UInt16, fingerprint: String, token: String) {
        queue.async { [self] in
            guard !closed, connection == nil else { return }
            let tls = NWProtocolTLS.Options()
            sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
            sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, trust, complete in
                let value = sec_trust_copy_ref(trust).takeRetainedValue()
                complete(CertificatePin.matches(trust: value, expectedSHA256Hex: fingerprint))
            }, queue)
            let nw = NWConnection(host: NWEndpoint.Host(host), port: NWEndpoint.Port(rawValue: port)!, using: NWParameters(tls: tls))
            connection = nw
            nw.stateUpdateHandler = { [weak self] state in
                guard let self, !self.closed else { return }
                switch state {
                case .ready:
                    self.enqueue(OutgoingMessage.auth(token: token))
                    self.receive()
                case .failed: self.fail("Falha na conexão TLS. Confira endereço, porta e certificado.")
                case .cancelled: self.fail("Conexão encerrada.")
                default: break
                }
            }
            nw.start(queue: queue)
            queue.asyncAfter(deadline: .now() + 10) { [weak self] in
                guard let self, !self.authenticated, !self.closed else { return }
                self.fail("Tempo esgotado ao conectar ou autenticar.")
            }
        }
    }
    func disconnect() { queue.async { [self] in fail("Desconectado.") } }
    func acknowledge(_ id: Int) {
        queue.async { [self] in
            guard !closed, authenticated, pendingFrame == id else { return }
            pendingFrame = nil
            enqueue(OutgoingMessage.ack(id: id))
        }
    }
    func send(_ event: RemoteInputEvent) {
        queue.async { [self] in
            guard !closed, authenticated else { return }
            switch event {
            case let .click(x, y, button): enqueue(OutgoingMessage.click(x: x, y: y, button: button))
            case let .text(text):
                let value = TextLimiter.truncateUTF16(text)
                if !value.isEmpty { enqueue(OutgoingMessage.text(value)) }
            case let .key(key, ctrl, alt, shift): enqueue(OutgoingMessage.key(key, ctrl: ctrl, alt: alt, shift: shift))
            case let .scroll(x, y, delta): enqueue(OutgoingMessage.scroll(x: x, y: y, delta: max(-1200, min(1200, delta))))
            case let .pointer(action, x, y, button): enqueue(OutgoingMessage.pointer(action: action, x: x, y: y, button: button))
            }
        }
    }
    private func enqueue(_ payload: Data?) {
        guard !closed, let payload, let framed = FrameCodec.encode(payload: payload) else { return }
        guard outgoing.count < 64 else { fail("Conexão lenta; entrada pendente descartada."); return }
        outgoing.append(framed); writeNext()
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
    private func receive() {
        guard !closed else { return }
        connection?.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, ended, error in
            guard let self, !self.closed else { return }
            do {
                if let data {
                    for payload in try self.decoder.append(data) { try self.handle(payload) }
                }
                if error != nil || ended { self.fail("O host encerrou a conexão.") }
                else { self.receive() }
            } catch { self.fail("Mensagem inválida ou incompatível recebida do host.") }
        }
    }
    private func handle(_ data: Data) throws {
        let message = try JSONDecoder().decode(IncomingMessage.self, from: data)
        lastReceived = Date()
        if !authenticated {
            guard message.type == "ready", message.version == 1 else { throw FramingError.malformed }
            authenticated = true
            Task { @MainActor [onState] in onState("Conectado. Clique na imagem para controlar.", true) }
            let timer = DispatchSource.makeTimerSource(queue: queue)
            timer.schedule(deadline: .now() + 2, repeating: 2)
            timer.setEventHandler { [weak self] in
                guard let self, !self.closed else { return }
                if Date().timeIntervalSince(self.lastReceived) > 10 { self.fail("Host sem resposta."); return }
                if self.pingStarted == nil {
                    self.pingStarted = DispatchTime.now().uptimeNanoseconds
                    self.enqueue(OutgoingMessage.ping())
                }
            }
            heartbeat = timer; timer.resume()
            return
        }
        switch message.type {
        case "pong":
            if let start = pingStarted {
                let milliseconds = Double(DispatchTime.now().uptimeNanoseconds - start) / 1_000_000
                pingStarted = nil
                Task { @MainActor [onRoundTrip] in onRoundTrip(milliseconds) }
            }
        case "frame":
            guard pendingFrame == nil, let id = message.id, id > lastFrame,
                  let width = message.width, let height = message.height,
                  width > 0, height > 0, width <= 8192, height <= 8192, width * height <= 33_554_432,
                  let encoded = message.png, let png = Data(base64Encoded: encoded) else { throw FramingError.malformed }
            pendingFrame = id; lastFrame = id
            Task { @MainActor [onFrame] in onFrame(png, width, height, id) }
        default: throw FramingError.malformed
        }
    }
    private func fail(_ message: String) {
        guard !closed else { return }
        closed = true; authenticated = false; outgoing.removeAll(); decoder = FrameDecoder()
        heartbeat?.cancel(); heartbeat = nil
        connection?.stateUpdateHandler = nil; connection?.cancel(); connection = nil
        Task { @MainActor [onState] in onState(message, false) }
    }
}
