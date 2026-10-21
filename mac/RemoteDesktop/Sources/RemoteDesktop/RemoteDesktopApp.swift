import AppKit

/// Ponto de entrada AppKit do cliente experimental TLS.
@main
@MainActor
struct RemoteDesktopApp {

    static func main() {
        let application = NSApplication.shared
        application.setActivationPolicy(.regular)
        application.applicationIconImage = nil // usa o ícone genérico; sem arte falsa de conectividade

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
        let args = CommandLine.arguments
        if args.count == 4, args[1] == "--reconnect-smoke" {
            do { reconnectSmoke = try ReconnectSmoke(sessionPath: args[2], outputPath: args[3]) }
            catch { exit(2) }
            return
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

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }

    func applicationSupportsSecureRestorableState(_ app: NSApplication) -> Bool {
        true
    }
}
