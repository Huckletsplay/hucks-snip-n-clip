import Carbon.HIToolbox
import Foundation

/// Global shortcuts through Carbon's hot key API. This route works while another app is in front
/// and, unlike an event tap, needs no Accessibility permission. Chord second strokes are reserved
/// only for 1.5 seconds after their first stroke, so a bare second key never replaces normal typing
/// while Huck's Snip 'n' Clip is idle.
final class HotKeyCenter {
    static let shared = HotKeyCenter()
    static let chordTimeout: TimeInterval = 1.5

    private struct Registration {
        var ref: EventHotKeyRef
        var binding: ShortcutBinding
        var handler: () -> Void
    }

    private struct PendingChord {
        var identifier: UInt32
        var ref: EventHotKeyRef
        var handler: () -> Void
    }

    /// Lets the menu-bar owner explain the pending second step without putting a popup into a Snip.
    var onChordStatusChanged: ((ShortcutStroke?) -> Void)?
    var onChordRegistrationFailed: ((ShortcutStroke) -> Void)?

    private var registrations: [UInt32: Registration] = [:]
    private var pendingChord: PendingChord?
    private var chordTimer: Timer?
    private var nextIdentifier: UInt32 = 1
    private var eventHandler: EventHandlerRef?
    private let signature: OSType = 0x48534E43 // 'HSNC'

    private init() {}

    func start() {
        guard eventHandler == nil else { return }

        var eventType = EventTypeSpec(
            eventClass: OSType(kEventClassKeyboard),
            eventKind: UInt32(kEventHotKeyPressed))

        InstallEventHandler(
            GetApplicationEventTarget(),
            { _, event, userData -> OSStatus in
                guard let event, let userData else { return OSStatus(eventNotHandledErr) }

                var hotKeyID = EventHotKeyID()
                let status = GetEventParameter(
                    event,
                    EventParamName(kEventParamDirectObject),
                    EventParamType(typeEventHotKeyID),
                    nil,
                    MemoryLayout<EventHotKeyID>.size,
                    nil,
                    &hotKeyID)

                guard status == noErr else { return status }

                let center = Unmanaged<HotKeyCenter>.fromOpaque(userData).takeUnretainedValue()
                center.fire(identifier: hotKeyID.id)
                return noErr
            },
            1,
            &eventType,
            Unmanaged.passUnretained(self).toOpaque(),
            &eventHandler)
    }

    /// Registers the first stroke permanently. For a chord, its second stroke is also reserved and
    /// released once here so a rebind fails immediately when macOS or another app already owns it.
    @discardableResult
    func register(_ binding: ShortcutBinding, handler: @escaping () -> Void) -> Bool {
        start()

        let identifier = claimIdentifier()
        guard let reference = register(stroke: binding.firstStroke, identifier: identifier) else {
            return false
        }

        if let second = binding.secondStroke {
            let validationIdentifier = claimIdentifier()
            guard let validationReference = register(
                stroke: second,
                identifier: validationIdentifier
            ) else {
                UnregisterEventHotKey(reference)
                return false
            }
            UnregisterEventHotKey(validationReference)
        }

        registrations[identifier] = Registration(
            ref: reference,
            binding: binding,
            handler: handler)
        return true
    }

    /// Registers one removable single-stroke key and returns the identifier needed to remove it.
    /// Output selection uses this alongside action registrations while idle, then removes only
    /// these identifiers during capture.
    func registerContextual(
        _ stroke: ShortcutStroke,
        handler: @escaping () -> Void
    ) -> UInt32? {
        start()
        let identifier = claimIdentifier()
        guard let reference = register(stroke: stroke, identifier: identifier) else { return nil }
        registrations[identifier] = Registration(
            ref: reference,
            binding: ShortcutBinding(firstStroke: stroke),
            handler: handler)
        return identifier
    }

    func unregister(identifiers: [UInt32]) {
        for identifier in identifiers {
            guard let registration = registrations.removeValue(forKey: identifier) else { continue }
            UnregisterEventHotKey(registration.ref)
        }
    }

    func unregisterAll() {
        cancelPendingChord()
        for registration in registrations.values {
            UnregisterEventHotKey(registration.ref)
        }
        registrations.removeAll()
    }

    private func fire(identifier: UInt32) {
        DispatchQueue.main.async { [weak self] in
            self?.handle(identifier: identifier)
        }
    }

    private func handle(identifier: UInt32) {
        if let pendingChord, pendingChord.identifier == identifier {
            let handler = pendingChord.handler
            cancelPendingChord()
            handler()
            return
        }

        guard let registration = registrations[identifier] else { return }
        cancelPendingChord()
        guard let second = registration.binding.secondStroke else {
            registration.handler()
            return
        }

        let secondIdentifier = claimIdentifier()
        guard let secondReference = register(stroke: second, identifier: secondIdentifier) else {
            onChordRegistrationFailed?(second)
            return
        }

        pendingChord = PendingChord(
            identifier: secondIdentifier,
            ref: secondReference,
            handler: registration.handler)
        onChordStatusChanged?(second)
        chordTimer = Timer.scheduledTimer(withTimeInterval: Self.chordTimeout, repeats: false) {
            [weak self] _ in
            self?.cancelPendingChord()
        }
    }

    private func cancelPendingChord() {
        chordTimer?.invalidate()
        chordTimer = nil
        if let pendingChord {
            UnregisterEventHotKey(pendingChord.ref)
            self.pendingChord = nil
            onChordStatusChanged?(nil)
        }
    }

    private func claimIdentifier() -> UInt32 {
        let identifier = nextIdentifier
        nextIdentifier &+= 1
        if nextIdentifier == 0 { nextIdentifier = 1 }
        return identifier
    }

    private func register(stroke: ShortcutStroke, identifier: UInt32) -> EventHotKeyRef? {
        var reference: EventHotKeyRef?
        let hotKeyID = EventHotKeyID(signature: signature, id: identifier)
        let status = RegisterEventHotKey(
            stroke.keyCode,
            stroke.modifiers,
            hotKeyID,
            GetApplicationEventTarget(),
            0,
            &reference)
        guard status == noErr else { return nil }
        return reference
    }
}
