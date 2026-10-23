import AppKit
import QuartzCore

@MainActor
enum AppMotion {
    private static var visibilityTransitions: [ObjectIdentifier: UUID] = [:]
    static var duration: TimeInterval {
        NSWorkspace.shared.accessibilityDisplayShouldReduceMotion ? 0 : 0.18
    }

    static func animate(_ changes: () -> Void, completion: (@MainActor @Sendable () -> Void)? = nil) {
        NSAnimationContext.runAnimationGroup { context in
            context.duration = duration
            context.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            context.allowsImplicitAnimation = duration > 0
            changes()
        } completionHandler: {
            Task { @MainActor in completion?() }
        }
    }

    static func setVisible(_ view: NSView, _ visible: Bool,
                           completion: (@MainActor @Sendable () -> Void)? = nil) {
        let key = ObjectIdentifier(view)
        let transition = UUID()
        visibilityTransitions[key] = transition
        guard duration > 0, view.window?.occlusionState.contains(.visible) == true else {
            view.isHidden = !visible
            view.alphaValue = 1
            visibilityTransitions[key] = nil
            completion?()
            return
        }
        if visible {
            if view.isHidden { view.alphaValue = 0 }
            view.isHidden = false
            animate({ view.animator().alphaValue = 1 }) {
                guard visibilityTransitions[key] == transition else { return }
                visibilityTransitions[key] = nil
                completion?()
            }
        } else {
            guard !view.isHidden else {
                visibilityTransitions[key] = nil
                completion?()
                return
            }
            animate({ view.animator().alphaValue = 0 }) {
                guard visibilityTransitions[key] == transition else { return }
                view.isHidden = true
                visibilityTransitions[key] = nil
                completion?()
            }
        }
    }
}
