import Foundation

enum RemoteWindowRecovery {
    static func target(_ previous: RemoteWindow, windows: [RemoteWindow], monitors: [RemoteMonitor]) -> RemoteWindow? {
        if let monitorId = previous.monitorId {
            return monitors.first(where: { $0.id == monitorId }).map(RemoteWindow.monitor)
        }
        return windows.first(where: { $0.id == previous.id }) ?? windows.first(where: {
            $0.process.caseInsensitiveCompare(previous.process) == .orderedSame &&
            !previous.title.isEmpty && $0.title == previous.title
        })
    }
}
