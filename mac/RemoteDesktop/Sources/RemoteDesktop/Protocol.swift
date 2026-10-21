import CryptoKit
import Foundation

/// Contrato do protocolo v2 (docs/PROTOCOL.md). Sem AppKit: enquadramento,
/// mensagens e provas de autenticação ficam testáveis isoladamente.
enum ProtocolV2 {
    static let version = 2
    static let maxMessageBytes = 16 * 1024 * 1024
    static let maxControlClientToHost = 8 * 1024
    static let maxControlHostToClient = 64 * 1024

    static let kindControl: UInt8 = 1
    static let kindMedia: UInt8 = 2

    static let codecH264: UInt8 = 1
    static let codecJpeg: UInt8 = 2
    static let codecPng: UInt8 = 3

    /// tipo(1) + codec(1) + flags(2) + seq(4) + ptsMs(4) + largura(2) + altura(2).
    static let mediaHeaderBytes = 16
    static let flagKeyframe: UInt16 = 1

    static let pinIterations: UInt32 = 600_000
    static let saltBytes = 32
    static let proofBytes = 32
    static let tokenBytes = 32
    static let pinMinLength = 6
    static let pinMaxLength = 128
}

enum ProtocolError: Error {
    case zeroLength
    case tooLarge
    case malformed
    case unsupported(String)

    var safeDescription: String {
        switch self {
        case .zeroLength: return "mensagem de comprimento zero"
        case .tooLarge: return "mensagem maior que o limite"
        case .malformed: return "mensagem inválida ou incompatível"
        case let .unsupported(detail): return detail
        }
    }
}

// MARK: - Enquadramento

enum FrameCodec {

    /// u32 big endian + tipo + corpo.
    static func control(_ json: Data) -> Data? {
        guard !json.isEmpty, json.count <= ProtocolV2.maxMessageBytes - 1 else { return nil }
        return frame(kind: ProtocolV2.kindControl, body: json)
    }

    static func media(codec: UInt8, keyframe: Bool, sequence: UInt32, ptsMs: UInt32,
                      width: UInt16, height: UInt16, payload: Data) -> Data? {
        guard !payload.isEmpty, payload.count + ProtocolV2.mediaHeaderBytes <= ProtocolV2.maxMessageBytes else { return nil }
        var body = Data(capacity: ProtocolV2.mediaHeaderBytes + payload.count)
        body.append(codec)
        appendBigEndian(keyframe ? ProtocolV2.flagKeyframe : 0, to: &body)
        appendBigEndian(sequence, to: &body)
        appendBigEndian(ptsMs, to: &body)
        appendBigEndian(width, to: &body)
        appendBigEndian(height, to: &body)
        body.append(payload)
        return frame(kind: ProtocolV2.kindMedia, body: body)
    }

    private static func frame(kind: UInt8, body: Data) -> Data? {
        let length = body.count + 1
        guard length > 0, length <= ProtocolV2.maxMessageBytes else { return nil }
        var framed = Data(capacity: 4 + length)
        appendBigEndian(UInt32(length), to: &framed)
        framed.append(kind)
        framed.append(body)
        return framed
    }

    private static func appendBigEndian<T: FixedWidthInteger>(_ value: T, to data: inout Data) {
        withUnsafeBytes(of: value.bigEndian) { data.append(contentsOf: $0) }
    }
}

struct IncomingFrame {
    let kind: UInt8
    let codec: UInt8
    let flags: UInt16
    let sequence: UInt32
    let ptsMs: UInt32
    let width: UInt16
    let height: UInt16
    let payload: Data

    var isControl: Bool { kind == ProtocolV2.kindControl }
    var isKeyframe: Bool { flags & ProtocolV2.flagKeyframe != 0 }
}

/// Decodificador incremental: aceita pedaços arbitrários de TCP.
struct FrameReader {

    private var buffer = Data()

    mutating func append(_ chunk: Data, maxControlBytes: Int) throws -> [IncomingFrame] {
        buffer.append(chunk)
        var frames: [IncomingFrame] = []

        while buffer.count >= 4 {
            let header = [UInt8](buffer.prefix(4))
            let length = (UInt32(header[0]) << 24) | (UInt32(header[1]) << 16) | (UInt32(header[2]) << 8) | UInt32(header[3])

            guard length > 0 else { throw ProtocolError.zeroLength }
            guard length <= UInt32(ProtocolV2.maxMessageBytes) else { throw ProtocolError.tooLarge }

            let total = 4 + Int(length)
            guard buffer.count >= total else { break }

            // Índices do Data podem não começar em zero após removeFirst.
            let body = buffer.subdata(in: buffer.startIndex + 4 ..< buffer.startIndex + total)
            buffer.removeFirst(total)
            frames.append(try Self.parse(body, maxControlBytes: maxControlBytes))
        }

        guard buffer.count <= ProtocolV2.maxMessageBytes + 4 else { throw ProtocolError.malformed }
        return frames
    }

    private static func parse(_ body: Data, maxControlBytes: Int) throws -> IncomingFrame {
        guard let kind = body.first else { throw ProtocolError.malformed }
        if kind == ProtocolV2.kindControl {
            let payload = Data(body.dropFirst())
            guard payload.count <= maxControlBytes else { throw ProtocolError.tooLarge }
            return IncomingFrame(kind: kind, codec: 0, flags: 0, sequence: 0, ptsMs: 0,
                                 width: 0, height: 0, payload: payload)
        }
        // Mídia precisa de cabeçalho completo e pelo menos 1 byte de carga:
        // sem isso um frame truncado leria fora dos limites e derrubaria o app.
        guard kind == ProtocolV2.kindMedia, body.count >= ProtocolV2.mediaHeaderBytes + 1 else {
            throw ProtocolError.malformed
        }
        // O corpo inclui o byte de tipo: tipo(1) codec(1) flags(2) seq(4) pts(4) w(2) h(2).
        let bytes = [UInt8](body)
        func read<T: FixedWidthInteger>(_ range: Range<Int>) -> T {
            var value: T = 0
            for index in range { value = (value << 8) | T(bytes[index]) }
            return value
        }
        return IncomingFrame(
            kind: kind,
            codec: bytes[1],
            flags: read(2..<4),
            sequence: read(4..<8),
            ptsMs: read(8..<12),
            width: read(12..<14),
            height: read(14..<16),
            payload: Data(bytes[ProtocolV2.mediaHeaderBytes...]))
    }
}

// MARK: - Mensagens do host

struct RemoteWindow: Decodable, Identifiable, Sendable {
    let id: Int64
    let title: String
    let process: String
    let width: Int
    let height: Int
}

enum HostMessage {
    case challenge(salt: Data, nonce: Data)
    case ready(role: String, host: String?, deviceToken: Data?)
    case authFailed(message: String, retryAfter: Int)
    case windows([RemoteWindow])
    case inputRejected(String)
    case error(String)
    case pong
    case video(codec: String, width: Int, height: Int)

    private struct Wire: Decodable {
        let type: String
        let version: Int?
        let role: String?
        let host: String?
        let deviceToken: String?
        let salt: String?
        let nonce: String?
        let windows: [RemoteWindow]?
        let message: String?
        let retryAfterSeconds: Int?
        let codec: String?
        let width: Int?
        let height: Int?
    }

    static func decode(_ payload: Data) throws -> HostMessage {
        let wire: Wire
        do { wire = try JSONDecoder().decode(Wire.self, from: payload) }
        catch { throw ProtocolError.malformed }

        if let version = wire.version, version != ProtocolV2.version {
            throw ProtocolError.unsupported("Versão de protocolo incompatível.")
        }

        switch wire.type {
        case "challenge":
            guard let salt = wire.salt.flatMap({ Data(base64Encoded: $0) }),
                  let nonce = wire.nonce.flatMap({ Data(base64Encoded: $0) }),
                  salt.count == ProtocolV2.saltBytes, nonce.count >= 16 else { throw ProtocolError.malformed }
            return .challenge(salt: salt, nonce: nonce)
        case "ready":
            guard let role = wire.role else { throw ProtocolError.malformed }
            return .ready(role: role, host: wire.host,
                          deviceToken: wire.deviceToken.flatMap { Data(base64Encoded: $0) })
        case "authFailed":
            return .authFailed(message: wire.message ?? "PIN incorreto.",
                               retryAfter: wire.retryAfterSeconds ?? 0)
        case "windows":
            return .windows(wire.windows ?? [])
        case "inputRejected":
            return .inputRejected(wire.message ?? "o Windows recusou a entrada")
        case "error":
            return .error(wire.message ?? "erro do host")
        case "pong":
            return .pong
        case "video":
            return .video(codec: wire.codec ?? "desconhecido",
                          width: wire.width ?? 0, height: wire.height ?? 0)
        default:
            throw ProtocolError.unsupported("Mensagem do host não reconhecida.")
        }
    }
}

// MARK: - Mensagens do cliente

private struct HelloPayload: Encodable {
    let type = "hello"
    let version = ProtocolV2.version
    let role: String
    let windowId: Int64?
    let clientName: String
}

private struct AuthPayload: Encodable {
    let type = "auth"
    let version = ProtocolV2.version
    let mode: String
    let proof: String
}

private struct SimplePayload: Encodable {
    let type: String
}

private struct OpenPayload: Encodable {
    let type = "open"
    let windowId: Int64
}

private struct ClickPayload: Encodable {
    let type = "click"
    let x: Double
    let y: Double
    let button: String
}

private struct TextPayload: Encodable {
    let type = "text"
    let text: String
}

private struct KeyPayload: Encodable {
    let type = "key"
    let key: String
    let ctrl: Bool
    let alt: Bool
    let shift: Bool
}

private struct ScrollPayload: Encodable {
    let type = "scroll"
    let x: Double
    let y: Double
    let delta: Int
}

private struct PointerPayload: Encodable {
    let type = "pointer"
    let action: String
    let x: Double
    let y: Double
    let button: String
}

enum OutgoingMessage {

    static func hello(role: String, windowId: Int64? = nil, clientName: String = "Mac") -> Data? {
        encode(HelloPayload(role: role, windowId: windowId, clientName: clientName))
    }

    static func auth(mode: String, proof: Data) -> Data? {
        encode(AuthPayload(mode: mode, proof: proof.base64EncodedString()))
    }

    static func list() -> Data? { encode(SimplePayload(type: "list")) }

    static func requestKeyframe() -> Data? { encode(SimplePayload(type: "requestKeyframe")) }

    static func ping() -> Data? { encode(SimplePayload(type: "ping")) }

    static func open(windowId: Int64) -> Data? { encode(OpenPayload(windowId: windowId)) }

    static func click(x: Double, y: Double, button: RemoteMouseButton) -> Data? {
        encode(ClickPayload(x: x, y: y, button: button.rawValue))
    }

    static func text(_ text: String) -> Data? { encode(TextPayload(text: text)) }

    static func key(_ key: RemoteKey, ctrl: Bool, alt: Bool, shift: Bool) -> Data? {
        encode(KeyPayload(key: key.rawValue, ctrl: ctrl, alt: alt, shift: shift))
    }

    static func scroll(x: Double, y: Double, delta: Int) -> Data? {
        encode(ScrollPayload(x: x, y: y, delta: delta))
    }

    static func pointer(action: RemotePointerAction, x: Double, y: Double,
                        button: RemoteMouseButton = .left) -> Data? {
        encode(PointerPayload(action: action.rawValue, x: x, y: y, button: button.rawValue))
    }

    private static func encode<T: Encodable>(_ value: T) -> Data? {
        guard let json = try? JSONEncoder().encode(value) else { return nil }
        return FrameCodec.control(json)
    }
}

// MARK: - Provas de autenticação

enum PasswordProof {

    /// *Trim* + NFC, igual ao host (.NET usa FormKC).
    static func normalize(_ pin: String) -> String {
        pin.trimmingCharacters(in: .whitespacesAndNewlines).precomposedStringWithCompatibilityMapping
    }

    static func isAcceptablePin(_ pin: String) -> Bool {
        let value = normalize(pin)
        return value.count >= ProtocolV2.pinMinLength && value.count <= ProtocolV2.pinMaxLength
    }

    static func deriveKey(pin: String, salt: Data) -> Data {
        let password = Data(normalize(pin).utf8)
        return pbkdf2HMACSHA256(password: password, salt: salt,
                                iterations: ProtocolV2.pinIterations, bytes: ProtocolV2.proofBytes)
    }

    static func pinProof(key: Data, nonce: Data) -> Data {
        proof(key: key, context: "remoteDesktop-v2/pin", nonce: nonce)
    }

    static func deviceProof(token: Data, nonce: Data) -> Data {
        proof(key: token, context: "remoteDesktop-v2/device", nonce: nonce)
    }

    static func equal(_ left: Data, _ right: Data) -> Bool {
        guard left.count == right.count else { return false }
        var difference: UInt8 = 0
        for (a, b) in zip(left, right) { difference |= a ^ b }
        return difference == 0
    }

    private static func proof(key: Data, context: String, nonce: Data) -> Data {
        var message = Data(context.utf8)
        message.append(0)
        message.append(nonce)
        return Data(HMAC<SHA256>.authenticationCode(for: message, using: SymmetricKey(data: key)))
    }

    /// PBKDF2-HMAC-SHA256 (RFC 8018). Só um bloco de 32 bytes é necessário.
    private static func pbkdf2HMACSHA256(password: Data, salt: Data, iterations: UInt32, bytes: Int) -> Data {
        let key = SymmetricKey(data: password)
        var derived = Data()
        var block: UInt32 = 1

        while derived.count < bytes {
            var message = salt
            withUnsafeBytes(of: block.bigEndian) { message.append(contentsOf: $0) }

            var u = Data(HMAC<SHA256>.authenticationCode(for: message, using: key))
            var accumulator = u
            if iterations > 1 {
                for _ in 2...max(iterations, 2) {
                    u = Data(HMAC<SHA256>.authenticationCode(for: u, using: key))
                    for index in 0..<accumulator.count { accumulator[index] ^= u[index] }
                }
            }
            derived.append(accumulator)
            block += 1
        }

        return derived.prefix(bytes)
    }
}

// MARK: - Entrada remota

enum RemoteMouseButton: String, Sendable, Hashable {
    case left
    case right
}

/// Fases de um arraste. `click` continua sendo o clique atômico (down + up).
/// `hover` move o ponteiro do PC sem pressionar botão.
enum RemotePointerAction: String, Sendable {
    case down
    case move
    case up
    case hover
}

/// Teclas especiais/atalhos aceitos pelo protocolo v2.
enum RemoteKey: String, Sendable {
    case enter
    case backspace
    case tab
    case escape
    case left
    case right
    case up
    case down
    case delete
    case home
    case end
    case a
    case c
    case v
    case x
    case z
    case b, d, e, f, g, h, i, j, k, l, m, n, o, p, q, r, s, t, u, w, y
    case pageup, pagedown, insert
    case f1, f2, f3, f4, f5, f6, f7, f8, f9, f10, f11, f12
}

enum RemoteInputEvent: Sendable {
    case click(x: Double, y: Double, button: RemoteMouseButton)
    case text(String)
    case key(RemoteKey, ctrl: Bool, alt: Bool, shift: Bool)
    case scroll(x: Double, y: Double, delta: Int)
    case pointer(action: RemotePointerAction, x: Double, y: Double, button: RemoteMouseButton)
    /// Pede um keyframe novo ao host (usado após erro de decodificação).
    case needKeyframe
}

enum TextLimiter {

    /// Trunca sem dividir pares substitutos (unidades UTF-16).
    static func truncateUTF16(_ text: String, limit: Int = 256) -> String {
        guard text.utf16.count > limit else { return text }
        var result = String()
        result.reserveCapacity(limit)
        var units = 0
        for character in text {
            let width = character.utf16.count
            guard units + width <= limit else { break }
            result.append(character)
            units += width
        }
        return result
    }
}
