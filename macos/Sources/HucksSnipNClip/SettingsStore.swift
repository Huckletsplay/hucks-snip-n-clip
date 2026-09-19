import Foundation

/// Reads and writes `settings.ini`, using the same key names and the same
/// `id|base64(name)|base64(path)` destination encoding as the Windows build, so the two
/// platforms describe their settings the same way.
final class SettingsStore {
    private static let currentVersion = 11
    private static let versionKey = "Version"
    private static let activeDestinationKey = "ActiveDestinationId"
    private static let destinationKey = "Destination"
    private static let routineNotificationsEnabledKey = "RoutineNotificationsEnabled"
    private static let recordingAudioModeKey = "RecordingAudioMode"
    private static let recordingFrameRateKey = "RecordingFrameRate"
    private static let recordingQualityKey = "RecordingQuality"
    private static let recordingResolutionKey = "RecordingResolution"
    private static let recordingCursorKey = "RecordingCursor"
    private static let computerAudioGainPercentKey = "ComputerAudioGainPercent"
    private static let microphoneGainPercentKey = "MicrophoneGainPercent"
    private static let microphoneDeviceIDKey = "MicrophoneDeviceId"
    private static let microphoneDeviceNameKey = "MicrophoneDeviceName"
    private static let captureFilenameLabelKey = "CaptureFilenameLabel"
    private static let captureFilenameTemplateKey = "CaptureFilenameTemplate"
    private static let captureFilenameCounterKey = "CaptureFilenameCounter"
    private static let shortcutKeyPrefix = "Shortcut."
    private static let outputShortcutKeyPrefix = "OutputShortcut."
    private static let legacyClipExportShortcutKeyPrefix = "ClipExportShortcut."
    // Recording controls use the Windows key names, so both platforms describe them the same way.
    private static let recordingShortcutKeyPrefix = "RecordingShortcut."
    private static let controllerPositionKeyPrefix = "ControllerPosition."
    private static let showFloatingRecordingControlsKey = "ShowFloatingRecordingControls"
    private static let floatingControllerOpacityKey = "FloatingControllerOpacityPercent"
    private static let floatingControllerSizeKey = "FloatingControllerSize"

    private let directory: URL
    private let defaultDestinationPath: () -> String

    let settingsURL: URL

    init(directory: URL, defaultDestinationPath: @escaping () -> String) {
        self.directory = directory
        self.settingsURL = directory.appendingPathComponent("settings.ini")
        self.defaultDestinationPath = defaultDestinationPath
    }

    static func createDefault() -> SettingsStore {
        return SettingsStore(
            directory: SettingsStore.supportDirectory,
            defaultDestinationPath: SettingsStore.findDefaultDestination)
    }

    static var supportDirectory: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("HucksSnipNClip", isDirectory: true)
    }

    static var recoveryDirectory: URL {
        return supportDirectory.appendingPathComponent("Recovery", isDirectory: true)
    }

    static var workingDirectory: URL {
        return supportDirectory.appendingPathComponent("Working", isDirectory: true)
    }

    func load() -> AppSettings {
        let settings = AppSettings()
        var seenShortcuts: Set<CaptureAction> = []
        var decodedOutputShortcuts: [String: ShortcutStroke] = [:]
        var recordingAudioModeSeen = false
        var recordingFrameRateSeen = false
        var recordingQualitySeen = false
        var recordingResolutionSeen = false
        var recordingCursorSeen = false
        var computerAudioGainSeen = false
        var microphoneGainSeen = false
        var captureFilenameLabelSeen = false
        var captureFilenameTemplateSeen = false
        var captureFilenameCounterSeen = false
        var seenRecordingShortcuts: Set<RecordingControlAction> = []
        var showFloatingControlsSeen = false
        var controllerOpacitySeen = false
        var controllerSizeSeen = false
        var needsWrite = false
        // Distinguishes an upgrade from a first run, so only a first run gets the new default.
        var hadExistingFile = false

        if let contents = try? String(contentsOf: settingsURL, encoding: .utf8) {
            hadExistingFile = true
            for rawLine in contents.components(separatedBy: .newlines) {
                let line = rawLine.trimmingCharacters(in: .whitespaces)
                // A comment is not a setting, even when its prose happens to contain an "=".
                guard !line.hasPrefix("#"), !line.hasPrefix(";") else { continue }
                guard let separator = line.firstIndex(of: "="), separator != line.startIndex else { continue }

                let key = String(line[line.startIndex..<separator]).trimmingCharacters(in: .whitespaces)
                let value = String(line[line.index(after: separator)...]).trimmingCharacters(in: .whitespaces)

                switch key.lowercased() {
                case SettingsStore.activeDestinationKey.lowercased():
                    settings.activeDestinationId = value.isEmpty ? nil : value
                case SettingsStore.destinationKey.lowercased():
                    if let destination = SettingsStore.decodeDestination(value) {
                        settings.destinations.append(destination)
                    }
                case SettingsStore.routineNotificationsEnabledKey.lowercased():
                    settings.routineNotificationsEnabled = (value.lowercased() == "true")
                case SettingsStore.recordingAudioModeKey.lowercased():
                    if let mode = RecordingAudioMode(rawValue: value) {
                        settings.recordingAudioMode = mode
                        recordingAudioModeSeen = true
                    } else {
                        // Never turn recording on because a malformed or unsupported value happened
                        // to fall through to AppSettings' new-install default.
                        settings.recordingAudioMode = .off
                        recordingAudioModeSeen = true
                        needsWrite = true
                    }
                case SettingsStore.recordingFrameRateKey.lowercased():
                    if let frameRate = Int(value), RecordingFrameRateSettings.isSupported(frameRate) {
                        settings.recordingFrameRate = frameRate
                        recordingFrameRateSeen = true
                    } else {
                        settings.recordingFrameRate = 60
                        recordingFrameRateSeen = true
                        needsWrite = true
                    }
                case SettingsStore.recordingQualityKey.lowercased():
                    if let quality = RecordingQuality.decode(value) {
                        settings.recordingQuality = quality
                        recordingQualitySeen = true
                    } else {
                        settings.recordingQuality = .balanced
                        recordingQualitySeen = true
                        needsWrite = true
                    }
                case SettingsStore.recordingResolutionKey.lowercased():
                    if let resolution = RecordingResolution.decode(value) {
                        settings.recordingResolution = resolution
                        recordingResolutionSeen = true
                    } else {
                        settings.recordingResolution = .native
                        recordingResolutionSeen = true
                        needsWrite = true
                    }
                case SettingsStore.recordingCursorKey.lowercased():
                    if value.caseInsensitiveCompare("true") == .orderedSame {
                        settings.recordingShowsCursor = true
                        recordingCursorSeen = true
                    } else if value.caseInsensitiveCompare("false") == .orderedSame {
                        settings.recordingShowsCursor = false
                        recordingCursorSeen = true
                    } else {
                        settings.recordingShowsCursor = false
                        recordingCursorSeen = true
                        needsWrite = true
                    }
                case SettingsStore.computerAudioGainPercentKey.lowercased():
                    if let gain = Int(value), SettingsStore.isSupportedAudioGainPercent(gain) {
                        settings.computerAudioGainPercent = gain
                        computerAudioGainSeen = true
                    } else {
                        settings.computerAudioGainPercent = 100
                        computerAudioGainSeen = true
                        needsWrite = true
                    }
                case SettingsStore.microphoneGainPercentKey.lowercased():
                    if let gain = Int(value), SettingsStore.isSupportedAudioGainPercent(gain) {
                        settings.microphoneGainPercent = gain
                        microphoneGainSeen = true
                    } else {
                        settings.microphoneGainPercent = 100
                        microphoneGainSeen = true
                        needsWrite = true
                    }
                case SettingsStore.microphoneDeviceIDKey.lowercased():
                    settings.microphoneDeviceID = value.isEmpty ? nil : value
                case SettingsStore.microphoneDeviceNameKey.lowercased():
                    settings.microphoneDeviceName = value.isEmpty ? nil : value
                case SettingsStore.captureFilenameLabelKey.lowercased():
                    if let decoded = SettingsStore.decodeText(value) {
                        settings.captureFilename.label = decoded
                        captureFilenameLabelSeen = true
                    } else {
                        settings.captureFilename.label = ""
                        captureFilenameLabelSeen = true
                        needsWrite = true
                    }
                case SettingsStore.captureFilenameTemplateKey.lowercased():
                    if let decoded = SettingsStore.decodeText(value) {
                        settings.captureFilename.template = decoded
                        captureFilenameTemplateSeen = true
                    } else {
                        settings.captureFilename.template = CaptureFilenameConfiguration.defaultTemplate
                        captureFilenameTemplateSeen = true
                        needsWrite = true
                    }
                case SettingsStore.captureFilenameCounterKey.lowercased():
                    if let counter = Int(value), counter >= 1 {
                        settings.captureFilename.nextCounter = counter
                        captureFilenameCounterSeen = true
                    } else {
                        settings.captureFilename.nextCounter = 1
                        captureFilenameCounterSeen = true
                        needsWrite = true
                    }
                case SettingsStore.showFloatingRecordingControlsKey.lowercased():
                    showFloatingControlsSeen = true
                    if value.caseInsensitiveCompare("true") == .orderedSame {
                        settings.showFloatingRecordingControls = true
                    } else if value.caseInsensitiveCompare("false") == .orderedSame {
                        settings.showFloatingRecordingControls = false
                    } else {
                        settings.showFloatingRecordingControls = true
                        needsWrite = true
                    }
                case SettingsStore.floatingControllerOpacityKey.lowercased():
                    controllerOpacitySeen = true
                    if let opacity = Int(value),
                       FloatingControllerMetrics.opacityRange.contains(opacity) {
                        settings.floatingControllerOpacityPercent = opacity
                    } else {
                        settings.floatingControllerOpacityPercent =
                            FloatingControllerMetrics.defaultOpacityPercent
                        needsWrite = true
                    }
                case SettingsStore.floatingControllerSizeKey.lowercased():
                    controllerSizeSeen = true
                    if let size = Int(value), FloatingControllerMetrics.sizeRange.contains(size) {
                        settings.floatingControllerSize = size
                    } else {
                        settings.floatingControllerSize = FloatingControllerMetrics.defaultSize
                        needsWrite = true
                    }
                case SettingsStore.versionKey.lowercased():
                    break
                default:
                    if key.lowercased().hasPrefix(
                        SettingsStore.recordingShortcutKeyPrefix.lowercased()
                    ) {
                        let name = String(key.dropFirst(SettingsStore.recordingShortcutKeyPrefix.count))
                        if let control = RecordingControlAction.allCases.first(where: {
                            $0.rawValue.caseInsensitiveCompare(name) == .orderedSame
                        }) {
                            if let stroke = ShortcutBinding.decodeStroke(value, requiresModifier: true) {
                                settings.setRecordingShortcut(stroke, for: control)
                                seenRecordingShortcuts.insert(control)
                            } else {
                                // A recognized control with a bad value: repair it to the default.
                                needsWrite = true
                            }
                        } else {
                            settings.preservedLines.append(line)
                        }
                    } else if key.lowercased().hasPrefix(
                        SettingsStore.controllerPositionKeyPrefix.lowercased()
                    ) {
                        // Written most-recently-used first, so reading in file order keeps which
                        // layout is oldest and therefore the first to be dropped.
                        let layoutKey = String(key.dropFirst(SettingsStore.controllerPositionKeyPrefix.count))
                        if let position = SettingsStore.decodePoint(value), !layoutKey.isEmpty {
                            settings.controllerLayouts.append(position, for: layoutKey)
                        } else {
                            needsWrite = true
                        }
                    } else if key.lowercased().hasPrefix(
                        SettingsStore.outputShortcutKeyPrefix.lowercased()
                    ) {
                        let destinationID = String(
                            key.dropFirst(SettingsStore.outputShortcutKeyPrefix.count))
                        if !destinationID.isEmpty,
                           let shortcut = ShortcutBinding.decodeStroke(
                            value,
                            requiresModifier: true) {
                            decodedOutputShortcuts[destinationID] = shortcut
                        } else {
                            // This is a recognized setting with a bad value, not a future key.
                            // Drop it so save can emit one repaired assignment instead of keeping
                            // a duplicate malformed line forever.
                            needsWrite = true
                        }
                    } else if key.lowercased().hasPrefix(
                        SettingsStore.legacyClipExportShortcutKeyPrefix.lowercased()
                    ) {
                        let destinationID = String(
                            key.dropFirst(SettingsStore.legacyClipExportShortcutKeyPrefix.count))
                        if !destinationID.isEmpty,
                           let shortcut = ShortcutBinding.decodeStroke(
                            value,
                            requiresModifier: true) {
                            decodedOutputShortcuts[destinationID] = shortcut
                        }
                        // Schema 9 briefly used stop-and-export semantics. Keep safe modified
                        // assignments, but rewrite them under the selected output-switch model.
                        needsWrite = true
                    } else if key.lowercased().hasPrefix(SettingsStore.shortcutKeyPrefix.lowercased()) {
                        let name = String(key.dropFirst(SettingsStore.shortcutKeyPrefix.count))
                        if let action = CaptureAction(rawValue: name), let binding = ShortcutBinding.decode(value) {
                            settings.setShortcut(binding, for: action)
                            seenShortcuts.insert(action)
                        } else {
                            settings.preservedLines.append(line)
                        }
                    } else {
                        settings.preservedLines.append(line)
                    }
                }
            }
        } else {
            needsWrite = true
        }

        if settings.destinations.isEmpty {
            let path = defaultDestinationPath()
            settings.destinations.append(SettingsStore.createDestination(path: path))
            needsWrite = true
        }

        if settings.activeDestination == nil {
            settings.activeDestinationId = settings.destinations.first?.id
            needsWrite = true
        } else if settings.activeDestinationId == nil {
            settings.activeDestinationId = settings.activeDestination?.id
            needsWrite = true
        }

        if !recordingAudioModeSeen {
            settings.recordingAudioMode = .computer
            needsWrite = true
        }

        // Preserve the Mac recorder's pre-setting behavior on migration: native capture targeting
        // 60 FPS with the same scaled 16 Mbps-at-1080p60 bitrate now named Balanced.
        if !recordingFrameRateSeen {
            settings.recordingFrameRate = 60
            needsWrite = true
        }

        if !recordingQualitySeen {
            settings.recordingQuality = .balanced
            needsWrite = true
        }

        // An existing install keeps recording exactly what it recorded before. Only a genuinely
        // new settings file takes the 1080p default, so nobody's output changes under them.
        if !recordingResolutionSeen {
            settings.recordingResolution = hadExistingFile ? .native : .p1080
            needsWrite = true
        }

        // Preserve the recorder's established cursor-free output on both upgrade and first run.
        // The user can opt in from the Recording menu.
        if !recordingCursorSeen {
            settings.recordingShowsCursor = false
            needsWrite = true
        }

        if !computerAudioGainSeen {
            settings.computerAudioGainPercent = 100
            needsWrite = true
        }

        if !microphoneGainSeen {
            settings.microphoneGainPercent = 100
            needsWrite = true
        }

        if !captureFilenameLabelSeen {
            settings.captureFilename.label = ""
            needsWrite = true
        }
        if !captureFilenameTemplateSeen {
            settings.captureFilename.template = CaptureFilenameConfiguration.defaultTemplate
            needsWrite = true
        }
        if !captureFilenameCounterSeen {
            settings.captureFilename.nextCounter = 1
            needsWrite = true
        }
        do {
            settings.captureFilename = try CaptureFilenameTemplate.normalized(settings.captureFilename)
        } catch {
            // An unsafe edit to settings.ini must never escape into a destination path. Restore the
            // old naming contract and keep the monotonic counter if it was otherwise valid.
            let counter = max(1, settings.captureFilename.nextCounter)
            settings.captureFilename = CaptureFilenameConfiguration(nextCounter: counter)
            needsWrite = true
        }

        if seenShortcuts.count != CaptureAction.allCases.count {
            var occupied = Set(seenShortcuts.map { settings.shortcut(for: $0) })
            for action in CaptureAction.allCases where !seenShortcuts.contains(action) {
                let binding = ShortcutBinding.migrationDefault(for: action, avoiding: occupied)
                settings.setShortcut(binding, for: action)
                occupied.insert(binding)
            }
            needsWrite = true
        }

        for destination in settings.destinations {
            if let shortcut = decodedOutputShortcuts[destination.id] {
                settings.setOutputShortcut(shortcut, for: destination.id)
            }
        }
        if settings.ensureOutputShortcuts() {
            needsWrite = true
        }

        if seenRecordingShortcuts.count != RecordingControlAction.allCases.count
            || !showFloatingControlsSeen
            || !controllerOpacitySeen
            || !controllerSizeSeen {
            // Missing keys take the AppSettings defaults: both controls on their documented keys,
            // the controller shown, 50% opacity, default size. Nothing existing is changed.
            needsWrite = true
        }
        if settings.ensureRecordingShortcuts() {
            needsWrite = true
        }

        if needsWrite {
            try? save(settings)
        }

        return settings
    }

    func save(_ settings: AppSettings) throws {
        guard !settings.destinations.isEmpty else {
            throw SettingsError.noDestination
        }

        _ = settings.ensureOutputShortcuts()

        let filenameConfiguration: CaptureFilenameConfiguration
        do {
            filenameConfiguration = try CaptureFilenameTemplate.normalized(settings.captureFilename)
        } catch {
            throw SettingsError.invalidCaptureFilename(error.localizedDescription)
        }

        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

        var lines: [String] = []
        lines.append("# Huck’s Snip ’n’ Clip (macOS). Lines starting with # are comments.")
        lines.append("\(SettingsStore.versionKey)=\(SettingsStore.currentVersion)")
        lines.append("\(SettingsStore.activeDestinationKey)=\(settings.activeDestinationId ?? "")")
        lines.append("\(SettingsStore.routineNotificationsEnabledKey)=\(settings.routineNotificationsEnabled)")
        lines.append("\(SettingsStore.recordingAudioModeKey)=\(settings.recordingAudioMode.rawValue)")
        lines.append("\(SettingsStore.recordingFrameRateKey)=\(RecordingFrameRateSettings.normalize(settings.recordingFrameRate))")
        lines.append("\(SettingsStore.recordingQualityKey)=\(settings.recordingQuality.rawValue)")
        lines.append("\(SettingsStore.recordingResolutionKey)=\(settings.recordingResolution.rawValue)")
        lines.append("\(SettingsStore.recordingCursorKey)=\(settings.recordingShowsCursor)")
        lines.append("\(SettingsStore.computerAudioGainPercentKey)=\(SettingsStore.normalizeAudioGainPercent(settings.computerAudioGainPercent))")
        lines.append("\(SettingsStore.microphoneGainPercentKey)=\(SettingsStore.normalizeAudioGainPercent(settings.microphoneGainPercent))")
        lines.append("\(SettingsStore.microphoneDeviceIDKey)=\(settings.microphoneDeviceID ?? "")")
        lines.append("\(SettingsStore.microphoneDeviceNameKey)=\(settings.microphoneDeviceName ?? "")")
        lines.append("\(SettingsStore.captureFilenameLabelKey)=\(SettingsStore.encodeText(filenameConfiguration.label))")
        lines.append("\(SettingsStore.captureFilenameTemplateKey)=\(SettingsStore.encodeText(filenameConfiguration.template))")
        lines.append("\(SettingsStore.captureFilenameCounterKey)=\(filenameConfiguration.nextCounter)")

        for destination in settings.destinations {
            lines.append("\(SettingsStore.destinationKey)=\(SettingsStore.encodeDestination(destination))")
        }

        for action in CaptureAction.allCases {
            lines.append("\(SettingsStore.shortcutKeyPrefix)\(action.rawValue)=\(settings.shortcut(for: action).encoded)")
        }

        for destination in settings.destinations {
            if let shortcut = settings.outputShortcut(for: destination.id) {
                lines.append(
                    "\(SettingsStore.outputShortcutKeyPrefix)\(destination.id)=\(shortcut.encoded)")
            }
        }

        for control in RecordingControlAction.allCases {
            lines.append(
                "\(SettingsStore.recordingShortcutKeyPrefix)\(control.rawValue)=\(settings.recordingShortcut(for: control).encoded)")
        }
        lines.append("\(SettingsStore.showFloatingRecordingControlsKey)=\(settings.showFloatingRecordingControls)")
        lines.append(
            "\(SettingsStore.floatingControllerOpacityKey)=\(FloatingControllerMetrics.normalizeOpacity(settings.floatingControllerOpacityPercent))")
        lines.append(
            "\(SettingsStore.floatingControllerSizeKey)=\(FloatingControllerMetrics.normalizeSize(settings.floatingControllerSize))")
        for layoutKey in settings.controllerLayouts.keys {
            guard let position = settings.controllerLayouts.position(for: layoutKey) else { continue }
            lines.append(
                "\(SettingsStore.controllerPositionKeyPrefix)\(layoutKey)=\(Int(position.x.rounded())),\(Int(position.y.rounded()))")
        }

        lines.append(contentsOf: settings.preservedLines)

        let contents = lines.joined(separator: "\n") + "\n"
        try contents.write(to: settingsURL, atomically: true, encoding: .utf8)
    }

    static func createDestination(path: String, name: String? = nil) -> CaptureDestination {
        let resolved = (path as NSString).standardizingPath
        let derivedName = name ?? (resolved as NSString).lastPathComponent
        return CaptureDestination(
            id: UUID().uuidString,
            name: derivedName.isEmpty ? resolved : derivedName,
            path: resolved)
    }

    static let supportedAudioGainPercents = [0, 50, 75, 100, 125, 150, 200, 300]

    static func isSupportedAudioGainPercent(_ gainPercent: Int) -> Bool {
        return gainPercent >= 0 && gainPercent <= 300
    }

    static func normalizeAudioGainPercent(_ gainPercent: Int) -> Int {
        return isSupportedAudioGainPercent(gainPercent) ? gainPercent : 100
    }

    private static func encodeDestination(_ destination: CaptureDestination) -> String {
        return destination.id
            + "|" + encodeText(destination.name)
            + "|" + encodeText(destination.path)
    }

    private static func decodeDestination(_ value: String) -> CaptureDestination? {
        let parts = value.components(separatedBy: "|")
        guard parts.count >= 3, !parts[0].isEmpty else { return nil }
        guard let name = decodeText(parts[1]), let path = decodeText(parts[2]) else { return nil }
        guard !name.isEmpty, !path.isEmpty else { return nil }
        return CaptureDestination(id: parts[0], name: name, path: path)
    }

    static func decodePoint(_ value: String) -> CGPoint? {
        let parts = value.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
        guard parts.count == 2, let x = Int(parts[0]), let y = Int(parts[1]) else { return nil }
        return CGPoint(x: x, y: y)
    }

    private static func encodeText(_ value: String) -> String {
        return Data(value.utf8).base64EncodedString()
    }

    private static func decodeText(_ value: String) -> String? {
        guard let data = Data(base64Encoded: value) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    /// Where a brand-new install saves until the user picks somewhere else.
    ///
    /// This is shipped software: it must never hunt the filesystem for a development folder. An
    /// earlier version walked up to ten parent directories from the app bundle looking for a hub
    /// `Ingest` folder. On someone else's Mac that walk climbs out of /Applications to their home
    /// folder and the volume root, so an unrelated `Ingest` folder would silently capture their
    /// files. Named destinations are added from the menu instead. Windows was fixed the same way.
    static func findDefaultDestination() -> String {
        let pictures = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory())
        return pictures.appendingPathComponent("Huck's Snip 'n' Clip", isDirectory: true).path
    }
}

enum SettingsError: LocalizedError {
    case noDestination
    case invalidCaptureFilename(String)

    var errorDescription: String? {
        switch self {
        case .noDestination:
            return "At least one destination is required."
        case .invalidCaptureFilename(let detail):
            return "The capture filename settings are not valid. \(detail)"
        }
    }
}
