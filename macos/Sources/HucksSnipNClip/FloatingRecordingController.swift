import AppKit

/// The controller's design grid, shared by drawing, hit testing, settings validation and tests.
/// Values match the accepted Windows controller: the H's 64-unit square with two buttons under its
/// posts, exactly as wide as the posts. Design y is measured down from the top, as on Windows;
/// the rectangle helpers convert to AppKit's bottom-up view coordinates.
enum FloatingControllerMetrics {
    static let defaultOpacityPercent = 50
    static let opacityRange = 10...100
    /// Size is the width of the H's own 64-unit square, in points. One number is the whole scale.
    static let defaultSize = 72
    static let sizeRange = 44...220

    static let designWidth: CGFloat = 64
    static let designHeight: CGFloat = 78
    static let hSquare: CGFloat = 64
    static let hBodyTop: CGFloat = 6
    static let hBodyBottom: CGFloat = 58
    static let leftPostX: CGFloat = 6
    static let rightPostX: CGFloat = 35
    static let postWidth: CGFloat = 23
    static let buttonTop: CGFloat = 60
    static let buttonHeight: CGFloat = 16
    static let gripSide: CGFloat = 12

    static func normalizeOpacity(_ percent: Int) -> Int {
        return min(opacityRange.upperBound, max(opacityRange.lowerBound, percent))
    }

    static func normalizeSize(_ size: Int) -> Int {
        return min(sizeRange.upperBound, max(sizeRange.lowerBound, size))
    }

    static func windowSize(for size: Int) -> CGSize {
        let scale = CGFloat(normalizeSize(size)) / hSquare
        return CGSize(width: (designWidth * scale).rounded(), height: (designHeight * scale).rounded())
    }

    static func leftButton(size: Int) -> CGRect {
        return rect(leftPostX, buttonTop, postWidth, buttonHeight, size: size)
    }

    static func rightButton(size: Int) -> CGRect {
        return rect(rightPostX, buttonTop, postWidth, buttonHeight, size: size)
    }

    static func leftPost(size: Int) -> CGRect {
        return rect(leftPostX, hBodyTop, postWidth, hBodyBottom - hBodyTop, size: size)
    }

    static func rightPost(size: Int) -> CGRect {
        return rect(rightPostX, hBodyTop, postWidth, hBodyBottom - hBodyTop, size: size)
    }

    /// The resize notch: the upper-right triangle of the right post's top corner.
    static func grip(size: Int) -> [CGPoint] {
        let scale = CGFloat(normalizeSize(size)) / hSquare
        let right = (rightPostX + postWidth) * scale
        let top = (designHeight - hBodyTop) * scale
        let span = gripSide * scale
        return [
            CGPoint(x: right - span, y: top),
            CGPoint(x: right, y: top),
            CGPoint(x: right, y: top - span)
        ]
    }

    static func gripContains(_ point: CGPoint, size: Int) -> Bool {
        let triangle = grip(size: size)
        let path = NSBezierPath()
        path.move(to: triangle[0])
        path.line(to: triangle[1])
        path.line(to: triangle[2])
        path.close()
        return path.contains(point)
    }

    /// A drag that moves right or up makes the controller larger - the direction the notch points.
    static func resizedSize(from startSize: Int, dragDelta: CGSize) -> Int {
        return normalizeSize(startSize + Int((dragDelta.width + dragDelta.height).rounded()))
    }

    /// Rounds edges rather than origin and size separately, so a button and the post above it
    /// that share an edge in the design grid still share it once scaled.
    private static func rect(
        _ x: CGFloat, _ y: CGFloat, _ width: CGFloat, _ height: CGFloat, size: Int
    ) -> CGRect {
        let scale = CGFloat(normalizeSize(size)) / hSquare
        let left = (x * scale).rounded()
        let right = ((x + width) * scale).rounded()
        let bottom = ((designHeight - y - height) * scale).rounded()
        let top = ((designHeight - y) * scale).rounded()
        return CGRect(x: left, y: bottom, width: right - left, height: top - bottom)
    }
}

/// The recording-only floating H.
///
/// Huck's Snip 'n' Clip has no persistent main window, and this does not become one: it exists only
/// while a clip is recording and is closed when a stop is requested. It carries the live H in white
/// with Pause/Resume and Stop directly under the H's two posts, so a recording can be controlled
/// with the menu bar hidden.
///
/// **There is no panel behind it.** The window is non-opaque with a clear background and draws only
/// the H, its two buttons and the notch. Click-through is the window server's doing, not AppKit's:
/// a mouse-down on a fully transparent pixel of a non-opaque window is delivered to whatever window
/// is underneath, even in another app, so the rectangular frame blocks nothing it does not draw.
/// Measured on 2026-09-19 with another app's clickable window under the controller. `probe` ties
/// the drawn alpha to `hitTest` so the two cannot drift apart.
///
/// It never becomes key or main, is a non-activating panel so clicking it does not bring the app
/// forward, and ignores window cycling. The app itself is an agent with no Dock or Command-Tab
/// entry. Right-button dragging the H moves it; right-dragging the notch resizes it; left clicks
/// are left free for its controls.
///
/// Keeping it out of recordings is the caller's job and is decided before it is shown: Screen and
/// Region filters exclude this whole process, and Window filters contain only the target window.
/// If neither holds, the caller must not show it. `sharingType = .none` is a second guard only.
@MainActor
final class FloatingRecordingController {
    private let panel: ControllerPanel
    private let view: ControllerView

    var onPauseResume: (() -> Void)?
    var onStop: (() -> Void)?
    /// After a right-drag ends, so the current layout can learn the new position.
    var onMoved: (() -> Void)?
    /// After a notch resize ends, so the chosen size can be remembered.
    var onResized: (() -> Void)?

    var frame: CGRect { panel.frame }
    var controllerSize: Int { view.size }
    var windowNumber: Int { panel.windowNumber }

    private init(origin: CGPoint, size: Int, opacityPercent: Int) {
        let normalized = FloatingControllerMetrics.normalizeSize(size)
        let windowSize = FloatingControllerMetrics.windowSize(for: normalized)
        panel = ControllerPanel(
            contentRect: CGRect(origin: origin, size: windowSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false)
        view = ControllerView(frame: CGRect(origin: .zero, size: windowSize), size: normalized)

        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.isFloatingPanel = true
        // Above the Region shade (.modalPanel) so the controls are never dimmed, below menus.
        panel.level = .statusBar
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.isMovable = false
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .none
        panel.acceptsMouseMovedEvents = true
        panel.sharingType = .none
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.alphaValue = CGFloat(FloatingControllerMetrics.normalizeOpacity(opacityPercent)) / 100
        panel.contentView = view

        view.onPauseResume = { [weak self] in self?.onPauseResume?() }
        view.onStop = { [weak self] in self?.onStop?() }
        view.onMoveDrag = { [weak self] origin in self?.panel.setFrameOrigin(origin) }
        view.onMoveEnded = { [weak self] in self?.onMoved?() }
        view.onResizeDrag = { [weak self] newSize in self?.apply(size: newSize) }
        view.onResizeEnded = { [weak self] in self?.onResized?() }
    }

    static func show(origin: CGPoint, size: Int, opacityPercent: Int) -> FloatingRecordingController {
        let controller = FloatingRecordingController(
            origin: origin,
            size: size,
            opacityPercent: opacityPercent)
        controller.panel.orderFrontRegardless()
        return controller
    }

    func move(to origin: CGPoint) {
        panel.setFrameOrigin(origin)
    }

    func update(inputLevel: Double, strainLevel: Double) {
        view.inputLevel = inputLevel
        view.strainLevel = strainLevel
        view.needsDisplay = true
    }

    func setPaused(_ paused: Bool) {
        guard view.paused != paused else { return }
        view.paused = paused
        view.needsDisplay = true
    }

    /// Disables both controls, so a second click cannot request a stop already happening.
    func setFinishing() {
        view.finishing = true
        view.needsDisplay = true
    }

    func setOpacityPercent(_ percent: Int) {
        panel.alphaValue = CGFloat(FloatingControllerMetrics.normalizeOpacity(percent)) / 100
    }

    func dismiss() {
        view.finishing = true
        panel.orderOut(nil)
        panel.contentView = nil
        panel.close()
    }

    /// The notch is at the top right, so the bottom-left corner - AppKit's frame origin - stays put.
    private func apply(size newSize: Int) {
        guard newSize != view.size else { return }
        let windowSize = FloatingControllerMetrics.windowSize(for: newSize)
        view.size = newSize
        panel.setFrame(CGRect(origin: panel.frame.origin, size: windowSize), display: true)
        view.frame = CGRect(origin: .zero, size: windowSize)
        view.needsDisplay = true
    }
}

/// ScreenCaptureKit lists - and so can exclude - an application only through an on-screen window it
/// owns. On current macOS the menu-bar H is hosted outside this process, so an idle Huck's owns no
/// window at all and a Screen or Region filter would come back unable to exclude it. This invisible
/// one-point window exists only while such a filter is being built, so the app can be named in it.
/// Once named, every window the app shows later - controller, shade, toasts - is excluded too.
@MainActor
final class CaptureExclusionAnchor {
    private let window: NSWindow

    init() {
        let origin = NSScreen.screens.first?.frame.origin ?? .zero
        window = NSWindow(
            contentRect: CGRect(origin: origin, size: CGSize(width: 1, height: 1)),
            styleMask: .borderless,
            backing: .buffered,
            defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = false
        window.ignoresMouseEvents = true
        window.isReleasedWhenClosed = false
        // A covered window does not count as on screen, so a full-screen app would hide the anchor
        // from ScreenCaptureKit. Nothing ordinary sits above this level.
        window.level = .screenSaver
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient, .ignoresCycle]
        window.orderFrontRegardless()
    }

    func dismiss() {
        window.orderOut(nil)
        window.close()
    }
}

extension FloatingRecordingController {
    /// Renders a real controller view off screen and reports, for each view-space point, the drawn
    /// alpha and whether `hitTest` claims it. A point the controller does not draw must have alpha
    /// 0 - that is what lets the window server pass the click on - and must not be claimed.
    static func probe(size: Int, at points: [CGPoint]) -> [(alpha: CGFloat, claimed: Bool)] {
        let normalized = FloatingControllerMetrics.normalizeSize(size)
        let bounds = CGRect(origin: .zero, size: FloatingControllerMetrics.windowSize(for: normalized))
        let view = ControllerView(frame: bounds, size: normalized)
        guard let bitmap = view.bitmapImageRepForCachingDisplay(in: bounds) else { return [] }
        view.cacheDisplay(in: bounds, to: bitmap)
        let scaleX = CGFloat(bitmap.pixelsWide) / bounds.width
        let scaleY = CGFloat(bitmap.pixelsHigh) / bounds.height
        return points.map { point in
            let x = min(bitmap.pixelsWide - 1, Int(point.x * scaleX))
            let y = min(bitmap.pixelsHigh - 1, Int((bounds.height - point.y) * scaleY))
            let alpha = bitmap.colorAt(x: x, y: y)?.alphaComponent ?? -1
            return (alpha, view.hitTest(point) != nil)
        }
    }
}

private final class ControllerPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

private final class ControllerView: NSView {
    private static let glyph = NSColor(calibratedWhite: 70.0 / 255.0, alpha: 1)
    private static let glyphHover = NSColor(calibratedWhite: 40.0 / 255.0, alpha: 1)
    private static let dimBody = NSColor(calibratedWhite: 168.0 / 255.0, alpha: 1)
    private static let gripResting = NSColor(calibratedWhite: 120.0 / 255.0, alpha: 1)
    private static let gripActive = NSColor(calibratedWhite: 40.0 / 255.0, alpha: 1)

    var size: Int
    var inputLevel = 0.0
    var strainLevel = 0.0
    var paused = false
    var finishing = false

    var onPauseResume: (() -> Void)?
    var onStop: (() -> Void)?
    var onMoveDrag: ((CGPoint) -> Void)?
    var onMoveEnded: (() -> Void)?
    var onResizeDrag: ((Int) -> Void)?
    var onResizeEnded: (() -> Void)?

    private enum Drag {
        case move(offset: CGSize)
        case resize(startMouse: CGPoint, startSize: Int)
    }

    private var drag: Drag?
    private var pressedLeft = false
    private var pressedRight = false
    private var hoverLeft = false
    private var hoverRight = false
    private var hoverGrip = false
    private var pointerInside = false

    init(frame: CGRect, size: Int) {
        self.size = size
        super.init(frame: frame)
        addTrackingArea(NSTrackingArea(
            rect: .zero,
            options: [.mouseEnteredAndExited, .mouseMoved, .activeAlways, .inVisibleRect],
            owner: self,
            userInfo: nil))
    }

    required init?(coder: NSCoder) {
        nil
    }

    override var isOpaque: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override var acceptsFirstResponder: Bool { false }

    private var leftButton: CGRect { FloatingControllerMetrics.leftButton(size: size) }
    private var rightButton: CGRect { FloatingControllerMetrics.rightButton(size: size) }

    /// Agrees with the drawn pixels. This does not create click-through - the window server already
    /// routes clicks on transparent pixels elsewhere - it keeps AppKit from treating a point that
    /// reaches this view anyway as one of the controller's own.
    override func hitTest(_ point: NSPoint) -> NSView? {
        let local = convert(point, from: superview)
        guard bounds.contains(local) else { return nil }
        let scale = CGFloat(size) / FloatingControllerMetrics.hSquare
        let hOrigin = (FloatingControllerMetrics.designHeight - FloatingControllerMetrics.hSquare) * scale
        let hPoint = CGPoint(x: local.x, y: local.y - hOrigin)
        if leftButton.contains(local) || rightButton.contains(local)
            || TrayIconFactory.controllerHPath(scale: scale).contains(hPoint) {
            return self
        }
        return nil
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.clear.setFill()
        bounds.fill(using: .copy)

        let scale = CGFloat(size) / FloatingControllerMetrics.hSquare
        NSGraphicsContext.saveGraphicsState()
        let transform = NSAffineTransform()
        transform.translateX(
            by: 0,
            yBy: (FloatingControllerMetrics.designHeight - FloatingControllerMetrics.hSquare) * scale)
        transform.concat()
        TrayIconFactory.drawControllerH(
            scale: scale,
            inputLevel: inputLevel,
            strainLevel: strainLevel,
            paused: paused,
            finishing: finishing)
        NSGraphicsContext.restoreGraphicsState()

        if pointerInside || isResizing {
            let triangle = FloatingControllerMetrics.grip(size: size)
            let path = NSBezierPath()
            path.move(to: triangle[0])
            path.line(to: triangle[1])
            path.line(to: triangle[2])
            path.close()
            (hoverGrip || isResizing ? Self.gripActive : Self.gripResting).setFill()
            path.fill()
        }

        drawButton(leftButton, scale: scale, hover: hoverLeft, isPause: true)
        drawButton(rightButton, scale: scale, hover: hoverRight, isPause: false)
    }

    private var isResizing: Bool {
        if case .resize = drag { return true }
        return false
    }

    private func drawButton(_ rect: CGRect, scale: CGFloat, hover: Bool, isPause: Bool) {
        let radius = 3 * scale
        (finishing ? Self.dimBody : NSColor.white).setFill()
        NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()

        (hover && !finishing ? Self.glyphHover : Self.glyph).setFill()
        let cx = rect.midX
        let cy = rect.midY
        if !isPause {
            let side = 7 * scale
            NSBezierPath.fill(CGRect(x: cx - side / 2, y: cy - side / 2, width: side, height: side))
        } else if paused {
            let width = 7 * scale
            let height = 8 * scale
            let play = NSBezierPath()
            play.move(to: CGPoint(x: cx - width / 2, y: cy + height / 2))
            play.line(to: CGPoint(x: cx + width / 2, y: cy))
            play.line(to: CGPoint(x: cx - width / 2, y: cy - height / 2))
            play.close()
            play.fill()
        } else {
            let barWidth = 2.5 * scale
            let barHeight = 8 * scale
            let gap = 2.5 * scale
            NSBezierPath.fill(CGRect(
                x: cx - gap / 2 - barWidth, y: cy - barHeight / 2, width: barWidth, height: barHeight))
            NSBezierPath.fill(CGRect(
                x: cx + gap / 2, y: cy - barHeight / 2, width: barWidth, height: barHeight))
        }
    }

    // MARK: - Left button: the two controls

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        pressedLeft = !finishing && leftButton.contains(point)
        pressedRight = !finishing && rightButton.contains(point)
    }

    override func mouseUp(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        defer {
            pressedLeft = false
            pressedRight = false
        }
        guard !finishing else { return }
        if pressedLeft && leftButton.contains(point) {
            onPauseResume?()
        } else if pressedRight && rightButton.contains(point) {
            onStop?()
        }
    }

    // MARK: - Right button: move, or resize from the notch

    override func rightMouseDown(with event: NSEvent) {
        guard let window else { return }
        let point = convert(event.locationInWindow, from: nil)
        let mouse = NSEvent.mouseLocation
        if FloatingControllerMetrics.gripContains(point, size: size) {
            drag = .resize(startMouse: mouse, startSize: size)
        } else {
            drag = .move(offset: CGSize(
                width: mouse.x - window.frame.origin.x,
                height: mouse.y - window.frame.origin.y))
        }
        needsDisplay = true
    }

    override func rightMouseDragged(with event: NSEvent) {
        let mouse = NSEvent.mouseLocation
        switch drag {
        case .move(let offset):
            onMoveDrag?(CGPoint(x: (mouse.x - offset.width).rounded(), y: (mouse.y - offset.height).rounded()))
        case .resize(let start, let startSize):
            let wanted = FloatingControllerMetrics.resizedSize(
                from: startSize,
                dragDelta: CGSize(width: mouse.x - start.x, height: mouse.y - start.y))
            onResizeDrag?(wanted)
        case nil:
            break
        }
    }

    override func rightMouseUp(with event: NSEvent) {
        let finished = drag
        drag = nil
        needsDisplay = true
        switch finished {
        case .move:
            onMoveEnded?()
        case .resize:
            onResizeEnded?()
        case nil:
            break
        }
    }

    // MARK: - Hover

    override func mouseMoved(with event: NSEvent) {
        updateHover(convert(event.locationInWindow, from: nil), inside: true)
    }

    override func mouseEntered(with event: NSEvent) {
        updateHover(convert(event.locationInWindow, from: nil), inside: true)
    }

    override func mouseExited(with event: NSEvent) {
        guard drag == nil else { return }
        updateHover(.zero, inside: false)
    }

    private func updateHover(_ point: CGPoint, inside: Bool) {
        let left = inside && !finishing && leftButton.contains(point)
        let right = inside && !finishing && rightButton.contains(point)
        let grip = inside && FloatingControllerMetrics.gripContains(point, size: size)
        guard left != hoverLeft || right != hoverRight || grip != hoverGrip || inside != pointerInside else {
            return
        }
        hoverLeft = left
        hoverRight = right
        hoverGrip = grip
        pointerInside = inside
        needsDisplay = true
    }
}
