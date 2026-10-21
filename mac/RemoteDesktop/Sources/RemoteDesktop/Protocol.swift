import Foundation

/// Limites do protocolo experimental v1.
enum ProtocolLimits {
    /// Cada mensagem é comprimento uint32 + JSON UTF-8; nunca mais que 16 MiB.
    static let maxMessageBytes = 16 * 1024 * 1024
    /// `text` transporta no máximo 256 unidades UTF-16.
    static let maxTextUTF16 = 256
    static let version = 1
}

/// Codificação do enquadramento: uint32 big endian + JSON UTF-8.
enum FrameCodec {

    static func encode(payload: Data) -> Data? {
        guard payload.count <= ProtocolLimits.maxMessageBytes else { return nil }
        var length = UInt32(payload.count).bigEndian
        var framed = withUnsafeBytes(of: &length) { Data($0) }
        framed.append(payload)
        return framed
    }
}

enum FramingError: Error {
    case zeroLength
    case tooLarge
    case malformed

    var safeDescription: String {
        switch self {
        case .zeroLength:
            return "mensagem de comprimento zero"
        case .tooLarge:
            return "mensagem maior que 16 MiB"
        case .malformed:
            return "enquadramento inválido"
        }
    }
}

/// Decodificador incremental. Aceita pedaços arbitrários de TCP e devolve
/// somente mensagens completas. Nunca mantém mais que uma mensagem + cabeçalho.
struct FrameDecoder {

    private var buffer = Data()

    mutating func append(_ chunk: Data) throws -> [Data] {
        buffer.append(chunk)

        var payloads: [Data] = []
        while buffer.count >= 4 {
            let header = [UInt8](buffer.prefix(4))
            let length = (UInt32(header[0]) << 24)
                | (UInt32(header[1]) << 16)
                | (UInt32(header[2]) << 8)
                | UInt32(header[3])

            guard length > 0 else { throw FramingError.zeroLength }
            guard length <= UInt32(ProtocolLimits.maxMessageBytes) else {
                throw FramingError.tooLarge
            }

            let total = 4 + Int(length)
            guard buffer.count >= total else { break }

            let payload = Data(buffer.dropFirst(4).prefix(Int(length)))
            buffer.removeFirst(total)
            payloads.append(payload)
        }

        // Defesa extra: um buffer acima do limite só pode ser lixo/ataque.
        guard buffer.count <= ProtocolLimits.maxMessageBytes + 4 else {
            throw FramingError.malformed
        }
        return payloads
    }
}

// MARK: - Mensagens enviadas pelo cliente

private struct AuthPayload: Encodable {
    let type: String
    let version: Int
    let token: String
}

private struct AckPayload: Encodable {
    let type: String
    let id: Int
}

private struct ClickPayload: Encodable {
    let type: String
    let x: Double
    let y: Double
    let button: String
}

private struct TextPayload: Encodable {
    let type: String
    let text: String
}

private struct KeyPayload: Encodable {
    let type: String
    let key: String
    let ctrl: Bool
    let alt: Bool
    let shift: Bool
}

private struct ScrollPayload: Encodable {
    let type: String
    let x: Double
    let y: Double
    let delta: Int
}

private struct PointerPayload: Encodable {
    let type: String
    let action: String
    let x: Double
    let y: Double
    let button: String
}

private struct PingPayload: Encodable {
    let type: String
}

enum OutgoingMessage {

    static func auth(token: String) -> Data? {
        encode(AuthPayload(type: "auth", version: ProtocolLimits.version, token: token))
    }

    static func ack(id: Int) -> Data? {
        encode(AckPayload(type: "ack", id: id))
    }

    static func click(x: Double, y: Double, button: RemoteMouseButton) -> Data? {
        encode(ClickPayload(type: "click", x: x, y: y, button: button.rawValue))
    }

    static func text(_ text: String) -> Data? {
        encode(TextPayload(type: "text", text: text))
    }

    static func key(_ key: RemoteKey, ctrl: Bool, alt: Bool, shift: Bool) -> Data? {
        encode(KeyPayload(type: "key", key: key.rawValue, ctrl: ctrl, alt: alt, shift: shift))
    }

    static func scroll(x: Double, y: Double, delta: Int) -> Data? {
        encode(ScrollPayload(type: "scroll", x: x, y: y, delta: delta))
    }

    static func pointer(action: RemotePointerAction, x: Double, y: Double, button: RemoteMouseButton) -> Data? {
        encode(PointerPayload(type: "pointer", action: action.rawValue, x: x, y: y, button: button.rawValue))
    }

    static func ping() -> Data? {
        encode(PingPayload(type: "ping"))
    }

    private static func encode<T: Encodable>(_ value: T) -> Data? {
        try? JSONEncoder().encode(value)
    }
}

// MARK: - Mensagens recebidas do host

struct IncomingMessage: Decodable {
    let type: String
    let version: Int?
    let id: Int?
    let width: Int?
    let height: Int?
    let png: String?
    let message: String?
}

// MARK: - Entrada remota

enum RemoteMouseButton: String, Sendable, Hashable {
    case left
    case right
}

/// Fases de um arraste. `click` continua sendo o clique atômico (down + up).
enum RemotePointerAction: String, Sendable {
    case down
    case move
    case up
}

/// Teclas especiais/atalhos aceitos pelo protocolo v1.
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
}

enum TextLimiter {

    /// Trunca sem dividir pares substitutos (unidades UTF-16).
    static func truncateUTF16(_ text: String, limit: Int = ProtocolLimits.maxTextUTF16) -> String {
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
