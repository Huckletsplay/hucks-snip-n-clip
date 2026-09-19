import AppKit
import CoreGraphics

/// Keeps the live Region target obvious after selection: the recorded rectangle stays at true
/// colour while the rest of the desktop is gently dimmed. The window belongs to this process,
/// which Region recording excludes from its ScreenCaptureKit filter, and sharingType is disabled
/// as a second guard against the guide appearing in the saved clip.
@MainActor
final class RegionRecordingShade {
    private let window: NSWindow

    init?(displaySpaceRegion: CGRect) {
        guard let primary = NSScreen.screens.first else { return nil }

        var desktopFrame = NSRect.zero
        for screen in NSScreen.screens {
            desktopFrame = desktopFrame.isEmpty ? screen.frame : desktopFrame.union(screen.frame)
        }
        guard !desktopFrame.isEmpty else { return nil }

        let screenRegion = Self.screenSpaceRect(
            for: displaySpaceRegion,
            primaryScreenMaxY: primary.frame.maxY)
        let viewRegion = screenRegion.offsetBy(
            dx: -desktopFrame.origin.x,
            dy: -desktopFrame.origin.y)

        window = NSWindow(
            contentRect: desktopFrame,
            styleMask: .borderless,
            backing: .buffered,
            defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        // Stay above ordinary and floating application windows, but below the menu bar and its
        // pop-up menu so Stop Clip Region remains easy to reach.
        window.level = .modalPanel
        window.ignoresMouseEvents = true
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        window.hasShadow = false
        window.sharingType = .none
        window.contentView = RegionRecordingShadeView(
            frame: NSRect(origin: .zero, size: desktopFrame.size),
            clearRegion: viewRegion)
    }

    func show() {
        window.orderFrontRegardless()
    }

    func dismiss() {
        window.orderOut(nil)
    }

    nonisolated static func screenSpaceRect(
        for displaySpaceRect: CGRect,
        primaryScreenMaxY: CGFloat
    ) -> CGRect {
        CGRect(
            x: displaySpaceRect.minX,
            y: primaryScreenMaxY - displaySpaceRect.maxY,
            width: displaySpaceRect.width,
            height: displaySpaceRect.height)
    }
}

private final class RegionRecordingShadeView: NSView {
    private let clearRegion: NSRect

    init(frame frameRect: NSRect, clearRegion: NSRect) {
        self.clearRegion = clearRegion
        super.init(frame: frameRect)
    }

    required init?(coder: NSCoder) {
        nil
    }

    override var isOpaque: Bool { false }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.withAlphaComponent(0.20).setFill()
        bounds.fill()

        // The selected Region remains completely unaltered, including at its edges.
        NSColor.clear.setFill()
        clearRegion.fill(using: .copy)
    }
}
