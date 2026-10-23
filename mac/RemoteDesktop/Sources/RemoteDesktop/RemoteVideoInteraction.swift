import AppKit

/// Geometria compartilhada entre os retângulos do cursor e o filtro de entrada remota.
@MainActor
enum RemoteVideoInteraction {
    static let hiddenCursor: NSCursor = {
        let image = NSImage(size: NSSize(width: 1, height: 1))
        image.lockFocus()
        NSColor.clear.setFill()
        NSRect(x: 0, y: 0, width: 1, height: 1).fill()
        image.unlockFocus()
        return NSCursor(image: image, hotSpot: .zero)
    }()

    static func visibleImageRect(frameRect: CGRect, bounds: CGRect, visibleRect: CGRect) -> CGRect {
        frameRect.intersection(bounds).intersection(visibleRect)
    }

    static func contains(_ point: CGPoint, in frameRect: CGRect, bounds: CGRect,
                         visibleRect: CGRect, exclusions: [CGRect]) -> Bool {
        let imageRect = visibleImageRect(frameRect: frameRect, bounds: bounds, visibleRect: visibleRect)
        return imageRect.contains(point) && !exclusions.contains { $0.contains(point) }
    }

    static func cursorRects(frameRect: CGRect, bounds: CGRect, visibleRect: CGRect,
                            exclusions: [CGRect]) -> [CGRect] {
        let imageRect = visibleImageRect(frameRect: frameRect, bounds: bounds, visibleRect: visibleRect)
        guard !imageRect.isNull, !imageRect.isEmpty else { return [] }
        return exclusions.reduce([imageRect]) { regions, exclusion in
            regions.flatMap { subtract(exclusion, from: $0) }
        }
    }

    private static func subtract(_ exclusion: CGRect, from rect: CGRect) -> [CGRect] {
        let overlap = rect.intersection(exclusion)
        guard !overlap.isNull, !overlap.isEmpty else { return [rect] }
        var result: [CGRect] = []
        if overlap.minY > rect.minY {
            result.append(CGRect(x: rect.minX, y: rect.minY, width: rect.width, height: overlap.minY - rect.minY))
        }
        if overlap.maxY < rect.maxY {
            result.append(CGRect(x: rect.minX, y: overlap.maxY, width: rect.width, height: rect.maxY - overlap.maxY))
        }
        if overlap.minX > rect.minX {
            result.append(CGRect(x: rect.minX, y: overlap.minY, width: overlap.minX - rect.minX, height: overlap.height))
        }
        if overlap.maxX < rect.maxX {
            result.append(CGRect(x: overlap.maxX, y: overlap.minY, width: rect.maxX - overlap.maxX, height: overlap.height))
        }
        return result.filter { !$0.isEmpty }
    }
}
