import AVFoundation
import CoreMedia
import CoreVideo
import Foundation
import VideoToolbox

/// Separa NALs H.264 de um fluxo Annex-B (start codes) ou já em AVCC.
enum H264Parser {

    /// NALs da access unit, na ordem em que aparecem.
    static func nalUnits(_ data: Data) -> [Data] {
        if data.count >= 4, data[0] == 0, data[1] == 0, data[2] == 0, data[3] == 1 {
            return splitAnnexB(data)
        }
        if data.count >= 3, data[0] == 0, data[1] == 0, data[2] == 1 {
            return splitAnnexB(data)
        }
        return splitAvcc(data)
    }

    /// Reconstrói o fluxo em AVCC (4 bytes de tamanho por NAL), formato exigido
    /// pelo VideoToolbox na decodificação.
    static func avcc(_ nals: [Data]) -> Data {
        var out = Data(capacity: nals.reduce(0) { $0 + $1.count + 4 })
        for nal in nals {
            var length = UInt32(nal.count).bigEndian
            withUnsafeBytes(of: &length) { out.append(contentsOf: $0) }
            out.append(nal)
        }
        return out
    }

    /// Tipo do NAL (5 bits do primeiro byte).
    static func type(of nal: Data) -> UInt8 {
        guard let first = nal.first else { return 0 }
        return first & 0x1F
    }

    private static func splitAnnexB(_ data: Data) -> [Data] {
        let bytes = [UInt8](data)
        var starts: [Int] = []
        var index = 0
        while index + 2 < bytes.count {
            if bytes[index] == 0, bytes[index + 1] == 0, bytes[index + 2] == 1 {
                starts.append(index)
                index += 3
                continue
            }
            if index + 3 < bytes.count, bytes[index] == 0, bytes[index + 1] == 0, bytes[index + 2] == 0, bytes[index + 3] == 1 {
                starts.append(index)
                index += 4
                continue
            }
            index += 1
        }
        guard !starts.isEmpty else { return [] }

        var units: [Data] = []
        for (position, start) in starts.enumerated() {
            let payloadStart = start + (bytes[start] == 0 && bytes[start + 1] == 0 && bytes[start + 2] == 0 ? 4 : 3)
            let end = position + 1 < starts.count ? starts[position + 1] : bytes.count
            guard end > payloadStart else { continue }
            units.append(Data(bytes[payloadStart..<end]))
        }
        return units
    }

    private static func splitAvcc(_ data: Data) -> [Data] {
        let bytes = [UInt8](data)
        var units: [Data] = []
        var offset = 0
        while offset + 4 <= bytes.count {
            var length = 0
            for index in 0..<4 { length = (length << 8) | Int(bytes[offset + index]) }
            offset += 4
            guard length > 0, offset + length <= bytes.count else { break }
            units.append(Data(bytes[offset..<(offset + length)]))
            offset += length
        }
        return units
    }
}

/// Caixa que transporta o buffer decodificado do callback do VideoToolbox até
/// o MainActor. O buffer é de uso exclusivo do decodificador, que o entrega
/// pronto para exibição.
struct SendablePixelBuffer: @unchecked Sendable {
    let buffer: CVImageBuffer
}

/// Decodificador H.264 do host (VideoToolbox) com saída em AVSampleBufferDisplayLayer.
@MainActor
final class H264Decoder {

    private var session: VTDecompressionSession?
    private var format: CMVideoFormatDescription?
    private var sequenceParameterSet: Data?
    private var pictureParameterSet: Data?
    private var awaitingKeyframe = true
    private var lastKeyframeRequest = Date.distantPast
    private var decodeFailureCount = 0
    /// <summary>PTS do próximo quadro, derivado de ptsMs (100 ns).</summary>
    private var presentationTime = CMTime.zero
    private var frameIndex: Int64 = 0

    let displayLayer = AVSampleBufferDisplayLayer()

    /// Pede um keyframe novo ao host (no máximo uma vez por segundo).
    var onNeedKeyframe: (() -> Void)?
    /// Resolution mudando, para ajustar a janela.
    var onResolution: ((Int, Int) -> Void)?
    var onError: ((String) -> Void)?
    /// Chamado a cada quadro decodificado com sucesso.
    var onDecoded: ((CGSize) -> Void)?
    /// Chamado quando um frame é entregue à camada de apresentação.
    var onPresented: ((Bool) -> Void)?

    init() {
        displayLayer.videoGravity = .resizeAspect
        displayLayer.backgroundColor = .black
        displayLayer.contentsGravity = .resizeAspect
    }

    /// Tamanho atual do vídeo, se houver.
    private(set) var pixelSize = CGSize.zero

    func decode(accessUnit: Data, keyframe: Bool, width: Int, height: Int, ptsMs: UInt32 = 0) {
        let nals = H264Parser.nalUnits(accessUnit)
        guard !nals.isEmpty else { return }

        if keyframe {
            for nal in nals where H264Parser.type(of: nal) == 7 { sequenceParameterSet = nal }
            for nal in nals where H264Parser.type(of: nal) == 8 { pictureParameterSet = nal }
        }

        guard let sps = sequenceParameterSet, let pps = pictureParameterSet else {
            requestKeyframe()
            return
        }

        if format == nil || needsNewFormat(sps: sps, pps: pps) {
            guard let description = makeFormatDescription(sps: sps, pps: pps) else {
                requestKeyframe()
                return
            }
            format = description
            awaitingKeyframe = keyframe
            // A sessão anterior refers a um formato velho: invalida antes de recriar.
            invalidateSession()
            createSession()
        }

        // Sem sessão (falha na criação), não há o que decodificar.
        guard session != nil else {
            requestKeyframe()
            return
        }

        guard awaitingKeyframe == false || keyframe else {
            // Quadro de referência perdido: espera o próximo keyframe.
            requestKeyframe()
            return
        }

        let payload = H264Parser.avcc(nals)
        guard let sample = makeSampleBuffer(payload: payload, ptsMs: ptsMs) else { return }

        var flags = VTDecodeInfoFlags()
        let status = VTDecompressionSessionDecodeFrame(session!, sampleBuffer: sample, flags: [], infoFlagsOut: &flags) { [weak self] status, _, imageBuffer, _, _ in
            guard let self else { return }
            if status != noErr {
                Task { @MainActor in
                    self.awaitingKeyframe = true
                    self.displayLayer.flush()
                    self.requestKeyframe()
                }
                return
            }
            // O frame decodificado precisa chegar à camada de apresentação; sem
            // isso a tela fica preta mesmo com o vídeo decodificando. A caixa
            // é criada aqui (ainda no contexto do callback) porque o buffer do
            // VideoToolbox não pode atravessar o isolamento por conta própria.
            let box = imageBuffer.map { SendablePixelBuffer(buffer: $0) }
            Task { @MainActor in
                if let box {
                    self.enqueue(pixelBuffer: box)
                    self.onDecoded?(self.pixelSize)
                } else {
                    self.onError?("O decodificador não entregou imagem para este quadro.")
                }
            }
        }

        if status != noErr {
            awaitingKeyframe = true
            displayLayer.flush()
            requestKeyframe()
            return
        }

        awaitingKeyframe = false
        if pixelSize.width != CGFloat(width) || pixelSize.height != CGFloat(height) {
            pixelSize = CGSize(width: width, height: height)
            onResolution?(width, height)
        }
    }

    /// <summary>Entrega o frame decodificado à camada de apresentação.</summary>
    private func enqueue(pixelBuffer: SendablePixelBuffer) {
        let imageBuffer = pixelBuffer.buffer

        // A descrição precisa vir do próprio buffer decodificado: a do SPS/PPS
        // pode diferir no alcance do vídeo (full range vs limited) e o
        // CMSampleBuffer seria recusado, deixando a tela preta.
        var imageDescription: CMVideoFormatDescription?
        let described = CMVideoFormatDescriptionCreateForImageBuffer(
            allocator: kCFAllocatorDefault,
            imageBuffer: imageBuffer,
            formatDescriptionOut: &imageDescription)
        guard described == noErr, let imageDescription else {
            onError?("Não foi possível descrever o quadro decodificado (\(described)).")
            return
        }

        var sample: CMSampleBuffer?
        var timing = CMSampleTimingInfo(duration: .invalid,
                                         presentationTimeStamp: .invalid,
                                         decodeTimeStamp: .invalid)
        let created = withUnsafePointer(to: &timing) { pointer in
            CMSampleBufferCreateReadyWithImageBuffer(
                allocator: kCFAllocatorDefault,
                imageBuffer: imageBuffer,
                formatDescription: imageDescription,
                sampleTiming: pointer,
                sampleBufferOut: &sample)
        }
        guard created == noErr, let sample else {
            onError?("Não foi possível criar o quadro para exibição (\(created)).")
            return
        }

        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: true) {
            let dict = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(dict,
                                 Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                                 Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
        }

        if displayLayer.status == .failed {
            displayLayer.flush()
        }
        // A camada está atrasada: descarta o quadro antigo em vez de acumular.
        if !displayLayer.isReadyForMoreMediaData {
            displayLayer.flush()
        }
        displayLayer.enqueue(sample)
        onPresented?(true)
    }

    /// Descarta o estado de referência (usado quando a janela muda de tamanho).
    func reset() {
        awaitingKeyframe = true
        displayLayer.flush()
        requestKeyframe()
    }

    private func requestKeyframe() {
        let now = Date()
        guard now.timeIntervalSince(lastKeyframeRequest) > 1 else { return }
        lastKeyframeRequest = now
        onNeedKeyframe?()
    }

    /// Lê um parameter set (0 = SPS, 1 = PPS) da descrição de formato atual.
    private func parameterSet(_ index: Int) -> Data? {
        guard let format else { return nil }
        var pointer: UnsafePointer<UInt8>?
        var size = 0
        var count = 0
        var headerLength: Int32 = 0
        let status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            format,
            parameterSetIndex: index,
            parameterSetPointerOut: &pointer,
            parameterSetSizeOut: &size,
            parameterSetCountOut: &count,
            nalUnitHeaderLengthOut: &headerLength)
        guard status == noErr, let pointer, size > 0 else { return nil }
        return Data(bytes: pointer, count: size)
    }

    private func needsNewFormat(sps: Data, pps: Data) -> Bool {
        parameterSet(0) != sps || parameterSet(1) != pps
    }

    private func makeFormatDescription(sps: Data, pps: Data) -> CMVideoFormatDescription? {
        var description: CMVideoFormatDescription?
        let status = sps.withUnsafeBytes { spsRaw in
            pps.withUnsafeBytes { ppsRaw in
                let pointers: [UnsafePointer<UInt8>] = [
                    spsRaw.bindMemory(to: UInt8.self).baseAddress!,
                    ppsRaw.bindMemory(to: UInt8.self).baseAddress!
                ]
                let sizes = [sps.count, pps.count]
                return pointers.withUnsafeBufferPointer { buffer in
                    CMVideoFormatDescriptionCreateFromH264ParameterSets(
                        allocator: kCFAllocatorDefault,
                        parameterSetCount: pointers.count,
                        parameterSetPointers: buffer.baseAddress!,
                        parameterSetSizes: sizes,
                        nalUnitHeaderLength: 4,
                        formatDescriptionOut: &description)
                }
            }
        }
        guard status == noErr else {
            onError?("Formato H.264 inválido.")
            return nil
        }
        return description
    }

    private func createSession() {
        guard let format else { return }
        var created: VTDecompressionSession?
        let attributes: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        ]
        let status = VTDecompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            formatDescription: format,
            decoderSpecification: nil,
            imageBufferAttributes: attributes as CFDictionary,
            outputCallback: nil,
            decompressionSessionOut: &created)
        guard status == noErr, let created else {
            onError?("Não foi possível criar o decodificador de vídeo.")
            session = nil
            return
        }
        VTSessionSetProperty(created, key: kVTDecompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        session = created
    }

    /// <summary>Invalida a sessão atual (evita acumular sessões em resize).</summary>
    private func invalidateSession() {
        if let session {
            VTDecompressionSessionInvalidate(session)
        }
        session = nil
        displayLayer.flush()
    }

    private func makeSampleBuffer(payload: Data, ptsMs: UInt32) -> CMSampleBuffer? {
        guard let format else { return nil }
        var block: CMBlockBuffer?
        let created = payload.withUnsafeBytes { raw -> OSStatus in
            let status = CMBlockBufferCreateWithMemoryBlock(
                allocator: kCFAllocatorDefault,
                memoryBlock: nil,
                blockLength: payload.count,
                blockAllocator: kCFAllocatorDefault,
                customBlockSource: nil,
                offsetToData: 0,
                dataLength: payload.count,
                flags: 0,
                blockBufferOut: &block)
            guard status == noErr, let block else { return status }
            return raw.baseAddress!.withMemoryRebound(to: UInt8.self, capacity: payload.count) { pointer in
                CMBlockBufferReplaceDataBytes(with: pointer, blockBuffer: block, offsetIntoDestination: 0, dataLength: payload.count)
            }
        }
        guard created == noErr, let block else { return nil }

        // PTS monotônico: o VideoToolbox usa a ordem de apresentação.
        presentationTime = CMTime(value: CMTimeValue(ptsMs) * 1_000_000, timescale: 1_000_000_000)
        frameIndex += 1

        var sample: CMSampleBuffer?
        var timing = CMSampleTimingInfo(duration: .invalid,
                                         presentationTimeStamp: presentationTime,
                                         decodeTimeStamp: presentationTime)
        let status = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault,
            dataBuffer: block,
            formatDescription: format,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleSizeEntryCount: 0,
            sampleSizeArray: nil,
            sampleBufferOut: &sample)
        guard status == noErr else { return nil }
        return sample
    }
}
