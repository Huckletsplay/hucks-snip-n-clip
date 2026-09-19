import AppKit
import Carbon.HIToolbox

/// Captures either one modified shortcut or a PC-style two-step chord without asking for
/// Accessibility permission. The focused panel owns the key events only while rebinding.
final class ShortcutCaptureOverlay {
    private static var active: ShortcutCaptureOverlay?
    private static let firstStrokeTimeoutSeconds = 10.0
    private static let secondStrokeTimeoutSeconds = 3

    private let window: CaptureWindow
    private let completion: (ShortcutBinding?) -> Void
    private var timeoutTimer: Timer?
    private var pendingFirstStroke: ShortcutStroke?
    private var secondStrokeDeadline: Date?
    private var finished = false

    private init(actionName: String, current: String, completion: @escaping (ShortcutBinding?) -> Void) {
        self.completion = completion
        self.window = CaptureWindow(
            contentRect: NSRect(x: 0, y: 0, width: 500, height: 174),
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

        let view = CaptureView(frame: NSRect(origin: .zero, size: window.frame.size))
        view.actionName = actionName
        view.currentBinding = current
        window.contentView = view
    }

    /// The first valid press starts a visible three-second window. Pressing another key creates a
    /// chord; letting the countdown finish saves the first press as an ordinary single shortcut.
    static func present(
        actionName: String,
        current: String,
        completion: @escaping (ShortcutBinding?) -> Void
    ) {
        active?.finish(with: nil)

        let overlay = ShortcutCaptureOverlay(
            actionName: actionName,
            current: current,
            completion: completion)
        active = overlay

        if let view = overlay.window.contentView as? CaptureView {
            view.onFirstStroke = { [weak overlay] stroke in
                overlay?.beginWaitingForSecondStroke(after: stroke)
            }
            view.onSecondStroke = { [weak overlay] stroke in
                overlay?.completeChord(with: stroke)
            }
            view.onCancel = { [weak overlay] in overlay?.finish(with: nil) }
        }

        if let screen = NSScreen.main {
            let frame = screen.visibleFrame
            let size = overlay.window.frame.size
            overlay.window.setFrameOrigin(NSPoint(
                x: frame.midX - size.width / 2,
                y: frame.midY - size.height / 2))
        }

        // Activate first: an LSUIElement app's window can otherwise come up behind the frontmost
        // app and never become key, leaving the prompt visible but deaf.
        NSApp.activate(ignoringOtherApps: true)
        overlay.window.makeKeyAndOrderFront(nil)
        overlay.window.makeFirstResponder(overlay.window.contentView)

        overlay.timeoutTimer = Timer.scheduledTimer(
            withTimeInterval: firstStrokeTimeoutSeconds,
            repeats: false
        ) { _ in
            DispatchQueue.main.async { overlay.finish(with: nil) }
        }
    }

    private func beginWaitingForSecondStroke(after stroke: ShortcutStroke) {
        pendingFirstStroke = stroke
        timeoutTimer?.invalidate()
        let deadline = Date().addingTimeInterval(TimeInterval(Self.secondStrokeTimeoutSeconds))
        secondStrokeDeadline = deadline
        updateSecondStrokeCountdown()

        timeoutTimer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
            DispatchQueue.main.async { self?.updateSecondStrokeCountdown() }
        }
    }

    private func updateSecondStrokeCountdown() {
        guard let first = pendingFirstStroke, let deadline = secondStrokeDeadline else { return }
        let remaining = max(0, deadline.timeIntervalSinceNow)
        let seconds = max(1, Int(ceil(remaining)))
        if let view = window.contentView as? CaptureView {
            view.showPending(first: first, secondsRemaining: seconds)
        }

        if remaining <= 0 {
            finish(with: ShortcutBinding(firstStroke: first))
        }
    }

    private func completeChord(with second: ShortcutStroke) {
        guard let first = pendingFirstStroke else { return }
        finish(with: ShortcutBinding(firstStroke: first, secondStroke: second))
    }

    private func finish(with binding: ShortcutBinding?) {
        guard !finished else { return }
        finished = true
        timeoutTimer?.invalidate()
        timeoutTimer = nil
        window.orderOut(nil)
        window.contentView = nil
        if ShortcutCaptureOverlay.active === self { ShortcutCaptureOverlay.active = nil }
        completion(binding)
    }
}

private final class CaptureWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

private final class CaptureView: NSView {
    var onFirstStroke: ((ShortcutStroke) -> Void)?
    var onSecondStroke: ((ShortcutStroke) -> Void)?
    var onCancel: (() -> Void)?
    var actionName = ""
    var currentBinding = ""
    private var pendingFirstStroke: ShortcutStroke?
    private var secondsRemaining = 0

    override var acceptsFirstResponder: Bool { true }

    func showPending(first: ShortcutStroke, secondsRemaining: Int) {
        pendingFirstStroke = first
        self.secondsRemaining = secondsRemaining
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        let rounded = NSBezierPath(roundedRect: bounds, xRadius: 14, yRadius: 14)
        NSColor.windowBackgroundColor.setFill()
        rounded.fill()
        NSColor.separatorColor.setStroke()
        rounded.lineWidth = 1
        rounded.stroke()

        if let pendingFirstStroke {
            draw("First press: \(pendingFirstStroke.displayString)", size: 15, weight: .semibold, top: 27)
            draw(
                "Press a second key within \(secondsRemaining)s for a sequence.",
                size: 12,
                weight: .regular,
                top: 60)
            draw("Or wait to save only \(pendingFirstStroke.displayString).", size: 11, weight: .regular, top: 88)
            draw("The second key may be used with or without modifiers. Esc cancels.", size: 11, weight: .regular, top: 114)
        } else {
            draw("Press the new shortcut for \(actionName)", size: 15, weight: .semibold, top: 27)
            draw("Currently \(currentBinding)", size: 12, weight: .regular, top: 60)
            draw(
                "First press: include Control, Option, Shift, or Command.",
                size: 11,
                weight: .regular,
                top: 88)
            draw("After that, press a second key or wait 3 seconds. Esc cancels.", size: 11, weight: .regular, top: 114)
        }
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
        let rect = NSRect(x: 16, y: bounds.height - top - size - 4, width: bounds.width - 32, height: size + 8)
        (text as NSString).draw(in: rect, withAttributes: attributes)
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == UInt16(kVK_Escape) {
            onCancel?()
            return
        }

        let requiresModifier = pendingFirstStroke == nil
        guard let stroke = ShortcutCaptureOverlay.stroke(
            keyCode: UInt32(event.keyCode),
            appKitModifiers: event.modifierFlags.intersection(.deviceIndependentFlagsMask),
            requiresModifier: requiresModifier) else {
            NSSound.beep()
            return
        }

        if pendingFirstStroke == nil {
            onFirstStroke?(stroke)
        } else {
            onSecondStroke?(stroke)
        }
    }

    override func cancelOperation(_ sender: Any?) {
        onCancel?()
    }
}

extension ShortcutCaptureOverlay {
    /// Translates an AppKit key event into an ordinary first-stroke binding.
    static func binding(from event: NSEvent) -> ShortcutBinding? {
        return binding(
            keyCode: UInt32(event.keyCode),
            appKitModifiers: event.modifierFlags.intersection(.deviceIndependentFlagsMask))
    }

    /// Retained for callers and tests that want a normal global first stroke.
    static func binding(
        keyCode: UInt32,
        appKitModifiers: NSEvent.ModifierFlags
    ) -> ShortcutBinding? {
        guard let stroke = stroke(
            keyCode: keyCode,
            appKitModifiers: appKitModifiers,
            requiresModifier: true) else { return nil }
        return ShortcutBinding(firstStroke: stroke)
    }

    /// Split out from the event so first- and second-stroke safety can be tested without synthetic
    /// NSEvents. Only keys that round-trip through settings.ini are accepted.
    static func stroke(
        keyCode: UInt32,
        appKitModifiers: NSEvent.ModifierFlags,
        requiresModifier: Bool
    ) -> ShortcutStroke? {
        var modifiers: UInt32 = 0
        if appKitModifiers.contains(.control) { modifiers |= UInt32(controlKey) }
        if appKitModifiers.contains(.option) { modifiers |= UInt32(optionKey) }
        if appKitModifiers.contains(.shift) { modifiers |= UInt32(shiftKey) }
        if appKitModifiers.contains(.command) { modifiers |= UInt32(cmdKey) }

        guard !requiresModifier || modifiers != 0 else { return nil }
        guard ShortcutBinding.keyName(for: keyCode) != nil else { return nil }
        return ShortcutStroke(modifiers: modifiers, keyCode: keyCode)
    }
}
