import Foundation

struct StreamPreferences: Sendable, Equatable {
    enum Quality: Sendable, Equatable {
        case automatic, low, balanced, high

        var bitrate: Int {
            switch self {
            case .automatic, .high: 12_000_000
            case .low: 3_000_000
            case .balanced: 6_000_000
            }
        }
    }

    enum Resolution: Sendable, Equatable {
        case native, p1080, p720

        var maxDimension: Int {
            switch self {
            case .native: 0
            case .p1080: 1920
            case .p720: 1280
            }
        }
    }

    var quality: Quality = .automatic
    var fps = 60
    var resolution: Resolution = .native

    var bitrate: Int { quality.bitrate }
    var maxDimension: Int { resolution.maxDimension }
}
