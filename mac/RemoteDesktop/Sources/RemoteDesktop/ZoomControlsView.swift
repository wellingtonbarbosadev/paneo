import AppKit

@MainActor
final class ZoomControlsView: NSView, NSPopoverDelegate {

    private weak var videoView: RemoteVideoView?
    private let button = NSButton()
    private let popover = NSPopover()
    private let slider = NSSlider(value: 100, minValue: 50, maxValue: 300, target: nil, action: nil)
    private let percentage = NSTextField(labelWithString: "100%")

    var isShowing: Bool { popover.isShown }

    init(videoView: RemoteVideoView) {
        self.videoView = videoView
        super.init(frame: .zero)

        button.image = NSImage(systemSymbolName: "plus.magnifyingglass", accessibilityDescription: "Zoom da janela")
        button.imagePosition = .imageOnly
        button.setAccessibilityLabel("Zoom da janela")
        button.target = self
        button.action = #selector(toggle(_:))
        button.translatesAutoresizingMaskIntoConstraints = false
        Glass.style(button)
        addSubview(button)
        NSLayoutConstraint.activate([
            button.leadingAnchor.constraint(equalTo: leadingAnchor),
            button.trailingAnchor.constraint(equalTo: trailingAnchor),
            button.topAnchor.constraint(equalTo: topAnchor),
            button.bottomAnchor.constraint(equalTo: bottomAnchor),
            widthAnchor.constraint(equalToConstant: 36),
            heightAnchor.constraint(equalToConstant: 28)
        ])

        let content = NSViewController()
        let panel = NSView(frame: NSRect(x: 0, y: 0, width: 280, height: 140))
        content.view = panel
        popover.contentViewController = content
        popover.behavior = .transient
        popover.delegate = self

        let title = NSTextField(labelWithString: "Zoom da janela")
        title.font = .systemFont(ofSize: 13, weight: .semibold)
        percentage.alignment = .right
        slider.isContinuous = true
        slider.numberOfTickMarks = 26
        slider.allowsTickMarkValuesOnly = true
        slider.target = self
        slider.action = #selector(changeZoom(_:))
        slider.setAccessibilityLabel("Escala da janela remota")

        let minimum = NSTextField(labelWithString: "50%")
        let maximum = NSTextField(labelWithString: "300%")
        minimum.font = .systemFont(ofSize: 11)
        maximum.font = .systemFont(ofSize: 11)
        minimum.textColor = .secondaryLabelColor
        maximum.textColor = .secondaryLabelColor
        maximum.alignment = .right

        let reset = NSButton(title: "100%", target: self, action: #selector(resetZoom(_:)))
        reset.setAccessibilityLabel("Restaurar zoom para 100%")
        Glass.style(reset)

        for view in [title, percentage, slider, minimum, maximum, reset] {
            view.translatesAutoresizingMaskIntoConstraints = false
            panel.addSubview(view)
        }
        NSLayoutConstraint.activate([
            title.leadingAnchor.constraint(equalTo: panel.leadingAnchor, constant: 16),
            title.topAnchor.constraint(equalTo: panel.topAnchor, constant: 14),
            percentage.trailingAnchor.constraint(equalTo: panel.trailingAnchor, constant: -16),
            percentage.centerYAnchor.constraint(equalTo: title.centerYAnchor),
            slider.leadingAnchor.constraint(equalTo: panel.leadingAnchor, constant: 16),
            slider.trailingAnchor.constraint(equalTo: panel.trailingAnchor, constant: -16),
            slider.topAnchor.constraint(equalTo: title.bottomAnchor, constant: 8),
            minimum.leadingAnchor.constraint(equalTo: slider.leadingAnchor),
            minimum.topAnchor.constraint(equalTo: slider.bottomAnchor, constant: 2),
            maximum.trailingAnchor.constraint(equalTo: slider.trailingAnchor),
            maximum.topAnchor.constraint(equalTo: slider.bottomAnchor, constant: 2),
            reset.centerXAnchor.constraint(equalTo: panel.centerXAnchor),
            reset.topAnchor.constraint(equalTo: minimum.bottomAnchor, constant: 8),
            reset.bottomAnchor.constraint(lessThanOrEqualTo: panel.bottomAnchor, constant: -8)
        ])

        videoView.onZoomStateChange = { [weak self] in self?.refresh() }
        refresh()
    }

    required init?(coder: NSCoder) {
        fatalError("A interface é construída em código; nib não é suportado.")
    }

    func refresh() {
        guard let videoView else {
            button.isEnabled = false
            close()
            return
        }
        let value = Int((videoView.zoomScale * 100).rounded())
        percentage.stringValue = "\(value)%"
        slider.doubleValue = Double(value)
        button.toolTip = "Zoom da janela · \(value)%"
        button.isEnabled = videoView.canZoom
        slider.isEnabled = videoView.canZoom
        if !videoView.canZoom { close() }
    }

    func close() {
        if popover.isShown { popover.performClose(nil) }
    }

    @objc private func toggle(_ sender: NSButton) {
        guard let videoView, videoView.canZoom else {
            refresh()
            return
        }
        if popover.isShown {
            close()
            return
        }
        refresh()
        videoView.resetInput()
        videoView.window?.makeFirstResponder(nil)
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .maxY)
        if let popoverWindow = popover.contentViewController?.view.window {
            popoverWindow.makeFirstResponder(slider)
        }
    }

    @objc private func changeZoom(_ sender: NSSlider) {
        let value = (sender.doubleValue / 10).rounded() * 10
        if let videoView, CGFloat(value) / 100 != videoView.zoomScale {
            videoView.setZoom(CGFloat(value) / 100)
        }
        refresh()
    }

    @objc private func resetZoom(_ sender: NSButton) {
        videoView?.setZoom(1)
        refresh()
    }

    func popoverDidClose(_ notification: Notification) {
        guard let videoView, videoView.canZoom,
              let window = videoView.window, window.isKeyWindow else { return }
        window.makeFirstResponder(videoView)
    }
}
