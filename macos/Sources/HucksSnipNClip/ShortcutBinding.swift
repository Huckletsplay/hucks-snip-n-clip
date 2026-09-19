import Carbon.HIToolbox
import Foundation

/// One physical key press in a shortcut. A first stroke must include a modifier so it cannot take
/// over ordinary typing while the app is idle. A second stroke may be bare because it is registered
/// globally for only the brief period after its first stroke.
struct ShortcutStroke: Equatable, Hashable {
    var modifiers: UInt32
    var keyCode: UInt32

    var displayString: String {
        var text = ""
        if modifiers & UInt32(controlKey) != 0 { text += "\u{2303}" }
        if modifiers & UInt32(optionKey) != 0 { text += "\u{2325}" }
        if modifiers & UInt32(shiftKey) != 0 { text += "\u{21E7}" }
        if modifiers & UInt32(cmdKey) != 0 { text += "\u{2318}" }
        return text + (ShortcutBinding.keyName(for: keyCode) ?? "?")
    }

    var encoded: String {
        var parts: [String] = []
        if modifiers & UInt32(controlKey) != 0 { parts.append("control") }
        if modifiers & UInt32(optionKey) != 0 { parts.append("option") }
        if modifiers & UInt32(shiftKey) != 0 { parts.append("shift") }
        if modifiers & UInt32(cmdKey) != 0 { parts.append("command") }
        parts.append(ShortcutBinding.keyName(for: keyCode) ?? "")
        return parts.joined(separator: "+")
    }
}

/// A global shortcut in the Carbon values `RegisterEventHotKey` wants. Most bindings contain one
/// stroke. A PC-style chord contains a modified first stroke and one temporary second stroke.
struct ShortcutBinding: Equatable, Hashable {
    var firstStroke: ShortcutStroke
    var secondStroke: ShortcutStroke?

    init(
        modifiers: UInt32,
        keyCode: UInt32,
        secondModifiers: UInt32? = nil,
        secondKeyCode: UInt32? = nil
    ) {
        firstStroke = ShortcutStroke(modifiers: modifiers, keyCode: keyCode)
        if let secondModifiers, let secondKeyCode {
            secondStroke = ShortcutStroke(modifiers: secondModifiers, keyCode: secondKeyCode)
        } else {
            secondStroke = nil
        }
    }

    init(firstStroke: ShortcutStroke, secondStroke: ShortcutStroke? = nil) {
        self.firstStroke = firstStroke
        self.secondStroke = secondStroke
    }

    var modifiers: UInt32 { firstStroke.modifiers }
    var keyCode: UInt32 { firstStroke.keyCode }
    var hasSecondStroke: Bool { secondStroke != nil }

    /// Windows uses Ctrl+Alt+Shift for every capture action. Control+Option+Shift is the direct
    /// Mac equivalent and, unlike anything with Command, does not collide with the built-in
    /// macOS screenshot shortcuts.
    static let captureModifiers = UInt32(controlKey | optionKey | shiftKey)
    static let outputModifiers = UInt32(controlKey | optionKey)

    /// Stable automatic choices for global output-selection shortcuts. They require modifiers
    /// because they remain available while the app is idle, before a Snip or Clip begins.
    static var defaultOutputStrokes: [ShortcutStroke] {
        let names = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"]
            + Array("ABCDEFGHIJKLMNOPQRSTUVWXYZ").map(String.init)
        return names.compactMap { name in
            keyCode(for: name).map { ShortcutStroke(modifiers: outputModifiers, keyCode: $0) }
        }
    }

    static func defaultBinding(for action: CaptureAction) -> ShortcutBinding {
        switch action {
        case .snipRegion:
            return ShortcutBinding(modifiers: captureModifiers, keyCode: UInt32(kVK_ANSI_S))
        case .snipWindow:
            return ShortcutBinding(modifiers: captureModifiers, keyCode: UInt32(kVK_ANSI_W))
        case .snipScreen:
            return ShortcutBinding(modifiers: captureModifiers, keyCode: UInt32(kVK_ANSI_F))
        case .clipRegion:
            return ShortcutBinding(modifiers: captureModifiers, keyCode: UInt32(kVK_ANSI_C))
        case .clipWindow:
            return ShortcutBinding(modifiers: captureModifiers, keyCode: UInt32(kVK_ANSI_V))
        case .clipScreen:
            return ShortcutBinding(modifiers: captureModifiers, keyCode: UInt32(kVK_ANSI_R))
        }
    }

    /// A newly introduced action must never take an explicit shortcut already present in an older
    /// settings file. Prefer the documented default, then the same key with Command added, then an
    /// unused Command-modified letter as a last-resort migration binding.
    static func migrationDefault(
        for action: CaptureAction,
        avoiding occupied: Set<ShortcutBinding>
    ) -> ShortcutBinding {
        let standard = defaultBinding(for: action)
        if !occupied.contains(where: { standard.conflictsInternally(with: $0) }) { return standard }

        let commandVariant = ShortcutBinding(
            modifiers: standard.modifiers | UInt32(cmdKey),
            keyCode: standard.keyCode)
        if !occupied.contains(where: { commandVariant.conflictsInternally(with: $0) }) {
            return commandVariant
        }

        for (keyCode, _) in letterKeyCodes {
            let candidate = ShortcutBinding(
                modifiers: captureModifiers | UInt32(cmdKey),
                keyCode: keyCode)
            if !occupied.contains(where: { candidate.conflictsInternally(with: $0) }) {
                return candidate
            }
        }

        return standard
    }

    /// What the user sees in the menu, in normal Mac symbols.
    var displayString: String {
        guard let secondStroke else { return firstStroke.displayString }
        return firstStroke.displayString + ", " + secondStroke.displayString
    }

    /// The settings-file form, e.g. `control+option+C > R`. A spaced arrow separates strokes so
    /// punctuation keys remain unambiguous. Existing one-stroke values retain their exact format.
    var encoded: String {
        guard let secondStroke else { return firstStroke.encoded }
        return firstStroke.encoded + " > " + secondStroke.encoded
    }

    static func decode(_ value: String) -> ShortcutBinding? {
        let strokes = value.components(separatedBy: " > ")
        guard strokes.count == 1 || strokes.count == 2,
              let first = decodeStroke(strokes[0], requiresModifier: true) else { return nil }

        if strokes.count == 1 {
            return ShortcutBinding(firstStroke: first)
        }

        guard let second = decodeStroke(strokes[1], requiresModifier: false) else { return nil }
        return ShortcutBinding(firstStroke: first, secondStroke: second)
    }

    func withSecondStroke(_ second: ShortcutStroke) -> ShortcutBinding {
        return ShortcutBinding(firstStroke: firstStroke, secondStroke: second)
    }

    func hasSameFirstStroke(as other: ShortcutBinding) -> Bool {
        return firstStroke == other.firstStroke
    }

    /// Carbon can reserve a combination only once. These are the three collisions the app can
    /// create itself: a shared prefix, a second step that starts another action, or vice versa.
    func conflictsInternally(with other: ShortcutBinding) -> Bool {
        return hasSameFirstStroke(as: other)
            || secondStroke == other.firstStroke
            || other.secondStroke == firstStroke
    }

    static func decodeStroke(
        _ value: String,
        requiresModifier: Bool
    ) -> ShortcutStroke? {
        let parts = value.split(separator: "+").map { $0.trimmingCharacters(in: .whitespaces) }
        guard let keyPart = parts.last, !keyPart.isEmpty else { return nil }
        guard let code = keyCode(for: keyPart) else { return nil }

        var modifiers: UInt32 = 0
        for part in parts.dropLast() {
            switch part.lowercased() {
            case "control", "ctrl": modifiers |= UInt32(controlKey)
            case "option", "alt": modifiers |= UInt32(optionKey)
            case "shift": modifiers |= UInt32(shiftKey)
            case "command", "cmd": modifiers |= UInt32(cmdKey)
            default: return nil
            }
        }

        guard !requiresModifier || modifiers != 0 else { return nil }
        return ShortcutStroke(modifiers: modifiers, keyCode: code)
    }

    private static let letterKeyCodes: [(UInt32, String)] = [
        (UInt32(kVK_ANSI_A), "A"), (UInt32(kVK_ANSI_B), "B"), (UInt32(kVK_ANSI_C), "C"),
        (UInt32(kVK_ANSI_D), "D"), (UInt32(kVK_ANSI_E), "E"), (UInt32(kVK_ANSI_F), "F"),
        (UInt32(kVK_ANSI_G), "G"), (UInt32(kVK_ANSI_H), "H"), (UInt32(kVK_ANSI_I), "I"),
        (UInt32(kVK_ANSI_J), "J"), (UInt32(kVK_ANSI_K), "K"), (UInt32(kVK_ANSI_L), "L"),
        (UInt32(kVK_ANSI_M), "M"), (UInt32(kVK_ANSI_N), "N"), (UInt32(kVK_ANSI_O), "O"),
        (UInt32(kVK_ANSI_P), "P"), (UInt32(kVK_ANSI_Q), "Q"), (UInt32(kVK_ANSI_R), "R"),
        (UInt32(kVK_ANSI_S), "S"), (UInt32(kVK_ANSI_T), "T"), (UInt32(kVK_ANSI_U), "U"),
        (UInt32(kVK_ANSI_V), "V"), (UInt32(kVK_ANSI_W), "W"), (UInt32(kVK_ANSI_X), "X"),
        (UInt32(kVK_ANSI_Y), "Y"), (UInt32(kVK_ANSI_Z), "Z")
    ]

    /// Everything beyond A-Z that a user might reasonably reach for when rebinding. Letters stay
    /// first in `keyCodes` so a migration looking for a free key still prefers one.
    private static let additionalKeyCodes: [(UInt32, String)] = [
        (UInt32(kVK_ANSI_0), "0"), (UInt32(kVK_ANSI_1), "1"), (UInt32(kVK_ANSI_2), "2"),
        (UInt32(kVK_ANSI_3), "3"), (UInt32(kVK_ANSI_4), "4"), (UInt32(kVK_ANSI_5), "5"),
        (UInt32(kVK_ANSI_6), "6"), (UInt32(kVK_ANSI_7), "7"), (UInt32(kVK_ANSI_8), "8"),
        (UInt32(kVK_ANSI_9), "9"),
        (UInt32(kVK_F1), "F1"), (UInt32(kVK_F2), "F2"), (UInt32(kVK_F3), "F3"),
        (UInt32(kVK_F4), "F4"), (UInt32(kVK_F5), "F5"), (UInt32(kVK_F6), "F6"),
        (UInt32(kVK_F7), "F7"), (UInt32(kVK_F8), "F8"), (UInt32(kVK_F9), "F9"),
        (UInt32(kVK_F10), "F10"), (UInt32(kVK_F11), "F11"), (UInt32(kVK_F12), "F12"),
        (UInt32(kVK_F13), "F13"), (UInt32(kVK_F14), "F14"), (UInt32(kVK_F15), "F15"),
        (UInt32(kVK_F16), "F16"), (UInt32(kVK_F17), "F17"), (UInt32(kVK_F18), "F18"),
        (UInt32(kVK_F19), "F19"), (UInt32(kVK_F20), "F20"),
        (UInt32(kVK_Space), "Space"), (UInt32(kVK_Return), "Return"), (UInt32(kVK_Tab), "Tab"),
        (UInt32(kVK_Delete), "Delete"), (UInt32(kVK_ForwardDelete), "ForwardDelete"),
        (UInt32(kVK_Home), "Home"), (UInt32(kVK_End), "End"),
        (UInt32(kVK_PageUp), "PageUp"), (UInt32(kVK_PageDown), "PageDown"),
        (UInt32(kVK_LeftArrow), "Left"), (UInt32(kVK_RightArrow), "Right"),
        (UInt32(kVK_UpArrow), "Up"), (UInt32(kVK_DownArrow), "Down"),
        (UInt32(kVK_ANSI_Minus), "-"), (UInt32(kVK_ANSI_Equal), "="),
        (UInt32(kVK_ANSI_LeftBracket), "["), (UInt32(kVK_ANSI_RightBracket), "]"),
        (UInt32(kVK_ANSI_Backslash), "\\"), (UInt32(kVK_ANSI_Semicolon), ";"),
        (UInt32(kVK_ANSI_Quote), "'"), (UInt32(kVK_ANSI_Comma), ","),
        (UInt32(kVK_ANSI_Period), "."), (UInt32(kVK_ANSI_Slash), "/"),
        (UInt32(kVK_ANSI_Grave), "`")
    ]

    static let keyCodes: [(UInt32, String)] = letterKeyCodes + additionalKeyCodes

    static func keyName(for code: UInt32) -> String? {
        return keyCodes.first { $0.0 == code }?.1
    }

    static func keyCode(for name: String) -> UInt32? {
        let upper = name.uppercased()
        return keyCodes.first { $0.1.uppercased() == upper }?.0
    }

    /// AppKit has no way to express a two-stroke menu equivalent. A named key such as F5 also has
    /// no single character, so either form uses the textual shortcut shown in the menu instead.
    var menuKeyEquivalent: String {
        guard secondStroke == nil else { return "" }
        let name = ShortcutBinding.keyName(for: keyCode) ?? ""
        return name.count == 1 ? name.lowercased() : ""
    }
}
