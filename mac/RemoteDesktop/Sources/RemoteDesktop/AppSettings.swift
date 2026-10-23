import AppKit

enum BarPosition: String, CaseIterable {
    case top, bottom, left, right

    var isVertical: Bool { self == .left || self == .right }
}

enum BarDensity: String, CaseIterable {
    case large, compact, icons
}

enum AppAppearance: String, CaseIterable {
    case system, light, dark
}

enum AppAccent: String, CaseIterable {
    case system, custom
}

@MainActor
final class AppSettings {
    static let shared = AppSettings()
    static let didChange = Notification.Name("AppSettings.didChange")

    private enum Key {
        static let barPosition = "settings.barPosition"
        static let barDensity = "settings.barDensity"
        static let appearance = "settings.appearance"
        static let accent = "settings.accent"
        static let accentColor = "settings.accentColor"
    }

    private let defaults: UserDefaults
    private let appliesGlobally: Bool

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        appliesGlobally = defaults === UserDefaults.standard
    }

    var barPosition: BarPosition {
        get { stored(BarPosition.self, key: Key.barPosition, fallback: .top) }
        set { save(newValue.rawValue, key: Key.barPosition) }
    }

    var barDensity: BarDensity {
        get { stored(BarDensity.self, key: Key.barDensity, fallback: .compact) }
        set { save(newValue.rawValue, key: Key.barDensity) }
    }

    var appearance: AppAppearance {
        get { stored(AppAppearance.self, key: Key.appearance, fallback: .system) }
        set {
            save(newValue.rawValue, key: Key.appearance)
            applyAppearance()
        }
    }

    var accent: AppAccent {
        get { stored(AppAccent.self, key: Key.accent, fallback: .system) }
        set { save(newValue.rawValue, key: Key.accent) }
    }

    var accentColor: NSColor {
        get {
            guard accent == .custom,
                  let hex = defaults.string(forKey: Key.accentColor),
                  let color = Self.color(from: hex) else { return .controlAccentColor }
            return color
        }
        set {
            let color = newValue.usingColorSpace(.deviceRGB) ?? .systemBlue
            defaults.set(String(format: "%02X%02X%02X",
                                Int(color.redComponent * 255),
                                Int(color.greenComponent * 255),
                                Int(color.blueComponent * 255)), forKey: Key.accentColor)
            changed()
        }
    }

    func applyAppearance() {
        guard appliesGlobally else { return }
        NSApp.appearance = nativeAppearance
    }

    func applyAppearance(to window: NSWindow) {
        applyAppearance()
        window.appearance = nativeAppearance
    }

    private var nativeAppearance: NSAppearance? {
        switch appearance {
        case .system: nil
        case .light: NSAppearance(named: .aqua)
        case .dark: NSAppearance(named: .darkAqua)
        }
    }

    private func stored<Value: RawRepresentable>(
        _ type: Value.Type, key: String, fallback: Value
    ) -> Value where Value.RawValue == String {
        guard let raw = defaults.string(forKey: key), let value = Value(rawValue: raw) else { return fallback }
        return value
    }

    private func save(_ value: String, key: String) {
        guard defaults.string(forKey: key) != value else { return }
        defaults.set(value, forKey: key)
        changed()
    }

    private func changed() {
        NotificationCenter.default.post(name: Self.didChange, object: self)
    }

    private static func color(from hex: String) -> NSColor? {
        guard hex.count == 6, let value = UInt32(hex, radix: 16) else { return nil }
        return NSColor(
            red: CGFloat((value >> 16) & 0xff) / 255,
            green: CGFloat((value >> 8) & 0xff) / 255,
            blue: CGFloat(value & 0xff) / 255,
            alpha: 1
        )
    }
}
