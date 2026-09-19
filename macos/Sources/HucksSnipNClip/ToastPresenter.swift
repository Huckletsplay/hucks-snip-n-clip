import AppKit

/// The brief confirmation after a capture. It never takes focus, never needs dismissing, and
/// is excluded from ScreenCaptureKit output so feedback can safely appear during recording.
@MainActor
enum ToastPresenter {
    private static var panel: NSPanel?
    private static var dismissTimer: Timer?

    static func show(_ message: String) {
        dismissTimer?.invalidate()
        panel?.orderOut(nil)
        panel = nil

        guard let screen = NSScreen.main else { return }

        let font = NSFont.systemFont(ofSize: 13, weight: .medium)
        let attributes: [NSAttributedString.Key: Any] = [
            .font: font,
            .foregroundColor: NSColor.white
        ]
        let textSize = (message as NSString).size(withAttributes: attributes)
        let horizontalPadding: CGFloat = 18
        let verticalPadding: CGFloat = 12
        let width = min(textSize.width + horizontalPadding * 2, 520)
        let height = textSize.height + verticalPadding * 2

        let frame = NSRect(
            x: screen.visibleFrame.maxX - width - 24,
            y: screen.visibleFrame.minY + 24,
            width: width,
            height: height)

        let toast = NSPanel(
            contentRect: frame,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false)
        toast.isOpaque = false
        toast.backgroundColor = .clear
        toast.level = .statusBar
        toast.ignoresMouseEvents = true
        toast.hasShadow = true
        toast.sharingType = .none
        toast.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]

        let content = ToastView(frame: NSRect(origin: .zero, size: frame.size))
        content.message = message
        toast.contentView = content
        toast.alphaValue = 0
        toast.orderFrontRegardless()

        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.12
            toast.animator().alphaValue = 1
        }

        panel = toast
        dismissTimer = Timer.scheduledTimer(withTimeInterval: 1.8, repeats: false) { _ in
            Task { @MainActor in
                fadeOut(toast)
            }
        }
    }

    private static func fadeOut(_ toast: NSPanel) {
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.25
            toast.animator().alphaValue = 0
        } completionHandler: {
            toast.orderOut(nil)
            if panel === toast { panel = nil }
        }
    }
}

private final class ToastView: NSView {
    var message: String = "" {
        didSet { needsDisplay = true }
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.withAlphaComponent(0.82).setFill()
        NSBezierPath(roundedRect: bounds, xRadius: 10, yRadius: 10).fill()

        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 13, weight: .medium),
            .foregroundColor: NSColor.white
        ]
        let size = (message as NSString).size(withAttributes: attributes)
        (message as NSString).draw(
            at: NSPoint(x: (bounds.width - size.width) / 2, y: (bounds.height - size.height) / 2),
            withAttributes: attributes)
    }
}
