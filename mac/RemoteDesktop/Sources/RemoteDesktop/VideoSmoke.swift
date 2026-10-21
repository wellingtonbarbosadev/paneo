import AppKit
import Foundation

/// Prova do decodificador: lê um arquivo com o enquadramento do protocolo v2
/// (quadros H.264 do host) e conta os quadros decodificados pelo VideoToolbox.
/// Não abre rede nem toca no Chaves.
@MainActor
enum VideoSmoke {

    /// Um quadro de mídia lido do arquivo.
    private struct MediaFrame {
        let codec: UInt8
        let keyframe: Bool
        let sequence: UInt32
        let pts: UInt32
        let width: Int
        let height: Int
        let payload: Data
    }

    static func run(inputPath: String, outputPath: String) -> Int32 {
        let input = URL(fileURLWithPath: inputPath)
        let output = URL(fileURLWithPath: outputPath)
        guard let data = try? Data(contentsOf: input) else {
            write(["success": false, "error": "arquivo de entrada ilegível"], to: output)
            return 2
        }

        let frames = parse(data)
        guard !frames.isEmpty else {
            write(["success": false, "error": "nenhum quadro de mídia encontrado"], to: output)
            return 2
        }

        let decoder = H264Decoder()
        var decoded = 0
        var failures = 0
        var size = CGSize.zero

        decoder.onDecoded = { decodedSize in
            decoded += 1
            if decodedSize != .zero { size = decodedSize }
        }
        decoder.onError = { _ in failures += 1 }
        var presentedFrames = 0
        var enqueueRejected = 0
        decoder.onPresented = { accepted in
            if accepted { presentedFrames += 1 } else { enqueueRejected += 1 }
        }

        // A camada de apresentação só renderiza dentro de uma janela: sem ela
        // o estado fica "desconhecido" e a prova não diria se o frame chegou
        // à tela.
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 640, height: 360),
                              styleMask: [.titled],
                              backing: .buffered,
                              defer: false)
        let host = NSView(frame: window.contentLayoutRect)
        host.wantsLayer = true
        host.layer?.addSublayer(decoder.displayLayer)
        decoder.displayLayer.frame = host.bounds
        window.contentView = host
        window.orderFront(nil)

        for frame in frames {
            guard frame.codec == ProtocolV2.codecH264 else { continue }
            decoder.decode(accessUnit: frame.payload,
                           keyframe: frame.keyframe,
                           width: frame.width,
                           height: frame.height,
                           ptsMs: frame.pts)
            RunLoop.current.run(until: Date().addingTimeInterval(0.005))
        }

        // Deixa o VideoToolbox concluir as decodificações pendentes.
        RunLoop.current.run(until: Date().addingTimeInterval(1.0))

        // Confere que o frame chegou à camada de apresentação: decodificar sem
        // exibir produz tela preta.
        let layer = decoder.displayLayer
        let presented = layer.status == .rendering

        write([
            "success": decoded > 0 && presented,
            "mediaFrames": frames.count,
            "decodedFrames": decoded,
            "failures": failures,
            "presented": presented,
            "presentedFrames": presentedFrames,
            "enqueueRejected": enqueueRejected,
            "layerStatus": layer.status.rawValue.description,
            "width": Int(size.width),
            "height": Int(size.height)
        ], to: output)
        window.close()
        return decoded > 0 && presented ? 0 : 1
    }

    /// Lê o enquadramento v2: u32 tamanho + tipo + corpo.
    private static func parse(_ data: Data) -> [MediaFrame] {
        let bytes = [UInt8](data)
        var frames: [MediaFrame] = []
        var offset = 0

        func readInt(_ source: [UInt8], _ range: Range<Int>) -> Int {
            var value = 0
            for index in range { value = (value << 8) | Int(source[index]) }
            return value
        }

        while offset + 4 <= bytes.count {
            let length = readInt(bytes, offset..<(offset + 4))
            offset += 4
            guard length > 0, offset + length <= bytes.count else { break }
            let body = Array(bytes[offset..<(offset + length)])
            offset += length

            guard let kind = body.first, kind == ProtocolV2.kindMedia else { continue }
            guard body.count >= ProtocolV2.mediaHeaderBytes else { continue }
            let payloadStart = ProtocolV2.mediaHeaderBytes
            let codec = body[1]
            let flags = UInt16(readInt(body, 2..<4))
            let sequence = UInt32(readInt(body, 4..<8))
            let pts = UInt32(readInt(body, 8..<12))
            let width = readInt(body, 12..<14)
            let height = readInt(body, 14..<16)
            frames.append(MediaFrame(codec: codec,
                                     keyframe: flags & ProtocolV2.flagKeyframe != 0,
                                     sequence: sequence,
                                     pts: pts,
                                     width: width,
                                     height: height,
                                     payload: Data(body[payloadStart...])))
        }
        return frames
    }

    private static func write(_ payload: [String: Any], to url: URL) {
        guard let data = try? JSONSerialization.data(withJSONObject: payload, options: [.prettyPrinted]) else { return }
        try? data.write(to: url)
    }
}
