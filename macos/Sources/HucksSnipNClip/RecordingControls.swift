import AppKit
import Carbon.HIToolbox
import CoreGraphics
import CryptoKit
import Foundation

/// The two controls a recording needs while the menu bar is hidden.
///
/// These are deliberately not `CaptureAction` members. A capture shortcut is registered the whole
/// time the app runs; these are registered only while a clip is actually recording and are removed
/// again while idle and during finalization, so they never replace a key needed for ordinary work.
enum RecordingControlAction: String, CaseIterable {
    case pauseResume = "PauseResume"
    case stop = "Stop"

    var displayName: String {
        switch self {
        case .pauseResume: return "Pause/Resume Recording"
        case .stop: return "Stop Recording"
        }
    }

    /// Control-Option-Shift matches the capture family and the accepted Windows roles.
    var defaultStroke: ShortcutStroke {
        switch self {
        case .pauseResume:
            return ShortcutStroke(modifiers: ShortcutBinding.captureModifiers, keyCode: UInt32(kVK_ANSI_P))
        case .stop:
            return ShortcutStroke(modifiers: ShortcutBinding.captureModifiers, keyCode: UInt32(kVK_ANSI_X))
        }
    }

    /// Escape is never a recording control. Accidentally ending a take is worse than requiring an
    /// explicit stop, so the key that dismisses every other surface must not reach a recording.
    static func isForbidden(_ stroke: ShortcutStroke) -> Bool {
        return stroke.keyCode == UInt32(kVK_Escape)
    }
}

/// One display as it contributes to a layout identity: which physical display it is, where it
/// sits relative to the others, how big it is, how it is scaled, and which way up it is.
struct DisplayLayoutEntry: Equatable {
    var displayID: String
    var frame: CGRect
    var scale: CGFloat
    var rotation: Int
    var primary: Bool
}

/// Turns the connected displays into a stable key, so the floating controller remembers a separate
/// position for each arrangement actually worked in. A point beside a laptop's own screen is off
/// the edge of a three-display desk, and rearranging the same displays moves where "the right-hand
/// monitor" is - so arrangement, size, scale and rotation are all part of the identity.
///
/// This is backend behavior. The user never sees, names or manages a profile.
enum DisplayLayoutIdentity {
    static func compose(_ entries: [DisplayLayoutEntry]) -> String {
        guard !entries.isEmpty else { return "none" }

        // NSScreen's order can change on its own, so sort into a canonical order. Position is still
        // carried by each frame, which AppKit expresses relative to the primary display's origin.
        let parts = entries.map { entry in
            String(
                format: "%@@%d,%d,%d,%d|scale%.2f|rot%d|%@",
                entry.displayID.uppercased(),
                Int(entry.frame.minX.rounded()),
                Int(entry.frame.minY.rounded()),
                Int(entry.frame.width.rounded()),
                Int(entry.frame.height.rounded()),
                Double(entry.scale),
                entry.rotation,
                entry.primary ? "primary" : "secondary")
        }.sorted()
        let descriptor = "\(entries.count);" + parts.joined(separator: ";")
        let digest = SHA256.hash(data: Data(descriptor.utf8))
        return digest.prefix(8).map { String(format: "%02x", $0) }.joined()
    }

    @MainActor
    static func current() -> String {
        return compose(snapshot())
    }

    @MainActor
    static func snapshot() -> [DisplayLayoutEntry] {
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        return NSScreen.screens.enumerated().map { index, screen in
            let number = (screen.deviceDescription[key] as? NSNumber)?.uint32Value
            return DisplayLayoutEntry(
                displayID: number.map(stableIdentifier(for:)) ?? "display#\(index)",
                frame: screen.frame,
                scale: screen.backingScaleFactor,
                rotation: number.map { Int(CGDisplayRotation($0).rounded()) } ?? 0,
                primary: index == 0)
        }
    }

    /// The display's own UUID survives replugging and reordering; the numeric display ID does not
    /// always. A display that will not identify itself still produces the same key every time.
    private static func stableIdentifier(for displayID: CGDirectDisplayID) -> String {
        if let uuid = CGDisplayCreateUUIDFromDisplayID(displayID)?.takeRetainedValue(),
           let text = CFUUIDCreateString(nil, uuid) as String? {
            return text
        }
        return "display#\(displayID)"
    }
}

/// A small, bounded, most-recently-used set of controller positions, one per display layout.
/// Bounded on purpose: a settings file growing a line for every arrangement ever plugged in is a
/// leak, not a memory. Twelve matches Windows.
struct RecordingControllerLayouts: Equatable {
    static let maxProfiles = 12

    private(set) var keys: [String] = []
    private var positions: [String: CGPoint] = [:]

    var count: Int { keys.count }

    func position(for layoutKey: String) -> CGPoint? {
        return positions[layoutKey]
    }

    /// Teaches one layout its position. Only that layout changes.
    mutating func setPosition(_ position: CGPoint, for layoutKey: String) {
        guard !layoutKey.isEmpty else { return }
        positions[layoutKey] = position
        promote(layoutKey)
    }

    /// Marks a layout as current without changing where its controller sits.
    mutating func touch(_ layoutKey: String) {
        guard positions[layoutKey] != nil else { return }
        promote(layoutKey)
    }

    /// Restores stored layouts in the most-recently-used order they were written.
    mutating func append(_ position: CGPoint, for layoutKey: String) {
        guard !layoutKey.isEmpty, positions[layoutKey] == nil, keys.count < Self.maxProfiles else {
            return
        }
        positions[layoutKey] = position
        keys.append(layoutKey)
    }

    private mutating func promote(_ layoutKey: String) {
        keys.removeAll { $0 == layoutKey }
        keys.insert(layoutKey, at: 0)
        while keys.count > Self.maxProfiles {
            positions.removeValue(forKey: keys.removeLast())
        }
    }
}

/// Where the controller goes on a layout never seen before, and how a remembered point is rescued
/// when the display it was learned on has shrunk or gone away. All rectangles are AppKit screen
/// coordinates; a position is the controller window's frame origin.
enum RecordingControllerPlacement {
    static let edgeMargin: CGFloat = 24

    /// Keeps the whole controller inside one display's visible area. A point that cannot be
    /// honored is pulled to the nearest usable place rather than discarded, so a layout keeps its
    /// learned corner instead of jumping back to a default.
    static func clamp(_ desired: CGPoint, size: CGSize, visibleFrames: [CGRect]) -> CGPoint {
        guard let first = visibleFrames.first else { return desired }

        let wanted = CGRect(origin: desired, size: size)
        var best = first
        var bestOverlap: CGFloat = -1
        for frame in visibleFrames {
            let intersection = wanted.intersection(frame)
            let overlap = intersection.isNull ? 0 : intersection.width * intersection.height
            if overlap > bestOverlap {
                bestOverlap = overlap
                best = frame
            }
        }
        if bestOverlap <= 0 {
            // Completely off every display - the layout changed underneath a saved point.
            best = nearest(to: desired, in: visibleFrames)
        }

        let x = min(max(desired.x, best.minX), max(best.minX, best.maxX - size.width))
        let y = min(max(desired.y, best.minY), max(best.minY, best.maxY - size.height))
        return CGPoint(x: x.rounded(), y: y.rounded())
    }

    /// A layout nobody has taught yet starts on the display being recorded, low and to the right,
    /// where a controller is least likely to sit on top of the subject.
    static func defaultOrigin(in visibleFrame: CGRect, size: CGSize) -> CGPoint {
        return CGPoint(
            x: max(visibleFrame.minX, visibleFrame.maxX - size.width - edgeMargin).rounded(),
            y: min(visibleFrame.minY + edgeMargin, max(visibleFrame.minY, visibleFrame.maxY - size.height)).rounded())
    }

    private static func nearest(to point: CGPoint, in frames: [CGRect]) -> CGRect {
        var nearest = frames[0]
        var best = CGFloat.greatestFiniteMagnitude
        for frame in frames {
            let dx = point.x < frame.minX ? frame.minX - point.x
                : (point.x > frame.maxX ? point.x - frame.maxX : 0)
            let dy = point.y < frame.minY ? frame.minY - point.y
                : (point.y > frame.maxY ? point.y - frame.maxY : 0)
            let distance = dx * dx + dy * dy
            if distance < best {
                best = distance
                nearest = frame
            }
        }
        return nearest
    }
}
