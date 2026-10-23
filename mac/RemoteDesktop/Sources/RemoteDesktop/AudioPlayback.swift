import AVFoundation
import Foundation

/// Reprodução de áudio em tempo real (WASAPI Loopback do Windows -> Mac).
@MainActor
final class AudioPlayback {

    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private let format: AVAudioFormat
    private var isRunning = false

    init() {
        guard let pcmFormat = AVAudioFormat(commonFormat: .pcmFormatInt16,
                                           sampleRate: 48000,
                                           channels: 2,
                                           interleaved: true) else {
            fatalError("Formato de áudio 48kHz 16-bit estéreo não suportado pelo CoreAudio.")
        }
        self.format = pcmFormat
        setupAudio()
    }

    private func setupAudio() {
        engine.attach(player)
        engine.connect(player, to: engine.mainMixerNode, format: format)
        do {
            try engine.start()
            player.play()
            isRunning = true
        } catch {
            isRunning = false
        }
    }

    func playPcm(data: Data) {
        guard isRunning, !data.isEmpty else { return }

        let bytesPerFrame = format.streamDescription.pointee.mBytesPerFrame
        guard bytesPerFrame > 0 else { return }
        let frameCount = UInt32(data.count) / bytesPerFrame
        guard frameCount > 0 else { return }

        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frameCount) else { return }
        buffer.frameLength = frameCount

        data.withUnsafeBytes { raw in
            if let base = raw.baseAddress {
                memcpy(buffer.int16ChannelData?[0], base, Int(data.count))
            }
        }

        player.scheduleBuffer(buffer, at: nil, options: [], completionHandler: nil)
    }

    func stop() {
        if isRunning {
            player.stop()
            engine.stop()
            isRunning = false
        }
    }
}
