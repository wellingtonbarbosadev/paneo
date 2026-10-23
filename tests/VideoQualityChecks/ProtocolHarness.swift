import Foundation

@main
struct ProtocolHarness {
    static func main() throws {
        let defaults = StreamPreferences()
        precondition(defaults.quality == .automatic && defaults.fps == 60 && defaults.resolution == .native)
        precondition(defaults.bitrate == 12_000_000 && defaults.maxDimension == 0)
        precondition(StreamPreferences(quality: .low, fps: 30, resolution: .p720).bitrate == 3_000_000)
        precondition(StreamPreferences(quality: .balanced, fps: 60, resolution: .p1080).maxDimension == 1920)

        guard let framed = OutgoingMessage.quality(fps: 30, bitrate: 3_000_000, maxDimension: 1280) else {
            fatalError("quality não serializou")
        }
        var reader = FrameReader()
        let frames = try reader.append(framed, maxControlBytes: ProtocolV2.maxControlClientToHost)
        struct QualityWire: Decodable { let maxDimension: Int; let fps: Int }
        let quality = try JSONDecoder().decode(QualityWire.self, from: frames[0].payload)
        precondition(quality.maxDimension == 1280 && quality.fps == 30)

        let video = try HostMessage.decode(Data(#"{"type":"video","codec":"h264","width":1280,"height":720,"maxDimension":true}"#.utf8))
        if case let .video(_, _, _, supportsScaling) = video { precondition(supportsScaling) }
        else { fatalError("anúncio video não decodificado") }
        let oldVideo = try HostMessage.decode(Data(#"{"type":"video","codec":"h264","width":1280,"height":720}"#.utf8))
        if case let .video(_, _, _, supportsScaling) = oldVideo { precondition(!supportsScaling) }
        else { fatalError("video antigo não decodificado") }
        let apps = try HostMessage.decode(Data(#"{"type":"apps","index":0,"total":1,"apps":[{"id":"x","name":"X","cmd":"x","process":"x.exe"}]}"#.utf8))
        if case let .appsChunk(_, _, values, truncated) = apps { precondition(values.count == 1 && values[0].process == "x.exe" && !truncated) }
        else { fatalError("chunk de apps não decodificado") }

        precondition(RemoteConnectionFailure.message(pinMismatch: true).contains("Certificado"))
        precondition(RemoteConnectionFailure.message(pinMismatch: false).contains("conexão TLS"))
        print("StreamPreferences/Protocol/Failure PASS")
    }
}
