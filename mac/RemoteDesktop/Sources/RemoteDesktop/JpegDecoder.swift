import AppKit
import Foundation

/// Decodificador JPEG do fallback do host.
///
/// O host usa JPEG quando não há encoder H.264 no Windows. O quadro chega no
/// mesmo enquadramento da mídia v2 e é desenhado em uma camada.
@MainActor
final class JpegDecoder {

    /// Camada onde o quadro é desenhado.
    let displayLayer = CALayer()

    /// Quadro decodificado mais recente (tamanho em pontos).
    private(set) var pixelSize = CGSize.zero

    /// Erro de decodificação, se houver.
    var onError: ((String) -> Void)?

    init() {
        displayLayer.contentsGravity = .resizeAspect
        displayLayer.masksToBounds = true
    }

    /// Decodifica um quadro JPEG e o publica na camada.
    /// - Returns: `true` quando o quadro foi desenhado.
    @discardableResult
    func decode(_ data: Data) -> Bool {
        guard let image = NSImage(data: data) else {
            onError?("Quadro JPEG inválido.")
            return false
        }

        guard let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
            onError?("Quadro JPEG sem imagem.")
            return false
        }

        let size = CGSize(width: cgImage.width, height: cgImage.height)
        let scale = windowScale()
        let bounds = CGRect(origin: .zero, size: CGSize(width: size.width / scale, height: size.height / scale))

        let context = CGContext(data: nil,
                                width: cgImage.width,
                                height: cgImage.height,
                                bitsPerComponent: 8,
                                bytesPerRow: 0,
                                space: CGColorSpaceCreateDeviceRGB(),
                                bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue)
        guard let context else {
            onError?("Não foi possível criar o contexto de desenho.")
            return false
        }
        context.draw(cgImage, in: CGRect(x: 0, y: 0, width: cgImage.width, height: cgImage.height))

        guard let rendered = context.makeImage() else {
            onError?("Não foi possível montar o quadro JPEG.")
            return false
        }

        displayLayer.contents = rendered
        displayLayer.contentsGravity = .resizeAspect
        if pixelSize != size {
            pixelSize = size
        }
        displayLayer.frame = bounds
        return true
    }

    private func windowScale() -> CGFloat {
        let screen = NSScreen.main
        return screen?.backingScaleFactor ?? 1
    }
}
