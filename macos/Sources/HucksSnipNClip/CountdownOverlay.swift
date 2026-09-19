import AppKit
import CoreGraphics

/// A temporary 3–2–1 surface shown before recording begins. It disappears completely before the
/// ScreenCaptureKit stream starts, so the countdown is never burned into the finished clip.
@MainActor
final class CountdownOverlay {
    private static var active: CountdownOverlay?

    private let window: CountdownWindow
    private let label: NSTextField
    private var task: Task<Void, Never>?
    private var continuation: CheckedContinuation<Bool, Never>?

    private init(displayID: CGDirectDisplayID) {
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        let screen = NSScreen.screens.first {
            ($0.deviceDescription[key] as? NSNumber)?.uint32Value == displayID
        } ?? NSScreen.main ?? NSScreen.screens[0]

        let size = NSSize(width: 180, height: 180)
        let origin = NSPoint(
            x: screen.frame.midX - size.width / 2,
            y: screen.frame.midY - size.height / 2)
        window = CountdownWindow(
            contentRect: NSRect(origin: origin, size: size),
            styleMask: .borderless,
            backing: .buffered,
            defer: false)
        window.isOpaque = false
        window.backgroundColor = NSColor.black.withAlphaComponent(0.78)
        window.level = .screenSaver
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        window.hasShadow = true

        label = NSTextField(labelWithString: "3")
        label.alignment = .center
        label.font = NSFont.monospacedDigitSystemFont(ofSize: 96, weight: .bold)
        label.textColor = .white
        label.frame = NSRect(x: 0, y: 28, width: size.width, height: 120)

        let view = CountdownView(frame: NSRect(origin: .zero, size: size))
        view.wantsLayer = true
        view.layer?.cornerRadius = 28
        view.layer?.masksToBounds = true
        view.onCancel = { [weak self] in self?.finish(completed: false) }
        view.addSubview(label)
        window.contentView = view
    }

    static func present(on displayID: CGDirectDisplayID) async -> Bool {
        active?.finish(completed: false)
        let overlay = CountdownOverlay(displayID: displayID)
        active = overlay
        return await overlay.run()
    }

    private func run() async -> Bool {
        await withCheckedContinuation { continuation in
            self.continuation = continuation
            NSApp.activate(ignoringOtherApps: true)
            window.makeKeyAndOrderFront(nil)
            window.makeFirstResponder(window.contentView)

            task = Task { @MainActor [weak self] in
                guard let self else { return }
                for value in [3, 2, 1] {
                    label.stringValue = String(value)
                    try? await Task.sleep(nanoseconds: 1_000_000_000)
                    if Task.isCancelled { return }
                }
                finish(completed: true)
            }
        }
    }

    private func finish(completed: Bool) {
        guard CountdownOverlay.active === self else { return }
        CountdownOverlay.active = nil
        task?.cancel()
        task = nil
        window.orderOut(nil)
        continuation?.resume(returning: completed)
        continuation = nil
    }
}

private final class CountdownWindow: NSWindow {
    override var canBecomeKey: Bool { true }
}

private final class CountdownView: NSView {
    var onCancel: (() -> Void)?
    override var acceptsFirstResponder: Bool { true }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            onCancel?()
        } else {
            super.keyDown(with: event)
        }
    }

    override func cancelOperation(_ sender: Any?) {
        onCancel?()
    }
}
