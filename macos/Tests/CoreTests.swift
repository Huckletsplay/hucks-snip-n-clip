import AppKit
import AVFoundation
import Carbon.HIToolbox
import AudioToolbox
import CoreGraphics
import CoreMedia
import CryptoKit
import Foundation
import ScreenCaptureKit

// Headless checks over the parts of the Mac build that have nothing to do with the screen:
// settings round-tripping, shortcut encoding, and the save/recovery path. Xcode is not installed,
// so XCTest is unavailable; this mirrors the Windows build, which compiles the same core sources
// into a small test executable and runs it.

private var failures: [String] = []
private var checks = 0

private func check(_ condition: Bool, _ message: String) {
    checks += 1
    if !condition {
        failures.append(message)
    }
}

private func checkEqual<T: Equatable>(_ actual: T, _ expected: T, _ message: String) {
    check(actual == expected, "\(message) (expected \(expected), got \(actual))")
}

private func checkThrows(_ message: String, _ operation: () throws -> Void) {
    do {
        try operation()
        check(false, message)
    } catch {
        check(true, message)
    }
}

private func makeTemporaryDirectory() throws -> URL {
    let url = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("hucks-snip-n-clip-tests-" + UUID().uuidString, isDirectory: true)
    try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

private func makeImage(width: Int, height: Int) -> CGImage? {
    guard let context = CGContext(
        data: nil,
        width: width,
        height: height,
        bitsPerComponent: 8,
        bytesPerRow: 0,
        space: CGColorSpaceCreateDeviceRGB(),
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }

    context.setFillColor(CGColor(red: 0, green: 0.62, blue: 0.61, alpha: 1))
    context.fill(CGRect(x: 0, y: 0, width: width, height: height))
    return context.makeImage()
}

private func testShortcutRoundTrip() {
    checkEqual(
        CaptureAction.allCases.map(\.rawValue),
        ["SnipRegion", "SnipWindow", "SnipScreen", "ClipRegion", "ClipWindow", "ClipScreen"],
        "implemented actions keep both families in Region, Window, Screen order")

    for action in CaptureAction.allCases {
        let binding = ShortcutBinding.defaultBinding(for: action)
        guard let decoded = ShortcutBinding.decode(binding.encoded) else {
            failures.append("\(action.rawValue) shortcut did not decode from \(binding.encoded)")
            checks += 1
            continue
        }

        checkEqual(decoded, binding, "\(action.rawValue) shortcut round-trip")
    }

    checkEqual(
        ShortcutBinding.defaultBinding(for: .snipScreen).displayString,
        "\u{2303}\u{2325}\u{21E7}F",
        "Snip Screen is shown as Control-Option-Shift-F")
    checkEqual(
        ShortcutBinding.defaultBinding(for: .snipWindow).displayString,
        "\u{2303}\u{2325}\u{21E7}W",
        "Snip Window is shown as Control-Option-Shift-W")
    checkEqual(
        ShortcutBinding.defaultBinding(for: .clipRegion).displayString,
        "\u{2303}\u{2325}\u{21E7}C",
        "Clip Region is shown as Control-Option-Shift-C")
    checkEqual(
        ShortcutBinding.defaultBinding(for: .clipWindow).displayString,
        "\u{2303}\u{2325}\u{21E7}V",
        "Clip Window is shown as Control-Option-Shift-V")
    checkEqual(
        ShortcutBinding.defaultBinding(for: .clipScreen).displayString,
        "\u{2303}\u{2325}\u{21E7}R",
        "Clip Screen is shown as Control-Option-Shift-R")

    check(!CaptureAction.snipRegion.isClip, "Snip Region is not classified as a clip")
    check(CaptureAction.clipRegion.isClip, "Clip Region is classified as a clip")
    check(CaptureAction.clipWindow.isClip, "Clip Window is classified as a clip")
    check(CaptureAction.clipScreen.isClip, "Clip Screen is classified as a clip")

    check(ShortcutBinding.decode("shift+S") != nil, "a single-modifier shortcut decodes")
    check(ShortcutBinding.decode("S") == nil, "a shortcut with no modifier is rejected")
    check(ShortcutBinding.decode("control+option+shift+") == nil, "a shortcut with no key is rejected")
    check(ShortcutBinding.decode("hyper+S") == nil, "an unknown modifier is rejected")

    let chord = ShortcutBinding(
        modifiers: UInt32(controlKey | optionKey),
        keyCode: UInt32(kVK_ANSI_C),
        secondModifiers: 0,
        secondKeyCode: UInt32(kVK_ANSI_R))
    check(chord.hasSecondStroke, "a binding reports its optional second stroke")
    checkEqual(chord.displayString, "\u{2303}\u{2325}C, R", "a two-step chord displays both presses")
    checkEqual(chord.encoded, "control+option+C > R", "a two-step chord has a readable settings value")
    checkEqual(ShortcutBinding.decode(chord.encoded), chord, "a bare-second-key chord round-trips")
    check(ShortcutBinding.decode("C > R") == nil, "a chord still rejects a bare first stroke")
    check(ShortcutBinding.decode("control+C > ") == nil, "a chord rejects a missing second key")

    let commaChord = ShortcutBinding(
        modifiers: UInt32(controlKey),
        keyCode: UInt32(kVK_ANSI_Comma),
        secondModifiers: 0,
        secondKeyCode: UInt32(kVK_ANSI_Comma))
    checkEqual(
        ShortcutBinding.decode(commaChord.encoded),
        commaChord,
        "the comma key remains unambiguous in both chord strokes")

    let bareExport = ShortcutBinding.decodeStroke("8", requiresModifier: false)
    checkEqual(
        bareExport,
        ShortcutStroke(modifiers: 0, keyCode: UInt32(kVK_ANSI_8)),
        "a temporary chord second stroke may be a bare key")
    check(
        ShortcutBinding.decodeStroke("8", requiresModifier: true) == nil,
        "the same bare key remains invalid for an always-on capture shortcut")
}

private func testOutputShortcuts() {
    let settings = AppSettings()
    settings.destinations = [
        CaptureDestination(id: "one", name: "One", path: "/tmp/one"),
        CaptureDestination(id: "two", name: "Two", path: "/tmp/two"),
        CaptureDestination(id: "three", name: "Three", path: "/tmp/three")
    ]

    check(settings.ensureOutputShortcuts(), "new destinations receive output shortcuts")
    checkEqual(
        settings.outputShortcut(for: "one")?.displayString,
        "\u{2303}\u{2325}1",
        "the first destination receives Control-Option-1")
    checkEqual(
        settings.outputShortcut(for: "two")?.displayString,
        "\u{2303}\u{2325}2",
        "the second destination receives Control-Option-2")
    checkEqual(
        Set(settings.destinations.compactMap { settings.outputShortcut(for: $0.id) }).count,
        3,
        "automatic output shortcuts are unique")

    let first = settings.outputShortcut(for: "one")!
    settings.setOutputShortcut(first, for: "two")
    check(settings.ensureOutputShortcuts(), "a duplicate output shortcut is repaired")
    check(
        settings.outputShortcut(for: "one") != settings.outputShortcut(for: "two"),
        "a repaired output shortcut no longer duplicates another destination")
    checkEqual(
        settings.outputShortcutConflicts(with: first, excluding: "two")?.id,
        "one",
        "the conflict lookup names the destination already using an output key")

    settings.setOutputShortcut(
        ShortcutStroke(modifiers: 0, keyCode: UInt32(kVK_ANSI_8)),
        for: "three")
    check(settings.ensureOutputShortcuts(), "a bare always-on output key is repaired")
    check(
        (settings.outputShortcut(for: "three")?.modifiers ?? 0) != 0,
        "every output shortcut retains at least one modifier")

    settings.setShortcut(ShortcutBinding(firstStroke: first), for: .snipRegion)
    check(settings.ensureOutputShortcuts(), "a capture-action collision reassigns output keys")
    check(
        settings.destinations.allSatisfy {
            settings.outputShortcut(for: $0.id) != first
        },
        "no output shortcut remains on a capture-action stroke")
    checkEqual(
        settings.captureActionConflicting(with: first),
        .snipRegion,
        "the capture conflict lookup checks action first and second strokes")

    settings.setShortcut(ShortcutBinding.defaultBinding(for: .snipRegion), for: .snipRegion)
    settings.resetOutputShortcutsToDefaults()
    checkEqual(
        settings.outputShortcut(for: "one")?.displayString,
        "\u{2303}\u{2325}1",
        "reset restores the first output-selection default")
}

private func testSettingsRoundTrip() throws {
    let directory = try makeTemporaryDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }

    let fallback = directory.appendingPathComponent("Fallback").path
    let store = SettingsStore(directory: directory, defaultDestinationPath: { fallback })

    let first = store.load()
    checkEqual(first.destinations.count, 1, "a fresh load seeds exactly one destination")
    checkEqual(first.destinations.first?.path, fallback, "the seeded destination uses the fallback path")
    check(first.activeDestination != nil, "a fresh load has an active destination")
    checkEqual(first.recordingAudioMode, .computer, "a fresh Mac setup records system audio")
    checkEqual(first.recordingFrameRate, 60, "a fresh Mac setup preserves the existing 60 FPS behavior")
    checkEqual(first.recordingQuality, .balanced, "a fresh Mac setup preserves the existing scaled bitrate")
    check(!first.recordingShowsCursor, "a fresh Mac setup keeps the cursor out of clips until requested")
    checkEqual(first.computerAudioGainPercent, 100, "fresh system/app gain is normal")
    checkEqual(first.microphoneGainPercent, 100, "fresh microphone gain is normal")
    checkEqual(first.microphoneDeviceID, nil, "a fresh setup follows the macOS default microphone")
    check(first.captureFilename.isDefault, "a fresh setup preserves the historical filename format")
    checkEqual(first.captureFilename.nextCounter, 1, "a fresh setup begins its filename counter at one")
    check(FileManager.default.fileExists(atPath: store.settingsURL.path), "a fresh load writes settings.ini")
    checkEqual(
        first.activeDestination.flatMap { first.outputShortcut(for: $0.id) }?.displayString,
        "\u{2303}\u{2325}1",
        "a fresh destination receives its global output-selection shortcut")

    // A name with a separator and non-ASCII text proves the base64 encoding is doing its job.
    let awkward = CaptureDestination(
        id: UUID().uuidString,
        name: "Ingest | \u{201C}caf\u{E9}\u{201D}",
        path: directory.appendingPathComponent("Ingest \u{2014} caf\u{E9}").path)
    first.destinations.append(awkward)
    first.activeDestinationId = awkward.id
    first.routineNotificationsEnabled = false
    first.recordingAudioMode = .off
    first.recordingFrameRate = 30
    first.recordingQuality = .high
    first.recordingResolution = .p720
    first.recordingShowsCursor = true
    first.computerAudioGainPercent = 75
    first.microphoneGainPercent = 300
    first.microphoneDeviceID = "external-microphone-id"
    first.microphoneDeviceName = "Desk Microphone"
    first.captureFilename = CaptureFilenameConfiguration(
        label: "Minecraft",
        template: "{label}_{kind}_{date}_{counter}",
        nextCounter: 42)
    let modifiedEight = ShortcutStroke(
        modifiers: ShortcutBinding.outputModifiers,
        keyCode: UInt32(kVK_ANSI_8))
    first.setOutputShortcut(modifiedEight, for: awkward.id)
    first.preservedLines.append("FutureRecordingControl=Enabled")
    try store.save(first)

    let second = store.load()
    checkEqual(second.destinations.count, 2, "both destinations survive a save and load")
    checkEqual(second.activeDestination?.id, awkward.id, "the active destination survives")
    checkEqual(second.activeDestination?.name, awkward.name, "a name containing a pipe survives")
    checkEqual(second.activeDestination?.path, awkward.path, "a non-ASCII path survives")
    check(!second.routineNotificationsEnabled, "the setting-change notification choice survives")
    checkEqual(second.recordingAudioMode, .off, "the video-only audio choice survives")
    checkEqual(second.recordingFrameRate, 30, "recording frame rate survives")
    checkEqual(second.recordingQuality, .high, "recording quality survives independently")
    checkEqual(second.recordingResolution, .p720, "recording resolution survives independently")
    check(second.recordingShowsCursor, "the recording cursor choice survives independently")
    checkEqual(second.computerAudioGainPercent, 75, "system/app gain survives")
    checkEqual(second.microphoneGainPercent, 300, "microphone gain survives")
    checkEqual(second.microphoneDeviceID, "external-microphone-id", "the selected microphone ID survives")
    checkEqual(second.microphoneDeviceName, "Desk Microphone", "the selected microphone name survives")
    checkEqual(second.captureFilename.label, "Minecraft", "the capture filename label survives")
    checkEqual(second.captureFilename.template, "{label}_{kind}_{date}_{counter}",
               "the capture filename template survives")
    checkEqual(second.captureFilename.nextCounter, 42, "the capture filename counter survives restart")
    checkEqual(
        second.outputShortcut(for: awkward.id),
        modifiedEight,
        "a modified output shortcut survives restart")
    check(
        second.preservedLines.contains("FutureRecordingControl=Enabled"),
        "a setting this build does not understand is carried through instead of dropped")

    let contents = try String(contentsOf: store.settingsURL, encoding: .utf8)
    check(contents.contains("Version=11"), "the recording-controls settings schema uses version 11")
    check(contents.contains("RecordingAudioMode=Off"), "the recording audio choice is written")
    check(contents.contains("RecordingFrameRate=30"), "recording frame rate is written")
    check(contents.contains("RecordingQuality=High"), "recording quality is written")
    check(contents.contains("RecordingResolution=720p"), "recording resolution is written")
    check(contents.contains("CaptureFilenameCounter=42"), "the next filename counter is written")
    check(!contents.contains("{label}_{kind}_{date}_{counter}"),
          "the filename template is encoded instead of becoming settings syntax")
    check(contents.contains("RecordingCursor=true"), "the recording cursor choice is written")
    check(contents.contains("ComputerAudioGainPercent=75"), "system/app gain is written")
    check(contents.contains("MicrophoneGainPercent=300"), "microphone gain is written")
    check(contents.contains("MicrophoneDeviceId=external-microphone-id"), "the microphone ID is written")
    check(contents.contains("MicrophoneDeviceName=Desk Microphone"), "the microphone name is written")
    check(contents.contains("Shortcut.SnipRegion="), "shortcuts are written to the file")
    check(
        contents.contains("OutputShortcut.\(awkward.id)=control+option+8"),
        "the per-destination output shortcut is written")
    check(!contents.contains(awkward.name), "destination names are encoded, not written raw")

    checkEqual(
        second.destination(withPath: awkward.path)?.id,
        awkward.id,
        "a destination is found by its path")

    // A comment whose prose contains an "=" must not be mistaken for a setting, or the file grows
    // by one junk line on every save.
    try store.save(second)
    let third = store.load()
    try store.save(third)
    let settled = try String(contentsOf: store.settingsURL, encoding: .utf8)
    let commentCount = settled
        .components(separatedBy: .newlines)
        .filter { $0.hasPrefix("#") }
        .count
    checkEqual(commentCount, 1, "repeated saves do not accumulate comment lines")

    // A comment written by hand, whatever it contains, survives without becoming a setting.
    try (settled + "# a hand-written note with an = inside it\n")
        .write(to: store.settingsURL, atomically: true, encoding: .utf8)
    let afterComment = store.load()
    check(
        !afterComment.preservedLines.contains(where: { $0.hasPrefix("#") }),
        "a comment is not carried through as an unknown setting")
    checkEqual(afterComment.destinations.count, 2, "a stray comment does not disturb the settings")

    // Schema 9 briefly treated these keys as stop-and-export controls. Safe modified assignments
    // migrate intact to the selected before-capture output-switch workflow and old keys disappear.
    let outputMigrationDirectory = directory.appendingPathComponent(
        "output-shortcut-migration",
        isDirectory: true)
    try FileManager.default.createDirectory(
        at: outputMigrationDirectory,
        withIntermediateDirectories: true)
    let outputMigrationStore = SettingsStore(
        directory: outputMigrationDirectory,
        defaultDestinationPath: { fallback })
    let encodedMigrationPath = Data(
        outputMigrationDirectory.appendingPathComponent("Ingest").path.utf8).base64EncodedString()
    try """
        Version=9
        ActiveDestinationId=legacy-output
        Destination=legacy-output|TGVnYWN5|\(encodedMigrationPath)
        ClipExportShortcut.legacy-output=control+option+7
        """.write(to: outputMigrationStore.settingsURL, atomically: true, encoding: .utf8)

    let outputMigrated = outputMigrationStore.load()
    checkEqual(
        outputMigrated.outputShortcut(for: "legacy-output")?.displayString,
        "\u{2303}\u{2325}7",
        "schema 9 keeps its safe modified destination key during output-workflow migration")
    let outputMigratedContents = try String(
        contentsOf: outputMigrationStore.settingsURL,
        encoding: .utf8)
    check(
        outputMigratedContents.contains("OutputShortcut.legacy-output=control+option+7"),
        "the migrated assignment is written under its schema 10 output key")
    check(
        !outputMigratedContents.contains("ClipExportShortcut."),
        "the temporary schema 9 export key is retired after migration")

    // Snip Window was added after the first Mac build. Its new default must not steal an explicit
    // W binding that an existing user had already assigned to Snip Screen.
    let migrationDirectory = directory.appendingPathComponent("new-action-migration", isDirectory: true)
    try FileManager.default.createDirectory(at: migrationDirectory, withIntermediateDirectories: true)
    let migrationStore = SettingsStore(directory: migrationDirectory, defaultDestinationPath: { fallback })
    try """
        Version=1
        RecordingAudioMode=ComputerAndMicrophone
        Shortcut.SnipRegion=control+option+shift+S
        Shortcut.SnipScreen=control+option+shift+W
        """.write(to: migrationStore.settingsURL, atomically: true, encoding: .utf8)

    let migrated = migrationStore.load()
    checkEqual(
        migrated.recordingAudioMode,
        .computerAndMicrophone,
        "a Windows combined-audio choice enables separate Mac tracks")
    checkEqual(migrated.recordingFrameRate, 60, "older Mac settings migrate to the prior 60 FPS behavior")
    checkEqual(migrated.recordingQuality, .balanced, "older Mac settings migrate to the prior scaled bitrate")
    checkEqual(
        migrated.shortcut(for: .snipScreen).displayString,
        "\u{2303}\u{2325}\u{21E7}W",
        "an existing custom screen shortcut survives the new Window action")
    checkEqual(
        migrated.shortcut(for: .snipWindow).displayString,
        "\u{2303}\u{2325}\u{21E7}\u{2318}W",
        "a colliding new action receives a deterministic Command-modified fallback")
    checkEqual(
        Set(CaptureAction.allCases.map { migrated.shortcut(for: $0) }).count,
        CaptureAction.allCases.count,
        "new-action migration leaves every Mac shortcut unique")

    let microphoneDirectory = directory.appendingPathComponent("microphone-migration", isDirectory: true)
    try FileManager.default.createDirectory(at: microphoneDirectory, withIntermediateDirectories: true)
    let microphoneStore = SettingsStore(
        directory: microphoneDirectory,
        defaultDestinationPath: { fallback })
    try """
        Version=1
        RecordingAudioMode=Microphone
        """.write(to: microphoneStore.settingsURL, atomically: true, encoding: .utf8)
    let microphoneAudio = microphoneStore.load()
    checkEqual(
        microphoneAudio.recordingAudioMode,
        .microphone,
        "a microphone-only setting loads as microphone audio")
    checkEqual(
        microphoneAudio.microphoneDeviceID,
        nil,
        "an older microphone setting keeps following the system default")

    check(RecordingAudioMode.computer.recordsSystemAudio, "system audio enables AAC recording")
    check(!RecordingAudioMode.off.recordsSystemAudio, "video-only mode disables AAC recording")
    check(RecordingAudioMode.microphone.recordsMicrophone, "microphone mode enables microphone capture")
    check(!RecordingAudioMode.computer.recordsMicrophone, "system/app audio does not enable the microphone")
    check(RecordingAudioMode.computerAndMicrophone.recordsSystemAudio, "dual audio enables system/app capture")
    check(RecordingAudioMode.computerAndMicrophone.recordsMicrophone, "dual audio enables microphone capture")
    check(!RecordingAudioMode.off.recordsAudio, "No Audio does not create an audio track")
    checkEqual(
        RecordingAudioMode.computer.summary,
        "System / App Audio",
        "captured-audio summary explains target-sensitive behavior")
    checkEqual(RecordingAudioMode.microphone.summary, "Microphone", "microphone summary is explicit")
    checkEqual(
        RecordingAudioMode.computerAndMicrophone.summary,
        "System / App + Microphone",
        "dual-track summary names both sources")
    checkEqual(RecordingAudioMode.off.summary, "No Audio", "off is not presented as an audio source")
    check(SettingsStore.isSupportedAudioGainPercent(0), "zero gain is supported as mute")
    check(SettingsStore.isSupportedAudioGainPercent(300), "300 percent gain is supported")
    check(!SettingsStore.isSupportedAudioGainPercent(301), "gain above 300 percent is rejected")
    checkEqual(SettingsStore.normalizeAudioGainPercent(-1), 100, "invalid gain falls back to normal")
    checkEqual(SettingsStore.supportedAudioGainPercents, [0, 50, 75, 100, 125, 150, 200, 300],
               "Mac gain choices match Windows")
}

private func testMicrophoneDeviceSelection() {
    let builtIn = MicrophoneDeviceDescriptor(id: "built-in", name: "MacBook Pro Microphone")
    let desk = MicrophoneDeviceDescriptor(id: "desk", name: "Desk Microphone")
    let devices = [builtIn, desk]

    let systemDefault = MicrophoneDeviceService.resolve(
        savedDeviceID: nil,
        savedDeviceName: nil,
        devices: devices,
        defaultDevice: builtIn)
    checkEqual(systemDefault.captureDeviceID, nil, "System Default leaves ScreenCaptureKit's device ID unset")
    check(
        systemDefault.displayName.contains("MacBook Pro Microphone"),
        "System Default names the input macOS currently uses")
    check(!systemDefault.savedDeviceMissing, "System Default is never treated as a missing saved device")

    let selected = MicrophoneDeviceService.resolve(
        savedDeviceID: desk.id,
        savedDeviceName: desk.name,
        devices: devices,
        defaultDevice: builtIn)
    checkEqual(selected.captureDeviceID, desk.id, "a connected saved microphone supplies its stable ID")
    checkEqual(selected.displayName, desk.name, "a connected saved microphone uses its current name")
    check(!selected.savedDeviceMissing, "a connected saved microphone is available")

    let disconnected = MicrophoneDeviceService.resolve(
        savedDeviceID: desk.id,
        savedDeviceName: desk.name,
        devices: [builtIn],
        defaultDevice: builtIn)
    checkEqual(
        disconnected.captureDeviceID,
        nil,
        "a disconnected saved microphone falls back to the system default")
    check(
        disconnected.displayName.contains("Desk Microphone")
            && disconnected.displayName.contains("Not Connected")
            && disconnected.displayName.contains("System Default"),
        "a disconnected saved microphone is explained instead of silently replaced")
    check(disconnected.savedDeviceMissing, "the caller can surface a missing-device warning")
}

private func testRecordingFrameRateAndQuality() {
    checkEqual(RecordingFrameRateSettings.normalize(0), 0, "Match Display is supported")
    checkEqual(RecordingFrameRateSettings.normalize(15), 15, "15 FPS is supported")
    checkEqual(RecordingFrameRateSettings.normalize(30), 30, "30 FPS is supported")
    checkEqual(RecordingFrameRateSettings.normalize(60), 60, "60 FPS is supported")
    checkEqual(RecordingFrameRateSettings.normalize(120), 60, "an unsupported saved FPS repairs to 60")
    checkEqual(RecordingFrameRateSettings.resolve(0, displayRefreshRate: 120), 120,
               "Match Display resolves a 120 Hz display")
    checkEqual(RecordingFrameRateSettings.resolve(0, displayRefreshRate: 0), 1,
               "Match Display always resolves to a valid encoder rate")
    checkEqual(RecordingFrameRateSettings.resolve(0, displayRefreshRate: 500), 240,
               "Match Display caps an implausible refresh rate")
    checkEqual(RecordingQuality.decode("balanced"), .balanced,
               "quality names decode case-insensitively like Windows")
    checkEqual(RecordingQuality.legacy.displayName, "Original",
               "the legacy-compatible stored value is presented as Original")

    checkEqual(ScreenClipRecorder.bitsPerSecond(
        quality: .legacy, width: 3840, height: 2160, framesPerSecond: 120), 8_000_000,
        "Original keeps a fixed 8 Mbps bitrate")
    checkEqual(ScreenClipRecorder.bitsPerSecond(
        quality: .balanced, width: 1920, height: 1080, framesPerSecond: 60), 16_000_000,
        "Balanced 1080p60 matches Windows at 16 Mbps")
    checkEqual(ScreenClipRecorder.bitsPerSecond(
        quality: .high, width: 1920, height: 1080, framesPerSecond: 60), 24_000_000,
        "High 1080p60 matches Windows at 24 Mbps")
    checkEqual(ScreenClipRecorder.bitsPerSecond(
        quality: .balanced, width: 1920, height: 1080, framesPerSecond: 30), 8_000_000,
        "Balanced bitrate scales with frame rate")
    checkEqual(ScreenClipRecorder.bitsPerSecond(
        quality: .high, width: 3840, height: 2160, framesPerSecond: 60), 80_000_000,
        "High 4K60 respects the shared 80 Mbps ceiling")
    checkEqual(ScreenClipRecorder.bitsPerSecond(
        quality: .balanced, width: 16, height: 16, framesPerSecond: 15), 2_000_000,
        "Balanced keeps its safe 2 Mbps floor")
    checkEqual(ScreenClipRecorder.bitsPerSecond(
        quality: .high, width: 16, height: 16, framesPerSecond: 15), 3_000_000,
        "High keeps its safe 3 Mbps floor")
}

private func testCaptureFilenameTemplates() throws {
    var calendar = Calendar(identifier: .gregorian)
    let utc = TimeZone(secondsFromGMT: 0)!
    calendar.timeZone = utc
    let capturedAt = calendar.date(from: DateComponents(
        year: 2026,
        month: 9,
        day: 13,
        hour: 6,
        minute: 34,
        second: 56,
        nanosecond: 789_000_000))!

    let defaults = CaptureFilenameConfiguration()
    checkEqual(
        try CaptureFilenameTemplate.stem(
            configuration: defaults,
            kind: .snip,
            capturedAt: capturedAt,
            counter: 1,
            timeZone: utc),
        "HucksSnipNClip_Snip_2026-09-13_06-34-56-789",
        "the default template exactly preserves the historical Snip filename")

    let custom = CaptureFilenameConfiguration(
        label: "Minecraft",
        template: "{label}_{kind}_{date}_{hour}-{minute}-{second}-{milliseconds}_{counter}",
        nextCounter: 7)
    checkEqual(
        try CaptureFilenameTemplate.stem(
            configuration: custom,
            kind: .clip,
            capturedAt: capturedAt,
            counter: 7,
            timeZone: utc),
        "Minecraft_Clip_2026-09-13_06-34-56-789_007",
        "all documented placeholders render with locale-independent local-time fields")

    let blank = try CaptureFilenameTemplate.normalized(CaptureFilenameConfiguration(
        label: "   ",
        template: "  ",
        nextCounter: 0))
    check(blank.isDefault, "a blank label and template normalize to the historical default")
    checkEqual(blank.nextCounter, 1, "a malformed zero counter repairs to one")

    for (template, message) in [
        ("{label}_{unknown}", "an unsupported placeholder is rejected"),
        ("{label", "an unmatched opening brace is rejected"),
        ("label}", "an unmatched closing brace is rejected"),
        ("../{label}", "path traversal is rejected"),
        ("{label}:capture", "cross-platform reserved filename characters are rejected"),
        ("CON", "Windows reserved filenames are rejected for the shared contract"),
        ("{label}.", "a trailing period is rejected")
    ] {
        checkThrows(message) {
            _ = try CaptureFilenameTemplate.stem(
                configuration: CaptureFilenameConfiguration(template: template),
                kind: .snip,
                capturedAt: capturedAt,
                counter: 1,
                timeZone: utc)
        }
    }
}

private func testMissingDestinationFallsBackToRecovery() throws {
    let directory = try makeTemporaryDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }

    guard let image = makeImage(width: 8, height: 6) else {
        failures.append("could not build a test image")
        checks += 1
        return
    }

    let recovery = directory.appendingPathComponent("Recovery", isDirectory: true)
    let service = CaptureSaveService(recoveryDirectory: recovery)

    let destination = directory.appendingPathComponent("Captures", isDirectory: true)
    let saved = try service.savePng(image, destinationDirectory: destination.path)
    check(!saved.usedRecovery, "a writable destination is used directly")
    check(FileManager.default.fileExists(atPath: saved.savedPath), "the snip file exists")
    check(
        (saved.savedPath as NSString).lastPathComponent.hasPrefix("HucksSnipNClip_Snip_"),
        "the snip uses the Huck's Snip 'n' Clip naming")
    check((saved.savedPath as NSString).pathExtension == "png", "the snip is a .png")

    // Two captures in the same millisecond must not overwrite each other.
    let again = try service.savePng(image, destinationDirectory: destination.path)
    check(again.savedPath != saved.savedPath, "a second snip gets its own filename")

    let customNames = CaptureFilenameConfiguration(
        label: "Minecraft",
        template: "{label}_{kind}_{counter}",
        nextCounter: 7)
    let custom = try service.savePng(
        image,
        destinationDirectory: destination.path,
        filenameConfiguration: customNames,
        capturedAt: Date(timeIntervalSince1970: 100),
        counter: 7)
    checkEqual(
        (custom.savedPath as NSString).lastPathComponent,
        "Minecraft_Snip_007.png",
        "a custom label and counter control a future Snip name")
    let originalCustomData = try Data(contentsOf: URL(fileURLWithPath: custom.savedPath))
    let collidingCustom = try service.savePng(
        image,
        destinationDirectory: destination.path,
        filenameConfiguration: customNames,
        capturedAt: Date(timeIntervalSince1970: 100),
        counter: 7)
    checkEqual(
        (collidingCustom.savedPath as NSString).lastPathComponent,
        "Minecraft_Snip_007_01.png",
        "an identical rendered name receives a collision suffix")
    checkEqual(
        try Data(contentsOf: URL(fileURLWithPath: custom.savedPath)),
        originalCustomData,
        "resolving a collision never overwrites the earlier capture")

    // A file where the destination folder should be makes the destination unusable.
    let blocked = directory.appendingPathComponent("Blocked")
    try Data().write(to: blocked)
    let recovered = try service.savePng(image, destinationDirectory: blocked.path)
    check(recovered.usedRecovery, "an unusable destination falls back to recovery")
    check(recovered.destinationError != nil, "the fallback records why the destination failed")
    check(
        recovered.savedPath.hasPrefix(recovery.path),
        "the recovered snip is inside the recovery folder")

    let empty = try service.savePng(image, destinationDirectory: nil)
    check(empty.usedRecovery, "no destination at all falls back to recovery")

    let workingDirectory = directory.appendingPathComponent("Working", isDirectory: true)
    try FileManager.default.createDirectory(at: workingDirectory, withIntermediateDirectories: true)
    let workingClip = workingDirectory.appendingPathComponent("complete.mp4")
    try Data("finished-video".utf8).write(to: workingClip)
    let clipDestination = directory.appendingPathComponent("Clips", isDirectory: true)
    let routedClip = try service.routeCompletedClip(
        workingClip,
        destinationDirectory: clipDestination.path,
        filenameConfiguration: customNames,
        capturedAt: Date(timeIntervalSince1970: 100),
        counter: 8)
    check(!routedClip.usedRecovery, "a finalized clip routes to a writable destination")
    check(FileManager.default.fileExists(atPath: routedClip.savedPath), "the routed clip exists")
    check(!FileManager.default.fileExists(atPath: workingClip.path), "routing removes the working clip")
    checkEqual(
        (routedClip.savedPath as NSString).lastPathComponent,
        "Minecraft_Clip_008.mp4",
        "a finalized clip uses the filename state reserved when recording began")
    check((routedClip.savedPath as NSString).pathExtension == "mp4", "the clip is an .mp4")

    let dualAudioWorkingClip = workingDirectory.appendingPathComponent("complete.mov")
    try Data("finished-dual-audio-video".utf8).write(to: dualAudioWorkingClip)
    let routedDualAudioClip = try service.routeCompletedClip(
        dualAudioWorkingClip,
        destinationDirectory: clipDestination.path)
    check(
        (routedDualAudioClip.savedPath as NSString).pathExtension == "mov",
        "a separate-track clip keeps its .mov container")
    check(
        !FileManager.default.fileExists(atPath: dualAudioWorkingClip.path),
        "routing removes the separate-track working clip")

    let recoveryWorkingClip = workingDirectory.appendingPathComponent("recovery.mp4")
    try Data("recovered-video".utf8).write(to: recoveryWorkingClip)
    let recoveredClip = try service.routeCompletedClip(
        recoveryWorkingClip,
        destinationDirectory: blocked.path)
    check(recoveredClip.usedRecovery, "an unusable clip destination falls back to recovery")
    check(recoveredClip.destinationError != nil, "the clip fallback records its destination error")
    check(recoveredClip.savedPath.hasPrefix(recovery.path), "the recovered clip is inside Recovery")
}

private func testMeterSmoothing() {
    let meter = MeterSmoother()
    checkEqual(meter.update(target: 0.9, elapsedMilliseconds: 150), 0.9, "meter reaches a peak within its attack time")
    checkEqual(meter.update(target: 0.0, elapsedMilliseconds: 400), 0.9, "meter holds a peak before releasing")
    checkEqual(meter.update(target: 0.0, elapsedMilliseconds: 400), 0.9, "meter holds for the full 800 ms")
    checkEqual(meter.update(target: 0.0, elapsedMilliseconds: 400), 0.0, "meter releases after the peak hold")

    meter.reset()
    checkEqual(meter.level, 0.0, "meter reset clears its level")
    checkEqual(meter.update(target: 2.0, elapsedMilliseconds: 150), 1.0, "meter clamps a high input")
    meter.reset()
    checkEqual(meter.update(target: -1.0, elapsedMilliseconds: 150), 0.0, "meter clamps a low input")

    let cpu = SystemLoadSampler()
    checkEqual(cpu.sample(), 0.0, "CPU sampler establishes a baseline before reporting load")
    let load = cpu.sample()
    check(load >= 0.0 && load <= 1.0, "CPU sampler reports a normalized load")
    cpu.reset()
    checkEqual(cpu.sample(), 0.0, "CPU sampler reset clears its baseline")
}

private func testScreenFrameAdmission() {
    check(
        ScreenFrameAdmission.shouldEncode(
            statusRawValue: SCFrameStatus.complete.rawValue,
            hasImageBuffer: true),
        "a complete screen frame with pixels is admitted")
    check(
        !ScreenFrameAdmission.shouldEncode(
            statusRawValue: SCFrameStatus.started.rawValue,
            hasImageBuffer: true),
        "the stream-start lifecycle frame is rejected")
    check(
        !ScreenFrameAdmission.shouldEncode(
            statusRawValue: SCFrameStatus.idle.rawValue,
            hasImageBuffer: true),
        "an idle callback is rejected")
    check(
        !ScreenFrameAdmission.shouldEncode(
            statusRawValue: SCFrameStatus.complete.rawValue,
            hasImageBuffer: false),
        "a complete status without image pixels is rejected")
    check(
        !ScreenFrameAdmission.shouldEncode(statusRawValue: nil, hasImageBuffer: true),
        "a screen frame without status is rejected")
}

private func testScreenClipDimensions() {
    checkEqual(
        ScreenCaptureService.evenPixelCount(321.2, scale: 2),
        642,
        "a Retina region becomes an even encoder width")
    checkEqual(
        ScreenCaptureService.evenPixelCount(100.6, scale: 1),
        100,
        "an odd rounded size is lowered to the nearest encoder-safe even value")
    checkEqual(
        ScreenCaptureService.evenPixelCount(0.1, scale: 1),
        2,
        "a clip target never becomes smaller than two pixels")
}

private func testRegionRecordingShadeGeometry() {
    let screenRect = RegionRecordingShade.screenSpaceRect(
        for: CGRect(x: -420, y: 125, width: 640, height: 360),
        primaryScreenMaxY: 982)
    checkEqual(screenRect.origin.x, -420, "Region shade preserves the global horizontal position")
    checkEqual(screenRect.origin.y, 497, "Region shade converts top-origin Y to AppKit screen Y")
    checkEqual(screenRect.size.width, 640, "Region shade preserves the selected width")
    checkEqual(screenRect.size.height, 360, "Region shade preserves the selected height")
}

private func testRegionSelectionGeometry() {
    let secondaryDisplaySelection = RegionSelectionGeometry.displaySpaceRect(
        viewRect: CGRect(x: 10, y: 20, width: 640, height: 360),
        windowOrigin: CGPoint(x: 1920, y: -1080),
        primaryScreenMaxY: 1080)
    checkEqual(secondaryDisplaySelection.origin.x, 1930,
               "region selection preserves a secondary display's horizontal position")
    checkEqual(secondaryDisplaySelection.origin.y, 1780,
               "region selection converts a display below the primary into capture coordinates")
    checkEqual(secondaryDisplaySelection.size.width, 640,
               "region selection preserves width on another display")
    checkEqual(secondaryDisplaySelection.size.height, 360,
               "region selection preserves height on another display")

    let leftDisplaySelection = RegionSelectionGeometry.displaySpaceRect(
        viewRect: CGRect(x: 25, y: 50, width: 300, height: 200),
        windowOrigin: CGPoint(x: -1920, y: 0),
        primaryScreenMaxY: 1080)
    checkEqual(leftDisplaySelection.origin.x, -1895,
               "region selection preserves a display left of the primary")
    checkEqual(leftDisplaySelection.origin.y, 830,
               "region selection converts a side-by-side display's vertical position")
}

private func testTrayIconStates() {
    let idle = TrayIconFactory.makeIcon()
    checkEqual(idle.size, NSSize(width: 18, height: 18), "idle H uses one logical size on every display")
    check(idle.isTemplate, "idle H is system-tinted on every display")

    let recording = TrayIconFactory.makeIcon(recording: true, inputLevel: 0.5, strainLevel: 0.5)
    checkEqual(recording.size, idle.size, "recording and idle H share the same logical bounds")
    check(!recording.isTemplate, "recording H keeps its meter colours instead of being system-tinted")

    let paused = TrayIconFactory.makeIcon(recording: true, paused: true)
    checkEqual(paused.size, idle.size, "paused and recording H share the same logical bounds")
    check(!paused.isTemplate, "paused H preserves its distinct pause treatment")
    checkEqual(
        paused.accessibilityDescription,
        "Huck’s Snip ’n’ Clip — recording paused",
        "paused H announces its state")

    let finishing = TrayIconFactory.makeIcon(recording: true, finishing: true)
    checkEqual(finishing.size, idle.size, "finishing and recording H share the same logical bounds")
    check(!finishing.isTemplate, "finishing H preserves its distinct progress treatment")
    checkEqual(
        finishing.accessibilityDescription,
        "Huck’s Snip ’n’ Clip — finishing recording",
        "finishing H announces its state")
}

/// Rasterizes the icon and reads the actual pixels, because the thing being asserted here is what
/// someone sees in the menu bar, not what a function returns.
private func samplePixel(_ image: NSImage, side: Int, x: Int, y: Int) -> (r: Int, g: Int, b: Int)? {
    guard let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: side, pixelsHigh: side,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .calibratedRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSGraphicsContext.current?.imageInterpolation = .none
    image.draw(in: NSRect(x: 0, y: 0, width: side, height: side))
    NSGraphicsContext.restoreGraphicsState()
    guard let color = rep.colorAt(x: x, y: y) else { return nil }
    return (
        Int((color.redComponent * 255).rounded()),
        Int((color.greenComponent * 255).rounded()),
        Int((color.blueComponent * 255).rounded()))
}

private func sampleAlpha(_ image: NSImage, side: Int, x: Int, y: Int) -> Int? {
    guard let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: side, pixelsHigh: side,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .calibratedRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSGraphicsContext.current?.imageInterpolation = .none
    image.draw(in: NSRect(x: 0, y: 0, width: side, height: side))
    NSGraphicsContext.restoreGraphicsState()
    return rep.colorAt(x: x, y: y).map { Int(($0.alphaComponent * 255).rounded()) }
}

private func testRestingCutFilmRaster() {
    let idle = TrayIconFactory.makeIcon()
    let cut = sampleAlpha(idle, side: 18, x: 5, y: 6) ?? 255
    let filmHole = sampleAlpha(idle, side: 18, x: 11, y: 4) ?? 255
    let filmBody = sampleAlpha(idle, side: 18, x: 13, y: 4) ?? 0
    check(cut < 128, "the actual 18-pixel resting H preserves the left cut path")
    check(filmHole < 128, "the actual 18-pixel resting H preserves a film perforation")
    check(filmBody > 128, "the actual 18-pixel resting H keeps film between its perforations")
}

/// Turns the actual 18-pixel icon into a hue-free byte signature. Every public state must retain a
/// different signature here; otherwise color was doing work that shape or meter height should do.
private func monochromeSignature(_ image: NSImage) -> Data? {
    let side = 18
    guard let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: side, pixelsHigh: side,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .calibratedRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSGraphicsContext.current?.imageInterpolation = .none
    NSAppearance(named: .aqua)?.performAsCurrentDrawingAppearance {
        image.draw(in: NSRect(x: 0, y: 0, width: side, height: side))
    }
    NSGraphicsContext.restoreGraphicsState()

    var bytes: [UInt8] = []
    bytes.reserveCapacity(side * side * 2)
    for y in 0..<side {
        for x in 0..<side {
            guard let color = rep.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB) else {
                bytes.append(0)
                bytes.append(0)
                continue
            }
            let luminance = 0.2126 * color.redComponent
                + 0.7152 * color.greenComponent
                + 0.0722 * color.blueComponent
            bytes.append(UInt8((luminance * 255).rounded()))
            bytes.append(UInt8((color.alphaComponent * 255).rounded()))
        }
    }
    return Data(bytes)
}

private func testMonochromeTrayIconStates() {
    let images = [
        TrayIconFactory.makeIcon(),
        TrayIconFactory.makeIcon(recording: true, inputLevel: 0.25, strainLevel: 0.25),
        TrayIconFactory.makeIcon(recording: true, inputLevel: 0.5, strainLevel: 0.5),
        TrayIconFactory.makeIcon(recording: true, inputLevel: 0.95, strainLevel: 0.5),
        TrayIconFactory.makeIcon(recording: true, inputLevel: 0.5, strainLevel: 0.95),
        TrayIconFactory.makeIcon(recording: true, paused: true),
        TrayIconFactory.makeIcon(recording: true, finishing: true)
    ]
    let signatures = images.compactMap(monochromeSignature)
    checkEqual(signatures.count, images.count, "every H state rasterizes for monochrome inspection")
    checkEqual(
        Set(signatures).count,
        images.count,
        "idle, meter levels, warning sides, pause, and finishing remain distinct without hue")
}

/// The right post used to be red from its first pixel, which made "red" mean nothing in particular.
/// It is now amber for ordinary load and turns red only once the whole machine is genuinely busy.
/// The left post keeps its approved behaviour, where only the part above the threshold changes.
private func testMeterWarningColors() {
    let side = 288
    // Right post spans x 35-58 of 64; left post plus connector spans x 6-35. Sampled well inside
    // each, and at a height both a half-full and a nearly-full bar cover.
    let rightX = 200
    let leftX = 90

    guard let ordinary = samplePixel(
        TrayIconFactory.makeIcon(recording: true, inputLevel: 0.5, strainLevel: 0.5),
        side: side, x: rightX, y: 200) else {
        check(false, "the recording icon rasterizes for inspection")
        return
    }
    check(
        ordinary.g > 110,
        "ordinary CPU load draws the right post amber, not red (got \(ordinary))")

    guard let overloaded = samplePixel(
        TrayIconFactory.makeIcon(recording: true, inputLevel: 0.5, strainLevel: 0.95),
        side: side, x: rightX, y: 200) else {
        check(false, "the overloaded icon rasterizes for inspection")
        return
    }
    check(
        overloaded.g < 110 && overloaded.r > 150,
        "an overloaded machine turns the whole right post red (got \(overloaded))")

    // Just below the threshold must still be amber, or the warning would fire early.
    if let justUnder = samplePixel(
        TrayIconFactory.makeIcon(recording: true, inputLevel: 0.5, strainLevel: 0.84),
        side: side, x: rightX, y: 200) {
        check(
            justUnder.g > 110,
            "load just under the threshold stays amber (got \(justUnder))")
    }

    // The left post is unchanged: green below the threshold with the warning colour only on top.
    if let loudBase = samplePixel(
        TrayIconFactory.makeIcon(recording: true, inputLevel: 0.95, strainLevel: 0.5),
        side: side, x: leftX, y: 150),
       let loudCap = samplePixel(
        TrayIconFactory.makeIcon(recording: true, inputLevel: 0.95, strainLevel: 0.5),
        side: side, x: leftX, y: 50) {
        check(
            loudBase.r < 130,
            "a loud input keeps its green body rather than flooding (got \(loudBase))")
        check(
            loudCap.r > 150,
            "a loud input still caps only the part above the threshold (got \(loudCap))")
    }

    checkEqual(
        TrayIconFactory.meterWarningThreshold,
        0.85,
        "both posts share one idea of entering the warning band")
}

private func testPauseTimeline() {
    let twoSeconds = CMTime(seconds: 2, preferredTimescale: 600)
    let tenSeconds = CMTime(seconds: 10, preferredTimescale: 600)
    let eightSeconds = ScreenClipRecorder.timelineTime(
        rawTime: tenSeconds,
        accumulatedPauseDuration: twoSeconds,
        activePauseStart: nil)
    checkEqual(CMTimeCompare(eightSeconds, CMTime(seconds: 8, preferredTimescale: 600)), 0,
               "completed pauses are removed from the clip timeline")

    let whilePaused = ScreenClipRecorder.timelineTime(
        rawTime: tenSeconds,
        accumulatedPauseDuration: twoSeconds,
        activePauseStart: CMTime(seconds: 7, preferredTimescale: 600))
    checkEqual(CMTimeCompare(whilePaused, CMTime(seconds: 5, preferredTimescale: 600)), 0,
               "stopping while paused removes the active pause interval")

    let requestedStop = CMTime(seconds: 8, preferredTimescale: 600)
    let audioEnd = CMTime(seconds: 8.04, preferredTimescale: 600)
    let sessionEnd = ScreenClipRecorder.sessionEndTime(
        requestedStopTime: requestedStop,
        lastVideoTime: CMTime(seconds: 7.9, preferredTimescale: 600),
        lastAudioEndTime: audioEnd,
        framesPerSecond: 60)
    checkEqual(CMTimeCompare(sessionEnd, audioEnd), 0,
               "writer session ends at the latest pause-adjusted media time")

    let videoLedEnd = ScreenClipRecorder.sessionEndTime(
        requestedStopTime: requestedStop,
        lastVideoTime: CMTime(seconds: 8.1, preferredTimescale: 600),
        lastAudioEndTime: nil,
        framesPerSecond: 60)
    check(CMTimeCompare(videoLedEnd, CMTime(seconds: 8.1, preferredTimescale: 600)) > 0,
          "writer session includes the final video frame")
}

private func testSeparateAudioTrackContainer() throws {
    let directory = try makeTemporaryDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let writer = try AVAssetWriter(
        outputURL: directory.appendingPathComponent("two-audio-tracks.mov"),
        fileType: .mov)
    let video = AVAssetWriterInput(mediaType: .video, outputSettings: [
        AVVideoCodecKey: AVVideoCodecType.h264,
        AVVideoWidthKey: 640,
        AVVideoHeightKey: 360
    ])
    let canAddVideo = writer.canAdd(video)
    check(canAddVideo, "MOV writer accepts its video input before audio tracks")
    if canAddVideo { writer.add(video) }
    let systemAudio = ScreenClipRecorder.makeAudioInput(title: "System / App Audio")
    let canAddSystemAudio = writer.canAdd(systemAudio)
    check(canAddSystemAudio, "MOV writer accepts the System / App Audio track")
    if canAddSystemAudio { writer.add(systemAudio) }
    let microphone = ScreenClipRecorder.makeAudioInput(title: "Microphone")
    let canAddMicrophone = writer.canAdd(microphone)
    check(canAddMicrophone, "MOV writer accepts a separate Microphone track")
    if canAddMicrophone { writer.add(microphone) }
    checkEqual(
        writer.inputs.filter { $0.mediaType == .audio }.count,
        2,
        "MOV writer retains two independent audio inputs")
    checkEqual(
        systemAudio.metadata.first?.stringValue,
        "System / App Audio",
        "system/app audio track carries its editor-visible name")
    checkEqual(
        microphone.metadata.first?.stringValue,
        "Microphone",
        "microphone track carries its editor-visible name")
    writer.cancelWriting()
}

/// The recorder counted dropped frames from the start and the app threw the number away, so a
/// Gaming-preset clip that lost 8% of its frames looked identical to a clean one. These checks pin
/// the distinction that makes the number worth showing: frames ScreenCaptureKit never offered are
/// not a fault, frames the encoder refused are.
/// Frame size, not bitrate, is what decides whether a recording holds its frame rate. This setting
/// must never impose a shape of its own: the app ships to other people on displays, windows, and
/// regions of every proportion, so the only thing it is allowed to do is put a ceiling on height.
/// The one promise a resolution setting has to keep on an upgrade: somebody who already records
/// with this app keeps getting exactly what they got yesterday. Only a genuinely new settings file
/// takes the 1080p default.
/// Rebinding is only safe if every key it can capture also survives a trip through settings.ini,
/// and if a combination that would hijack the user's keyboard is refused before it is ever
/// registered.
private func testShortcutRebinding() {
    // Every key the capture surface can offer must round-trip through the settings file.
    for (code, name) in ShortcutBinding.keyCodes {
        checkEqual(ShortcutBinding.keyName(for: code), name, "key code \(code) is named \(name)")
        checkEqual(ShortcutBinding.keyCode(for: name), code, "key name \(name) decodes to its code")
        let binding = ShortcutBinding(modifiers: ShortcutBinding.captureModifiers, keyCode: code)
        checkEqual(
            ShortcutBinding.decode(binding.encoded),
            binding,
            "a \(name) shortcut survives a settings.ini round trip")
    }

    // Key names must be unique, or decoding a settings file would silently pick the wrong key.
    let names = Set(ShortcutBinding.keyCodes.map { $0.1.uppercased() })
    checkEqual(names.count, ShortcutBinding.keyCodes.count, "no two keys share a name")
    let codes = Set(ShortcutBinding.keyCodes.map { $0.0 })
    checkEqual(codes.count, ShortcutBinding.keyCodes.count, "no two key names share a code")

    // A bare first key would replace normal typing globally. A bare second key is safe because it
    // is listened for only during the short chord window, matching the PC behavior.
    check(
        ShortcutCaptureOverlay.binding(keyCode: UInt32(kVK_ANSI_S), appKitModifiers: []) == nil,
        "a key pressed with no modifier is refused rather than taking over that key everywhere")
    check(
        ShortcutCaptureOverlay.binding(keyCode: UInt32(kVK_ANSI_S), appKitModifiers: [.shift]) != nil,
        "a single modifier is enough to make a shortcut")
    check(
        ShortcutCaptureOverlay.stroke(
            keyCode: UInt32(kVK_ANSI_R),
            appKitModifiers: [],
            requiresModifier: false) != nil,
        "a modifier-free second stroke is accepted during chord capture")
    check(
        ShortcutCaptureOverlay.stroke(
            keyCode: UInt32(kVK_ANSI_R),
            appKitModifiers: [],
            requiresModifier: true) == nil,
        "the same bare key is still rejected as a first stroke")

    // A key this build cannot name could be written to settings.ini and never read back.
    check(
        ShortcutCaptureOverlay.binding(keyCode: 999, appKitModifiers: [.command]) == nil,
        "a key this build cannot name is refused rather than saved unreadably")

    // AppKit modifiers have to reach Carbon as the values RegisterEventHotKey expects.
    let all = ShortcutCaptureOverlay.binding(
        keyCode: UInt32(kVK_ANSI_K),
        appKitModifiers: [.control, .option, .shift, .command])
    checkEqual(
        all?.modifiers,
        UInt32(controlKey | optionKey | shiftKey | cmdKey),
        "every AppKit modifier maps to its Carbon equivalent")
    checkEqual(all?.keyCode, UInt32(kVK_ANSI_K), "the pressed key survives translation")

    let controlOnly = ShortcutCaptureOverlay.binding(
        keyCode: UInt32(kVK_F5), appKitModifiers: [.control])
    checkEqual(controlOnly?.modifiers, UInt32(controlKey), "Control alone maps to Carbon's controlKey")
    checkEqual(controlOnly?.displayString, "\u{2303}F5", "a function-key shortcut displays readably")

    // A named key has no single menu character; printing "f5" into the shortcut column would lie.
    checkEqual(
        ShortcutBinding(modifiers: ShortcutBinding.captureModifiers, keyCode: UInt32(kVK_F5))
            .menuKeyEquivalent,
        "",
        "a named key shows no menu key equivalent rather than a misleading letter")
    checkEqual(
        ShortcutBinding(modifiers: ShortcutBinding.captureModifiers, keyCode: UInt32(kVK_ANSI_S))
            .menuKeyEquivalent,
        "s",
        "a letter still shows its menu key equivalent")
    checkEqual(
        ShortcutBinding(
            modifiers: ShortcutBinding.captureModifiers,
            keyCode: UInt32(kVK_ANSI_C),
            secondModifiers: 0,
            secondKeyCode: UInt32(kVK_ANSI_R)).menuKeyEquivalent,
        "",
        "a chord does not pretend AppKit can express it as one menu equivalent")

    let base = ShortcutBinding(
        modifiers: UInt32(controlKey | optionKey),
        keyCode: UInt32(kVK_ANSI_C),
        secondModifiers: UInt32(controlKey),
        secondKeyCode: UInt32(kVK_ANSI_R))
    let samePrefix = ShortcutBinding(
        modifiers: UInt32(controlKey | optionKey),
        keyCode: UInt32(kVK_ANSI_C),
        secondModifiers: 0,
        secondKeyCode: UInt32(kVK_ANSI_S))
    let startsOnSecond = ShortcutBinding(modifiers: UInt32(controlKey), keyCode: UInt32(kVK_ANSI_R))
    let completesOnFirst = ShortcutBinding(
        modifiers: UInt32(controlKey),
        keyCode: UInt32(kVK_ANSI_K),
        secondModifiers: UInt32(controlKey | optionKey),
        secondKeyCode: UInt32(kVK_ANSI_C))
    let unrelated = ShortcutBinding(
        modifiers: UInt32(controlKey | optionKey),
        keyCode: UInt32(kVK_ANSI_X),
        secondModifiers: 0,
        secondKeyCode: UInt32(kVK_ANSI_Y))
    check(base.conflictsInternally(with: samePrefix), "two actions cannot share a chord prefix")
    check(base.conflictsInternally(with: startsOnSecond), "a second stroke cannot start another action")
    check(base.conflictsInternally(with: completesOnFirst), "a first stroke cannot complete another chord")
    check(!base.conflictsInternally(with: unrelated), "unrelated two-step chords do not conflict")

    // The shipped defaults must not collide with each other, or one action would be unreachable.
    let defaults = CaptureAction.allCases.map { ShortcutBinding.defaultBinding(for: $0) }
    checkEqual(
        Set(defaults).count,
        CaptureAction.allCases.count,
        "no two capture actions ship with the same default shortcut")
}

private func testLaunchAtLoginPresentation() {
    let off = LaunchAtLoginService.presentation(for: .off)
    checkEqual(off.title, "Start at Login", "an unregistered login item uses the normal title")
    checkEqual(off.checkmark, .off, "an unregistered login item is unchecked")

    let on = LaunchAtLoginService.presentation(for: .on)
    checkEqual(on.title, "Start at Login", "an enabled login item keeps the normal title")
    checkEqual(on.checkmark, .on, "an enabled login item is checked")

    let approval = LaunchAtLoginService.presentation(for: .requiresApproval)
    check(
        approval.title.contains("Needs Approval"),
        "a login item disabled by macOS says that approval is needed")
    checkEqual(
        approval.checkmark,
        .mixed,
        "a registered login item awaiting approval is not presented as fully enabled")

}

private func testRecordingResolutionMigration() throws {
    let directory = FileManager.default.temporaryDirectory
        .appendingPathComponent("hsnc-resolution-\(UUID().uuidString)", isDirectory: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

    let store = SettingsStore(
        directory: directory,
        defaultDestinationPath: { directory.appendingPathComponent("Ingest").path })

    // A first run has no settings file at all.
    let fresh = store.load()
    checkEqual(
        fresh.recordingResolution,
        .p1080,
        "a brand-new install records at 1080p so a clip holds its frame rate out of the box")

    // An existing install: a settings file written before this setting existed.
    let legacy = """
        Version=4
        RecordingAudioMode=ComputerAndMicrophone
        RecordingFrameRate=60
        RecordingQuality=High
        Destination=abc|SW5nZXN0|\(Data(directory.path.utf8).base64EncodedString())
        ActiveDestinationId=abc
        """
    try legacy.write(to: store.settingsURL, atomically: true, encoding: .utf8)
    let upgraded = store.load()
    checkEqual(
        upgraded.recordingResolution,
        .native,
        "an existing install keeps recording full size instead of silently shrinking")
    checkEqual(upgraded.recordingFrameRate, 60, "migration leaves the frame rate alone")
    checkEqual(upgraded.recordingQuality, .high, "migration leaves the quality alone")
    checkEqual(
        upgraded.recordingAudioMode,
        .computerAndMicrophone,
        "migration leaves the audio mode alone")
    check(
        !upgraded.recordingShowsCursor,
        "an existing install keeps its established cursor-free recording behavior")

    // An unreadable value must fall back to the safe existing behavior, never to a guess.
    let corrupt = store.load()
    corrupt.recordingResolution = .p720
    try store.save(corrupt)
    var written = try String(contentsOf: store.settingsURL, encoding: .utf8)
    written = written.replacingOccurrences(of: "RecordingResolution=720p", with: "RecordingResolution=4K")
    written = written.replacingOccurrences(of: "RecordingCursor=false", with: "RecordingCursor=maybe")
    try written.write(to: store.settingsURL, atomically: true, encoding: .utf8)
    let repaired = store.load()
    checkEqual(
        repaired.recordingResolution,
        .native,
        "an unrecognized stored resolution repairs to full size rather than a guess")
    check(
        !repaired.recordingShowsCursor,
        "an unrecognized cursor value repairs to hidden rather than recording unexpectedly")
    let repairedContents = try String(contentsOf: store.settingsURL, encoding: .utf8)
    check(
        repairedContents.contains("RecordingCursor=false"),
        "a repaired cursor value is written back canonically")
}

private func testRecordingResolution() {
    // A 16:10-ish laptop display. Scaling keeps its proportions rather than forcing 16:9.
    let laptop = RecordingResolution.outputSize(
        sourceWidth: 3024, sourceHeight: 1964, resolution: .p1080)
    checkEqual(laptop.height, 1080, "a 1080p ceiling gives exactly 1080 lines")
    checkEqual(laptop.width, 1662, "a 3024x1964 display keeps its own proportions at 1080p")

    // A 16:9 display lands on the familiar number without that number being hard-coded anywhere.
    let sixteenNine = RecordingResolution.outputSize(
        sourceWidth: 3840, sourceHeight: 2160, resolution: .p1080)
    checkEqual(sixteenNine.width, 1920, "a 16:9 source scales to 1920 wide at 1080p")
    checkEqual(sixteenNine.height, 1080, "a 16:9 source scales to 1080 tall at 1080p")

    // An ultrawide stays ultrawide.
    let ultrawide = RecordingResolution.outputSize(
        sourceWidth: 3440, sourceHeight: 1440, resolution: .p720)
    checkEqual(ultrawide.height, 720, "an ultrawide obeys the height ceiling")
    checkEqual(ultrawide.width, 1720, "an ultrawide keeps its width rather than being cropped")

    // A tall narrow region is just as valid a capture as a display.
    let tall = RecordingResolution.outputSize(
        sourceWidth: 600, sourceHeight: 1800, resolution: .p1080)
    checkEqual(tall.height, 1080, "a tall region obeys the height ceiling")
    checkEqual(tall.width, 360, "a tall region stays tall and narrow")

    // Never enlarge: a small region must record at its own size, not be blown up to the ceiling.
    let smallRegion = RecordingResolution.outputSize(
        sourceWidth: 162, sourceHeight: 34, resolution: .p1080)
    checkEqual(smallRegion.width, 162, "a small region is never widened to reach the ceiling")
    checkEqual(smallRegion.height, 34, "a small region is never heightened to reach the ceiling")

    // Native is a true pass-through, including sizes the scaler would have had to round.
    let native = RecordingResolution.outputSize(
        sourceWidth: 1923, sourceHeight: 1081, resolution: .native)
    checkEqual(native.width, 1923, "native passes the source width through untouched")
    checkEqual(native.height, 1081, "native passes the source height through untouched")

    // H.264 encodes in 2x2 blocks, so an edge this setting produces can never come out odd. A
    // source that was already under the ceiling is passed straight through instead, odd sizes and
    // all, because that is exactly what the recorder did before this setting existed and an
    // untouched capture must not start changing size.
    for source in [(1001, 1999), (777, 1333), (3001, 2001), (999, 1081)] {
        for resolution in [RecordingResolution.p1440, .p1080, .p720] {
            let size = RecordingResolution.outputSize(
                sourceWidth: source.0, sourceHeight: source.1, resolution: resolution)
            let wasScaled = source.1 > (resolution.maximumHeight ?? Int.max)
            if wasScaled {
                check(
                    size.width % 2 == 0 && size.height % 2 == 0,
                    "scaled \(source.0)x\(source.1) to \(resolution.rawValue) stays even-sided")
                check(
                    size.height == resolution.maximumHeight,
                    "scaled \(source.0)x\(source.1) to \(resolution.rawValue) meets the ceiling")
            } else {
                check(
                    size.width == source.0 && size.height == source.1,
                    "\(source.0)x\(source.1) is under the \(resolution.rawValue) ceiling and is untouched")
            }
            check(
                size.width >= 2 && size.height >= 2,
                "\(source.0)x\(source.1) at \(resolution.rawValue) stays a real picture")
        }
    }

    // A degenerate target must not divide by zero or produce a negative picture.
    let empty = RecordingResolution.outputSize(sourceWidth: 0, sourceHeight: 0, resolution: .p1080)
    checkEqual(empty.width, 0, "a zero-width source is passed through rather than divided by")
    checkEqual(empty.height, 0, "a zero-height source is passed through rather than divided by")

    checkEqual(
        RecordingResolution.decode("1080P"),
        RecordingResolution.p1080,
        "a stored resolution decodes regardless of case")
    check(
        RecordingResolution.decode("4k") == nil,
        "an unknown resolution is rejected rather than guessed")
}

private func testRecordingCursorConfiguration() {
    let configuration = SCStreamConfiguration()
    ScreenClipRecorder.applyCursorPreference(to: configuration, showsCursor: true)
    check(configuration.showsCursor, "the visible cursor choice reaches ScreenCaptureKit")

    ScreenClipRecorder.applyCursorPreference(to: configuration, showsCursor: false)
    check(!configuration.showsCursor, "the hidden cursor choice reaches ScreenCaptureKit")
}

private func testFrameDelivery() {
    let clean = FrameDelivery(
        requestedFramesPerSecond: 60,
        offeredFrames: 1200,
        droppedFrames: 0,
        seconds: 20)
    checkEqual(clean.writtenFrames, 1200, "a clean recording writes every offered frame")
    checkEqual(clean.droppedFraction, 0.0, "a clean recording reports no dropped share")
    checkEqual(clean.achievedFramesPerSecond, 60.0, "a clean 60 FPS recording achieves 60 FPS")
    check(!clean.isStrained, "a clean recording is not strained")
    check(clean.strainNotice == nil, "a clean recording adds nothing to the saved-clip toast")

    // A still screen legitimately produces few changed frames. Its measured rate remains visible,
    // but it must not be mislabeled as a drop.
    let still = FrameDelivery(
        requestedFramesPerSecond: 60,
        offeredFrames: 40,
        droppedFrames: 0,
        seconds: 20)
    checkEqual(still.droppedFraction, 0.0, "a still screen drops no frames however few arrive")
    checkEqual(still.achievedFramesPerSecond, 2.0, "a still screen reports its honest low rate")
    check(!still.isStrained, "a still screen is not a strained recording")
    check(still.strainNotice == nil, "a still screen stays silent in the saved-clip toast")
    checkEqual(
        still.deliverySummary,
        "2 new FPS / 60 max",
        "a still screen's low change rate is visible without calling it loss")

    // The measured 21:03 gameplay case: the encoder could not keep up at 3024x1964.
    let strained = FrameDelivery(
        requestedFramesPerSecond: 60,
        offeredFrames: 1200,
        droppedFrames: 96,
        seconds: 20)
    checkEqual(strained.writtenFrames, 1104, "a strained recording writes only what was accepted")
    check(
        abs(strained.droppedFraction - 0.08) < 0.0001,
        "a strained recording reports the share the encoder refused")
    check(
        abs(strained.achievedFramesPerSecond - 55.2) < 0.0001,
        "a strained recording reports the frame rate it actually achieved")
    check(strained.isStrained, "losing 8% of offered frames counts as strained")
    checkEqual(
        strained.strainNotice,
        "encoder dropped 8%",
        "a strained recording names only the loss it can prove")

    // The measured 21:55 case cannot honestly be called a drop because ScreenCaptureKit's frame
    // interval is a maximum and the source's change rate is unknown. It still has to reach the user.
    let upstream = FrameDelivery(
        requestedFramesPerSecond: 120,
        offeredFrames: 1520,
        droppedFrames: 0,
        seconds: 20)
    check(!upstream.isStrained, "an unprovable upstream shortfall is not mislabeled as a drop")
    checkEqual(
        upstream.deliverySummary,
        "76 new FPS / 120 max",
        "the measured upstream result reaches the saved-clip notice without inventing loss")

    // Guards against dividing by zero when a clip is stopped before any frame arrives.
    let empty = FrameDelivery(
        requestedFramesPerSecond: 60,
        offeredFrames: 0,
        droppedFrames: 0,
        seconds: 0)
    checkEqual(empty.droppedFraction, 0.0, "an empty recording reports no dropped share")
    checkEqual(empty.achievedFramesPerSecond, 0.0, "an empty recording reports no frame rate")
    check(empty.strainNotice == nil, "an empty recording adds nothing to the toast")

    checkEqual(
        FrameDelivery.tooltipFragment(droppedFraction: 0.08),
        "dropping 8%",
        "the live tooltip names the dropped share while recording")
    check(
        FrameDelivery.tooltipFragment(droppedFraction: 0.0) == nil,
        "the live tooltip stays quiet when no frames are being lost")
    check(
        FrameDelivery.tooltipFragment(droppedFraction: 0.01) == nil,
        "a single stray dropped frame does not turn the tooltip into a warning")
    check(
        FrameDelivery.tooltipFragment(droppedFraction: FrameDelivery.strainThreshold) != nil,
        "the strain threshold itself reads as strained")
}

private func testAudioGainMath() {
    checkEqual(ScreenClipRecorder.scaledFloatSample(0.25, multiplier: 2), 0.5,
               "audio gain raises a PCM sample")
    checkEqual(ScreenClipRecorder.scaledFloatSample(0.75, multiplier: 2), 1,
               "positive PCM gain saturates without clipping overflow")
    checkEqual(ScreenClipRecorder.scaledFloatSample(-0.75, multiplier: 2), -1,
               "negative PCM gain saturates without clipping overflow")
    checkEqual(ScreenClipRecorder.scaledFloatSample(0.75, multiplier: 0), 0,
               "zero-percent gain mutes a PCM sample")
}

private func testAudioGainSampleBuffer() {
    let values: [Float] = [0.25, -0.75, 0.75, 0]
    let samples = UnsafeMutablePointer<Float>.allocate(capacity: values.count)
    samples.initialize(from: values, count: values.count)
    defer {
        samples.deinitialize(count: values.count)
        samples.deallocate()
    }

    let byteCount = values.count * MemoryLayout<Float>.size
    var blockBuffer: CMBlockBuffer?
    let blockStatus = CMBlockBufferCreateWithMemoryBlock(
        allocator: kCFAllocatorDefault,
        memoryBlock: samples,
        blockLength: byteCount,
        blockAllocator: kCFAllocatorNull,
        customBlockSource: nil,
        offsetToData: 0,
        dataLength: byteCount,
        flags: 0,
        blockBufferOut: &blockBuffer)
    checkEqual(blockStatus, noErr, "a mutable PCM block buffer can be created")
    guard let blockBuffer else { return }

    var streamDescription = AudioStreamBasicDescription(
        mSampleRate: 48_000,
        mFormatID: kAudioFormatLinearPCM,
        mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
        mBytesPerPacket: UInt32(MemoryLayout<Float>.size),
        mFramesPerPacket: 1,
        mBytesPerFrame: UInt32(MemoryLayout<Float>.size),
        mChannelsPerFrame: 1,
        mBitsPerChannel: 32,
        mReserved: 0)
    var formatDescription: CMAudioFormatDescription?
    let formatStatus = CMAudioFormatDescriptionCreate(
        allocator: kCFAllocatorDefault,
        asbd: &streamDescription,
        layoutSize: 0,
        layout: nil,
        magicCookieSize: 0,
        magicCookie: nil,
        extensions: nil,
        formatDescriptionOut: &formatDescription)
    checkEqual(formatStatus, noErr, "a Float32 PCM format description can be created")
    guard let formatDescription else { return }

    var sampleBuffer: CMSampleBuffer?
    let sampleStatus = CMAudioSampleBufferCreateReadyWithPacketDescriptions(
        allocator: kCFAllocatorDefault,
        dataBuffer: blockBuffer,
        formatDescription: formatDescription,
        sampleCount: values.count,
        presentationTimeStamp: .zero,
        packetDescriptions: nil,
        sampleBufferOut: &sampleBuffer)
    checkEqual(sampleStatus, noErr, "a Float32 PCM sample buffer can be created")
    guard let sampleBuffer else { return }

    ScreenClipRecorder.applyGainInPlace(to: sampleBuffer, percent: 200)
    checkEqual(samples[0], 0.5, "sample-buffer gain changes the encoded PCM data")
    checkEqual(samples[1], -1, "sample-buffer gain saturates negative PCM")
    checkEqual(samples[2], 1, "sample-buffer gain saturates positive PCM")
    checkEqual(samples[3], 0, "sample-buffer gain preserves silence")
}

private func testPersistentMenuChoiceView() {
    var selected = false
    var activationCount = 0
    let choice = PersistentMenuChoiceView(
        title: "Keep Menu Open",
        style: .checkbox,
        isSelected: { selected },
        activation: {
            selected.toggle()
            activationCount += 1
        })

    checkEqual(choice.displayedStateForTesting, .off, "persistent menu choice begins unchecked")
    choice.activateForTesting()
    checkEqual(activationCount, 1, "persistent menu choice invokes its setting action")
    checkEqual(choice.displayedStateForTesting, .on, "persistent menu choice refreshes after its action")

    let disabled = PersistentMenuChoiceView(
        title: "Disabled While Recording",
        style: .radio,
        isSelected: { false },
        isEnabled: { false },
        activation: { activationCount += 1 })
    check(!disabled.isEnabledForTesting, "persistent menu choice reflects a disabled setting")
    disabled.activateForTesting()
    checkEqual(activationCount, 1, "a disabled persistent menu choice cannot invoke its action")
}

// MARK: - Recording controls, floating controller, pasteboard

private let controlOptionShift = UInt32(controlKey | optionKey | shiftKey)
private let controlOption = UInt32(controlKey | optionKey)

private func testRecordingControlSettings() throws {
    let directory = try makeTemporaryDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let store = SettingsStore(directory: directory, defaultDestinationPath: { directory.path })

    let fresh = store.load()
    checkEqual(
        fresh.recordingShortcut(for: .pauseResume),
        ShortcutStroke(modifiers: controlOptionShift, keyCode: UInt32(kVK_ANSI_P)),
        "a fresh install pauses and resumes with Control-Option-Shift-P")
    checkEqual(
        fresh.recordingShortcut(for: .stop),
        ShortcutStroke(modifiers: controlOptionShift, keyCode: UInt32(kVK_ANSI_X)),
        "a fresh install stops with Control-Option-Shift-X")
    check(fresh.showFloatingRecordingControls, "the floating controller is on by default")
    checkEqual(fresh.floatingControllerOpacityPercent, 50, "the floating controller defaults to 50% opacity")
    checkEqual(fresh.floatingControllerSize, 72, "the floating controller has a default size")
    checkEqual(fresh.controllerLayouts.count, 0, "no layout position is written until one is taught")

    let written = try String(contentsOf: store.settingsURL, encoding: .utf8)
    check(written.contains("RecordingShortcut.PauseResume=control+option+shift+P"),
          "the Pause/Resume shortcut uses the Windows key name")
    check(written.contains("RecordingShortcut.Stop=control+option+shift+X"),
          "the Stop shortcut uses the Windows key name")
    check(written.contains("ShowFloatingRecordingControls=true"), "the controller visibility is written")
    check(written.contains("FloatingControllerOpacityPercent=50"), "the controller opacity is written")
    check(written.contains("FloatingControllerSize=72"), "the controller size is written")

    fresh.setRecordingShortcut(
        ShortcutStroke(modifiers: controlOptionShift, keyCode: UInt32(kVK_ANSI_K)), for: .pauseResume)
    fresh.showFloatingRecordingControls = false
    fresh.floatingControllerOpacityPercent = 30
    fresh.floatingControllerSize = 150
    fresh.controllerLayouts.setPosition(CGPoint(x: 100, y: 40), for: "laptop")
    fresh.controllerLayouts.setPosition(CGPoint(x: 2900, y: 900), for: "desk")
    try store.save(fresh)

    let reloaded = store.load()
    checkEqual(
        reloaded.recordingShortcut(for: .pauseResume).keyCode, UInt32(kVK_ANSI_K),
        "an edited recording shortcut survives restart")
    check(!reloaded.showFloatingRecordingControls, "hiding the controller survives restart")
    checkEqual(reloaded.floatingControllerOpacityPercent, 30, "the chosen opacity survives restart")
    checkEqual(reloaded.floatingControllerSize, 150, "the chosen size survives restart")
    checkEqual(reloaded.controllerLayouts.position(for: "laptop"), CGPoint(x: 100, y: 40),
               "each layout keeps its own exact position")
    checkEqual(reloaded.controllerLayouts.position(for: "desk"), CGPoint(x: 2900, y: 900),
               "a second layout keeps an independent position")
    checkEqual(reloaded.controllerLayouts.keys, ["desk", "laptop"],
               "layouts reload in most-recently-used order")

    // Disabling the controller forgets neither opacity nor any layout position.
    reloaded.showFloatingRecordingControls = true
    try store.save(reloaded)
    let reenabled = store.load()
    checkEqual(reenabled.floatingControllerOpacityPercent, 30, "re-enabling keeps the chosen opacity")
    checkEqual(reenabled.controllerLayouts.count, 2, "re-enabling keeps every learned layout")

    // Bad values are repaired to safe defaults instead of being carried forward.
    let broken = try String(contentsOf: store.settingsURL, encoding: .utf8)
        .replacingOccurrences(of: "FloatingControllerOpacityPercent=30", with: "FloatingControllerOpacityPercent=5")
        .replacingOccurrences(of: "FloatingControllerSize=150", with: "FloatingControllerSize=999")
        .replacingOccurrences(of: "ShowFloatingRecordingControls=true", with: "ShowFloatingRecordingControls=maybe")
        .replacingOccurrences(of: "RecordingShortcut.Stop=control+option+shift+X", with: "RecordingShortcut.Stop=nonsense")
    try broken.write(to: store.settingsURL, atomically: true, encoding: .utf8)
    let repaired = store.load()
    checkEqual(repaired.floatingControllerOpacityPercent, 50, "an out-of-range opacity repairs to 50%")
    checkEqual(repaired.floatingControllerSize, 72, "an out-of-range size repairs to the default")
    check(repaired.showFloatingRecordingControls, "an unreadable visibility repairs to shown")
    checkEqual(repaired.recordingShortcut(for: .stop).keyCode, UInt32(kVK_ANSI_X),
               "an unreadable Stop shortcut repairs to its default")
    checkEqual(repaired.recordingShortcut(for: .pauseResume).keyCode, UInt32(kVK_ANSI_K),
               "repairing one control leaves the other untouched")
}

/// The upgrade path from the released schema 10: every existing value survives unchanged, unknown
/// lines are carried through, and only the new keys are added.
private func testRecordingControlMigration() throws {
    let directory = try makeTemporaryDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let store = SettingsStore(directory: directory, defaultDestinationPath: { "/unused" })
    let name = Data("Ingest".utf8).base64EncodedString()
    let path = Data("/Volumes/Somewhere/Ingest".utf8).base64EncodedString()
    let pictures = Data("Pictures".utf8).base64EncodedString()
    let picturesPath = Data("/Users/someone/Pictures".utf8).base64EncodedString()
    let label = Data("Game".utf8).base64EncodedString()
    let template = Data("{label}_{kind}_{counter}".utf8).base64EncodedString()
    let legacy = """
        # Huck’s Snip ’n’ Clip (macOS). Lines starting with # are comments.
        Version=10
        ActiveDestinationId=B
        RoutineNotificationsEnabled=false
        RecordingAudioMode=ComputerAndMicrophone
        RecordingFrameRate=30
        RecordingQuality=High
        RecordingResolution=720p
        RecordingCursor=true
        ComputerAudioGainPercent=150
        MicrophoneGainPercent=200
        MicrophoneDeviceId=mic-1
        MicrophoneDeviceName=Desk Mic
        CaptureFilenameLabel=\(label)
        CaptureFilenameTemplate=\(template)
        CaptureFilenameCounter=87
        Destination=A|\(name)|\(path)
        Destination=B|\(pictures)|\(picturesPath)
        Shortcut.SnipRegion=control+option+shift+S
        Shortcut.SnipWindow=control+option+shift+W
        Shortcut.SnipScreen=control+option+shift+F
        Shortcut.ClipRegion=control+option+shift+C
        Shortcut.ClipWindow=control+option+shift+V
        Shortcut.ClipScreen=control+option+J > 8
        OutputShortcut.A=control+option+1
        OutputShortcut.B=control+option+2
        SomeFutureKey=kept
        """
    try legacy.write(to: store.settingsURL, atomically: true, encoding: .utf8)

    let upgraded = store.load()
    let contents = try String(contentsOf: store.settingsURL, encoding: .utf8)
    let before = Set(legacy.components(separatedBy: "\n").filter { !$0.hasPrefix("Version=") })
    let after = Set(contents.components(separatedBy: "\n"))
    check(before.isSubset(of: after), "every schema 10 line survives the upgrade byte for byte")
    checkEqual(after.subtracting(before).filter { !$0.isEmpty }.sorted(), [
        "FloatingControllerOpacityPercent=50",
        "FloatingControllerSize=72",
        "RecordingShortcut.PauseResume=control+option+shift+P",
        "RecordingShortcut.Stop=control+option+shift+X",
        "ShowFloatingRecordingControls=true",
        "Version=11"
    ], "the upgrade only adds the recording-control keys and the new version")
    checkEqual(upgraded.captureFilename.nextCounter, 87, "the filename counter is untouched")
    checkEqual(upgraded.activeDestinationId, "B", "the active output is untouched")
    check(upgraded.preservedLines.contains("SomeFutureKey=kept"), "unknown keys are carried through")

    // A recording control stored on a key a capture shortcut now uses is moved, never the capture.
    let collided = contents.replacingOccurrences(
        of: "RecordingShortcut.Stop=control+option+shift+X",
        with: "RecordingShortcut.Stop=control+option+shift+S")
    try collided.write(to: store.settingsURL, atomically: true, encoding: .utf8)
    let repaired = store.load()
    checkEqual(repaired.recordingShortcut(for: .stop).keyCode, UInt32(kVK_ANSI_X),
               "a colliding Stop shortcut returns to its free default")
    checkEqual(repaired.shortcut(for: .snipRegion).keyCode, UInt32(kVK_ANSI_S),
               "the capture shortcut it collided with is untouched")
}

private func testRecordingControlConflicts() {
    let settings = AppSettings()
    settings.destinations = [
        CaptureDestination(id: "a", name: "Alpha", path: "/tmp/a"),
        CaptureDestination(id: "b", name: "Beta", path: "/tmp/b")
    ]
    _ = settings.ensureOutputShortcuts()

    let escape = ShortcutStroke(modifiers: controlOptionShift, keyCode: UInt32(kVK_Escape))
    check(settings.recordingControlConflict(escape, for: .stop) != nil, "Escape can never stop a recording")
    check(RecordingControlAction.isForbidden(escape), "Escape is forbidden with or without modifiers")
    check(settings.recordingControlConflict(
        ShortcutStroke(modifiers: 0, keyCode: UInt32(kVK_ANSI_P)), for: .pauseResume) != nil,
          "a bare recording key is refused")
    check(settings.recordingControlConflict(
        settings.shortcut(for: .clipScreen).firstStroke, for: .stop) != nil,
          "a capture shortcut's first stroke is refused")
    settings.setShortcut(
        ShortcutBinding(
            firstStroke: ShortcutStroke(modifiers: controlOption, keyCode: UInt32(kVK_ANSI_J)),
            secondStroke: ShortcutStroke(modifiers: controlOptionShift, keyCode: UInt32(kVK_ANSI_Q))),
        for: .clipScreen)
    check(settings.recordingControlConflict(
        ShortcutStroke(modifiers: controlOptionShift, keyCode: UInt32(kVK_ANSI_Q)), for: .stop) != nil,
          "a capture chord's second stroke is refused")
    check(settings.recordingControlConflict(
        settings.outputShortcut(for: "a")!, for: .stop) != nil, "an Output shortcut is refused")
    check(settings.recordingControlConflict(
        settings.recordingShortcut(for: .pauseResume), for: .stop) != nil,
          "the other recording control's key is refused")
    check(settings.recordingControlConflict(
        ShortcutStroke(modifiers: controlOptionShift, keyCode: UInt32(kVK_ANSI_K)), for: .stop) == nil,
          "a free modified key is accepted")
    checkEqual(settings.recordingControlConflicting(with: ShortcutBinding(
        firstStroke: settings.recordingShortcut(for: .stop))), .stop,
        "a capture rebind onto the Stop key is detected")

    // A new output never receives a recording key, and an existing output is never evicted.
    let fresh = AppSettings()
    fresh.setRecordingShortcut(ShortcutStroke(modifiers: controlOption, keyCode: UInt32(kVK_ANSI_2)), for: .pauseResume)
    fresh.destinations = [
        CaptureDestination(id: "a", name: "Alpha", path: "/tmp/a"),
        CaptureDestination(id: "b", name: "Beta", path: "/tmp/b")
    ]
    _ = fresh.ensureOutputShortcuts()
    checkEqual(fresh.outputShortcut(for: "b")?.keyCode, UInt32(kVK_ANSI_3),
               "a newly assigned output key skips the Pause/Resume key")

    let kept = AppSettings()
    kept.destinations = [CaptureDestination(id: "a", name: "Alpha", path: "/tmp/a")]
    let taken = ShortcutStroke(modifiers: controlOptionShift, keyCode: UInt32(kVK_ANSI_P))
    kept.setOutputShortcut(taken, for: "a")
    _ = kept.ensureOutputShortcuts()
    check(kept.ensureRecordingShortcuts(), "a recording key an output already owns is repaired")
    checkEqual(kept.outputShortcut(for: "a"), taken, "the existing output keeps its key")
    check(kept.recordingShortcut(for: .pauseResume) != taken, "Pause/Resume moves to a free key")
    checkEqual(kept.recordingControlConflict(kept.recordingShortcut(for: .pauseResume), for: .pauseResume),
               nil, "the repaired Pause/Resume key is conflict-free")
}

private func testDisplayLayoutIdentity() {
    let laptop = DisplayLayoutEntry(
        displayID: "LAPTOP", frame: CGRect(x: 0, y: 0, width: 1512, height: 982),
        scale: 2, rotation: 0, primary: true)
    let monitor = DisplayLayoutEntry(
        displayID: "MONITOR", frame: CGRect(x: 1512, y: 0, width: 2560, height: 1440),
        scale: 1, rotation: 0, primary: false)
    let docked = DisplayLayoutIdentity.compose([laptop, monitor])

    checkEqual(docked.count, 16, "a layout key is a short fixed-length identity")
    checkEqual(DisplayLayoutIdentity.compose([monitor, laptop]), docked,
               "display enumeration order does not change the layout")
    check(DisplayLayoutIdentity.compose([laptop]) != docked, "laptop-only and docked are different layouts")

    var movedLeft = monitor
    movedLeft.frame.origin.x = -2560
    check(DisplayLayoutIdentity.compose([laptop, movedLeft]) != docked,
          "rearranging the same displays is a different layout")
    var rescaled = monitor
    rescaled.scale = 2
    check(DisplayLayoutIdentity.compose([laptop, rescaled]) != docked, "a scale change is a different layout")
    var rotated = monitor
    rotated.rotation = 90
    check(DisplayLayoutIdentity.compose([laptop, rotated]) != docked, "a rotation is a different layout")
    var resized = monitor
    resized.frame.size = CGSize(width: 1920, height: 1080)
    check(DisplayLayoutIdentity.compose([laptop, resized]) != docked, "a resolution change is a different layout")
    var otherMonitor = monitor
    otherMonitor.displayID = "OTHER"
    check(DisplayLayoutIdentity.compose([laptop, otherMonitor]) != docked,
          "a different physical display is a different layout")
}

private func testControllerLayoutMemory() {
    var layouts = RecordingControllerLayouts()
    for index in 0..<13 {
        layouts.setPosition(CGPoint(x: index, y: index), for: "layout\(index)")
    }
    checkEqual(layouts.count, RecordingControllerLayouts.maxProfiles, "layout memory is bounded at twelve")
    checkEqual(layouts.position(for: "layout0"), nil, "the least recently used layout is dropped")
    checkEqual(layouts.keys.first, "layout12", "the newest layout is first")

    layouts.touch("layout1")
    checkEqual(layouts.keys.first, "layout1", "returning to a layout makes it most recent")
    checkEqual(layouts.position(for: "layout1"), CGPoint(x: 1, y: 1), "touching does not move a layout")

    layouts.setPosition(CGPoint(x: 500, y: 60), for: "layout5")
    checkEqual(layouts.position(for: "layout5"), CGPoint(x: 500, y: 60), "teaching one layout updates it")
    checkEqual(layouts.position(for: "layout6"), CGPoint(x: 6, y: 6), "teaching one layout leaves others alone")

    var restored = RecordingControllerLayouts()
    for index in 0..<20 { restored.append(CGPoint(x: index, y: 0), for: "k\(index)") }
    checkEqual(restored.count, 12, "a hand-edited long list is capped on load")
    restored.append(CGPoint(x: 99, y: 99), for: "k0")
    checkEqual(restored.position(for: "k0"), CGPoint(x: 0, y: 0), "a duplicate stored key keeps its first value")
}

private func testControllerPlacement() {
    let laptop = CGRect(x: 0, y: 0, width: 1512, height: 944)
    let monitor = CGRect(x: 1512, y: 0, width: 2560, height: 1415)
    let size = CGSize(width: 72, height: 88)

    let inside = CGPoint(x: 2000, y: 400)
    checkEqual(RecordingControllerPlacement.clamp(inside, size: size, visibleFrames: [laptop, monitor]), inside,
               "a reachable position is restored exactly")
    checkEqual(
        RecordingControllerPlacement.clamp(CGPoint(x: 1490, y: 100), size: size, visibleFrames: [laptop]),
        CGPoint(x: 1512 - 72, y: 100),
        "a controller hanging off an edge is pulled fully on screen")
    checkEqual(
        RecordingControllerPlacement.clamp(CGPoint(x: 3900, y: 1300), size: size, visibleFrames: [laptop]),
        CGPoint(x: 1512 - 72, y: 944 - 88),
        "a position on a disconnected display is re-homed to the nearest remaining one")
    checkEqual(
        RecordingControllerPlacement.clamp(CGPoint(x: -500, y: -500), size: size, visibleFrames: [laptop, monitor]),
        CGPoint(x: 0, y: 0),
        "a position off every display lands at the nearest corner")
    checkEqual(
        RecordingControllerPlacement.defaultOrigin(in: monitor, size: size),
        CGPoint(x: monitor.maxX - 72 - 24, y: 24),
        "a new layout starts low and to the right on the recording display")
}

private func testFloatingControllerGeometry() {
    for size in [44, 72, 100, 152, 220] {
        let leftPost = FloatingControllerMetrics.leftPost(size: size)
        let rightPost = FloatingControllerMetrics.rightPost(size: size)
        let left = FloatingControllerMetrics.leftButton(size: size)
        let right = FloatingControllerMetrics.rightButton(size: size)
        check(left.minX == leftPost.minX && left.maxX == leftPost.maxX,
              "the Pause button is exactly under the left post at size \(size)")
        check(right.minX == rightPost.minX && right.maxX == rightPost.maxX,
              "the Stop button is exactly under the right post at size \(size)")
        check(left.maxY <= leftPost.minY && right.maxY <= rightPost.minY,
              "both buttons sit below the H at size \(size)")
        check(left.maxX < right.minX, "the buttons do not touch at size \(size)")
        let window = FloatingControllerMetrics.windowSize(for: size)
        check(CGRect(origin: .zero, size: window).contains(right) && right.minY >= 0,
              "the buttons fit inside the window at size \(size)")
        let grip = FloatingControllerMetrics.grip(size: size)
        check(grip.allSatisfy { rightPost.insetBy(dx: -0.5, dy: -0.5).contains($0) },
              "the resize notch is cut from the right post's top corner at size \(size)")
        check(FloatingControllerMetrics.gripContains(
            CGPoint(x: rightPost.maxX - 1, y: rightPost.maxY - 1), size: size),
              "the notch catches the post's top-right corner at size \(size)")
        check(!FloatingControllerMetrics.gripContains(CGPoint(x: rightPost.midX, y: rightPost.midY), size: size),
              "the middle of the post is not the notch at size \(size)")
    }

    checkEqual(FloatingControllerMetrics.windowSize(for: 72), CGSize(width: 72, height: 88),
               "the default controller keeps the Windows 64x78 proportions")
    checkEqual(FloatingControllerMetrics.normalizeSize(10), 44, "size is clamped to the minimum")
    checkEqual(FloatingControllerMetrics.normalizeSize(1000), 220, "size is clamped to the maximum")
    checkEqual(FloatingControllerMetrics.normalizeOpacity(0), 10, "opacity never drops below 10%")
    checkEqual(FloatingControllerMetrics.normalizeOpacity(150), 100, "opacity never exceeds 100%")
    checkEqual(FloatingControllerMetrics.resizedSize(from: 72, dragDelta: CGSize(width: 20, height: 10)), 102,
               "dragging the notch right and up enlarges the controller")
    checkEqual(FloatingControllerMetrics.resizedSize(from: 72, dragDelta: CGSize(width: -300, height: 0)), 44,
               "shrinking stops at the minimum size")
}

/// The controller draws the H and nothing else: the gap between the posts and the corners beside
/// the H stay fully transparent, so there is no panel behind it.
private func testFloatingControllerHasNoPanel() {
    let size = 64
    guard let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
        samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
        bytesPerRow: 0, bitsPerPixel: 0),
          let context = NSGraphicsContext(bitmapImageRep: bitmap) else {
        check(false, "a bitmap context is available for the controller render check")
        return
    }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = context
    TrayIconFactory.drawControllerH(scale: 1, inputLevel: 0, strainLevel: 0, paused: false, finishing: false)
    NSGraphicsContext.restoreGraphicsState()

    // NSBitmapImageRep rows run top-down; design y is bottom-up here.
    func alpha(_ x: Int, _ y: Int) -> CGFloat { bitmap.colorAt(x: x, y: size - 1 - y)?.alphaComponent ?? -1 }
    func white(_ x: Int, _ y: Int) -> Bool {
        guard let color = bitmap.colorAt(x: x, y: size - 1 - y)?.usingColorSpace(.deviceRGB) else { return false }
        return color.redComponent > 0.95 && color.greenComponent > 0.95 && color.blueComponent > 0.95
            && color.alphaComponent > 0.95
    }
    check(white(17, 50), "the idle controller H is white")
    checkEqual(alpha(32, 50), 0, "the gap above the crossbar is not part of the controller")
    checkEqual(alpha(32, 12), 0, "the gap below the crossbar is not part of the controller")
    checkEqual(alpha(2, 2), 0, "the corner beside the H is not part of the controller")
    checkEqual(alpha(62, 62), 0, "the far corner beside the H is not part of the controller")
}

/// The controller window is rectangular, so only fully transparent pixels let a click through to
/// the app underneath. Its gaps must be transparent and unclaimed; its controls drawn and claimed.
private func testFloatingControllerClickThrough() {
    MainActor.assumeIsolated {
        for size in [44, 72, 220] {
            let s = CGFloat(size) / 64
            let height = FloatingControllerMetrics.windowSize(for: size).height
            func point(_ x: CGFloat, _ yDown: CGFloat) -> CGPoint { CGPoint(x: x * s, y: height - yDown * s) }
            let gaps = [point(32, 16), point(32, 68), point(1, 1), point(63, 1), point(1, 77), point(32, 48)]
            let drawn = [point(15, 30), point(47, 40), point(17, 68), point(46, 68), point(56, 8), point(32, 32)]
            checkEqual(FloatingRecordingController.probe(size: size, at: gaps + drawn).count, gaps.count + drawn.count,
                       "the controller renders off screen for probing at size \(size)")
            for (index, result) in FloatingRecordingController.probe(size: size, at: gaps).enumerated() {
                checkEqual(result.alpha, 0, "controller gap \(index) is fully transparent at size \(size)")
                check(!result.claimed, "controller gap \(index) is not claimed by the controller at size \(size)")
            }
            for (index, result) in FloatingRecordingController.probe(size: size, at: drawn).enumerated() {
                check(result.alpha > 0.9, "controller part \(index) is drawn opaque at size \(size)")
                check(result.claimed, "controller part \(index) is claimed by the controller at size \(size)")
            }
        }
    }
}

private func testCapturePasteboard() throws {
    let directory = try makeTemporaryDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let pasteboard = NSPasteboard(name: NSPasteboard.Name("HucksSnipNClipTests-\(UUID().uuidString)"))
    defer { pasteboard.releaseGlobally() }

    guard let image = makeImage(width: 40, height: 30) else {
        check(false, "a test image can be created")
        return
    }
    let saved = try CaptureSaveService(recoveryDirectory: directory.appendingPathComponent("Recovery"))
        .savePng(image, destinationDirectory: directory.path)
    check(CapturePasteboard.copySnip(atPath: saved.savedPath, to: pasteboard), "a saved Snip is copied")
    checkEqual(pasteboard.data(forType: .png), try Data(contentsOf: URL(fileURLWithPath: saved.savedPath)),
               "the pasted PNG is byte-for-byte the routed file")
    check(pasteboard.data(forType: .tiff) != nil, "a TIFF copy is offered for applications that need it")
    let pasted = NSImage(pasteboard: pasteboard)
    checkEqual(pasted?.representations.first?.pixelsWide, 40, "the pasted image has the Snip's pixel width")

    let clip = directory.appendingPathComponent("clip.mp4")
    try Data([0, 0, 0, 24]).write(to: clip)
    check(CapturePasteboard.copyClip(atPath: clip.path, to: pasteboard), "a finished Clip is copied")
    let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: nil) as? [URL]
    checkEqual(urls?.first?.standardizedFileURL, clip.standardizedFileURL,
               "Command-V receives the finished Clip file")
    check(pasteboard.data(forType: .png) == nil, "copying a Clip replaces the earlier Snip image")

    let missing = directory.appendingPathComponent("missing.png").path
    check(!CapturePasteboard.copySnip(atPath: missing, to: pasteboard),
          "a missing Snip reports a copy failure instead of throwing")
    check(!CapturePasteboard.copyClip(atPath: missing, to: pasteboard),
          "a missing Clip reports a copy failure instead of throwing")
    checkEqual((pasteboard.readObjects(forClasses: [NSURL.self], options: nil) as? [URL])?.first?.standardizedFileURL,
               clip.standardizedFileURL, "a failed copy leaves the previous clipboard alone")
}


// MARK: - Update checking (fixtures only; GitHub is never contacted)

private final class FixtureUpdateClient: UpdateHTTPClient, @unchecked Sendable {
    var responses: [URL: (Data, URL?, Int)] = [:]
    var failing: Set<URL> = []
    var delayNanoseconds: UInt64 = 0
    private let lock = NSLock()
    private var requestedURLs: [URL] = []
    var requested: [URL] { lock.lock(); defer { lock.unlock() }; return requestedURLs }

    private func record(_ url: URL) { lock.lock(); requestedURLs.append(url); lock.unlock() }

    private func respond(_ url: URL) async throws -> (Data, URL?, Int) {
        record(url)
        if delayNanoseconds > 0 { try await Task.sleep(nanoseconds: delayNanoseconds) }
        if failing.contains(url) { throw URLError(.notConnectedToInternet) }
        return responses[url] ?? (Data(), url, 404)
    }

    func data(from url: URL) async throws -> (Data, URL?, Int) { try await respond(url) }

    func download(from url: URL) async throws -> (URL, URL?, Int) {
        let (data, final, status) = try await respond(url)
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("fixture-\(UUID().uuidString)")
        try data.write(to: file)
        return (file, final, status)
    }
}

private final class RecordingUpdatePresenter: UpdatePresenter, @unchecked Sendable {
    var acceptDownload = true
    var acceptOpen = true
    var events: [String] = []
    var openedDMG: URL?
    var failure: UpdateError?

    func showUpToDate(installed: SemanticVersion, latest: SemanticVersion) async { events.append("upToDate \(installed) \(latest)") }
    func showNewerWithoutMacDownload(installed: SemanticVersion, latest: SemanticVersion, releasePage: URL?) async { events.append("noMacDownload \(latest)") }
    func confirmDownload(_ offer: UpdateOffer) async -> Bool { events.append("confirmDownload \(offer.available)"); return acceptDownload }
    func confirmOpen(_ offer: UpdateOffer, verifiedDMG: URL, sha256: String) async -> Bool {
        events.append("confirmOpen \(verifiedDMG.lastPathComponent) exists=\(FileManager.default.fileExists(atPath: verifiedDMG.path))")
        return acceptOpen
    }
    func open(verifiedDMG: URL) async { events.append("open"); openedDMG = verifiedDMG }
    func showFailure(_ error: UpdateError) async { events.append("failure"); failure = error }
}

private final class AsyncResultBox<T>: @unchecked Sendable { var result: Result<T, Error>? }

/// Runs async work from the synchronous test harness without involving the main actor.
private func waitFor<T>(_ operation: @escaping @Sendable () async throws -> T) -> Result<T, Error> {
    let semaphore = DispatchSemaphore(value: 0)
    let box = AsyncResultBox<T>()
    Task.detached {
        do { box.result = .success(try await operation()) } catch { box.result = .failure(error) }
        semaphore.signal()
    }
    semaphore.wait()
    return box.result!
}

private let fixtureRelease = UpdateService.latestReleaseURL
private func fixtureAsset(_ name: String, size: Int? = nil) -> GitHubReleaseAsset {
    GitHubReleaseAsset(
        name: name,
        browserDownloadURL: URL(string: "https://github.com/Huckletsplay/hucks-snip-n-clip/releases/download/v0.1.6/\(name)")!,
        size: size)
}

private func releaseJSON(tag: String, assets: [GitHubReleaseAsset]) -> Data {
    let list = assets.map {
        #"{"name":"\#($0.name)","browser_download_url":"\#($0.browserDownloadURL.absoluteString)""#
            + ($0.size.map { #","size":\#($0)"# } ?? "") + "}"
    }.joined(separator: ",")
    return Data(#"{"tag_name":"\#(tag)","html_url":"https://github.com/Huckletsplay/hucks-snip-n-clip/releases/tag/\#(tag)","assets":[\#(list)]}"#.utf8)
}

private func testSemanticVersions() {
    checkEqual(SemanticVersion("0.1.5")?.description, "0.1.5", "a plain version parses")
    checkEqual(SemanticVersion("v0.1.5")?.description, "0.1.5", "the documented v prefix is accepted")
    checkEqual(SemanticVersion(" V1.2.3 ")?.description, "1.2.3", "an upper-case prefix and whitespace are tolerated")
    check(SemanticVersion("0.1.5") == SemanticVersion("v0.1.5"), "the installed version equals the same tag")
    check(SemanticVersion("0.1.6")! > SemanticVersion("0.1.5")!, "a newer patch compares greater")
    check(SemanticVersion("0.1.10")! > SemanticVersion("0.1.9")!, "components compare numerically, not as text")
    check(SemanticVersion("1.0.0")! > SemanticVersion("0.9.9")!, "a newer major compares greater")
    check(SemanticVersion("0.1.4")! < SemanticVersion("0.1.5")!, "an older release compares lower")
    for bad in ["", "v", "latest", "0.1", "0.1.5.1", "0.1.x", "v0.1.5-beta", "1..2", "-1.0.0", "0.1.5 beta", "٣.1.1", "1.2.3456789"] {
        check(SemanticVersion(bad) == nil, "a malformed tag is refused: '\(bad)'")
    }
}

private func testMacReleaseAssetSelection() {
    let version = SemanticVersion("0.1.6")!
    checkEqual(MacReleaseAssets.dmgName(for: version), "HucksSnipNClip-0.1.6-macOS-universal-unsigned-beta.dmg",
               "the Mac DMG name is the unsigned-beta release name")
    checkEqual(MacReleaseAssets.checksumName(for: version), "HucksSnipNClip-0.1.6-macOS-universal-unsigned-beta.dmg.sha256.txt",
               "the checksum name is the portable file release.sh writes")

    let dmg = fixtureAsset("HucksSnipNClip-0.1.6-macOS-universal-unsigned-beta.dmg")
    let sum = fixtureAsset("HucksSnipNClip-0.1.6-macOS-universal-unsigned-beta.dmg.sha256.txt")
    let decoys = [
        fixtureAsset("HucksSnipNClip-0.1.6-windows-x64-setup.exe"),
        fixtureAsset("HucksSnipNClip-0.1.6-windows-x64-setup.exe.sha256"),
        fixtureAsset("HucksSnipNClip-0.1.6-macOS-universal-UNNOTARIZED.dmg"),
        fixtureAsset("HucksSnipNClip-0.1.6-macOS-universal-UNNOTARIZED.dmg.sha256.txt"),
        fixtureAsset("Source code (zip)"),
        fixtureAsset("HucksSnipNClip-0.1.5-macOS-universal-unsigned-beta.dmg")
    ]
    let picked = UpdateService.selectMacAssets(from: decoys + [dmg, sum], version: version)
    checkEqual(picked?.dmg, dmg, "only the exact Mac DMG is selected among decoys")
    checkEqual(picked?.checksum, sum, "only its exact checksum is selected")

    check(UpdateService.selectMacAssets(from: decoys + [dmg], version: version) == nil, "a DMG without its checksum is refused")
    check(UpdateService.selectMacAssets(from: decoys + [sum], version: version) == nil, "a checksum without its DMG is refused")
    check(UpdateService.selectMacAssets(from: decoys, version: version) == nil, "Windows, ad-hoc, source and older assets are never selected")
    let nearMisses = [
        fixtureAsset("hucksSnipNClip-0.1.6-macOS-universal-unsigned-beta.dmg"),
        fixtureAsset("HucksSnipNClip-0.1.6-macOS-universal-unsigned-beta.dmg.zip"),
        fixtureAsset("HucksSnipNClip-0.1.6-macOS-universal-unsigned-beta.dmg.sha256")
    ]
    check(UpdateService.selectMacAssets(from: nearMisses, version: version) == nil, "similarly named assets are refused")
    check(UpdateService.selectMacAssets(from: [dmg, dmg, sum], version: version) == nil, "an ambiguous duplicate DMG is refused")
    let foreign = GitHubReleaseAsset(name: dmg.name, browserDownloadURL: URL(string: "https://example.com/\(dmg.name)")!, size: nil)
    check(UpdateService.selectMacAssets(from: [foreign, sum], version: version) == nil, "a DMG not served by github.com is refused")
    let plain = GitHubReleaseAsset(name: dmg.name, browserDownloadURL: URL(string: "http://github.com/\(dmg.name)")!, size: nil)
    check(UpdateService.selectMacAssets(from: [plain, sum], version: version) == nil, "a DMG not served over HTTPS is refused")
}

private func testChecksumParsing() {
    let name = "HucksSnipNClip-0.1.6-macOS-universal-unsigned-beta.dmg"
    let digest = String(repeating: "ab", count: 32)
    checkEqual(try? UpdateService.parseChecksum("\(digest)  \(name)\n", expectedFileName: name), digest,
               "shasum output parses to its digest")
    checkEqual(try? UpdateService.parseChecksum("\(digest.uppercased()) *\(name)", expectedFileName: name), digest,
               "a binary-mode marker and upper-case hex are accepted and normalized")
    func failure(_ text: String) -> UpdateError? {
        do { _ = try UpdateService.parseChecksum(text, expectedFileName: name); return nil }
        catch { return error as? UpdateError }
    }
    checkEqual(failure("\(digest.dropLast())  \(name)"), .malformedChecksum, "a short digest is malformed")
    checkEqual(failure("\(String(repeating: "zz", count: 32))  \(name)"), .malformedChecksum, "a non-hex digest is malformed")
    checkEqual(failure(""), .malformedChecksum, "an empty checksum file is malformed")
    checkEqual(failure(digest), .malformedChecksum, "a digest with no file name is malformed")
    checkEqual(failure("\(digest)  \(name)\n\(digest)  other.dmg"), .malformedChecksum, "more than one value is malformed")
    checkEqual(failure("\(digest)  HucksSnipNClip-0.1.6-windows-x64-setup.exe"),
               .checksumNamesDifferentFile(expected: name, found: "HucksSnipNClip-0.1.6-windows-x64-setup.exe"),
               "a checksum naming another file is refused")
    check(UpdateError.malformedChecksum.isSecurityFailure && UpdateError.digestMismatch.isSecurityFailure,
          "checksum failures are presented as security stops")
    check(!UpdateError.httpStatus(500).isSecurityFailure, "an ordinary server failure is not a security stop")
}

private func testUpdateCheckOutcomes() {
    let dmg = fixtureAsset("HucksSnipNClip-0.1.6-macOS-universal-unsigned-beta.dmg")
    let sum = fixtureAsset("HucksSnipNClip-0.1.6-macOS-universal-unsigned-beta.dmg.sha256.txt")
    func outcome(_ installed: String, _ configure: (FixtureUpdateClient) -> Void) -> Result<UpdateCheckOutcome, Error> {
        let client = FixtureUpdateClient()
        configure(client)
        let service = UpdateService(client: client, workDirectory: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString))
        return waitFor { try await service.check(installedVersion: installed) }
    }
    func error(_ result: Result<UpdateCheckOutcome, Error>) -> UpdateError? {
        if case .failure(let e) = result { return e as? UpdateError }
        return nil
    }

    let current = outcome("0.1.5") { $0.responses[fixtureRelease] = (releaseJSON(tag: "v0.1.5", assets: []), nil, 200) }
    checkEqual(try? current.get(), .upToDate(installed: SemanticVersion("0.1.5")!, latest: SemanticVersion("0.1.5")!),
               "an installed version equal to the latest release is up to date")
    let older = outcome("0.1.5") { $0.responses[fixtureRelease] = (releaseJSON(tag: "v0.1.4", assets: [dmg, sum]), nil, 200) }
    checkEqual(try? older.get(), .upToDate(installed: SemanticVersion("0.1.5")!, latest: SemanticVersion("0.1.4")!),
               "an older remote release is never offered")
    let newer = outcome("0.1.5") { $0.responses[fixtureRelease] = (releaseJSON(tag: "v0.1.6", assets: [dmg, sum]), nil, 200) }
    if case .available(let offer)? = try? newer.get() {
        checkEqual(offer.available.description, "0.1.6", "a newer release is detected")
        checkEqual(offer.dmg.name, dmg.name, "the offer carries the exact Mac DMG")
    } else {
        check(false, "a newer release with a Mac DMG pair is offered")
    }
    let noMac = outcome("0.1.5") { $0.responses[fixtureRelease] = (releaseJSON(tag: "v0.1.6", assets: [fixtureAsset("HucksSnipNClip-0.1.6-windows-x64-setup.exe")]), nil, 200) }
    if case .newerWithoutMacDownload? = try? noMac.get() { check(true, "a newer Windows-only release is reported without an offer") }
    else { check(false, "a newer Windows-only release is reported without an offer") }

    checkEqual(error(outcome("0.1.5") { $0.responses[fixtureRelease] = (releaseJSON(tag: "latest", assets: [dmg, sum]), nil, 200) }),
               .malformedReleaseVersion("latest"), "a malformed release tag is refused with an explanation")
    checkEqual(error(outcome("0.1.5") { $0.responses[fixtureRelease] = (Data("not json".utf8), nil, 200) }),
               .unreadableRelease, "unreadable JSON is reported")
    checkEqual(error(outcome("0.1.5") { $0.responses[fixtureRelease] = (Data(), nil, 503) }),
               .httpStatus(503), "an HTTP failure is reported")
    if case .network? = error(outcome("0.1.5") { $0.failing.insert(fixtureRelease) }) { check(true, "a network failure is reported") }
    else { check(false, "a network failure is reported") }
    checkEqual(error(outcome("dev") { _ in }), .installedVersionUnknown("dev"), "an unreadable installed version stops the check")
    if case .insecureURL? = error(outcome("0.1.5") {
        $0.responses[fixtureRelease] = (releaseJSON(tag: "v0.1.6", assets: [dmg, sum]), URL(string: "http://api.github.com/x"), 200)
    }) { check(true, "a redirect off HTTPS is refused") } else { check(false, "a redirect off HTTPS is refused") }
}

private func updateFixture(dmgBytes: Data, checksumText: String?, checksumStatus: Int = 200, advertisedSize: Int? = nil)
    -> (UpdateService, UpdateOffer, FixtureUpdateClient) {
    let dmg = fixtureAsset("HucksSnipNClip-0.1.6-macOS-universal-unsigned-beta.dmg", size: advertisedSize ?? dmgBytes.count)
    let sum = fixtureAsset("HucksSnipNClip-0.1.6-macOS-universal-unsigned-beta.dmg.sha256.txt")
    let client = FixtureUpdateClient()
    client.responses[fixtureRelease] = (releaseJSON(tag: "v0.1.6", assets: [dmg, sum]), nil, 200)
    client.responses[dmg.browserDownloadURL] = (dmgBytes, URL(string: "https://objects.githubusercontent.com/dmg"), 200)
    if let checksumText { client.responses[sum.browserDownloadURL] = (Data(checksumText.utf8), nil, checksumStatus) }
    let service = UpdateService(client: client, workDirectory: FileManager.default.temporaryDirectory
        .appendingPathComponent("hsnc-update-test-\(UUID().uuidString)", isDirectory: true))
    let offer = UpdateOffer(installed: SemanticVersion("0.1.5")!, available: SemanticVersion("0.1.6")!,
                            dmg: dmg, checksum: sum, releasePage: nil)
    return (service, offer, client)
}

private func sha256Hex(_ data: Data) -> String {
    SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
}

private func testUpdateDownloadVerification() {
    let bytes = Data((0..<5000).map { UInt8($0 % 251) })
    let name = "HucksSnipNClip-0.1.6-macOS-universal-unsigned-beta.dmg"

    let (good, offer, _) = updateFixture(dmgBytes: bytes, checksumText: "\(sha256Hex(bytes))  \(name)\n")
    try? FileManager.default.createDirectory(at: good.workDirectory, withIntermediateDirectories: true)
    try? Data("stale".utf8).write(to: good.workDirectory.appendingPathComponent("old.dmg.partial"))
    if case .success(let verified) = waitFor({ try await good.downloadAndVerify(offer) }) {
        checkEqual(verified.dmg.lastPathComponent, name, "the verified DMG keeps its release name")
        checkEqual(try? Data(contentsOf: verified.dmg), bytes, "the verified DMG is the downloaded bytes")
        checkEqual(verified.sha256, sha256Hex(bytes), "the reported digest is the computed SHA-256")
        check(verified.dmg.path.hasPrefix(FileManager.default.temporaryDirectory.path),
              "the update lands in the private temporary folder")
        checkEqual((try? FileManager.default.contentsOfDirectory(atPath: good.workDirectory.path))?.sorted(), [name],
                   "obsolete and partial files are gone; only the verified DMG remains")
    } else {
        check(false, "a matching download verifies")
    }
    try? FileManager.default.removeItem(at: good.workDirectory)

    func failure(_ fixture: (UpdateService, UpdateOffer, FixtureUpdateClient)) -> UpdateError? {
        let result = waitFor { try await fixture.0.downloadAndVerify(fixture.1) }
        check(!FileManager.default.fileExists(atPath: fixture.0.workDirectory.path),
              "a failed update leaves no downloaded file behind")
        if case .failure(let error) = result { return error as? UpdateError }
        return nil
    }
    checkEqual(failure(updateFixture(dmgBytes: bytes, checksumText: "\(String(repeating: "0", count: 64))  \(name)")),
               .digestMismatch, "a digest mismatch stops the update")
    checkEqual(failure(updateFixture(dmgBytes: bytes, checksumText: nil)), .missingChecksum, "a missing checksum stops the update")
    checkEqual(failure(updateFixture(dmgBytes: bytes, checksumText: "oops", checksumStatus: 404)), .missingChecksum,
               "an unavailable checksum stops the update")
    checkEqual(failure(updateFixture(dmgBytes: bytes, checksumText: "not a checksum")), .malformedChecksum,
               "a malformed checksum stops the update")
    checkEqual(failure(updateFixture(dmgBytes: bytes, checksumText: "\(sha256Hex(bytes))  something-else.dmg")),
               .checksumNamesDifferentFile(expected: name, found: "something-else.dmg"),
               "a checksum for another file stops the update")
    checkEqual(failure(updateFixture(dmgBytes: bytes, checksumText: "\(sha256Hex(bytes))  \(name)", advertisedSize: bytes.count + 10)),
               .incompleteDownload(expected: bytes.count + 10, received: bytes.count), "a partial download stops the update")
}

private func testUpdateFlowStates() throws {
    let bytes = Data("dmg".utf8)
    let name = "HucksSnipNClip-0.1.6-macOS-universal-unsigned-beta.dmg"

    func run(_ fixture: (UpdateService, UpdateOffer, FixtureUpdateClient), installed: String = "0.1.5",
             configure: (RecordingUpdatePresenter) -> Void = { _ in }) -> (UpdateFlow, RecordingUpdatePresenter) {
        let presenter = RecordingUpdatePresenter()
        configure(presenter)
        let flow = UpdateFlow(service: fixture.0, presenter: presenter, installedVersion: installed)
        if let task = flow.start() { _ = waitFor { await task.value } }
        checkEqual(flow.phase, .idle, "the updater returns to idle after \(presenter.events.last ?? "no event")")
        return (flow, presenter)
    }

    let up = run(updateFixture(dmgBytes: bytes, checksumText: nil), installed: "0.1.6")
    checkEqual(up.1.events, ["upToDate 0.1.6 0.1.6"], "an up-to-date check shows only that result")

    let fixture = updateFixture(dmgBytes: bytes, checksumText: "\(sha256Hex(bytes))  \(name)")
    let declined = run(fixture) { $0.acceptDownload = false }
    checkEqual(declined.1.events, ["confirmDownload 0.1.6"], "declining the download stops before any download")
    check(!fixture.2.requested.contains(fixture.1.dmg.browserDownloadURL), "a declined update downloads nothing")

    let notOpened = run(updateFixture(dmgBytes: bytes, checksumText: "\(sha256Hex(bytes))  \(name)")) { $0.acceptOpen = false }
    checkEqual(notOpened.1.events, ["confirmDownload 0.1.6", "confirmOpen \(name) exists=true"],
               "the DMG is verified before the user is asked to open it")
    check(notOpened.1.openedDMG == nil, "declining to open leaves the DMG unopened")

    let opened = run(updateFixture(dmgBytes: bytes, checksumText: "\(sha256Hex(bytes))  \(name)"))
    checkEqual(opened.1.events.last, "open", "accepting opens the verified DMG")
    checkEqual(opened.1.openedDMG?.lastPathComponent, name, "only the verified DMG is opened")

    let tampered = run(updateFixture(dmgBytes: bytes, checksumText: "\(String(repeating: "f", count: 64))  \(name)"))
    checkEqual(tampered.1.failure, .digestMismatch, "a tampered download shows a security failure")
    check(tampered.1.openedDMG == nil && !tampered.1.events.contains(where: { $0.hasPrefix("confirmOpen") }),
          "a tampered download is never offered for opening")

    let offline = updateFixture(dmgBytes: bytes, checksumText: nil)
    offline.2.failing.insert(fixtureRelease)
    if case .network? = run(offline).1.failure { check(true, "a network failure is shown") } else { check(false, "a network failure is shown") }

    // A second request while one runs is ignored.
    let slow = updateFixture(dmgBytes: bytes, checksumText: nil)
    slow.2.delayNanoseconds = 300_000_000
    let presenter = RecordingUpdatePresenter()
    let flow = UpdateFlow(service: slow.0, presenter: presenter, installedVersion: "0.1.6")
    let first = flow.start()
    checkEqual(flow.phase, .checking, "a running check reports Checking")
    check(flow.start() == nil, "a duplicate check is suppressed while one runs")
    if let first { _ = waitFor { await first.value } }
    checkEqual(slow.2.requested.count, 1, "only one request reached GitHub")
    checkEqual(flow.phase, .idle, "the updater is idle again after the suppressed duplicate")

    // The Settings row.
    checkEqual(UpdateMenuState.item(phase: .idle, captureLocked: false).title, "Check for Updates…", "idle row title")
    check(UpdateMenuState.item(phase: .idle, captureLocked: false).enabled, "idle row is enabled")
    checkEqual(UpdateMenuState.item(phase: .checking, captureLocked: false).title, "Checking for Updates…", "running row title")
    check(!UpdateMenuState.item(phase: .checking, captureLocked: false).enabled, "running row is disabled")
    check(!UpdateMenuState.item(phase: .idle, captureLocked: true).enabled, "the row is disabled during a capture")

    // A full accepted update leaves every setting byte for byte as it was.
    let directory = try makeTemporaryDirectory()
    defer { try? FileManager.default.removeItem(at: directory) }
    let store = SettingsStore(directory: directory, defaultDestinationPath: { directory.path })
    let settings = store.load()
    settings.captureFilename.nextCounter = 41
    settings.floatingControllerOpacityPercent = 30
    settings.controllerLayouts.setPosition(CGPoint(x: 10, y: 20), for: "desk")
    try store.save(settings)
    let before = try Data(contentsOf: store.settingsURL)
    _ = run(updateFixture(dmgBytes: bytes, checksumText: "\(sha256Hex(bytes))  \(name)"))
    checkEqual(try Data(contentsOf: store.settingsURL), before, "an update leaves the settings file byte-identical")
}


// MARK: - Region selection: Snip commits on release, Clip is reviewed first

private func testRegionSelectionSession() {
    let bounds = CGRect(x: 0, y: 0, width: 1512, height: 982)

    var snip = RegionSelectionSession(purpose: .snip)
    snip.mouseDown(at: CGPoint(x: 100, y: 100), screen: 0)
    snip.mouseDragged(to: CGPoint(x: 300, y: 250))
    checkEqual(snip.mouseUp(at: CGPoint(x: 300, y: 250), bounds: bounds),
               .commit(CGRect(x: 100, y: 100, width: 200, height: 150), screen: 0),
               "a Region Snip commits on mouse release")
    check(!snip.showsReviewHint(onScreen: 0), "a Region Snip never shows the review hint")
    var click = RegionSelectionSession(purpose: .snip)
    click.mouseDown(at: CGPoint(x: 5, y: 5), screen: 0)
    checkEqual(click.mouseUp(at: CGPoint(x: 5, y: 5), bounds: bounds), .cancel, "a Snip click without a drag cancels, as before")
    var snipKeys = RegionSelectionSession(purpose: .snip)
    checkEqual(snipKeys.key(RegionSelectionSession.returnKey), .none, "Return does nothing to a Snip")

    var clip = RegionSelectionSession(purpose: .clip)
    clip.mouseDown(at: CGPoint(x: 100, y: 100), screen: 0)
    clip.mouseDragged(to: CGPoint(x: 400, y: 300))
    checkEqual(clip.mouseUp(at: CGPoint(x: 400, y: 300), bounds: bounds), .none,
               "a Region Clip stays pending after mouse release")
    checkEqual(clip.pending, .init(rect: CGRect(x: 100, y: 100, width: 300, height: 200), screen: 0),
               "the released Clip region is held for review")
    checkEqual(clip.visibleSelection(onScreen: 0, bounds: bounds), CGRect(x: 100, y: 100, width: 300, height: 200),
               "the pending region stays visible")
    check(clip.showsReviewHint(onScreen: 0), "the pending Clip region shows how to accept, redraw or cancel")
    check(!clip.showsReviewHint(onScreen: 1), "the hint appears only on the display holding the region")

    var stray = clip
    stray.mouseDown(at: CGPoint(x: 900, y: 900), screen: 0)
    checkEqual(stray.mouseUp(at: CGPoint(x: 900, y: 900), bounds: bounds), .none, "a stray click does not end a Clip selection")
    checkEqual(stray.pending, clip.pending, "a stray click keeps the reviewed region")

    var redraw = clip
    redraw.mouseDown(at: CGPoint(x: 600, y: 500), screen: 0)
    redraw.mouseDragged(to: CGPoint(x: 700, y: 650))
    checkEqual(redraw.key(RegionSelectionSession.returnKey), .none, "Return mid-drag does not commit a half-drawn region")
    check(!redraw.showsReviewHint(onScreen: 0), "the hint hides while redrawing")
    checkEqual(redraw.visibleSelection(onScreen: 0, bounds: bounds), CGRect(x: 600, y: 500, width: 100, height: 150),
               "the new drag is what is shown while redrawing")
    _ = redraw.mouseUp(at: CGPoint(x: 700, y: 650), bounds: bounds)
    checkEqual(redraw.key(RegionSelectionSession.returnKey), .commit(CGRect(x: 600, y: 500, width: 100, height: 150), screen: 0),
               "Return accepts the redrawn region, replacing the first")

    var otherDisplay = clip
    otherDisplay.mouseDown(at: CGPoint(x: 10, y: 10), screen: 1)
    otherDisplay.mouseDragged(to: CGPoint(x: 210, y: 110))
    _ = otherDisplay.mouseUp(at: CGPoint(x: 210, y: 110), bounds: bounds)
    checkEqual(otherDisplay.pending?.screen, 1, "redrawing on another display moves the region there")
    checkEqual(otherDisplay.visibleSelection(onScreen: 0, bounds: bounds), nil, "the old display no longer shows a region")
    checkEqual(otherDisplay.key(RegionSelectionSession.keypadEnterKey), .commit(CGRect(x: 10, y: 10, width: 200, height: 100), screen: 1),
               "keypad Enter accepts too, on the display the region was drawn on")

    var cancelled = clip
    checkEqual(cancelled.key(RegionSelectionSession.escapeKey), .cancel, "Escape cancels a pending Clip region")
    var empty = RegionSelectionSession(purpose: .clip)
    checkEqual(empty.key(RegionSelectionSession.returnKey), .none, "Return with nothing selected does nothing")
    checkEqual(cancelled.key(0), .none, "other keys are ignored")

    // Escape reaches the selection surface only. It can never be a capture, Output or recording
    // key, so nothing registered while a recording runs responds to it.
    check(ShortcutBinding.keyName(for: UInt32(kVK_Escape)) == nil, "no stored shortcut can be Escape")
    check(AppSettings().recordingControlConflict(
        ShortcutStroke(modifiers: 0, keyCode: UInt32(kVK_Escape)), for: .stop) != nil,
          "Escape is refused as a recording control")
    MainActor.assumeIsolated {
        check(!RegionSelectionOverlay.isPresented, "no selection surface exists outside a selection")
    }
}

private func testResetConfirmationDefaultsToCancel() {
    MainActor.assumeIsolated {
        let safe = CopyableAlert.makeConfirmAlert(title: "Reset Shortcuts", confirmButtonTitle: "Reset", defaultToCancel: true)
        checkEqual(safe.buttons.first?.title, "Reset", "the reset button is present")
        checkEqual(safe.buttons.first?.keyEquivalent, "", "Return cannot trigger Reset")
        checkEqual(safe.buttons.dropFirst().first?.keyEquivalent, "\r", "Return means Cancel on the reset confirmation")
        let ordinary = CopyableAlert.makeConfirmAlert(title: "Remove Output", confirmButtonTitle: "Remove", defaultToCancel: false)
        checkEqual(ordinary.buttons.first?.keyEquivalent, "\r", "other confirmations keep their standard default")
    }
}

@main
struct CoreTests {
    static func main() {
        do {
            testShortcutRoundTrip()
            testOutputShortcuts()
            try testSettingsRoundTrip()
            testRecordingFrameRateAndQuality()
            try testCaptureFilenameTemplates()
            try testMissingDestinationFallsBackToRecovery()
            testMeterSmoothing()
            testScreenFrameAdmission()
            testScreenClipDimensions()
            testRegionRecordingShadeGeometry()
            testRegionSelectionGeometry()
            testTrayIconStates()
            testRestingCutFilmRaster()
            testMonochromeTrayIconStates()
            testMeterWarningColors()
            testPauseTimeline()
            try testSeparateAudioTrackContainer()
            testAudioGainMath()
            testAudioGainSampleBuffer()
            testFrameDelivery()
            testRecordingResolution()
            try testRecordingResolutionMigration()
            testRecordingCursorConfiguration()
            testShortcutRebinding()
            testLaunchAtLoginPresentation()
            testMicrophoneDeviceSelection()
            testPersistentMenuChoiceView()
            try testRecordingControlSettings()
            try testRecordingControlMigration()
            testRecordingControlConflicts()
            testDisplayLayoutIdentity()
            testControllerLayoutMemory()
            testControllerPlacement()
            testFloatingControllerGeometry()
            testFloatingControllerHasNoPanel()
            try testCapturePasteboard()
            testFloatingControllerClickThrough()
            testSemanticVersions()
            testMacReleaseAssetSelection()
            testChecksumParsing()
            testUpdateCheckOutcomes()
            testUpdateDownloadVerification()
            try testUpdateFlowStates()
            testRegionSelectionSession()
            testResetConfirmationDefaultsToCancel()
        } catch {
            failures.append("a test threw: \(error)")
        }

        if failures.isEmpty {
            print("All \(checks) checks passed.")
            exit(0)
        }

        for failure in failures {
            print("FAILED: \(failure)")
        }

        print("\(failures.count) of \(checks) checks failed.")
        exit(1)
    }
}
