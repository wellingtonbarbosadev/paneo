import AppKit
import CryptoKit
import Foundation

@MainActor
@main
struct StreamDecoderHarness {
    private struct CheckFailure: Error, CustomStringConvertible {
        let description: String
    }

    private struct MediaFrame {
        let keyframe: Bool
        let width: Int
        let height: Int
        let pts: UInt32
        let payload: Data
    }

    static func main() throws {
        guard CommandLine.arguments.count == 2 else {
            throw CheckFailure(description: "uso: stream-decoder-harness <video-media.bin>")
        }
        let frames = try readFrames(at: URL(fileURLWithPath: CommandLine.arguments[1]))
        let keyframes = frames.indices.filter { frames[$0].keyframe }
        try check(keyframes.count >= 2, "fixture tem duas sequências com keyframes")

        let firstKey = keyframes[0]
        let secondKey = keyframes[1]
        let firstSequence = Array(frames[firstKey..<secondKey])
        let secondSequence = Array(frames[secondKey...])
        try check(firstSequence.count > 1 && secondSequence.count > 1,
                  "cada sequência tem keyframe e quadros seguintes")
        guard let lastFirstFrame = firstSequence.last,
              let delta = secondSequence.first(where: { !$0.keyframe }) else {
            throw CheckFailure(description: "fixture não tem delta em cada sequência")
        }

        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let decoder = H264Decoder()
        var decoded = 0
        var presented = 0
        var thumbnails: [String] = []
        decoder.onDecoded = { _ in decoded += 1 }
        decoder.onPresented = { accepted in if accepted { presented += 1 } }
        decoder.onThumbnail = { thumbnails.append(signature($0)) }

        // A camada precisa estar hospedada numa janela para verificar rendering.
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 640, height: 360),
                              styleMask: [.titled], backing: .buffered, defer: false)
        let host = NSView(frame: window.contentLayoutRect)
        host.wantsLayer = true
        host.layer?.addSublayer(decoder.displayLayer)
        decoder.displayLayer.frame = host.bounds
        window.contentView = host
        window.orderFront(nil)
        defer { window.close() }

        for frame in firstSequence { feed(frame, to: decoder, pump: true) }
        try pumpUntil(timeout: 3) { decoded > 0 && presented > 0 && decoder.displayLayer.status == .rendering }
        guard let oldSnapshot = decoder.captureSnapshot() else {
            throw CheckFailure(description: "primeiro stream não produziu snapshot")
        }
        let oldSignature = signature(oldSnapshot)
        let oldDecoded = decoded
        let oldPresented = presented
        let oldThumbnailCount = thumbnails.count

        // Deixa um callback do stream velho pendente e troca sem ceder o actor.
        feed(lastFirstFrame, to: decoder, pump: false)
        decoder.beginStream()
        try check(decoder.pixelSize == .zero, "beginStream limpa pixelSize imediatamente")
        try check(decoder.captureSnapshot() == nil, "beginStream limpa snapshot imediatamente")
        pump(for: 0.2)
        try check(decoded == oldDecoded && presented == oldPresented,
                  "callback pendente do stream anterior não chega após beginStream")
        try check(thumbnails.count == oldThumbnailCount,
                  "thumbnail pendente do stream anterior não chega após beginStream")

        // Simula um stream que começa com delta: deve esperar seu keyframe.
        feed(delta, to: decoder, pump: false)
        pump(for: 0.15)
        try check(decoded == oldDecoded && presented == oldPresented,
                  "delta inicial do segundo stream é ignorado até o keyframe")

        for frame in secondSequence { feed(frame, to: decoder, pump: true) }
        try pumpUntil(timeout: 3) {
            decoded > oldDecoded && presented > oldPresented && decoder.displayLayer.status == .rendering
        }
        try pumpUntil(timeout: 2) { thumbnails.count > oldThumbnailCount }
        guard let newSnapshot = decoder.captureSnapshot() else {
            throw CheckFailure(description: "segundo stream não produziu snapshot")
        }
        let newSignature = signature(newSnapshot)
        try check(newSnapshot.width == secondSequence[0].width && newSnapshot.height == secondSequence[0].height,
                  "snapshot foi repopulado pelo segundo stream")
        if newSignature != oldSignature {
            try check(thumbnails.last != oldSignature, "thumbnail final não é do stream anterior")
        }

        print("PASS stream decoder: \(firstSequence.count) + \(secondSequence.count) access units; " +
              "decoded=\(decoded), presented=\(presented), thumbnails=\(thumbnails.count), " +
              "size=\(Int(decoder.pixelSize.width))x\(Int(decoder.pixelSize.height)), layer=\(decoder.displayLayer.status.rawValue)")
    }

    private static func readFrames(at url: URL) throws -> [MediaFrame] {
        let data = try Data(contentsOf: url)
        var reader = FrameReader()
        var parsed: [IncomingFrame] = []
        var offset = 0
        while offset < data.count {
            let end = min(offset + 64 * 1024, data.count)
            parsed += try reader.append(data.subdata(in: offset..<end),
                                        maxControlBytes: ProtocolV2.maxControlHostToClient)
            offset = end
        }
        return parsed.compactMap { frame in
            guard !frame.isControl, frame.codec == ProtocolV2.codecH264 else { return nil }
            return MediaFrame(keyframe: frame.isKeyframe, width: Int(frame.width), height: Int(frame.height),
                              pts: frame.ptsMs, payload: frame.payload)
        }
    }

    private static func feed(_ frame: MediaFrame, to decoder: H264Decoder, pump shouldPump: Bool) {
        decoder.decode(accessUnit: frame.payload, keyframe: frame.keyframe,
                       width: frame.width, height: frame.height, ptsMs: frame.pts)
        if shouldPump { pump(for: 0.005) }
    }

    private static func pump(for seconds: TimeInterval) {
        RunLoop.current.run(until: Date().addingTimeInterval(seconds))
    }

    private static func pumpUntil(timeout: TimeInterval, condition: () -> Bool) throws {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() && Date() < deadline { pump(for: 0.01) }
        try check(condition(), "timeout aguardando callback/apresentação VideoToolbox")
    }

    private static func signature(_ image: CGImage) -> String {
        guard let bytes = image.dataProvider?.data as Data? else { return "no-image-data" }
        return SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
    }

    private static func check(_ condition: @autoclosure () -> Bool, _ message: String) throws {
        guard condition() else { throw CheckFailure(description: message) }
    }
}
