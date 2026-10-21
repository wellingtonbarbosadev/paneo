import AppKit

/// Indicador circular do estado do cliente.
/// Âmbar = aguardando; nenhum estado verde é exibido porque não há conexão.
@MainActor
final class StateIndicatorDot: NSView {

    var tint: NSColor = .systemGray {
        didSet { needsDisplay = true }
    }

    override var intrinsicContentSize: NSSize {
        NSSize(width: 12, height: 12)
    }

    override func draw(_ dirtyRect: NSRect) {
        tint.setFill()
        NSBezierPath(ovalIn: bounds).fill()
    }
}

/// Espaço destinado à janela remota, vazio por decisão de projeto.
///
/// Desenha apenas uma borda sutil e um texto explicativo. Não exibe aplicativos,
/// vídeos nem quadros simulados: o transporte e a captura do agente Windows não
/// existem nesta versão.
@MainActor
final class RemoteAreaPlaceholder: NSView {

    private let message: NSTextField

    init() {
        message = NSTextField(wrappingLabelWithString: Self.explanationText)
        message.font = NSFont.systemFont(ofSize: NSFont.smallSystemFontSize)
        message.textColor = .secondaryLabelColor
        message.alignment = .center
        message.isSelectable = false
        message.translatesAutoresizingMaskIntoConstraints = false
        super.init(frame: NSRect(x: 0, y: 0, width: 600, height: 200))
        wantsLayer = true
        addSubview(message)
        NSLayoutConstraint.activate([
            message.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 16),
            message.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -16),
            message.centerYAnchor.constraint(equalTo: centerYAnchor)
        ])
    }

    required init?(coder: NSCoder) {
        fatalError("A interface é construída em código; nib não é suportado.")
    }

    override var isFlipped: Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.textBackgroundColor.setFill()
        dirtyRect.fill()

        NSColor.separatorColor.setStroke()
        let border = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 6, yRadius: 6)
        border.lineWidth = 1
        border.stroke()
    }

    private static let explanationText = """
        Seu aplicativo Windows aparecerá aqui.

        A conexão estará disponível em uma próxima etapa do protótipo.
        """
}
