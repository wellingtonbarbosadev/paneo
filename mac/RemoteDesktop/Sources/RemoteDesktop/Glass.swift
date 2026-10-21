import AppKit

/// Aparência "Liquid Glass" com alternativa para macOS 14/15.
///
/// No macOS 26+ usa `NSGlassEffectView` (e `NSGlassEffectContainerView` para
/// agrupar painéis). Antes disso, `NSVisualEffectView` com cantos arredondados.
@MainActor
enum Glass {

    /// Painel translúcido com cantos arredondados.
    static func panel(cornerRadius: CGFloat = 18, material: NSVisualEffectView.Material = .hudWindow) -> NSView {
        if #available(macOS 26.0, *) {
            let glass = NSGlassEffectView()
            glass.cornerRadius = cornerRadius
            glass.style = .regular
            if #available(macOS 27.0, *) {
                glass.effectIsInteractive = true
            }
            return glass
        }

        let effect = NSVisualEffectView()
        effect.material = material
        effect.blendingMode = .behindWindow
        effect.state = .active
        effect.wantsLayer = true
        effect.layer?.cornerRadius = cornerRadius
        effect.layer?.masksToBounds = true
        return effect
    }

    /// Contêiner que agrupa descendentes de vidro (macOS 26+).
    static func container(spacing: CGFloat = 8) -> NSView {
        if #available(macOS 26.0, *) {
            let container = NSGlassEffectContainerView()
            container.spacing = spacing
            return container
        }
        return NSView()
    }

    /// Estilo de botão: vidro no macOS 26+, arredondado antes disso.
    static func style(_ button: NSButton) {
        if #available(macOS 26.0, *) {
            button.bezelStyle = .glass
        } else {
            button.bezelStyle = .rounded
        }
    }

    /// Título de seção dentro de um painel translúcido.
    static func caption(_ text: String) -> NSTextField {
        let label = NSTextField(labelWithString: text)
        label.font = .systemFont(ofSize: 11, weight: .semibold)
        label.textColor = .secondaryLabelColor
        return label
    }
}
