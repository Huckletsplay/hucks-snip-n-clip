import Foundation
import ServiceManagement

enum LaunchAtLoginState: Equatable {
    case off
    case on
    case requiresApproval
}

enum LaunchAtLoginCheckmark: Equatable {
    case off
    case on
    case mixed
}

struct LaunchAtLoginPresentation: Equatable {
    let title: String
    let checkmark: LaunchAtLoginCheckmark
    let toolTip: String
}

/// Keeps the menu honest by reading ServiceManagement directly. This deliberately does not mirror
/// the choice into settings.ini: macOS is the authority and a saved preference could disagree after
/// the user changes Login Items in System Settings.
enum LaunchAtLoginService {
    static var state: LaunchAtLoginState {
        switch SMAppService.mainApp.status {
        case .notRegistered, .notFound:
            // A never-registered main app reports .notFound on macOS 26. Registration is still the
            // correct first operation; disabling the row here would make first use impossible.
            return .off
        case .enabled:
            return .on
        case .requiresApproval:
            return .requiresApproval
        @unknown default:
            return .off
        }
    }

    static func presentation(for state: LaunchAtLoginState) -> LaunchAtLoginPresentation {
        switch state {
        case .off:
            return LaunchAtLoginPresentation(
                title: "Start at Login",
                checkmark: .off,
                toolTip: "Open Huck’s Snip ’n’ Clip automatically after you sign in.")
        case .on:
            return LaunchAtLoginPresentation(
                title: "Start at Login",
                checkmark: .on,
                toolTip: "Huck’s Snip ’n’ Clip will open automatically after you sign in.")
        case .requiresApproval:
            return LaunchAtLoginPresentation(
                title: "Start at Login — Needs Approval…",
                checkmark: .mixed,
                toolTip: "macOS has this login item turned off. Choose this to open Login Items settings.")
        }
    }

    static func register() throws {
        try SMAppService.mainApp.register()
    }

    static func unregister() throws {
        try SMAppService.mainApp.unregister()
    }

    static func openSystemSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }
}
