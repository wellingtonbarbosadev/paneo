import AppKit

/// Ponto de entrada AppKit do cliente experimental TLS.
@main
@MainActor
struct RemoteDesktopApp {

    static func main() {
        let application = NSApplication.shared
        application.setActivationPolicy(.regular)
        // O bundle fornece o ícone Paneo declarado em CFBundleIconFile.

        application.mainMenu = AppMenus.makeMainMenu()

        let delegate = AppDelegate()
        application.delegate = delegate

        application.run()
    }
}

/// Delega o ciclo de vida do aplicativo: cria e mantém a janela principal.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {

    private var windowControllers: [MainWindowController] = []
    private var smoke: RemoteSmoke?
    private var reconnectSmoke: ReconnectSmoke?

    func applicationDidFinishLaunching(_ notification: Notification) {
        AppSettings.shared.applyAppearance()
        let args = CommandLine.arguments
        if args.count == 4, args[1] == "--reconnect-smoke" {
            do { reconnectSmoke = try ReconnectSmoke(sessionPath: args[2], outputPath: args[3]) }
            catch { exit(2) }
            return
        }
        if args.count == 4, args[1] == "--video-smoke" {
            exit(VideoSmoke.run(inputPath: args[2], outputPath: args[3]))
        }
        if args.count == 4, args[1] == "--remote-smoke" {
            do { smoke = try RemoteSmoke(sessionPath: args[2], outputPath: args[3]) }
            catch { exit(2) }
            return
        }
        newConnection(nil)
    }
    @objc func newConnection(_ sender: Any?) {
        let controller = MainWindowController()
        controller.onClose = { [weak self, weak controller] in
            self?.windowControllers.removeAll { $0 === controller }
        }
        windowControllers.append(controller)
        controller.showWindow()
    }

    @objc func showSettings(_ sender: Any?) {
        let key = NSApp.keyWindow
        let controller = windowControllers.first { $0.window === key } ?? windowControllers.first
        controller?.showSettings()
    }

    /// ⌘⇧B: recolhe ou mostra a lista de apps da janela em foco.
    @objc func toggleAppBar(_ sender: Any?) {
        let key = NSApp.keyWindow
        let controller = windowControllers.first { $0.window === key } ?? windowControllers.first
        controller?.toggleBarCollapsed()
    }

    /// ⌘K: abre ou fecha a busca rápida de janelas.
    @objc func openQuickSearch(_ sender: Any?) {
        let key = NSApp.keyWindow
        let controller = windowControllers.first { $0.window === key } ?? windowControllers.first
        controller?.toggleQuickSearch()
    }

    /// ⌘`: avança para a próxima janela remota.
    @objc func cycleNextWindow(_ sender: Any?) {
        let key = NSApp.keyWindow
        let controller = windowControllers.first { $0.window === key } ?? windowControllers.first
        controller?.cycleWindow(forward: true)
    }

    /// ⌘⇧`: volta para a janela remota anterior.
    @objc func cyclePreviousWindow(_ sender: Any?) {
        let key = NSApp.keyWindow
        let controller = windowControllers.first { $0.window === key } ?? windowControllers.first
        controller?.cycleWindow(forward: false)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }

    func applicationSupportsSecureRestorableState(_ app: NSApplication) -> Bool {
        true
    }
}
