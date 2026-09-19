import AppKit
import CoreGraphics

/// The drag-a-rectangle surface. It is a temporary interaction surface, not a window the user
/// manages: one coordinated window appears on every display for one selection, then all go away.
///
/// A Snip commits the moment the mouse is released. A Clip region stays pending so it can be
/// reviewed first: Return accepts it, dragging again replaces it, and Escape cancels. The rules
/// live in `RegionSelectionSession`; this type only presents them.
@MainActor
final class RegionSelectionOverlay {
    private static var active: RegionSelectionOverlay?

    private let windows: [OverlayWindow]
    private let completion: (CGRect?) -> Void
    fileprivate var session: RegionSelectionSession

    private init(purpose: RegionSelectionSession.Purpose, completion: @escaping (CGRect?) -> Void) {
        self.completion = completion
        self.session = RegionSelectionSession(purpose: purpose)
        self.windows = NSScreen.screens.map { screen in
            // NSScreen.frame is already in AppKit's global coordinate space. Do not also pass
            // `screen:` to NSWindow's initializer: that interprets the origin relative to the
            // screen and adds a secondary display's offset a second time.
            let window = OverlayWindow(
                contentRect: screen.frame,
                styleMask: .borderless,
                backing: .buffered,
                defer: false)
            window.isOpaque = false
            window.backgroundColor = .clear
            window.level = .screenSaver
            window.ignoresMouseEvents = false
            window.acceptsMouseMovedEvents = true
            window.collectionBehavior = [
                .canJoinAllSpaces,
                .canJoinAllApplications,
                .fullScreenAuxiliary,
                .stationary,
                .ignoresCycle
            ]
            window.hasShadow = false
            window.hidesOnDeactivate = false
            window.animationBehavior = .none
            window.sharingType = .none
            window.isMovable = false
            window.isMovableByWindowBackground = false
            window.isReleasedWhenClosed = false
            return window
        }
    }

    /// True while any selection surface is on screen. Capture must not begin until it is false.
    static var isPresented: Bool { active != nil }

    /// Presents the overlay and calls back with the chosen region in display space, or nil when
    /// the user cancelled. The windows are ordered out before the callback runs.
    static func present(
        purpose: RegionSelectionSession.Purpose,
        completion: @escaping (CGRect?) -> Void
    ) {
        active?.finish(.cancel)

        let overlay = RegionSelectionOverlay(purpose: purpose, completion: completion)
        active = overlay

        // Activate first. Plain orderFront calls made while an LSUIElement app is inactive can
        // leave non-key windows behind other apps on secondary displays/Spaces.
        NSApp.activate(ignoringOtherApps: true)

        for (index, window) in overlay.windows.enumerated() {
            let view = OverlayView(frame: NSRect(origin: .zero, size: window.frame.size))
            view.screenIndex = index
            view.overlay = overlay
            window.contentView = view
            window.orderFrontRegardless()
        }

        let pointer = NSEvent.mouseLocation
        let keyWindow = overlay.windows.first { $0.frame.contains(pointer) } ?? overlay.windows.first
        if let keyWindow, let view = keyWindow.contentView {
            keyWindow.makeKey()
            keyWindow.makeFirstResponder(view)
        }
        NSCursor.crosshair.push()
    }

    fileprivate func apply(_ outcome: RegionSelectionSession.Outcome) {
        if outcome == .none {
            windows.forEach { $0.contentView?.needsDisplay = true }
        } else {
            finish(outcome)
        }
    }

    private func finish(_ outcome: RegionSelectionSession.Outcome) {
        guard RegionSelectionOverlay.active === self else { return }
        RegionSelectionOverlay.active = nil

        NSCursor.pop()
        windows.forEach {
            $0.orderOut(nil)
            $0.contentView = nil
        }

        guard case .commit(let viewRect, let screenIndex) = outcome,
              windows.indices.contains(screenIndex),
              let primary = NSScreen.screens.first else {
            completion(nil)
            return
        }

        completion(RegionSelectionGeometry.displaySpaceRect(
            viewRect: viewRect,
            windowOrigin: windows[screenIndex].frame.origin,
            primaryScreenMaxY: primary.frame.maxY))
    }
}

extension RegionSelectionOverlay {
    /// Draws the real overlay view for one display off screen. The live windows are excluded from
    /// screen capture, so this is the way to look at what a pending selection shows.
    static func renderPreview(size: CGSize, session: RegionSelectionSession, screen: Int = 0) -> NSBitmapImageRep? {
        let view = OverlayView(frame: CGRect(origin: .zero, size: size))
        view.screenIndex = screen
        view.previewSession = session
        guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return nil }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        return bitmap
    }
}

/// The rules of one selection, apart from any window so they can be tested directly. Rectangles
/// are in the view space of the display they were drawn on.
struct RegionSelectionSession {
    enum Purpose: Equatable {
        /// Commits on mouse release: drag, release, done.
        case snip
        /// Stays pending on release so the region can be reviewed, redrawn, accepted or cancelled.
        case clip
    }

    enum Outcome: Equatable {
        case none
        case commit(CGRect, screen: Int)
        case cancel
    }

    struct Selection: Equatable {
        var rect: CGRect
        var screen: Int
    }

    static let escapeKey: UInt16 = 53
    static let returnKey: UInt16 = 36
    static let keypadEnterKey: UInt16 = 76

    let purpose: Purpose
    private(set) var pending: Selection?
    private var anchor: CGPoint?
    private var current: CGPoint?
    private var dragScreen: Int?

    init(purpose: Purpose) {
        self.purpose = purpose
    }

    var isDragging: Bool { anchor != nil }

    mutating func mouseDown(at point: CGPoint, screen: Int) {
        anchor = point
        current = point
        dragScreen = screen
    }

    mutating func mouseDragged(to point: CGPoint) {
        guard anchor != nil else { return }
        current = point
    }

    mutating func mouseUp(at point: CGPoint, bounds: CGRect) -> Outcome {
        current = point
        let drawn = dragRect(clippedTo: bounds)
        let screen = dragScreen
        anchor = nil
        current = nil
        dragScreen = nil

        guard let drawn, let screen, drawn.width >= 1, drawn.height >= 1 else {
            // A click without a drag: a Snip treats it as cancel, as before. A Clip keeps any
            // region already chosen, so a stray click cannot throw a reviewed selection away.
            return purpose == .snip ? .cancel : .none
        }
        switch purpose {
        case .snip:
            return .commit(drawn, screen: screen)
        case .clip:
            pending = Selection(rect: drawn, screen: screen)
            return .none
        }
    }

    mutating func key(_ keyCode: UInt16) -> Outcome {
        switch keyCode {
        case Self.escapeKey:
            return .cancel
        case Self.returnKey, Self.keypadEnterKey:
            guard purpose == .clip, !isDragging, let pending else { return .none }
            return .commit(pending.rect, screen: pending.screen)
        default:
            return .none
        }
    }

    /// What one display should show: the rectangle being dragged there, otherwise a pending
    /// Clip region that belongs to it.
    func visibleSelection(onScreen screen: Int, bounds: CGRect) -> CGRect? {
        if isDragging {
            return dragScreen == screen ? dragRect(clippedTo: bounds) : nil
        }
        return pending?.screen == screen ? pending?.rect : nil
    }

    /// Whether the review hint belongs on this display.
    func showsReviewHint(onScreen screen: Int) -> Bool {
        return purpose == .clip && !isDragging && pending?.screen == screen
    }

    private func dragRect(clippedTo bounds: CGRect) -> CGRect? {
        guard let anchor, let current else { return nil }
        let raw = CGRect(
            x: min(anchor.x, current.x),
            y: min(anchor.y, current.y),
            width: abs(current.x - anchor.x),
            height: abs(current.y - anchor.y))
        let clipped = raw.intersection(bounds)
        return clipped.isNull ? nil : clipped
    }
}

/// Pure coordinate conversion kept outside the MainActor UI type so multi-display arrangements
/// can be verified without presenting windows.
enum RegionSelectionGeometry {
    static func displaySpaceRect(
        viewRect: CGRect,
        windowOrigin: CGPoint,
        primaryScreenMaxY: CGFloat
    ) -> CGRect {
        let screenRect = CGRect(
            x: viewRect.minX + windowOrigin.x,
            y: viewRect.minY + windowOrigin.y,
            width: viewRect.width,
            height: viewRect.height)
        return CGRect(
            x: screenRect.minX,
            y: primaryScreenMaxY - screenRect.maxY,
            width: screenRect.width,
            height: screenRect.height)
    }
}

/// A borderless window has to opt in to becoming key, or Escape never reaches the view.
private final class OverlayWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

private final class OverlayView: NSView {
    weak var overlay: RegionSelectionOverlay?
    var screenIndex = 0
    /// Only for `renderPreview`; the live view reads its overlay's session.
    var previewSession: RegionSelectionSession?

    override var acceptsFirstResponder: Bool { true }

    // Secondary overlay windows are visible but not initially key. Accept their first click so the
    // user can begin a region on any display without a throwaway activation click.
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .crosshair)
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.withAlphaComponent(0.35).setFill()
        bounds.fill()

        guard let session = overlay?.session ?? previewSession,
              let selection = session.visibleSelection(onScreen: screenIndex, bounds: bounds),
              selection.width >= 1, selection.height >= 1 else { return }

        // Punch the selection back out to true colour so the user sees what they are about to take.
        NSColor.clear.set()
        selection.fill(using: .copy)

        NSColor.white.setStroke()
        let border = NSBezierPath(rect: selection.insetBy(dx: 0.5, dy: 0.5))
        border.lineWidth = 1
        border.stroke()

        var lines = ["\(Int(selection.width.rounded())) x \(Int(selection.height.rounded()))"]
        if session.showsReviewHint(onScreen: screenIndex) {
            lines.append("Return to record  ·  Drag to redraw  ·  Esc to cancel")
        }
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium),
            .foregroundColor: NSColor.white
        ]
        let label = lines.joined(separator: "\n")
        let size = (label as NSString).size(withAttributes: attributes)
        let padding: CGFloat = 6
        var labelOrigin = NSPoint(x: selection.minX, y: selection.maxY + padding)
        if labelOrigin.y + size.height > bounds.maxY {
            labelOrigin.y = selection.minY - size.height - padding
        }
        labelOrigin.x = min(max(bounds.minX + padding, labelOrigin.x), bounds.maxX - size.width - padding)

        let backdrop = NSRect(
            x: labelOrigin.x - padding / 2,
            y: labelOrigin.y - padding / 2,
            width: size.width + padding,
            height: size.height + padding)
        NSColor.black.withAlphaComponent(0.7).setFill()
        NSBezierPath(roundedRect: backdrop, xRadius: 4, yRadius: 4).fill()
        (label as NSString).draw(at: labelOrigin, withAttributes: attributes)
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeKey()
        window?.makeFirstResponder(self)
        overlay?.session.mouseDown(at: convert(event.locationInWindow, from: nil), screen: screenIndex)
        overlay?.apply(.none)
    }

    override func mouseDragged(with event: NSEvent) {
        overlay?.session.mouseDragged(to: convert(event.locationInWindow, from: nil))
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        guard let overlay else { return }
        let outcome = overlay.session.mouseUp(at: convert(event.locationInWindow, from: nil), bounds: bounds)
        overlay.apply(outcome)
    }

    override func keyDown(with event: NSEvent) {
        // Escape cancels; Return accepts a pending Clip region. Nothing else takes keys here.
        guard let overlay else { return }
        overlay.apply(overlay.session.key(event.keyCode))
    }

    override func cancelOperation(_ sender: Any?) {
        overlay?.apply(.cancel)
    }
}
