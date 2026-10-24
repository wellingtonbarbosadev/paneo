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
        applicationItem.title = "Paneo"
        mainMenu.addItem(applicationItem)

        let applicationMenu = NSMenu(title: "Paneo")
        applicationMenu.addItem(item("Sobre Paneo", #selector(NSApplication.orderFrontStandardAboutPanel(_:)), ""))
        applicationMenu.addItem(.separator())
        applicationMenu.addItem(item("Ocultar Paneo", #selector(NSApplication.hide(_:)), "h"))
        applicationMenu.addItem(item("Ocultar os Outros", #selector(NSApplication.hideOtherApplications(_:)), ""))
        applicationMenu.addItem(item("Mostrar Todos", #selector(NSApplication.unhideAllApplications(_:)), ""))
        applicationMenu.addItem(.separator())
        applicationMenu.addItem(item("Ajustes…", #selector(AppDelegate.showSettings(_:)), ","))
        applicationMenu.addItem(.separator())
        applicationMenu.addItem(item("Encerrar Paneo", #selector(NSApplication.terminate(_:)), "q"))
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

        // 3b. Visualizar — lista recolhível, busca rápida e tela cheia
        let viewItem = NSMenuItem()
        mainMenu.addItem(viewItem)
        let viewMenu = NSMenu(title: "Visualizar")
        let quickSearch = item("Busca Rápida...", #selector(AppDelegate.openQuickSearch(_:)), "k")
        viewMenu.addItem(quickSearch)
        viewMenu.addItem(.separator())
        viewMenu.addItem(item("Aumentar Zoom", #selector(RemoteVideoView.zoomIn(_:)), "+"))
        viewMenu.addItem(item("Diminuir Zoom", #selector(RemoteVideoView.zoomOut(_:)), "-"))
        viewMenu.addItem(item("Restaurar Zoom", #selector(RemoteVideoView.resetZoom(_:)), "0"))
        viewMenu.addItem(.separator())
        let cycleNext = item("Próxima Janela Remota", #selector(AppDelegate.cycleNextWindow(_:)), "`")
        viewMenu.addItem(cycleNext)
        let cyclePrev = item("Janela Remota Anterior", #selector(AppDelegate.cyclePreviousWindow(_:)), "~")
        cyclePrev.keyEquivalentModifierMask = [.command, .shift]
        viewMenu.addItem(cyclePrev)
        viewMenu.addItem(.separator())
        let toggleBar = item("Recolher/Mostrar Lista de Apps", #selector(AppDelegate.toggleAppBar(_:)), "b")
        toggleBar.keyEquivalentModifierMask = [.command, .shift]
        viewMenu.addItem(toggleBar)
        let fullScreen = item("Entrar em Tela Cheia", #selector(NSWindow.toggleFullScreen(_:)), "f")
        fullScreen.keyEquivalentModifierMask = [.command, .control]
        viewMenu.addItem(fullScreen)
        viewItem.submenu = viewMenu

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
