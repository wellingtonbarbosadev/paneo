import AppKit

@MainActor
final class StreamControlsViewController: NSViewController {
    var onChange: ((StreamPreferences) -> Void)?
    private var preferences = StreamPreferences()
    private var supportsScaling: Bool?
    private let quality = NSPopUpButton()
    private let resolution = NSPopUpButton()
    private let fps = NSPopUpButton()
    private let capability = NSTextField(wrappingLabelWithString: "")

    override func loadView() {
        let view = NSView(frame: NSRect(x: 0, y: 0, width: 300, height: 190))
        self.view = view
        for (button, title) in [(quality, "Qualidade"), (resolution, "Resolução"), (fps, "Quadros por segundo")] {
            button.addItems(withTitles: title == "Qualidade" ? ["Automática", "Baixa", "Equilibrada", "Alta"] : title == "Resolução" ? ["Nativa", "Até 1920 px", "Até 1280 px"] : ["30 FPS", "60 FPS"])
            button.target = self
            button.action = #selector(change(_:))
            button.setAccessibilityLabel(title)
        }
        resolution.toolTip = "Limita o lado maior da imagem transmitida, mantendo a proporção."
        capability.font = .systemFont(ofSize: 11)
        capability.textColor = .secondaryLabelColor
        capability.lineBreakMode = .byWordWrapping
        var rows: [NSView] = [("Qualidade", quality), ("Resolução", resolution), ("Quadros", fps)].map { title, control in
            NSStackView(views: [NSTextField(labelWithString: title), control])
        }
        rows.append(capability)
        let stack = NSStackView(views: rows)
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 10
        stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 16),
            stack.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -16),
            stack.topAnchor.constraint(equalTo: view.topAnchor, constant: 16),
            stack.bottomAnchor.constraint(lessThanOrEqualTo: view.bottomAnchor, constant: -16),
            quality.widthAnchor.constraint(equalToConstant: 160),
            resolution.widthAnchor.constraint(equalTo: quality.widthAnchor),
            fps.widthAnchor.constraint(equalTo: quality.widthAnchor)
        ])
        updateControls()
    }

    func configure(_ preferences: StreamPreferences, supportsScaling: Bool?) {
        self.preferences = preferences
        self.supportsScaling = supportsScaling
        if isViewLoaded { updateControls() }
    }

    private func updateControls() {
        let qualityIndex: Int
        switch preferences.quality {
        case .automatic: qualityIndex = 0
        case .low: qualityIndex = 1
        case .balanced: qualityIndex = 2
        case .high: qualityIndex = 3
        }
        quality.selectItem(at: qualityIndex)
        let resolutionIndex: Int
        switch preferences.resolution {
        case .native: resolutionIndex = 0
        case .p1080: resolutionIndex = 1
        case .p720: resolutionIndex = 2
        }
        resolution.selectItem(at: resolutionIndex)
        fps.selectItem(at: preferences.fps == 30 ? 0 : 1)
        capability.stringValue = preferences.resolution == .native || supportsScaling != false
            ? ""
            : "Este host não oferece limite de resolução nesta sessão."
    }

    @objc private func change(_ sender: NSPopUpButton) {
        if sender === quality {
            preferences.quality = [StreamPreferences.Quality.automatic, .low, .balanced, .high][sender.indexOfSelectedItem]
        } else if sender === resolution {
            preferences.resolution = [StreamPreferences.Resolution.native, .p1080, .p720][sender.indexOfSelectedItem]
        } else if sender === fps {
            preferences.fps = sender.indexOfSelectedItem == 0 ? 30 : 60
        } else {
            return
        }
        updateControls()
        onChange?(preferences)
    }
}
