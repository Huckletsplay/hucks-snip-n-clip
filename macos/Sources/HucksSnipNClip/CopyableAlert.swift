import AppKit

/// Every modal message exposes selectable text, so an exact error can be pasted into
/// troubleshooting instead of retyped from a screenshot. The Windows build makes the same
/// promise through CopyableDialog.
@MainActor
enum CopyableAlert {
    static func show(title: String, detail: String, extraButtonTitle: String? = nil, extraAction: (() -> Void)? = nil) {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = title

        if let extraButtonTitle {
            alert.addButton(withTitle: extraButtonTitle)
        }

        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "Copy Details")

        let textView = NSTextView(frame: NSRect(x: 0, y: 0, width: 420, height: 130))
        textView.string = detail
        textView.isEditable = false
        textView.isSelectable = true
        textView.drawsBackground = false
        textView.font = NSFont.systemFont(ofSize: 12)

        let scrollView = NSScrollView(frame: NSRect(x: 0, y: 0, width: 420, height: 130))
        scrollView.documentView = textView
        scrollView.hasVerticalScroller = true
        scrollView.drawsBackground = false
        alert.accessoryView = scrollView

        NSApp.activate(ignoringOtherApps: true)
        let response = alert.runModal()

        if extraButtonTitle != nil, response == .alertFirstButtonReturn {
            extraAction?()
            return
        }

        let copyResponse: NSApplication.ModalResponse = extraButtonTitle == nil
            ? .alertSecondButtonReturn
            : .alertThirdButtonReturn

        if response == copyResponse {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(detail, forType: .string)
        }
    }

    /// A destructive or consequential confirmation with the same selectable/copyable detail as
    /// every other modal message in the app.
    ///
    /// With `defaultToCancel`, Return means Cancel and the confirm button has no key at all, so
    /// replacing something the user customized takes a deliberate click on it.
    static func confirm(
        title: String,
        detail: String,
        confirmButtonTitle: String,
        defaultToCancel: Bool = false
    ) -> Bool {
        let alert = makeConfirmAlert(
            title: title,
            confirmButtonTitle: confirmButtonTitle,
            defaultToCancel: defaultToCancel)

        let textView = NSTextView(frame: NSRect(x: 0, y: 0, width: 420, height: 90))
        textView.string = detail
        textView.isEditable = false
        textView.isSelectable = true
        textView.drawsBackground = false
        textView.font = NSFont.systemFont(ofSize: 12)
        alert.accessoryView = textView

        NSApp.activate(ignoringOtherApps: true)
        let response = alert.runModal()
        if response == .alertThirdButtonReturn {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(detail, forType: .string)
            return false
        }

        return response == .alertFirstButtonReturn
    }

    static func makeConfirmAlert(title: String, confirmButtonTitle: String, defaultToCancel: Bool) -> NSAlert {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = title
        let confirm = alert.addButton(withTitle: confirmButtonTitle)
        let cancel = alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Copy Details")
        if defaultToCancel {
            confirm.keyEquivalent = ""
            cancel.keyEquivalent = "\r"
        }
        return alert
    }
}
