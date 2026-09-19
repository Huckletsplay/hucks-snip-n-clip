import AppKit

/// A compact modal editor launched from the menu bar. Preview and validation update with every
/// keystroke, so an unsafe template can never reach the Save button or the destination folder.
@MainActor
final class FilenameSettingsPanel: NSObject, NSTextFieldDelegate {
    private let alert = NSAlert()
    private let labelField: NSTextField
    private let templateField: NSTextField
    private let previewField = NSTextField(labelWithString: "")
    private let nextCounter: Int

    private init(configuration: CaptureFilenameConfiguration) {
        labelField = NSTextField(string: configuration.label)
        templateField = NSTextField(string: configuration.template)
        nextCounter = configuration.nextCounter
        super.init()

        alert.messageText = "Capture Filenames"
        alert.informativeText = "A blank label becomes HucksSnipNClip. The app adds .png, .mp4, or .mov.\n"
            + "Placeholders: "
            + CaptureFilenameTemplate.supportedPlaceholders.map { "{\($0)}" }.joined(separator: ", ")
        alert.addButton(withTitle: "Save")
        alert.addButton(withTitle: "Cancel")

        let accessory = NSView(frame: NSRect(x: 0, y: 0, width: 520, height: 152))
        let labelTitle = NSTextField(labelWithString: "Label")
        labelTitle.frame = NSRect(x: 0, y: 126, width: 520, height: 18)
        labelField.frame = NSRect(x: 0, y: 98, width: 520, height: 24)
        labelField.placeholderString = "HucksSnipNClip"

        let templateTitle = NSTextField(labelWithString: "Template")
        templateTitle.frame = NSRect(x: 0, y: 70, width: 520, height: 18)
        templateField.frame = NSRect(x: 0, y: 42, width: 520, height: 24)

        previewField.frame = NSRect(x: 0, y: 0, width: 520, height: 34)
        previewField.maximumNumberOfLines = 2
        previewField.lineBreakMode = .byTruncatingMiddle
        previewField.font = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)

        for view in [labelTitle, labelField, templateTitle, templateField, previewField] {
            accessory.addSubview(view)
        }
        alert.accessoryView = accessory
        labelField.delegate = self
        templateField.delegate = self
        updatePreview()
    }

    static func present(configuration: CaptureFilenameConfiguration)
        -> CaptureFilenameConfiguration? {
        let panel = FilenameSettingsPanel(configuration: configuration)
        NSApp.activate(ignoringOtherApps: true)
        guard panel.alert.runModal() == .alertFirstButtonReturn else { return nil }

        var result = configuration
        result.label = panel.labelField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        result.template = panel.templateField.stringValue
        return try? CaptureFilenameTemplate.normalized(result)
    }

    func controlTextDidChange(_ notification: Notification) {
        updatePreview()
    }

    private func updatePreview() {
        var configuration = CaptureFilenameConfiguration(
            label: labelField.stringValue,
            template: templateField.stringValue,
            nextCounter: nextCounter)
        do {
            configuration = try CaptureFilenameTemplate.normalized(configuration)
            let now = Date()
            let snip = try CaptureFilenameTemplate.stem(
                configuration: configuration,
                kind: .snip,
                capturedAt: now,
                counter: nextCounter) + ".png"
            let clip = try CaptureFilenameTemplate.stem(
                configuration: configuration,
                kind: .clip,
                capturedAt: now,
                counter: nextCounter) + ".mp4"
            previewField.stringValue = "Snip: \(snip)\nClip: \(clip)"
            previewField.textColor = .secondaryLabelColor
            alert.buttons.first?.isEnabled = true
        } catch {
            previewField.stringValue = error.localizedDescription
            previewField.textColor = .systemRed
            alert.buttons.first?.isEnabled = false
        }
    }
}
