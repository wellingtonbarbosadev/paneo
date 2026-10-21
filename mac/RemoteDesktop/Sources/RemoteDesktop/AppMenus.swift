import AppKit

/// Menu principal mínimo, em português.
///
/// Existem por usabilidade básica: encerrar (⌘Q), fechar janela (⌘W) e
/// cortar/copiar/colar nos campos de conexão (⌘X/⌘C/⌘V).
/// Command permanece local; Control é enviado ao Windows pela área remota.
@MainActor
enum AppMenus {

    static func makeMainMenu() -> NSMenu {
        let mainMenu = NSMenu()

        // 1. Menu do aplicativo
        let applicationItem = NSMenuItem()
        applicationItem.title = "RemoteDesktop"
        mainMenu.addItem(applicationItem)

        let applicationMenu = NSMenu(title: "RemoteDesktop")
        applicationMenu.addItem(item("Sobre RemoteDesktop", #selector(NSApplication.orderFrontStandardAboutPanel(_:)), ""))
        applicationMenu.addItem(.separator())
        applicationMenu.addItem(item("Ocultar RemoteDesktop", #selector(NSApplication.hide(_:)), "h"))
        applicationMenu.addItem(item("Ocultar os Outros", #selector(NSApplication.hideOtherApplications(_:)), ""))
        applicationMenu.addItem(item("Mostrar Todos", #selector(NSApplication.unhideAllApplications(_:)), ""))
        applicationMenu.addItem(.separator())
        applicationMenu.addItem(item("Encerrar RemoteDesktop", #selector(NSApplication.terminate(_:)), "q"))
        applicationItem.submenu = applicationMenu

        // 2. Arquivo
        let fileItem = NSMenuItem()
        mainMenu.addItem(fileItem)
        let fileMenu = NSMenu(title: "Arquivo")
        fileMenu.addItem(item("Nova Conexão", #selector(AppDelegate.newConnection(_:)), "n"))
        fileMenu.addItem(item("Fechar Janela", #selector(NSWindow.performClose(_:)), "w"))
        fileItem.submenu = fileMenu

        // 3. Editar — necessário para os campos de endereço e porta
        let editItem = NSMenuItem()
        mainMenu.addItem(editItem)
        let editMenu = NSMenu(title: "Editar")
        editMenu.addItem(item("Cortar", #selector(NSText.cut(_:)), "x"))
        editMenu.addItem(item("Copiar", #selector(NSText.copy(_:)), "c"))
        editMenu.addItem(item("Colar", #selector(NSText.paste(_:)), "v"))
        editMenu.addItem(.separator())
        editMenu.addItem(item("Selecionar Tudo", #selector(NSText.selectAll(_:)), "a"))
        editItem.submenu = editMenu

        // 4. Janela
        let windowItem = NSMenuItem()
        mainMenu.addItem(windowItem)
        let windowMenu = NSMenu(title: "Janela")
        windowMenu.addItem(item("Minimizar", #selector(NSWindow.performMiniaturize(_:)), "m"))
        windowMenu.addItem(item("Ampliar", #selector(NSWindow.zoom(_:)), ""))
        windowItem.submenu = windowMenu

        return mainMenu
    }

    private static func item(_ title: String, _ action: Selector, _ keyEquivalent: String) -> NSMenuItem {
        NSMenuItem(title: title, action: action, keyEquivalent: keyEquivalent)
    }
}
