import AppKit
import Foundation

/// Sincronização bidirecional de área de transferência (clipboard) entre Mac e Windows.
@MainActor
final class ClipboardSync {

    var onSendClipboard: ((String) -> Void)?

    private var timer: Timer?
    private var lastLocalChangeCount: Int = NSPasteboard.general.changeCount
    private var lastSentText: String = ""
    private var lastReceivedText: String = ""

    init() {
        startObserving()
    }

    func startObserving() {
        timer?.invalidate()
        lastLocalChangeCount = NSPasteboard.general.changeCount
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            Task { @MainActor in
                self?.checkLocalClipboard()
            }
        }
    }

    func stopObserving() {
        timer?.invalidate()
        timer = nil
    }

    func checkLocalClipboard() {
        let pb = NSPasteboard.general
        guard pb.changeCount != lastLocalChangeCount else { return }
        lastLocalChangeCount = pb.changeCount

        guard let text = pb.string(forType: .string), !text.isEmpty else { return }
        if text == lastReceivedText || text == lastSentText { return }

        // Limita a 16 KiB para integridade do canal de controle
        let truncated = String(text.prefix(16 * 1024))
        lastSentText = truncated
        onSendClipboard?(truncated)
    }

    func applyRemoteClipboard(_ text: String) {
        guard !text.isEmpty, text != lastSentText else { return }
        lastReceivedText = text
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
        lastLocalChangeCount = pb.changeCount
    }
}
