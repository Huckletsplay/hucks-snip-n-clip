import AppKit

/// Presents each update decision as a copyable alert. Update results are never routine setting
/// notices, so Setting Change Notifications cannot hide them.
final class AppKitUpdatePresenter: UpdatePresenter, @unchecked Sendable {
    func showUpToDate(installed: SemanticVersion, latest: SemanticVersion) async {
        let newer = installed > latest
            ? "\n\nThis copy is newer than the latest published release."
            : ""
        await MainActor.run {
            CopyableAlert.show(
                title: "You’re up to date",
                detail: "Installed: \(installed)\nLatest release: \(latest)\(newer)")
        }
    }

    func showNewerWithoutMacDownload(installed: SemanticVersion, latest: SemanticVersion, releasePage: URL?) async {
        await MainActor.run {
            CopyableAlert.show(
                title: "Update not available for Mac yet",
                detail: "Version \(latest) has been released, but its verified macOS download is not "
                    + "published yet. Installed: \(installed).",
                extraButtonTitle: releasePage == nil ? nil : "Open Release Page",
                extraAction: releasePage.map { page in { NSWorkspace.shared.open(page) } })
        }
    }

    func confirmDownload(_ offer: UpdateOffer) async -> Bool {
        await MainActor.run {
            CopyableAlert.confirm(
                title: "Update available",
                detail: "Version \(offer.available) is available. Installed: \(offer.installed).\n\n"
                    + "Huck’s Snip ’n’ Clip will download the macOS DMG and its published SHA-256 "
                    + "checksum and verify them before anything is opened. Nothing is installed or "
                    + "replaced automatically.",
                confirmButtonTitle: "Download and Verify")
        }
    }

    func confirmOpen(_ offer: UpdateOffer, verifiedDMG: URL, sha256: String) async -> Bool {
        await MainActor.run {
            CopyableAlert.confirm(
                title: "Verified update ready",
                detail: "Installed: \(offer.installed)\nAvailable: \(offer.available)\n"
                    + "Download verified: its SHA-256 matches the published checksum.\n\(sha256)\n\n"
                    + "On a Mac, an update installs from the DMG: open it, drag Huck’s Snip ’n’ Clip "
                    + "onto Applications, and replace the old copy. Your settings, Outputs, shortcuts "
                    + "and captures are kept. Nothing changes if you cancel.",
                confirmButtonTitle: "Open DMG")
        }
    }

    func open(verifiedDMG: URL) async {
        await MainActor.run {
            NSWorkspace.shared.open(verifiedDMG)
            CopyableAlert.show(
                title: "Finish the update",
                detail: """
                    1. Quit Huck’s Snip ’n’ Clip from its menu-bar H.
                    2. In the DMG window, drag Huck’s Snip ’n’ Clip onto Applications and choose Replace.
                    3. Open it again from Applications.

                    This is an unsigned beta. If macOS says it cannot verify the app, open System \
                    Settings > Privacy & Security and choose Open Anyway. If a capture then fails \
                    with no dialog, turn Huck’s Snip ’n’ Clip on again under Screen & System Audio \
                    Recording.
                    """)
        }
    }

    func showFailure(_ error: UpdateError) async {
        await MainActor.run {
            CopyableAlert.show(
                title: error.isSecurityFailure ? "Update stopped for safety" : "Update check failed",
                detail: error.errorDescription ?? "The update check did not complete.")
        }
    }
}
