import Foundation

/// A named folder a capture can be routed to. Mirrors the Windows `CaptureDestination`.
struct CaptureDestination: Equatable {
    var id: String
    var name: String
    var path: String
}

/// Uses the same persisted names as Windows. ScreenCaptureKit interprets `computer` according to
/// the selected visual target: system audio for Screen/Region and selected-app audio for Window.
enum RecordingAudioMode: String, Equatable {
    case off = "Off"
    case computer = "Computer"
    case microphone = "Microphone"
    case computerAndMicrophone = "ComputerAndMicrophone"

    var recordsSystemAudio: Bool { self == .computer || self == .computerAndMicrophone }
    var recordsMicrophone: Bool { self == .microphone || self == .computerAndMicrophone }
    var recordsAudio: Bool { self != .off }

    var summary: String {
        switch self {
        case .off: return "No Audio"
        case .computer: return "System / App Audio"
        case .microphone: return "Microphone"
        case .computerAndMicrophone: return "System / App + Microphone"
        }
    }
}

/// Uses the same persisted names and bitrate behavior as Windows. `legacy` is presented as
/// Original in the menu; the stored value remains `Legacy` for cross-platform compatibility.
enum RecordingQuality: String, Equatable, CaseIterable {
    case legacy = "Legacy"
    case balanced = "Balanced"
    case high = "High"

    var displayName: String {
        switch self {
        case .legacy: return "Original"
        case .balanced: return "Balanced"
        case .high: return "High"
        }
    }

    static func decode(_ value: String) -> RecordingQuality? {
        return allCases.first { $0.rawValue.caseInsensitiveCompare(value) == .orderedSame }
    }
}

/// A ceiling on the recorded picture height, and nothing more opinionated than that.
///
/// The shape of whatever is being captured is always preserved - a display, a window, or a dragged
/// region, on any Mac, whatever its proportions. No aspect ratio is imposed, and a source already
/// shorter than the ceiling is never enlarged, so a small region still records at its own size.
///
/// This exists because frame size, not bitrate, is what decides whether a recording holds its frame
/// rate. Measured on an M3 Pro, the hardware H.264 encoder sustains about 75 FPS at 3024x1964 and
/// about 200 FPS at 1080p, while changing the bitrate from 8 to 69 Mbps moves that number not at
/// all. ScreenCaptureKit does the scaling on the GPU while it is already compositing the frame, so
/// a smaller recording costs nothing to produce and leaves the media engine far more headroom.
enum RecordingResolution: String, Equatable, CaseIterable {
    case native = "Native"
    case p1440 = "1440p"
    case p1080 = "1080p"
    case p720 = "720p"

    var displayName: String {
        switch self {
        case .native: return "Native — Full Source Size"
        case .p1440: return "1440p — Sharp"
        case .p1080: return "1080p — Balanced"
        case .p720: return "720p — Smallest"
        }
    }

    var maximumHeight: Int? {
        switch self {
        case .native: return nil
        case .p1440: return 1440
        case .p1080: return 1080
        case .p720: return 720
        }
    }

    static func decode(_ value: String) -> RecordingResolution? {
        return allCases.first { $0.rawValue.caseInsensitiveCompare(value) == .orderedSame }
    }

    /// Scales the captured picture down to the ceiling, keeping the source's proportions and never
    /// enlarging. Both edges stay even because H.264 encodes in 2x2 blocks.
    static func outputSize(
        sourceWidth: Int,
        sourceHeight: Int,
        resolution: RecordingResolution
    ) -> (width: Int, height: Int) {
        guard sourceWidth > 0, sourceHeight > 0 else { return (sourceWidth, sourceHeight) }
        guard let ceiling = resolution.maximumHeight, sourceHeight > ceiling else {
            return (sourceWidth, sourceHeight)
        }
        let scaledWidth = Double(sourceWidth) * Double(ceiling) / Double(sourceHeight)
        return (max(2, Int(scaledWidth.rounded()) & ~1), max(2, ceiling & ~1))
    }
}

enum RecordingFrameRateSettings {
    static let choices = [15, 30, 60, 0]

    static func isSupported(_ frameRate: Int) -> Bool {
        return choices.contains(frameRate)
    }

    static func normalize(_ frameRate: Int) -> Int {
        return isSupported(frameRate) ? frameRate : 60
    }

    static func resolve(_ configured: Int, displayRefreshRate: Int) -> Int {
        let normalized = normalize(configured)
        guard normalized == 0 else { return normalized }
        return min(240, max(1, displayRefreshRate))
    }

    static func summary(_ configured: Int) -> String {
        return normalize(configured) == 0 ? "Match Display" : "\(normalize(configured)) FPS"
    }
}

/// Keep both command families in the same Region, Window, Screen order used by Windows.
enum CaptureAction: String, CaseIterable {
    case snipRegion = "SnipRegion"
    case snipWindow = "SnipWindow"
    case snipScreen = "SnipScreen"
    case clipRegion = "ClipRegion"
    case clipWindow = "ClipWindow"
    case clipScreen = "ClipScreen"

    var displayName: String {
        switch self {
        case .snipRegion: return "Snip Region"
        case .snipWindow: return "Snip Window"
        case .snipScreen: return "Snip Screen"
        case .clipRegion: return "Clip Region"
        case .clipWindow: return "Clip Window"
        case .clipScreen: return "Clip Screen"
        }
    }

    var isClip: Bool {
        switch self {
        case .clipRegion, .clipWindow, .clipScreen: return true
        case .snipRegion, .snipWindow, .snipScreen: return false
        }
    }
}

final class AppSettings {
    var destinations: [CaptureDestination] = []
    var activeDestinationId: String?
    var routineNotificationsEnabled = true
    var recordingAudioMode: RecordingAudioMode = .computer
    var recordingFrameRate = 60
    var recordingQuality: RecordingQuality = .balanced
    // New installs record at 1080p so a clip holds its frame rate out of the box; an existing
    // settings file without the key migrates to .native instead, which is what it was already doing.
    var recordingResolution: RecordingResolution = .p1080
    // Cursor capture is opt-in. Existing installs recorded without it, and migration must not
    // silently place a pointer into clips that previously excluded one.
    var recordingShowsCursor = false
    var computerAudioGainPercent = 100
    var microphoneGainPercent = 100
    // Nil means follow the current macOS default. The cached name is only for explaining a saved
    // device that is temporarily disconnected; ScreenCaptureKit receives the stable unique ID.
    var microphoneDeviceID: String?
    var microphoneDeviceName: String?
    var captureFilename = CaptureFilenameConfiguration()

    // The recording-only floating H. Shown by default at half opacity; hiding it keeps its size,
    // opacity and every learned layout position for when it is turned back on.
    var showFloatingRecordingControls = true
    var floatingControllerOpacityPercent = FloatingControllerMetrics.defaultOpacityPercent
    var floatingControllerSize = FloatingControllerMetrics.defaultSize
    var controllerLayouts = RecordingControllerLayouts()

    /// Settings keys this build does not understand - recording controls not yet ported, for
    /// example - are carried through untouched so saving never discards something a later version
    /// put there.
    var preservedLines: [String] = []

    private var shortcuts: [CaptureAction: ShortcutBinding] = [:]
    private var outputShortcuts: [String: ShortcutStroke] = [:]
    private var recordingShortcuts: [RecordingControlAction: ShortcutStroke] = [:]

    init() {
        for action in CaptureAction.allCases {
            shortcuts[action] = ShortcutBinding.defaultBinding(for: action)
        }
        for control in RecordingControlAction.allCases {
            recordingShortcuts[control] = control.defaultStroke
        }
    }

    func recordingShortcut(for control: RecordingControlAction) -> ShortcutStroke {
        return recordingShortcuts[control] ?? control.defaultStroke
    }

    func setRecordingShortcut(_ stroke: ShortcutStroke, for control: RecordingControlAction) {
        recordingShortcuts[control] = stroke
    }

    /// Why a proposed recording-control key cannot be used, or nil when it is free.
    ///
    /// A recording control is live only during a take, but Carbon reserves a combination only once,
    /// so it is still checked against everything else the app reserves: both strokes of every
    /// capture shortcut, every Output shortcut, and the other recording control. An overlap would
    /// mean the control silently failed to register at exactly the moment it is needed.
    func recordingControlConflict(
        _ stroke: ShortcutStroke,
        for control: RecordingControlAction
    ) -> String? {
        if RecordingControlAction.isForbidden(stroke) {
            return "Escape never stops a recording. Choose another key."
        }
        if stroke.modifiers == 0 {
            return "Recording shortcuts need Control, Option, Shift, or Command."
        }
        for action in CaptureAction.allCases {
            let binding = shortcut(for: action)
            if binding.firstStroke == stroke || binding.secondStroke == stroke {
                return "\(stroke.displayString) is part of \(action.displayName) (\(binding.displayString))."
            }
        }
        for other in RecordingControlAction.allCases where other != control {
            if recordingShortcut(for: other) == stroke {
                return "\(stroke.displayString) is already \(other.displayName)."
            }
        }
        for destination in destinations where outputShortcuts[destination.id] == stroke {
            return "\(stroke.displayString) selects \(destination.name)."
        }
        return nil
    }

    /// The recording control, if any, that already uses one of a proposed binding's strokes.
    func recordingControlConflicting(with binding: ShortcutBinding) -> RecordingControlAction? {
        return RecordingControlAction.allCases.first { control in
            let stroke = recordingShortcut(for: control)
            return binding.firstStroke == stroke || binding.secondStroke == stroke
        }
    }

    /// Repairs a recording control that something else has collided with, so a take always has
    /// working Pause/Resume and Stop keys. Tries the documented default, then the first free
    /// Control-Option-Shift letter. Returns true when a binding changed.
    @discardableResult
    func ensureRecordingShortcuts() -> Bool {
        var changed = false
        for control in RecordingControlAction.allCases {
            guard recordingControlConflict(recordingShortcut(for: control), for: control) != nil else {
                continue
            }
            var fallback = control.defaultStroke
            if recordingControlConflict(fallback, for: control) != nil {
                let candidates = ShortcutBinding.keyCodes.prefix(26).map {
                    ShortcutStroke(modifiers: ShortcutBinding.captureModifiers, keyCode: $0.0)
                }
                if let free = candidates.first(where: { recordingControlConflict($0, for: control) == nil }) {
                    fallback = free
                }
            }
            // With every candidate taken there is nothing better to offer. Leave the binding alone
            // rather than rewriting the file on every load; registration failure is reported when
            // the recording starts.
            guard fallback != recordingShortcut(for: control) else { continue }
            recordingShortcuts[control] = fallback
            changed = true
        }
        return changed
    }

    func shortcut(for action: CaptureAction) -> ShortcutBinding {
        return shortcuts[action] ?? ShortcutBinding.defaultBinding(for: action)
    }

    func setShortcut(_ binding: ShortcutBinding, for action: CaptureAction) {
        shortcuts[action] = binding
    }

    func outputShortcut(for destinationID: String) -> ShortcutStroke? {
        return outputShortcuts[destinationID]
    }

    func setOutputShortcut(_ shortcut: ShortcutStroke?, for destinationID: String) {
        outputShortcuts[destinationID] = shortcut
    }

    func resetOutputShortcutsToDefaults() {
        outputShortcuts.removeAll()
        _ = ensureOutputShortcuts()
    }

    /// Gives every named output a stable, modified, nonconflicting global shortcut when possible.
    /// Existing valid assignments win; a bare key, duplicate, capture-action collision, or new
    /// output receives the first free Control-Option number/letter.
    @discardableResult
    func ensureOutputShortcuts() -> Bool {
        let captureStrokes = Set(shortcuts.values.flatMap { binding -> [ShortcutStroke] in
            var result = [binding.firstStroke]
            if let second = binding.secondStroke { result.append(second) }
            return result
        })
        let recordingStrokes = Set(RecordingControlAction.allCases.map { recordingShortcut(for: $0) })
        var used: Set<ShortcutStroke> = []
        var changed = false

        for destination in destinations {
            if let existing = outputShortcuts[destination.id],
               existing.modifiers != 0,
               !used.contains(existing),
               !captureStrokes.contains(existing) {
                used.insert(existing)
                continue
            }

            // A newly assigned key also avoids the recording controls. An existing assignment is
            // never evicted for them; ensureRecordingShortcuts moves the recording control instead.
            let replacement = ShortcutBinding.defaultOutputStrokes.first { candidate in
                !used.contains(candidate) && !captureStrokes.contains(candidate)
                    && !recordingStrokes.contains(candidate)
            }
            if outputShortcuts[destination.id] != replacement {
                outputShortcuts[destination.id] = replacement
                changed = true
            }
            if let replacement { used.insert(replacement) }
        }

        return changed
    }

    func outputShortcutConflicts(with stroke: ShortcutStroke, excluding destinationID: String) -> CaptureDestination? {
        return destinations.first { destination in
            destination.id != destinationID && outputShortcuts[destination.id] == stroke
        }
    }

    func captureActionConflicting(with stroke: ShortcutStroke) -> CaptureAction? {
        return CaptureAction.allCases.first { action in
            let binding = shortcut(for: action)
            return binding.firstStroke == stroke || binding.secondStroke == stroke
        }
    }

    var activeDestination: CaptureDestination? {
        if let id = activeDestinationId,
           let match = destinations.first(where: { $0.id == id }) {
            return match
        }

        return destinations.first
    }

    func destination(withPath path: String) -> CaptureDestination? {
        let target = (path as NSString).standardizingPath
        return destinations.first {
            ($0.path as NSString).standardizingPath.compare(target, options: .caseInsensitive) == .orderedSame
        }
    }
}
