import AppKit

// The app is an accessory: it lives in the menu bar with no Dock tile and no main window.
//
// Top-level code is not main-actor isolated, so the setup is wrapped explicitly. `delegate` is
// held by this scope for the whole life of the process because NSApplication only weakly
// references its delegate, and `run()` does not return until the app quits.
MainActor.assumeIsolated {
    let application = NSApplication.shared
    let delegate = AppDelegate()
    application.delegate = delegate
    application.setActivationPolicy(.accessory)
    application.run()
}
