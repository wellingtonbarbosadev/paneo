import Foundation
import AppKit

@MainActor
@main
struct WindowCommandsHarness {
    static func main() throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let reading = try json(OutgoingMessage.windowAction("reading", enabled: true, width: 1440, height: 900))
        check(reading["type"] as? String == "windowAction" && reading["action"] as? String == "reading" &&
              reading["enabled"] as? Bool == true && reading["width"] as? Int == 1440 &&
              reading["height"] as? Int == 900, "reading action carries enabled logical dimensions")

        let maximize = try json(OutgoingMessage.windowAction("maximize"))
        check(maximize["action"] as? String == "maximize", "maximize action encodes")
        let viewHello = try json(OutgoingMessage.hello(role: "view"))
        let controlHello = try json(OutgoingMessage.hello(role: "control"))
        check(viewHello["windowCommands"] as? Bool == true && controlHello["windowCommands"] as? Bool == false,
              "only view hello opts into the extended window protocol")
        check((try json(OutgoingMessage.windowMenus()))["type"] as? String == "windowMenus",
              "menu snapshot request encodes")
        check((try json(OutgoingMessage.windowMenuInvoke(id: "snapshot/2/item/1")))["id"] as? String == "snapshot/2/item/1",
              "menu invocation keeps the opaque snapshot id")

        guard case let .windowState(state) = try HostMessage.decode(Data(
            #"{"type":"windowState","windowId":42,"minimized":true,"maximized":false,"reading":false,"canResize":true}"#.utf8)) else {
            fatalError("windowState was not decoded")
        }
        check(state.windowId == 42 && state.minimized && state.canResize, "window state is decoded")

        guard case let .windows(windows, _, _) = try HostMessage.decode(Data(
            #"{"type":"windows","windows":[{"id":42,"title":"Doc","process":"app.exe","width":800,"height":600,"minimized":true}]}"#.utf8)) else {
            fatalError("windows list was not decoded")
        }
        check(windows.first?.minimized == true, "minimized window is kept in the list")
        guard case let .windows(oldWindows, _, _) = try HostMessage.decode(Data(
            #"{"type":"windows","windows":[{"id":42,"title":"Doc","process":"app.exe","width":800,"height":600}]}"#.utf8)) else {
            fatalError("legacy windows list was not decoded")
        }
        check(oldWindows.first?.minimized == false, "legacy windows default to not minimized")

        guard case let .windowMenus(snapshot) = try HostMessage.decode(Data(
            #"{"type":"windowMenus","supported":true,"items":[{"id":"f","title":"Arquivo","enabled":true,"checked":false,"separator":false,"children":[{"id":"open","title":"Abrir","enabled":false,"checked":true,"separator":false,"children":[]}]},{"id":"s","title":"","enabled":false,"checked":false,"separator":true,"children":[]}]}"#.utf8)) else {
            fatalError("windowMenus was not decoded")
        }
        check(snapshot.supported && snapshot.items[0].children[0].checked && !snapshot.items[0].children[0].enabled &&
              snapshot.items[1].separator, "menu hierarchy preserves disabled, checked and separator flags")

        let actions = WindowActionsView(frame: .zero)
        actions.update(state: RemoteWindowState(windowId: 42, minimized: true, maximized: false,
                                                reading: false, canResize: true),
                       windowId: 42, isMonitor: false)
        let actionsMenu = actions.makeActionsMenu()!
        check(actionsMenu.items[1].title == "Restaurar", "minimized window offers Restore")
        let autoFit = actionsMenu.items.first { $0.representedObject as? String == "reading" }!
        check(autoFit.title == "Ajustar automaticamente" && autoFit.state == .off,
              "auto-fit menu offers an unchecked toggle when disabled")
        actions.update(state: RemoteWindowState(windowId: 42, minimized: false, maximized: false,
                                                reading: true, canResize: true),
                       windowId: 42, isMonitor: false, autoFitEnabled: true)
        let enabledAutoFit = actions.makeActionsMenu()!.items.first { $0.representedObject as? String == "reading" }!
        check(enabledAutoFit.title == autoFit.title && enabledAutoFit.state == .on,
              "auto-fit menu keeps its title and checks the enabled toggle")
        var toggled = false
        actions.onAction = { action, enabled in toggled = action == "reading" && enabled == nil }
        NSApp.sendAction(enabledAutoFit.action!, to: enabledAutoFit.target, from: enabledAutoFit)
        check(toggled, "auto-fit menu click requests the per-window toggle")
        actions.update(state: RemoteWindowState(windowId: 7, minimized: false, maximized: false,
                                                reading: false, canResize: true),
                       windowId: 42, isMonitor: false)
        check(actions.makeActionsMenu() == nil, "state for a different HWND is rejected")

        let disabledMenus = actions.makeMenusMenu(RemoteWindowMenus(supported: true, items: [
            RemoteWindowMenuItem(id: "disabled", title: "Indisponível", enabled: false,
                                 checked: false, separator: false, children: [])
        ], reason: nil))
        check(!disabledMenus.autoenablesItems && !disabledMenus.items[0].isEnabled,
              "disabled host menu item remains disabled in AppKit")

        do {
            let repeatedItems: [[String: Any]] = (0..<129).map { index in
                ["id": String(index), "title": "Item", "enabled": true,
                 "checked": false, "separator": false, "children": []]
            }
            let oversized = try JSONSerialization.data(withJSONObject: ["type": "windowMenus", "supported": true,
                                                                          "items": repeatedItems])
            _ = try HostMessage.decode(oversized)
            fatalError("oversized menu snapshot was accepted")
        } catch is ProtocolError {
            print("PASS oversized menu snapshot rejected")
        }
        var deepItem: [String: Any] = ["id": "leaf", "title": "Leaf", "enabled": true,
                                       "checked": false, "separator": false, "children": []]
        for depth in 0..<4 {
            deepItem = ["id": "level\(depth)", "title": "Submenu", "enabled": true,
                        "checked": false, "separator": false, "children": [deepItem]]
        }
        do {
            let tooDeep = try JSONSerialization.data(withJSONObject: ["type": "windowMenus", "supported": true,
                                                                       "items": [deepItem]])
            _ = try HostMessage.decode(tooDeep)
            fatalError("too-deep menu snapshot was accepted")
        } catch is ProtocolError {
            print("PASS menu nesting limit enforced")
        }

        guard case let .windowCommandResult(action, success, message) = try HostMessage.decode(Data(
            #"{"type":"windowCommandResult","action":"close","success":false,"message":"recusado"}"#.utf8)) else {
            fatalError("windowCommandResult was not decoded")
        }
        check(action == "close" && !success && message == "recusado", "command error result is decoded")
        do {
            _ = try HostMessage.decode(Data(#"{"type":"windowState","windowId":42}"#.utf8))
            fatalError("incomplete windowState was accepted")
        } catch is ProtocolError {
            print("PASS incomplete windowState rejected")
        }
    }

    private static func json(_ data: Data?) throws -> [String: Any] {
        guard let data, data.count > 5,
              let value = try JSONSerialization.jsonObject(with: data.dropFirst(5)) as? [String: Any] else {
            throw ProtocolError.malformed
        }
        return value
    }

    private static func check(_ condition: Bool, _ description: String) {
        guard condition else { fatalError(description) }
        print("PASS \(description)")
    }
}
