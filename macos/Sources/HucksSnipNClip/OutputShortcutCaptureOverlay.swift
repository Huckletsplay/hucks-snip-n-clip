import AppKit
import Carbon.HIToolbox

/// Captures one global output-selection shortcut. It requires a modifier because the resulting
/// binding is available while the app is idle, before the user starts a Snip or Clip.
final class OutputShortcutCaptureOverlay {
    private static var active: OutputShortcutCaptureOverlay?
    private static let timeoutSeconds = 10.0

    private let window: OutputShortcutWindow
    private let completion: (ShortcutStroke?) -> Void
    private var timer: Timer?
    private var finished = false

    private init(
        title: String,
        current: String,
        instruction: String,
        completion: @escaping (ShortcutStroke?) -> Void
    ) {
        self.completion = completion
        window = OutputShortcutWindow(
            contentRect: NSRect(x: 0, y: 0, width: 500, height: 154),
            styleMask: .borderless,
            backing: .buffered,
            defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        window.level = .screenSaver
        window.hasShadow = true
        window.hidesOnDeactivate = false
        window.animationBehavior = .none
        window.isMovable = false
        window.isReleasedWhenClosed = false
        window.sharingType = .none
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]

        let view = OutputShortcutView(frame: NSRect(origin: .zero, size: window.frame.size))
        view.title = title
        view.currentShortcut = current
        view.instruction = instruction
        view.onCapture = { [weak self] shortcut in self?.finish(with: shortcut) }
        window.contentView = view
    }

    static func present(
        destinationName: String,
        current: String,
        completion: @escaping (ShortcutStroke?) -> Void
    ) {
        present(
            title: "Select \(destinationName) as Output",
            current: current,
            instruction: "Press one modified key combination to select this output before capture.",
            completion: completion)
    }

    /// The same one-stroke editor, reused for the recording-only Pause/Resume and Stop keys.
    static func present(
        title: String,
        current: String,
        instruction: String,
        completion: @escaping (ShortcutStroke?) -> Void
    ) {
        active?.finish(with: nil)
        let overlay = OutputShortcutCaptureOverlay(
            title: title,
            current: current,
            instruction: instruction,
            completion: completion)
        active = overlay

        if let screen = NSScreen.main {
            let frame = screen.visibleFrame
            let size = overlay.window.frame.size
            overlay.window.setFrameOrigin(NSPoint(
                x: frame.midX - size.width / 2,
                y: frame.midY - size.height / 2))
        }

        NSApp.activate(ignoringOtherApps: true)
        overlay.window.makeKeyAndOrderFront(nil)
        overlay.window.makeFirstResponder(overlay.window.contentView)
        overlay.timer = Timer.scheduledTimer(withTimeInterval: timeoutSeconds, repeats: false) {
            [weak overlay] _ in
            overlay?.finish(with: nil)
        }
    }

    private func finish(with shortcut: ShortcutStroke?) {
        guard !finished else { return }
        finished = true
        timer?.invalidate()
        timer = nil
        window.orderOut(nil)
        window.contentView = nil
        if Self.active === self { Self.active = nil }
        completion(shortcut)
    }
}

private final class OutputShortcutWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

private final class OutputShortcutView: NSView {
    var onCapture: ((ShortcutStroke?) -> Void)?
    var title = ""
    var currentShortcut = ""
    var instruction = ""

    override var acceptsFirstResponder: Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        let rounded = NSBezierPath(roundedRect: bounds, xRadius: 14, yRadius: 14)
        NSColor.windowBackgroundColor.setFill()
        rounded.fill()
        NSColor.separatorColor.setStroke()
        rounded.lineWidth = 1
        rounded.stroke()

        draw(title, size: 15, weight: .semibold, top: 27)
        draw("Currently \(currentShortcut)", size: 12, weight: .regular, top: 59)
        draw(instruction, size: 11, weight: .regular, top: 87)
        draw("Esc cancels. This closes by itself after 10 seconds.",
             size: 11, weight: .regular, top: 109)
    }

    private func draw(_ text: String, size: CGFloat, weight: NSFont.Weight, top: CGFloat) {
        let style = NSMutableParagraphStyle()
        style.alignment = .center
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: size, weight: weight),
            .foregroundColor: weight == .semibold
                ? NSColor.labelColor
                : NSColor.secondaryLabelColor,
            .paragraphStyle: style
        ]
        let rect = NSRect(
            x: 16,
            y: bounds.height - top - size - 4,
            width: bounds.width - 32,
            height: size + 8)
        (text as NSString).draw(in: rect, withAttributes: attributes)
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == UInt16(kVK_Escape) {
            onCapture?(nil)
            return
        }

        guard let shortcut = ShortcutCaptureOverlay.stroke(
            keyCode: UInt32(event.keyCode),
            appKitModifiers: event.modifierFlags.intersection(.deviceIndependentFlagsMask),
            requiresModifier: true) else {
            NSSound.beep()
            return
        }
        onCapture?(shortcut)
    }

    override func cancelOperation(_ sender: Any?) {
        onCapture?(nil)
    }
}
