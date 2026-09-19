import AppKit
import Carbon.HIToolbox
import CoreGraphics

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let store = SettingsStore.createDefault()
    private let capture = ScreenCaptureService()
    private let saver = CaptureSaveService.createDefault()

    private var settings = AppSettings()
    private var statusItem: NSStatusItem?
    private var isCapturing = false
    private var lastSavedPath: String?
    private var lastExternalApplicationPID: pid_t?
    private var clipRecorder: ScreenClipRecorder?
    private var clipAction: CaptureAction?
    private var clipDestination: CaptureDestination?
    private var clipCapturedAt: Date?
    private var clipFilenameConfiguration: CaptureFilenameConfiguration?
    private var clipFilenameCounter: Int?
    private var outputHotKeyIdentifiers: [UInt32] = []
    private var recordingControlHotKeyIdentifiers: [UInt32] = []
    private var floatingController: FloatingRecordingController?
    private var controllerLayoutKey: String?
    private var clipTargetDisplayID: CGDirectDisplayID?
    private var clipExcludesOwnWindows = false
    private var controllerHiddenForSafety = false
    private var updateFlow: UpdateFlow?
    private var clipStopRequested = false
    private var clipPaused = false
    private var quitAfterClipFinishes = false
    private var regionRecordingShade: RegionRecordingShade?
    private var clipMeterTimer: Timer?
    private let inputMeter = MeterSmoother()
    private let strainMeter = MeterSmoother()
    private let pressureMeter = MeterSmoother()
    private let systemLoadSampler = SystemLoadSampler()
    private var persistentMenuChoices: [PersistentMenuChoiceView] = []
    private var isPerformingPersistentMenuAction = false
    private weak var recordingSummaryItem: NSMenuItem?
    private weak var outputSummaryItem: NSMenuItem?
    private weak var microphoneSummaryItem: NSMenuItem?
    private weak var systemGainSummaryItem: NSMenuItem?
    private weak var microphoneGainSummaryItem: NSMenuItem?
    private weak var controllerOpacitySummaryItem: NSMenuItem?

    func applicationDidFinishLaunching(_ notification: Notification) {
        settings = store.load()
        rememberExternalApplication(NSWorkspace.shared.frontmostApplication)
        NSWorkspace.shared.notificationCenter.addObserver(
            self,
            selector: #selector(workspaceApplicationDidActivate(_:)),
            name: NSWorkspace.didActivateApplicationNotification,
            object: nil)
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(screenParametersDidChange(_:)),
            name: NSApplication.didChangeScreenParametersNotification,
            object: nil)

        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.image = TrayIconFactory.makeIcon()
        item.button?.toolTip = "Huck’s Snip ’n’ Clip"
        let menu = NSMenu()
        // State belongs to the capture state machine. AppKit's default automatic validation would
        // re-enable Stop/Pause merely because this delegate implements their action selectors.
        menu.autoenablesItems = false
        menu.delegate = self
        item.menu = menu
        statusItem = item

        HotKeyCenter.shared.onChordStatusChanged = { [weak self] secondStroke in
            guard let self else { return }
            if let secondStroke {
                self.statusItem?.button?.toolTip = "Huck’s Snip ’n’ Clip — press \(secondStroke.displayString)"
            } else {
                self.restoreStatusToolTipAfterChord()
            }
        }
        HotKeyCenter.shared.onChordRegistrationFailed = { secondStroke in
            ToastPresenter.show("Could not listen for \(secondStroke.displayString)")
        }

        let installedVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
        let flow = UpdateFlow(
            service: UpdateService(
                client: URLSessionUpdateClient(userAgent: "HucksSnipNClip-macOS-Updater/\(installedVersion)"),
                workDirectory: UpdateService.defaultWorkDirectory()),
            presenter: AppKitUpdatePresenter(),
            installedVersion: installedVersion)
        flow.onPhaseChange = { [weak self] in
            Task { @MainActor in self?.rebuildMenu() }
        }
        updateFlow = flow

        rebuildMenu()
        registerShortcuts()

        if !ScreenCaptureService.ensurePermission() {
            ToastPresenter.show("Screen Recording is off - see the menu bar H")
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        clipMeterTimer?.invalidate()
        regionRecordingShade?.dismiss()
        dismissFloatingController()
        HotKeyCenter.shared.unregisterAll()
        NSWorkspace.shared.notificationCenter.removeObserver(self)
        NotificationCenter.default.removeObserver(self)
    }

    // MARK: - Shortcuts

    /// Re-registers every action, because Carbon hot keys are registered individually and the only
    /// honest way to find out whether a combination is free is to ask macOS for it.
    ///
    /// With a `target`, returns whether that one rebound action was accepted. Without one, returns
    /// whether the entire set registered, which lets Reset All remain transactional. A failure on
    /// any non-target action still surfaces as a toast.
    @discardableResult
    private func registerShortcuts(
        reportingFailureFor target: CaptureAction? = nil,
        reportingOutputFailureFor targetOutputID: String? = nil
    ) -> Bool {
        HotKeyCenter.shared.unregisterAll()
        outputHotKeyIdentifiers.removeAll()
        recordingControlHotKeyIdentifiers.removeAll()

        var targetRegistered = true
        var registrationsAllSucceeded = true
        for action in CaptureAction.allCases {
            let binding = settings.shortcut(for: action)
            let registered = HotKeyCenter.shared.register(binding) { [weak self] in
                self?.perform(action)
            }

            if !registered {
                registrationsAllSucceeded = false
                if action == target {
                    targetRegistered = false
                } else {
                    ToastPresenter.show("\(binding.displayString) is already taken by another app")
                }
            }
        }

        let outputsRegistered = registerOutputShortcuts(reportingFailureFor: targetOutputID)
        registrationsAllSucceeded = registrationsAllSucceeded && outputsRegistered
        registerRecordingControlShortcuts()

        // A targeted rebind only needs to know whether that action survived. Callers performing a
        // whole-set operation, such as Reset All, need every registration to have succeeded.
        if target != nil { return targetRegistered }
        if targetOutputID != nil { return outputsRegistered }
        return registrationsAllSucceeded
    }

    /// Output keys are available only before capture. They select the active route used by both
    /// Snips and Clips, then leave capture itself to the existing action shortcuts.
    @discardableResult
    private func registerOutputShortcuts(reportingFailureFor targetID: String? = nil) -> Bool {
        guard clipRecorder == nil, !isCapturing else { return true }

        var targetRegistered = targetID == nil
        var registrationsAllSucceeded = true
        for destination in settings.destinations {
            guard let shortcut = settings.outputShortcut(for: destination.id) else { continue }
            if let identifier = HotKeyCenter.shared.registerContextual(shortcut, handler: {
                [weak self] in
                self?.selectOutput(destinationID: destination.id)
            }) {
                outputHotKeyIdentifiers.append(identifier)
                if destination.id == targetID { targetRegistered = true }
            } else {
                registrationsAllSucceeded = false
                if destination.id == targetID {
                    targetRegistered = false
                } else {
                    ToastPresenter.show(
                        "\(shortcut.displayString) for \(destination.name) is already taken")
                }
            }
        }

        return targetID.map { _ in targetRegistered } ?? registrationsAllSucceeded
    }

    private func unregisterOutputShortcuts() {
        HotKeyCenter.shared.unregister(identifiers: outputHotKeyIdentifiers)
        outputHotKeyIdentifiers.removeAll()
    }

    /// Pause/Resume and Stop exist as global keys only while a take is running - never while idle,
    /// never during finalization. A key another app already owns is reported and left exactly as
    /// stored: the menu and the floating controller still work, so the take is never stranded.
    private func registerRecordingControlShortcuts() {
        unregisterRecordingControlShortcuts()
        guard clipRecorder != nil, !clipStopRequested else { return }

        for control in RecordingControlAction.allCases {
            let stroke = settings.recordingShortcut(for: control)
            if let identifier = HotKeyCenter.shared.registerContextual(stroke, handler: {
                [weak self] in
                self?.performRecordingControl(control)
            }) {
                recordingControlHotKeyIdentifiers.append(identifier)
            } else {
                let verb = control == .stop ? "stop" : "pause"
                ToastPresenter.show(
                    "\(stroke.displayString) could not \(verb) this recording - another app has it. "
                        + "Use the menu bar or the floating controls.")
            }
        }
    }

    private func unregisterRecordingControlShortcuts() {
        HotKeyCenter.shared.unregister(identifiers: recordingControlHotKeyIdentifiers)
        recordingControlHotKeyIdentifiers.removeAll()
    }

    private func performRecordingControl(_ control: RecordingControlAction) {
        // Idle and finishing are not states these keys act in. They are unregistered then, and
        // this second guard keeps a queued key press from reaching a take that has already ended.
        guard clipRecorder != nil, !clipStopRequested, !isCapturing else { return }
        switch control {
        case .stop: stopClip()
        case .pauseResume: toggleClipPause()
        }
    }

    private func selectOutput(destinationID: String) {
        guard clipRecorder == nil,
              !isCapturing,
              let destination = settings.destinations.first(where: { $0.id == destinationID })
        else { return }

        let previous = settings.activeDestinationId
        guard previous != destination.id else {
            // Selection is confirmed even when routine setting notices are off. These keys are
            // pressed blind, with the menu closed, to arm the very next capture - a silent switch
            // would route a Snip or Clip somewhere the user cannot see until it has already
            // landed. That makes this a capture result, not a harmless setting change.
            ToastPresenter.show("Output already selected: \(destination.name)")
            return
        }

        settings.activeDestinationId = destination.id
        guard persist(rollback: { self.settings.activeDestinationId = previous }) else { return }
        ToastPresenter.show("Output selected: \(destination.name)")
        rebuildMenu()
    }

    // MARK: - Menu

    func menuNeedsUpdate(_ menu: NSMenu) {
        rebuildMenu()
    }

    private func rebuildMenu() {
        guard let menu = statusItem?.menu else { return }
        menu.removeAllItems()
        persistentMenuChoices.removeAll()
        recordingSummaryItem = nil
        outputSummaryItem = nil
        microphoneSummaryItem = nil
        systemGainSummaryItem = nil
        microphoneGainSummaryItem = nil
        controllerOpacitySummaryItem = nil
        let captureControlsLocked = clipRecorder != nil || isCapturing

        let heading = NSMenuItem(title: "Huck’s Snip ’n’ Clip", action: nil, keyEquivalent: "")
        heading.isEnabled = false
        menu.addItem(heading)

        let brandLine = NSMenuItem(title: "By Quintin Huckaby", action: nil, keyEquivalent: "")
        brandLine.isEnabled = false
        menu.addItem(brandLine)
        menu.addItem(.separator())

        if !CGPreflightScreenCaptureAccess() {
            let permissionItem = NSMenuItem(
                title: "Allow Screen Recording...",
                action: #selector(openScreenRecordingSettings),
                keyEquivalent: "")
            permissionItem.target = self
            permissionItem.isEnabled = !captureControlsLocked
            menu.addItem(permissionItem)
            menu.addItem(.separator())
        }

        for action in CaptureAction.allCases {
            let binding = settings.shortcut(for: action)
            let title = binding.hasSecondStroke
                ? "\(action.displayName) — \(binding.displayString)"
                : action.displayName
            let item = NSMenuItem(
                title: title,
                action: #selector(handleCaptureMenuItem(_:)),
                keyEquivalent: binding.menuKeyEquivalent)
            item.keyEquivalentModifierMask = menuModifierMask(for: binding)
            item.representedObject = action.rawValue
            item.target = self
            item.isEnabled = captureActionIsEnabled(action)
            menu.addItem(item)
        }

        menu.addItem(.separator())

        let stopItem = NSMenuItem(
            title: "Stop Recording",
            action: #selector(stopCurrentRecording),
            keyEquivalent: "")
        stopItem.target = self
        stopItem.isEnabled = clipRecorder != nil && !clipStopRequested && !isCapturing
        menu.addItem(stopItem)

        let pauseItem = NSMenuItem(
            title: clipPaused ? "Resume Recording" : "Pause Recording",
            action: #selector(toggleClipPause),
            keyEquivalent: "")
        pauseItem.target = self
        pauseItem.isEnabled = clipRecorder != nil && !clipStopRequested && !isCapturing
        menu.addItem(pauseItem)

        menu.addItem(.separator())

        let openLastItem = NSMenuItem(
            title: "Open Last Capture",
            action: #selector(openLastCapture),
            keyEquivalent: "")
        openLastItem.target = self
        openLastItem.isEnabled = !captureControlsLocked
            && (lastSavedPath.map(FileManager.default.fileExists(atPath:)) ?? false)
        menu.addItem(openLastItem)

        menu.addItem(.separator())

        menu.addItem(createRecordingMenu())

        menu.addItem(.separator())

        let displayedDestination = clipRecorder == nil
            ? settings.activeDestination
            : (clipDestination ?? settings.activeDestination)
        let destinationItem = NSMenuItem(
            title: "Output — \(displayedDestination?.name ?? "None")",
            action: nil,
            keyEquivalent: "")
        let destinationMenu = NSMenu()

        for destination in settings.destinations {
            let item = NSMenuItem(
                title: destination.name,
                action: #selector(selectDestination(_:)),
                keyEquivalent: "")
            item.representedObject = destination.id
            item.target = self
            item.state = destination.id == settings.activeDestination?.id ? .on : .off
            attachPersistentChoice(
                to: item,
                style: .radio,
                isSelected: { [weak self] in
                    self?.settings.activeDestination?.id == destination.id
                })
            destinationMenu.addItem(item)
        }

        destinationMenu.addItem(.separator())

        let addItem = NSMenuItem(title: "Add Output...", action: #selector(addDestination), keyEquivalent: "")
        addItem.target = self
        destinationMenu.addItem(addItem)

        let removeItem = NSMenuItem(
            title: "Remove Current Output...",
            action: #selector(removeCurrentDestination),
            keyEquivalent: "")
        removeItem.target = self
        removeItem.isEnabled = settings.destinations.count > 1
        destinationMenu.addItem(removeItem)

        let revealItem = NSMenuItem(
            title: "Open Current Output",
            action: #selector(revealDestination),
            keyEquivalent: "")
        revealItem.target = self
        destinationMenu.addItem(revealItem)

        destinationItem.submenu = destinationMenu
        destinationItem.isEnabled = !captureControlsLocked
        outputSummaryItem = destinationItem
        menu.addItem(destinationItem)

        menu.addItem(.separator())
        menu.addItem(createSettingsMenu(locked: captureControlsLocked))

        let aboutItem = NSMenuItem(title: "About Huck’s Snip ’n’ Clip", action: #selector(showAbout), keyEquivalent: "")
        aboutItem.target = self
        aboutItem.isEnabled = !captureControlsLocked
        menu.addItem(aboutItem)

        menu.addItem(.separator())

        let quitItem = NSMenuItem(title: "Quit", action: #selector(quit), keyEquivalent: "q")
        quitItem.target = self
        quitItem.isEnabled = !captureControlsLocked
        menu.addItem(quitItem)
    }

    /// Pointer activation of an embedded control keeps the open menu and submenu hierarchy alive.
    /// The underlying NSMenuItem action remains the one authority for validation, persistence, and
    /// rollback; this wrapper only changes how AppKit tracks the click.
    private func attachPersistentChoice(
        to item: NSMenuItem,
        style: PersistentMenuChoiceView.Style,
        isSelected: @escaping () -> Bool
    ) {
        let choice = PersistentMenuChoiceView(
            title: item.title,
            style: style,
            isSelected: isSelected,
            isEnabled: { [weak item] in item?.isEnabled ?? false },
            activation: { [weak self, weak item] in
                guard let self, let item, let action = item.action else { return }
                self.isPerformingPersistentMenuAction = true
                _ = NSApp.sendAction(action, to: item.target, from: item)
                self.isPerformingPersistentMenuAction = false
                self.refreshPersistentMenuChoices()
            })
        item.view = choice
        persistentMenuChoices.append(choice)
    }

    private func refreshPersistentMenuChoices() {
        for choice in persistentMenuChoices { choice.refresh() }
        recordingSummaryItem?.title = recordingMenuTitle()
        outputSummaryItem?.title = "Output — \(settings.activeDestination?.name ?? "None")"

        let devices = MicrophoneDeviceService.availableDevices()
        let defaultDevice = MicrophoneDeviceService.defaultDevice()
        let microphone = MicrophoneDeviceService.resolve(
            savedDeviceID: settings.microphoneDeviceID,
            savedDeviceName: settings.microphoneDeviceName,
            devices: devices,
            defaultDevice: defaultDevice)
        microphoneSummaryItem?.title = "Microphone — \(microphone.displayName)"
        systemGainSummaryItem?.title = "System / App Gain — \(settings.computerAudioGainPercent)%"
        microphoneGainSummaryItem?.title = "Microphone Gain — \(settings.microphoneGainPercent)%"
        controllerOpacitySummaryItem?.title = "Controller Opacity — "
            + "\(FloatingControllerMetrics.normalizeOpacity(settings.floatingControllerOpacityPercent))%"
    }

    private func refreshAfterSettingAction() {
        if !isPerformingPersistentMenuAction { rebuildMenu() }
    }

    private func createSettingsMenu(locked: Bool) -> NSMenuItem {
        let setting = NSMenuItem(title: "Settings", action: nil, keyEquivalent: "")
        // Openable during a take only to reach the floating controller's two appearance choices;
        // every other row inside stays disabled while locked.
        setting.isEnabled = true

        let submenu = NSMenu()
        submenu.autoenablesItems = false
        submenu.addItem(createOutputShortcutsMenu(locked: locked))
        submenu.addItem(createFilenameMenu(locked: locked))
        submenu.addItem(createShortcutsMenu(locked: locked))
        submenu.addItem(createRecordingControlsMenu(locked: locked))
        submenu.addItem(.separator())

        let notificationsItem = NSMenuItem(
            title: "Setting Change Notifications",
            action: #selector(toggleSettingChangeNotifications),
            keyEquivalent: "")
        notificationsItem.target = self
        notificationsItem.state = settings.routineNotificationsEnabled ? .on : .off
        notificationsItem.isEnabled = !locked
        attachPersistentChoice(
            to: notificationsItem,
            style: .checkbox,
            isSelected: { [weak self] in self?.settings.routineNotificationsEnabled ?? false })
        submenu.addItem(notificationsItem)

        let loginState = LaunchAtLoginService.state
        let loginPresentation = LaunchAtLoginService.presentation(for: loginState)
        let loginItem = NSMenuItem(
            title: loginPresentation.title,
            action: #selector(toggleStartAtLogin),
            keyEquivalent: "")
        loginItem.target = self
        switch loginPresentation.checkmark {
        case .off:
            loginItem.state = .off
        case .on:
            loginItem.state = .on
        case .mixed:
            loginItem.state = .mixed
        }
        loginItem.isEnabled = !locked
        submenu.addItem(loginItem)

        submenu.addItem(.separator())
        let settingsFile = NSMenuItem(
            title: "Open Settings File",
            action: #selector(openSettingsFile),
            keyEquivalent: "")
        settingsFile.target = self
        settingsFile.isEnabled = !locked
        submenu.addItem(settingsFile)

        // Contacts GitHub only when chosen. Unavailable during a take, and while a check is running.
        let updateState = UpdateMenuState.item(
            phase: updateFlow?.phase ?? .idle,
            captureLocked: locked)
        let updates = NSMenuItem(
            title: updateState.title,
            action: #selector(checkForUpdates),
            keyEquivalent: "")
        updates.target = self
        updates.isEnabled = updateState.enabled
        submenu.addItem(updates)

        setting.submenu = submenu
        return setting
    }

    // MARK: - Capture

    @objc private func handleCaptureMenuItem(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String,
              let action = CaptureAction(rawValue: raw) else { return }
        perform(action)
    }

    private func perform(_ action: CaptureAction) {
        let targetWindowPID = (action == .snipWindow || action == .clipWindow)
            ? currentExternalApplicationPID()
            : nil

        if action.isClip {
            toggleClip(action, targetWindowPID: targetWindowPID)
            return
        }

        guard !isCapturing, clipRecorder == nil else { return }
        unregisterOutputShortcuts()
        isCapturing = true
        rebuildMenu()

        Task { @MainActor in
            defer {
                isCapturing = false
                _ = registerOutputShortcuts()
                rebuildMenu()
            }

            do {
                switch action {
                case .snipRegion:
                    guard let region = await requestRegion(.snip) else { return }
                    // Let the overlay finish leaving the screen before the shutter.
                    try await Task.sleep(nanoseconds: 120_000_000)
                    let image = try await capture.captureRegion(region)
                    try save(image)
                case .snipWindow:
                    let image = try await capture.captureWindow(forApplicationPID: targetWindowPID)
                    try save(image)
                case .snipScreen:
                    let point = ScreenCaptureService.currentMouseInDisplaySpace()
                    let image = try await capture.captureDisplay(containing: point)
                    try save(image)
                case .clipRegion, .clipWindow, .clipScreen:
                    break
                }
            } catch {
                let detail = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
                if case ScreenCaptureError.permissionDenied = error {
                    CopyableAlert.show(
                        title: "\(action.displayName) needs Screen Recording",
                        detail: detail,
                        extraButtonTitle: "Open System Settings",
                        extraAction: { AppDelegate.openScreenRecordingPane() })
                } else {
                    CopyableAlert.show(title: "\(action.displayName) failed", detail: detail)
                }
            }
        }
    }

    private func toggleClip(_ action: CaptureAction, targetWindowPID: pid_t?) {
        guard action.isClip else { return }
        if clipRecorder != nil {
            if clipAction == action { stopClip() }
            return
        }
        startClip(action, targetWindowPID: targetWindowPID)
    }

    private func startClip(_ action: CaptureAction, targetWindowPID: pid_t?) {
        guard !isCapturing, clipRecorder == nil else { return }
        unregisterOutputShortcuts()
        isCapturing = true
        rebuildMenu()

        Task { @MainActor in
            defer {
                isCapturing = false
                if clipRecorder == nil {
                    _ = registerOutputShortcuts()
                }
                rebuildMenu()
            }

            do {
                let target: ScreenClipTarget
                switch action {
                case .clipRegion:
                    // Reviewed before recording: Return accepts, a new drag redraws, Escape
                    // cancels with no file, no counter and no settings change.
                    guard let region = await requestRegion(.clip) else { return }
                    let anchor = CaptureExclusionAnchor()
                    defer { anchor.dismiss() }
                    target = try await capture.resolveRegionClipTarget(region)
                case .clipWindow:
                    target = try await capture.resolveWindowClipTarget(
                        forApplicationPID: targetWindowPID)
                case .clipScreen:
                    let point = ScreenCaptureService.currentMouseInDisplaySpace()
                    let anchor = CaptureExclusionAnchor()
                    defer { anchor.dismiss() }
                    target = try await capture.resolveDisplayClipTarget(containing: point)
                case .snipRegion, .snipWindow, .snipScreen:
                    return
                }

                let microphoneSelection = MicrophoneDeviceService.selection(
                    savedDeviceID: settings.microphoneDeviceID,
                    savedDeviceName: settings.microphoneDeviceName)
                if settings.recordingAudioMode.recordsMicrophone,
                   microphoneSelection.savedDeviceMissing {
                    ToastPresenter.show(microphoneSelection.displayName)
                }

                guard await CountdownOverlay.present(on: target.displayID) else { return }
                // Let the countdown window leave the compositor before ScreenCaptureKit starts.
                try await Task.sleep(nanoseconds: 150_000_000)

                let resolvedFrameRate = RecordingFrameRateSettings.resolve(
                    settings.recordingFrameRate,
                    displayRefreshRate: ScreenCaptureService.refreshRate(for: target.displayID))

                let recorder = ScreenClipRecorder(
                    workingDirectory: SettingsStore.workingDirectory,
                    framesPerSecond: resolvedFrameRate,
                    audioMode: settings.recordingAudioMode,
                    recordingQuality: settings.recordingQuality,
                    recordingResolution: settings.recordingResolution,
                    showsCursor: settings.recordingShowsCursor,
                    systemAudioGainPercent: settings.computerAudioGainPercent,
                    microphoneGainPercent: settings.microphoneGainPercent,
                    microphoneDeviceID: microphoneSelection.captureDeviceID)
                let capturedAt = Date()
                let filenameConfiguration = settings.captureFilename
                try await recorder.start(target: target)
                clipDestination = settings.activeDestination
                clipAction = action
                clipCapturedAt = capturedAt
                clipFilenameConfiguration = filenameConfiguration
                clipFilenameCounter = reserveCaptureCounter()
                clipRecorder = recorder
                clipStopRequested = false
                clipPaused = false
                clipTargetDisplayID = target.displayID
                clipExcludesOwnWindows = target.excludesOwnWindows
                controllerHiddenForSafety = false
                registerRecordingControlShortcuts()
                if let region = target.displaySpaceRegion,
                   let shade = RegionRecordingShade(displaySpaceRegion: region) {
                    regionRecordingShade = shade
                    shade.show()
                }
                showFloatingController()
                startClipMeters()
                statusItem?.button?.toolTip = "Recording \(action.displayName.dropFirst(5)) — \(resolvedFrameRate) FPS + \(settings.recordingResolution.rawValue) + \(settings.recordingQuality.displayName) + \(settings.recordingAudioMode.summary) — \(settings.recordingShortcut(for: .stop).displayString) to stop — output: \(clipDestination?.name ?? "destination")"
            } catch {
                let detail = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
                if case ScreenCaptureError.permissionDenied = error {
                    CopyableAlert.show(
                        title: "\(action.displayName) needs Screen Recording",
                        detail: detail,
                        extraButtonTitle: "Open System Settings",
                        extraAction: { AppDelegate.openScreenRecordingPane() })
                } else if case ScreenClipError.microphonePermissionDenied = error {
                    CopyableAlert.show(
                        title: "Microphone access is off",
                        detail: detail,
                        extraButtonTitle: "Open System Settings",
                        extraAction: { AppDelegate.openMicrophonePane() })
                } else {
                    CopyableAlert.show(title: "\(action.displayName) could not start", detail: detail)
                }
            }
        }
    }

    private func stopClip() {
        guard let recorder = clipRecorder, !clipStopRequested else { return }
        let finishingAction = clipAction ?? .clipScreen
        clipStopRequested = true
        unregisterRecordingControlShortcuts()
        floatingController?.setFinishing()
        dismissFloatingController()
        clipMeterTimer?.invalidate()
        clipMeterTimer = nil
        statusItem?.button?.image = TrayIconFactory.makeIcon(recording: true, finishing: true)
        statusItem?.button?.toolTip = "Finishing \(finishingAction.displayName.dropFirst(5))…"
        regionRecordingShade?.dismiss()
        regionRecordingShade = nil
        rebuildMenu()

        Task { @MainActor in
            do {
                let workingURL = try await recorder.stop()
                let delivery = recorder.takeCompletedDelivery()
                let result = try saver.routeCompletedClip(
                    workingURL,
                    destinationDirectory: clipDestination?.path,
                    filenameConfiguration: clipFilenameConfiguration ?? settings.captureFilename,
                    capturedAt: clipCapturedAt ?? Date(),
                    counter: clipFilenameCounter ?? 1)
                lastSavedPath = result.savedPath
                let copied = CapturePasteboard.copyClip(atPath: result.savedPath)
                let clipboard = copied ? "" : " - could not copy to the clipboard"
                let safety = controllerHiddenForSafety
                    ? " - floating controls were hidden to keep them out of this clip"
                    : ""

                // Always expose the measured changed-frame rate. ScreenCaptureKit's configured FPS
                // is a maximum rather than a promised schedule, so a lower rate is not labeled a
                // drop unless AVAssetWriter actually refused a complete frame.
                let frameRate = delivery.map { " - \($0.deliverySummary)" } ?? ""
                let strain = delivery?.strainNotice.map { " - \($0)" } ?? ""
                if result.usedRecovery {
                    ToastPresenter.show(
                        "Output unavailable - clip saved to Recovery\(frameRate)\(strain)\(clipboard)\(safety)")
                } else {
                    ToastPresenter.show(
                        "Clip saved to \(clipDestination?.name ?? "destination")\(frameRate)\(strain)\(clipboard)\(safety)")
                }
            } catch {
                let detail = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
                CopyableAlert.show(title: "\(finishingAction.displayName) failed", detail: detail)
            }

            clipRecorder = nil
            clipAction = nil
            clipDestination = nil
            clipCapturedAt = nil
            clipFilenameConfiguration = nil
            clipFilenameCounter = nil
            clipStopRequested = false
            clipPaused = false
            clipTargetDisplayID = nil
            clipExcludesOwnWindows = false
            controllerHiddenForSafety = false
            statusItem?.button?.image = TrayIconFactory.makeIcon()
            statusItem?.button?.toolTip = "Huck’s Snip ’n’ Clip"
            _ = registerOutputShortcuts()
            rebuildMenu()

            if quitAfterClipFinishes {
                NSApp.terminate(nil)
            }
        }
    }

    private func startClipMeters() {
        clipMeterTimer?.invalidate()
        inputMeter.reset()
        strainMeter.reset()
        pressureMeter.reset()
        systemLoadSampler.reset()
        updateClipMeters()
        clipMeterTimer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.updateClipMeters() }
        }
    }

    private func updateClipMeters() {
        guard let recorder = clipRecorder, !clipStopRequested else { return }
        if recorder.currentFailure() != nil {
            stopClip()
            return
        }
        if clipPaused {
            floatingController?.setPaused(true)
            statusItem?.button?.image = TrayIconFactory.makeIcon(recording: true, paused: true)
            let action = clipAction ?? .clipScreen
            statusItem?.button?.toolTip = "Paused \(action.displayName.dropFirst(5)) — choose Resume Recording or Stop Recording"
            return
        }

        let telemetry = recorder.takeTelemetry()
        // The right H post is a real CPU gauge. Frame pressure must not be presented as CPU usage,
        // so it reaches the user through the tooltip instead. A raw 100 ms window holds only a
        // handful of frames and reads as 0/17/33%, so it is smoothed like the other two meters.
        let rawStrain = systemLoadSampler.sample()
        let input = inputMeter.update(target: telemetry.audioPeak, elapsedMilliseconds: 100)
        let strain = strainMeter.update(target: rawStrain, elapsedMilliseconds: 100)
        let pressure = pressureMeter.update(
            target: telemetry.framePressure,
            elapsedMilliseconds: 100)
        statusItem?.button?.image = TrayIconFactory.makeIcon(
            recording: true,
            inputLevel: input,
            strainLevel: strain)
        floatingController?.setPaused(false)
        floatingController?.update(inputLevel: input, strainLevel: strain)
        let audioStatus = settings.recordingAudioMode.recordsAudio
            ? String(format: "%@ %.0f%%", settings.recordingAudioMode.summary, input * 100)
            : settings.recordingAudioMode.summary
        let action = clipAction ?? .clipScreen
        let frames = FrameDelivery.tooltipFragment(droppedFraction: pressure).map { " — \($0)" } ?? ""
        statusItem?.button?.toolTip = String(
            format: "Recording %@ — %@ — CPU %.0f%%%@ — use Stop Recording or %@",
            String(action.displayName.dropFirst(5)),
            audioStatus,
            strain * 100,
            frames,
            settings.shortcut(for: action).displayString)
    }

    private func captureActionIsEnabled(_ action: CaptureAction) -> Bool {
        return !isCapturing && clipRecorder == nil
    }

    @objc private func stopCurrentRecording() {
        stopClip()
    }

    @objc private func toggleClipPause() {
        guard let recorder = clipRecorder, !clipStopRequested, !isCapturing else { return }
        if clipPaused {
            guard recorder.resume() else { return }
            clipPaused = false
        } else {
            guard recorder.pause() else { return }
            clipPaused = true
        }
        updateClipMeters()
        rebuildMenu()
    }

    private func requestRegion(_ purpose: RegionSelectionSession.Purpose) async -> CGRect? {
        return await withCheckedContinuation { continuation in
            RegionSelectionOverlay.present(purpose: purpose) { rect in
                continuation.resume(returning: rect)
            }
        }
    }

    private func save(_ image: CGImage) throws {
        let destination = settings.activeDestination
        let result = try saver.savePng(
            image,
            destinationDirectory: destination?.path,
            filenameConfiguration: settings.captureFilename,
            capturedAt: Date(),
            counter: reserveCaptureCounter())
        lastSavedPath = result.savedPath
        // The capture is already safe on disk. A pasteboard failure is a warning, never a failure.
        let clipboard = CapturePasteboard.copySnip(atPath: result.savedPath)
            ? ""
            : " - could not copy to the clipboard"

        if result.usedRecovery {
            ToastPresenter.show("Output unavailable - saved to Recovery\(clipboard)")
        } else {
            ToastPresenter.show("Snip saved to \(destination?.name ?? "destination")\(clipboard)")
        }
    }

    // MARK: - Shortcuts

    private func createShortcutsMenu(locked: Bool) -> NSMenuItem {
        let item = NSMenuItem(title: "Shortcuts", action: nil, keyEquivalent: "")
        item.isEnabled = !locked

        let submenu = NSMenu()
        for action in CaptureAction.allCases {
            let binding = settings.shortcut(for: action)
            let choice = NSMenuItem(
                title: "\(action.displayName) — \(binding.displayString)",
                action: #selector(beginShortcutRebind(_:)),
                keyEquivalent: "")
            choice.target = self
            choice.representedObject = action.rawValue
            choice.isEnabled = !locked
            submenu.addItem(choice)
        }

        submenu.addItem(.separator())
        let reset = NSMenuItem(
            title: "Reset All Shortcuts to Defaults…",
            action: #selector(resetShortcutsToDefaults),
            keyEquivalent: "")
        reset.target = self
        reset.isEnabled = !locked
        submenu.addItem(reset)

        item.submenu = submenu
        return item
    }

    private func createOutputShortcutsMenu(locked: Bool) -> NSMenuItem {
        let item = NSMenuItem(title: "Output Shortcuts", action: nil, keyEquivalent: "")
        item.isEnabled = !locked

        let submenu = NSMenu()
        let explanation = NSMenuItem(
            title: "Before capture: select output…",
            action: nil,
            keyEquivalent: "")
        explanation.isEnabled = false
        submenu.addItem(explanation)
        submenu.addItem(.separator())

        for destination in settings.destinations {
            let shortcut = settings.outputShortcut(for: destination.id)
            let choice = NSMenuItem(
                title: "\(destination.name) — \(shortcut?.displayString ?? "Unassigned")",
                action: #selector(beginOutputShortcutRebind(_:)),
                keyEquivalent: "")
            choice.target = self
            choice.representedObject = destination.id
            choice.isEnabled = !locked
            submenu.addItem(choice)
        }

        submenu.addItem(.separator())
        let reset = NSMenuItem(
            title: "Reset Output Shortcuts to Defaults",
            action: #selector(resetOutputShortcutsToDefaults),
            keyEquivalent: "")
        reset.target = self
        reset.isEnabled = !locked
        submenu.addItem(reset)

        item.submenu = submenu
        return item
    }

    @objc private func beginShortcutRebind(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String,
              let action = CaptureAction(rawValue: raw),
              clipRecorder == nil,
              !isCapturing else { return }

        // Registered Carbon hot keys are consumed before the focused panel can read them. Suspend
        // the six actions while the editor is listening, just as the Windows editor does, then
        // restore the full set on cancellation or through applyRebind's transactional path.
        HotKeyCenter.shared.unregisterAll()
        ShortcutCaptureOverlay.present(
            actionName: action.displayName,
            current: settings.shortcut(for: action).displayString
        ) { [weak self] binding in
            guard let self else { return }
            guard let binding else {
                _ = self.registerShortcuts(reportingFailureFor: nil)
                return
            }
            self.applyRebind(binding, to: action)
        }
    }

    @objc private func beginOutputShortcutRebind(_ sender: NSMenuItem) {
        guard let destinationID = sender.representedObject as? String,
              let destination = settings.destinations.first(where: { $0.id == destinationID }),
              clipRecorder == nil,
              !isCapturing else { return }

        // Carbon consumes registered global keys before a focused panel can see them. Suspend the
        // action shortcuts while this one-stroke editor listens, then restore them on every exit.
        HotKeyCenter.shared.unregisterAll()
        OutputShortcutCaptureOverlay.present(
            destinationName: destination.name,
            current: settings.outputShortcut(for: destination.id)?.displayString ?? "Unassigned"
        ) { [weak self] shortcut in
            guard let self else { return }
            guard let shortcut else {
                _ = self.registerShortcuts(reportingFailureFor: nil)
                return
            }
            self.applyOutputShortcut(shortcut, to: destination)
        }
    }

    private func applyOutputShortcut(
        _ shortcut: ShortcutStroke,
        to destination: CaptureDestination
    ) {
        if let clash = settings.outputShortcutConflicts(
            with: shortcut,
            excluding: destination.id
        ) {
            CopyableAlert.show(
                title: "Output shortcut already in use",
                detail: "\(shortcut.displayString) already selects \(clash.name). "
                    + "Choose a different key for \(destination.name).")
            _ = registerShortcuts(reportingFailureFor: nil)
            return
        }

        if let action = settings.captureActionConflicting(with: shortcut) {
            CopyableAlert.show(
                title: "Capture shortcut already in use",
                detail: "\(shortcut.displayString) is part of \(action.displayName) "
                    + "(\(settings.shortcut(for: action).displayString)). Choose another output key.")
            _ = registerShortcuts(reportingFailureFor: nil)
            return
        }

        if let control = RecordingControlAction.allCases.first(where: {
            settings.recordingShortcut(for: $0) == shortcut
        }) {
            CopyableAlert.show(
                title: "Recording shortcut already in use",
                detail: "\(shortcut.displayString) is \(control.displayName) during a recording. "
                    + "Choose another output key.")
            _ = registerShortcuts(reportingFailureFor: nil)
            return
        }

        let previous = settings.outputShortcut(for: destination.id)
        guard previous != shortcut else {
            _ = registerShortcuts(reportingFailureFor: nil)
            return
        }

        // Test the key while action hot keys are suspended. This catches combinations already
        // owned by macOS or another app before saving a route that could never be used.
        guard let testIdentifier = HotKeyCenter.shared.registerContextual(shortcut, handler: {}) else {
            _ = registerShortcuts(reportingFailureFor: nil)
            CopyableAlert.show(
                title: "That output shortcut is not available",
                detail: "macOS would not register \(shortcut.displayString). Another program is "
                    + "most likely already using it.")
            return
        }
        HotKeyCenter.shared.unregister(identifiers: [testIdentifier])

        settings.setOutputShortcut(shortcut, for: destination.id)
        guard registerShortcuts(reportingOutputFailureFor: destination.id) else {
            settings.setOutputShortcut(previous, for: destination.id)
            _ = registerShortcuts(reportingFailureFor: nil)
            CopyableAlert.show(
                title: "That output shortcut is not available",
                detail: "macOS would not register \(shortcut.displayString) for \(destination.name). "
                    + "The previous shortcut was restored.")
            return
        }
        guard persist(rollback: {
            self.settings.setOutputShortcut(previous, for: destination.id)
            _ = self.registerShortcuts(reportingFailureFor: nil)
        }) else { return }

        showSettingNotification(
            "\(destination.name) output shortcut set to \(shortcut.displayString)")
        rebuildMenu()
    }

    /// The app checks its own prefixes and second steps before asking Carbon whether another app
    /// owns either combination. Neither kind of refusal may leave an action with no working
    /// shortcut, so a failed registration always restores the previous binding.
    private func applyRebind(_ binding: ShortcutBinding, to action: CaptureAction) {
        let exportClash = settings.destinations.first { destination in
            guard let output = settings.outputShortcut(for: destination.id) else { return false }
            return binding.firstStroke == output || binding.secondStroke == output
        }
        if let exportClash,
           let output = settings.outputShortcut(for: exportClash.id) {
            CopyableAlert.show(
                title: "Output shortcut already in use",
                detail: "\(output.displayString) selects \(exportClash.name) for the next capture. "
                    + "Choose another capture shortcut.")
            _ = registerShortcuts(reportingFailureFor: nil)
            return
        }

        if let control = settings.recordingControlConflicting(with: binding) {
            CopyableAlert.show(
                title: "Recording shortcut already in use",
                detail: "\(settings.recordingShortcut(for: control).displayString) is "
                    + "\(control.displayName) during a recording. Choose another capture shortcut.")
            _ = registerShortcuts(reportingFailureFor: nil)
            return
        }

        if let clash = CaptureAction.allCases.first(where: {
            $0 != action && binding.conflictsInternally(with: settings.shortcut(for: $0))
        }) {
            let other = settings.shortcut(for: clash)
            let title: String
            let detail: String
            if binding.hasSameFirstStroke(as: other) {
                title = "Shortcut prefix already in use"
                detail = "\(binding.displayString) and \(other.displayString) begin with the same "
                    + "shortcut. Choose a different first press for \(action.displayName) or "
                    + "\(clash.displayName)."
            } else if binding.secondStroke == other.firstStroke {
                title = "Shortcut second press already in use"
                detail = "The second press in \(binding.displayString) already starts "
                    + "\(clash.displayName) (\(other.displayString)). Choose a different second press."
            } else {
                title = "Shortcut first press already in use"
                detail = "\(binding.firstStroke.displayString) already completes \(clash.displayName) "
                    + "(\(other.displayString)). Choose a different first press."
            }
            CopyableAlert.show(title: title, detail: detail)
            _ = registerShortcuts(reportingFailureFor: nil)
            return
        }

        let previous = settings.shortcut(for: action)
        guard previous != binding else {
            _ = registerShortcuts(reportingFailureFor: nil)
            return
        }

        settings.setShortcut(binding, for: action)
        guard registerShortcuts(reportingFailureFor: action) else {
            settings.setShortcut(previous, for: action)
            _ = registerShortcuts(reportingFailureFor: nil)
            CopyableAlert.show(
                title: "That shortcut is not available",
                detail: "macOS would not register \(binding.displayString) for \(action.displayName). "
                    + "Another program is most likely already using it. "
                    + "\(action.displayName) has been left on \(previous.displayString).")
            return
        }

        guard persist(rollback: {
            self.settings.setShortcut(previous, for: action)
            _ = self.registerShortcuts(reportingFailureFor: nil)
        }) else { return }

        showSettingNotification("\(action.displayName) shortcut set to \(binding.displayString)")
        rebuildMenu()
    }

    @objc private func resetShortcutsToDefaults() {
        guard clipRecorder == nil, !isCapturing else { return }
        // As on Windows: a reset replaces every capture shortcut at once, so it is confirmed first.
        guard CopyableAlert.confirm(
            title: "Reset Shortcuts",
            detail: "Reset all six capture shortcuts to the Huck’s Snip ’n’ Clip defaults?",
            confirmButtonTitle: "Reset",
            defaultToCancel: true) else { return }
        let previous = CaptureAction.allCases.map { ($0, settings.shortcut(for: $0)) }
        let previousExports = settings.destinations.map {
            ($0.id, settings.outputShortcut(for: $0.id))
        }
        let previousRecording = RecordingControlAction.allCases.map {
            ($0, settings.recordingShortcut(for: $0))
        }
        for action in CaptureAction.allCases {
            settings.setShortcut(ShortcutBinding.defaultBinding(for: action), for: action)
        }
        _ = settings.ensureOutputShortcuts()
        _ = settings.ensureRecordingShortcuts()
        guard registerShortcuts(reportingFailureFor: nil) else {
            for (action, binding) in previous { settings.setShortcut(binding, for: action) }
            for (control, stroke) in previousRecording { settings.setRecordingShortcut(stroke, for: control) }
            for (destinationID, shortcut) in previousExports {
                settings.setOutputShortcut(shortcut, for: destinationID)
            }
            _ = registerShortcuts(reportingFailureFor: nil)
            CopyableAlert.show(
                title: "The default shortcuts are not available",
                detail: "macOS could not register every default combination, most likely because "
                    + "another program is using one. Your previous shortcuts were restored.")
            rebuildMenu()
            return
        }
        guard persist(rollback: {
            for (action, binding) in previous { self.settings.setShortcut(binding, for: action) }
            for (control, stroke) in previousRecording {
                self.settings.setRecordingShortcut(stroke, for: control)
            }
            for (destinationID, shortcut) in previousExports {
                self.settings.setOutputShortcut(shortcut, for: destinationID)
            }
            _ = self.registerShortcuts(reportingFailureFor: nil)
        }) else { return }
        showSettingNotification("All shortcuts reset to their defaults")
        rebuildMenu()
    }

    @objc private func resetOutputShortcutsToDefaults() {
        guard clipRecorder == nil, !isCapturing else { return }
        let previous = settings.destinations.map {
            ($0.id, settings.outputShortcut(for: $0.id))
        }
        settings.resetOutputShortcutsToDefaults()
        guard registerShortcuts(reportingFailureFor: nil) else {
            for (destinationID, shortcut) in previous {
                settings.setOutputShortcut(shortcut, for: destinationID)
            }
            _ = registerShortcuts(reportingFailureFor: nil)
            CopyableAlert.show(
                title: "The default output shortcuts are not available",
                detail: "macOS could not register every default output combination. "
                    + "Your previous output shortcuts were restored.")
            rebuildMenu()
            return
        }
        guard persist(rollback: {
            for (destinationID, shortcut) in previous {
                self.settings.setOutputShortcut(shortcut, for: destinationID)
            }
            _ = self.registerShortcuts(reportingFailureFor: nil)
        }) else { return }

        showSettingNotification("Output shortcuts reset to their defaults")
        rebuildMenu()
    }

    // MARK: - Floating recording controller

    /// Shows the recording-only H when the setting allows it and the recording is guaranteed not to
    /// contain it. The guarantee comes from the capture filter chosen before this is ever shown, so
    /// there is no frame in which the controller could reach the recording.
    private func showFloatingController() {
        dismissFloatingController()
        guard settings.showFloatingRecordingControls, clipRecorder != nil, !clipStopRequested else {
            return
        }
        guard clipExcludesOwnWindows else {
            // A missing controller is safer than one burned into the clip. Say so after the take,
            // when a notice cannot land in the recording either.
            controllerHiddenForSafety = true
            return
        }

        let layoutKey = DisplayLayoutIdentity.current()
        controllerLayoutKey = layoutKey
        let size = FloatingControllerMetrics.windowSize(for: settings.floatingControllerSize)
        let known = settings.controllerLayouts.position(for: layoutKey)
        // A layout nobody has taught yet starts safely on the display being recorded. It is not
        // written down until the first right-drag teaches it.
        let desired = known ?? RecordingControllerPlacement.defaultOrigin(
            in: recordingScreen()?.visibleFrame ?? .zero,
            size: size)
        let placed = RecordingControllerPlacement.clamp(
            desired,
            size: size,
            visibleFrames: visibleFrames())

        let controller = FloatingRecordingController.show(
            origin: placed,
            size: settings.floatingControllerSize,
            opacityPercent: settings.floatingControllerOpacityPercent)
        controller.onPauseResume = { [weak self] in self?.performRecordingControl(.pauseResume) }
        controller.onStop = { [weak self] in self?.performRecordingControl(.stop) }
        controller.onMoved = { [weak self] in self?.floatingControllerMoved() }
        controller.onResized = { [weak self] in self?.floatingControllerResized() }
        controller.setPaused(clipPaused)
        floatingController = controller

        if let known, known != placed {
            // A remembered point that no longer fits is corrected in place for this layout only.
            storeControllerPosition(placed, for: layoutKey)
        } else if known != nil {
            settings.controllerLayouts.touch(layoutKey)
        }
    }

    private func dismissFloatingController() {
        floatingController?.dismiss()
        floatingController = nil
        controllerLayoutKey = nil
    }

    private func floatingControllerMoved() {
        guard let controller = floatingController, let layoutKey = controllerLayoutKey else { return }
        let placed = RecordingControllerPlacement.clamp(
            controller.frame.origin,
            size: controller.frame.size,
            visibleFrames: visibleFrames())
        if placed != controller.frame.origin { controller.move(to: placed) }
        storeControllerPosition(placed, for: layoutKey)
    }

    /// A resize is a deliberate choice, so it is kept - and so is where the resized controller
    /// ended up, because growing it can push it against a display edge.
    private func floatingControllerResized() {
        guard let controller = floatingController else { return }
        let placed = RecordingControllerPlacement.clamp(
            controller.frame.origin,
            size: controller.frame.size,
            visibleFrames: visibleFrames())
        if placed != controller.frame.origin { controller.move(to: placed) }
        settings.floatingControllerSize = controller.controllerSize
        if let layoutKey = controllerLayoutKey {
            settings.controllerLayouts.setPosition(placed, for: layoutKey)
        }
        // Remembering a control's size or place is never worth interrupting a recording.
        try? store.save(settings)
    }

    private func storeControllerPosition(_ position: CGPoint, for layoutKey: String) {
        settings.controllerLayouts.setPosition(position, for: layoutKey)
        try? store.save(settings)
    }

    /// Displays were added, removed, rearranged, rescaled or rotated during a take. A familiar
    /// layout gets its own remembered place; a new one gets a safe default on the recording
    /// display; either way the controller is kept fully reachable.
    @objc private func screenParametersDidChange(_ notification: Notification) {
        guard let controller = floatingController else { return }
        let layoutKey = DisplayLayoutIdentity.current()
        let size = controller.frame.size
        let frames = visibleFrames()

        if let known = settings.controllerLayouts.position(for: layoutKey) {
            let placed = RecordingControllerPlacement.clamp(known, size: size, visibleFrames: frames)
            controller.move(to: placed)
            if placed != known {
                storeControllerPosition(placed, for: layoutKey)
            } else {
                settings.controllerLayouts.touch(layoutKey)
            }
        } else if layoutKey != controllerLayoutKey {
            let origin = RecordingControllerPlacement.defaultOrigin(
                in: recordingScreen()?.visibleFrame ?? frames.first ?? .zero,
                size: size)
            controller.move(to: RecordingControllerPlacement.clamp(origin, size: size, visibleFrames: frames))
        } else {
            let placed = RecordingControllerPlacement.clamp(
                controller.frame.origin, size: size, visibleFrames: frames)
            if placed != controller.frame.origin { controller.move(to: placed) }
        }
        controllerLayoutKey = layoutKey
    }

    private func visibleFrames() -> [CGRect] {
        return NSScreen.screens.map(\.visibleFrame)
    }

    /// The display being recorded, or, if it has gone away, the one under the pointer.
    private func recordingScreen() -> NSScreen? {
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        if let clipTargetDisplayID,
           let screen = NSScreen.screens.first(where: {
               ($0.deviceDescription[key] as? NSNumber)?.uint32Value == clipTargetDisplayID
           }) {
            return screen
        }
        let mouse = NSEvent.mouseLocation
        return NSScreen.screens.first { $0.frame.contains(mouse) } ?? NSScreen.main ?? NSScreen.screens.first
    }

    // MARK: - Recording controls settings

    private func createRecordingControlsMenu(locked: Bool) -> NSMenuItem {
        // The two controller choices stay reachable during a take, as on Windows: they only change
        // how the controller looks, and the controller only exists during the take. Everything that
        // could change where a clip goes or how it is encoded stays locked.
        let item = NSMenuItem(title: "Recording Controls", action: nil, keyEquivalent: "")
        item.isEnabled = true

        let submenu = NSMenu()
        submenu.autoenablesItems = false
        let explanation = NSMenuItem(title: "During recording: pause or stop…", action: nil, keyEquivalent: "")
        explanation.isEnabled = false
        submenu.addItem(explanation)
        submenu.addItem(.separator())

        for control in RecordingControlAction.allCases {
            let choice = NSMenuItem(
                title: "\(control.displayName) — \(settings.recordingShortcut(for: control).displayString)",
                action: #selector(beginRecordingControlRebind(_:)),
                keyEquivalent: "")
            choice.target = self
            choice.representedObject = control.rawValue
            choice.isEnabled = !locked
            submenu.addItem(choice)
        }

        let reset = NSMenuItem(
            title: "Reset Recording Shortcuts to Defaults",
            action: #selector(resetRecordingShortcutsToDefaults),
            keyEquivalent: "")
        reset.target = self
        reset.isEnabled = !locked
        submenu.addItem(reset)
        submenu.addItem(.separator())

        let show = NSMenuItem(
            title: "Show Floating Recording Controls",
            action: #selector(toggleFloatingRecordingControls),
            keyEquivalent: "")
        show.target = self
        show.state = settings.showFloatingRecordingControls ? .on : .off
        show.isEnabled = true
        attachPersistentChoice(
            to: show,
            style: .checkbox,
            isSelected: { [weak self] in self?.settings.showFloatingRecordingControls ?? false })
        submenu.addItem(show)

        let current = FloatingControllerMetrics.normalizeOpacity(settings.floatingControllerOpacityPercent)
        let opacity = NSMenuItem(title: "Controller Opacity — \(current)%", action: nil, keyEquivalent: "")
        opacity.isEnabled = true
        controllerOpacitySummaryItem = opacity
        let opacityMenu = NSMenu()
        opacityMenu.autoenablesItems = false
        for percent in stride(from: 10, through: 100, by: 10) {
            let choice = NSMenuItem(
                title: "\(percent)%",
                action: #selector(selectControllerOpacity(_:)),
                keyEquivalent: "")
            choice.target = self
            choice.representedObject = percent
            choice.state = current == percent ? .on : .off
            choice.isEnabled = true
            attachPersistentChoice(
                to: choice,
                style: .radio,
                isSelected: { [weak self] in
                    guard let self else { return false }
                    return FloatingControllerMetrics.normalizeOpacity(
                        self.settings.floatingControllerOpacityPercent) == percent
                })
            opacityMenu.addItem(choice)
        }
        opacity.submenu = opacityMenu
        submenu.addItem(opacity)

        item.submenu = submenu
        return item
    }

    @objc private func toggleFloatingRecordingControls() {
        let previous = settings.showFloatingRecordingControls
        settings.showFloatingRecordingControls.toggle()
        guard persist(rollback: { self.settings.showFloatingRecordingControls = previous }) else { return }

        // Turning it off hides only the controller; shortcuts and menu controls stay. Its size,
        // opacity and every learned position are kept for when it comes back.
        if clipRecorder != nil, !clipStopRequested {
            if settings.showFloatingRecordingControls {
                showFloatingController()
            } else {
                dismissFloatingController()
            }
        }
        showSettingNotification(settings.showFloatingRecordingControls
            ? "Floating recording controls shown during recording"
            : "Floating recording controls hidden")
        refreshAfterSettingAction()
    }

    @objc private func selectControllerOpacity(_ sender: NSMenuItem) {
        guard let percent = sender.representedObject as? Int,
              FloatingControllerMetrics.opacityRange.contains(percent) else { return }
        let previous = settings.floatingControllerOpacityPercent
        settings.floatingControllerOpacityPercent = percent
        guard persist(rollback: { self.settings.floatingControllerOpacityPercent = previous }) else { return }
        floatingController?.setOpacityPercent(percent)
        showSettingNotification("Controller opacity set to \(percent)%")
        refreshAfterSettingAction()
    }

    @objc private func beginRecordingControlRebind(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String,
              let control = RecordingControlAction(rawValue: raw),
              clipRecorder == nil,
              !isCapturing else { return }

        // Carbon consumes registered global keys before a focused panel can see them.
        HotKeyCenter.shared.unregisterAll()
        OutputShortcutCaptureOverlay.present(
            title: "\(control.displayName) Shortcut",
            current: settings.recordingShortcut(for: control).displayString,
            instruction: "Press one modified key combination. It works only while a clip is recording."
        ) { [weak self] stroke in
            guard let self else { return }
            guard let stroke else {
                _ = self.registerShortcuts(reportingFailureFor: nil)
                return
            }
            self.applyRecordingShortcut(stroke, to: control)
        }
    }

    private func applyRecordingShortcut(_ stroke: ShortcutStroke, to control: RecordingControlAction) {
        if let conflict = settings.recordingControlConflict(stroke, for: control) {
            CopyableAlert.show(
                title: "Recording shortcut not available",
                detail: "\(conflict) \(control.displayName) has been left on "
                    + "\(settings.recordingShortcut(for: control).displayString).")
            _ = registerShortcuts(reportingFailureFor: nil)
            return
        }

        let previous = settings.recordingShortcut(for: control)
        guard previous != stroke else {
            _ = registerShortcuts(reportingFailureFor: nil)
            return
        }

        // Test the key now, while idle. It is only registered during a take, so a key another app
        // owns must be refused here rather than discovered mid-recording.
        guard let testIdentifier = HotKeyCenter.shared.registerContextual(stroke, handler: {}) else {
            _ = registerShortcuts(reportingFailureFor: nil)
            CopyableAlert.show(
                title: "That recording shortcut is not available",
                detail: "macOS would not register \(stroke.displayString). Another program is most "
                    + "likely already using it. \(control.displayName) has been left on "
                    + "\(previous.displayString).")
            return
        }
        HotKeyCenter.shared.unregister(identifiers: [testIdentifier])

        settings.setRecordingShortcut(stroke, for: control)
        _ = registerShortcuts(reportingFailureFor: nil)
        guard persist(rollback: { self.settings.setRecordingShortcut(previous, for: control) }) else {
            return
        }
        showSettingNotification("\(control.displayName) shortcut set to \(stroke.displayString)")
        rebuildMenu()
    }

    @objc private func resetRecordingShortcutsToDefaults() {
        guard clipRecorder == nil, !isCapturing else { return }
        let previous = RecordingControlAction.allCases.map { ($0, settings.recordingShortcut(for: $0)) }
        for control in RecordingControlAction.allCases {
            settings.setRecordingShortcut(control.defaultStroke, for: control)
        }
        if let clash = RecordingControlAction.allCases.lazy.compactMap({ control in
            self.settings.recordingControlConflict(control.defaultStroke, for: control)
        }).first {
            for (control, stroke) in previous { settings.setRecordingShortcut(stroke, for: control) }
            CopyableAlert.show(
                title: "The default recording shortcuts are not available",
                detail: "\(clash) Your previous recording shortcuts were kept.")
            return
        }
        guard persist(rollback: {
            for (control, stroke) in previous { self.settings.setRecordingShortcut(stroke, for: control) }
        }) else { return }
        showSettingNotification("Recording shortcuts reset to their defaults")
        rebuildMenu()
    }

    // MARK: - Recording settings

    private func createFilenameMenu(locked: Bool) -> NSMenuItem {
        let configuration = settings.captureFilename
        let summary = configuration.isDefault ? "Default" : "Custom"
        let item = NSMenuItem(title: "Filenames — \(summary)", action: nil, keyEquivalent: "")
        item.isEnabled = !locked

        let submenu = NSMenu()
        let now = Date()
        let counter = configuration.nextCounter
        let clipExtension = settings.recordingAudioMode == .computerAndMicrophone ? "mov" : "mp4"
        for (kind, title, pathExtension) in [
            (CaptureFilenameKind.snip, "Next Snip", "png"),
            (CaptureFilenameKind.clip, "Next Clip", clipExtension)
        ] {
            let preview = (try? CaptureFilenameTemplate.stem(
                configuration: configuration,
                kind: kind,
                capturedAt: now,
                counter: counter)) ?? "Invalid filename settings"
            let previewItem = NSMenuItem(
                title: "\(title) — \(preview).\(pathExtension)",
                action: nil,
                keyEquivalent: "")
            previewItem.isEnabled = false
            submenu.addItem(previewItem)
        }

        submenu.addItem(.separator())
        let configure = NSMenuItem(
            title: "Configure Filenames…",
            action: #selector(configureCaptureFilenames),
            keyEquivalent: "")
        configure.target = self
        submenu.addItem(configure)

        let reset = NSMenuItem(
            title: "Reset to Default",
            action: #selector(resetCaptureFilenames),
            keyEquivalent: "")
        reset.target = self
        reset.isEnabled = !configuration.isDefault
        submenu.addItem(reset)

        item.submenu = submenu
        return item
    }

    @objc private func configureCaptureFilenames() {
        let previous = settings.captureFilename
        guard let updated = FilenameSettingsPanel.present(configuration: previous) else { return }
        settings.captureFilename = updated
        guard persist(rollback: { self.settings.captureFilename = previous }) else { return }
        showSettingNotification("Capture filename settings saved")
        rebuildMenu()
    }

    @objc private func resetCaptureFilenames() {
        let previous = settings.captureFilename
        settings.captureFilename.label = ""
        settings.captureFilename.template = CaptureFilenameConfiguration.defaultTemplate
        guard persist(rollback: { self.settings.captureFilename = previous }) else { return }
        showSettingNotification("Capture filenames reset to default")
        rebuildMenu()
    }

    /// Reserves the counter in settings before a file is finalized. Gaps are harmless; reuse after
    /// a crash is not. The save service still claims the actual path with O_EXCL, so even a failed
    /// settings write cannot overwrite an existing capture.
    private func reserveCaptureCounter() -> Int {
        let previous = settings.captureFilename.nextCounter
        let counter = max(1, previous)
        settings.captureFilename.nextCounter = counter == Int.max ? 1 : counter + 1
        _ = persist(rollback: { self.settings.captureFilename.nextCounter = previous })
        return counter
    }

    private func createRecordingMenu() -> NSMenuItem {
        let item = NSMenuItem(
            title: recordingMenuTitle(),
            action: nil,
            keyEquivalent: "")
        item.isEnabled = clipRecorder == nil && !isCapturing
        recordingSummaryItem = item

        let submenu = NSMenu()
        let frameRate = NSMenuItem(title: "Frame Rate", action: nil, keyEquivalent: "")
        let frameRateMenu = NSMenu()
        for (title, value) in [
            ("15 FPS — Smallest / Lightest", 15),
            ("30 FPS — Balanced", 30),
            ("60 FPS — Smoothest", 60),
            ("Match Display Refresh", 0)
        ] {
            let choice = NSMenuItem(
                title: title,
                action: #selector(selectRecordingFrameRate(_:)),
                keyEquivalent: "")
            choice.target = self
            choice.representedObject = value
            choice.state = settings.recordingFrameRate == value ? .on : .off
            attachPersistentChoice(
                to: choice,
                style: .radio,
                isSelected: { [weak self] in self?.settings.recordingFrameRate == value })
            frameRateMenu.addItem(choice)
        }
        frameRate.submenu = frameRateMenu
        submenu.addItem(frameRate)

        let resolution = NSMenuItem(
            title: "Resolution — \(settings.recordingResolution.displayName)",
            action: nil,
            keyEquivalent: "")
        let resolutionMenu = NSMenu()
        for resolutionValue in [
            RecordingResolution.native,
            RecordingResolution.p1440,
            RecordingResolution.p1080,
            RecordingResolution.p720
        ] {
            let choice = NSMenuItem(
                title: resolutionValue.displayName,
                action: #selector(selectRecordingResolution(_:)),
                keyEquivalent: "")
            choice.target = self
            choice.representedObject = resolutionValue.rawValue
            choice.state = settings.recordingResolution == resolutionValue ? .on : .off
            attachPersistentChoice(
                to: choice,
                style: .radio,
                isSelected: { [weak self] in
                    self?.settings.recordingResolution == resolutionValue
                })
            resolutionMenu.addItem(choice)
        }
        resolution.submenu = resolutionMenu
        submenu.addItem(resolution)

        let quality = NSMenuItem(
            title: "Quality — \(settings.recordingQuality.displayName)",
            action: nil,
            keyEquivalent: "")
        let qualityMenu = NSMenu()
        for (title, qualityValue) in [
            ("Original — Fixed 8 Mbps", RecordingQuality.legacy),
            ("Balanced — Detail / File Size", RecordingQuality.balanced),
            ("High — More Motion Detail", RecordingQuality.high)
        ] {
            let choice = NSMenuItem(
                title: title,
                action: #selector(selectRecordingQuality(_:)),
                keyEquivalent: "")
            choice.target = self
            choice.representedObject = qualityValue.rawValue
            choice.state = settings.recordingQuality == qualityValue ? .on : .off
            attachPersistentChoice(
                to: choice,
                style: .radio,
                isSelected: { [weak self] in self?.settings.recordingQuality == qualityValue })
            qualityMenu.addItem(choice)
        }
        quality.submenu = qualityMenu
        submenu.addItem(quality)

        let cursor = NSMenuItem(
            title: "Show Cursor in Clips",
            action: #selector(toggleRecordingCursor),
            keyEquivalent: "")
        cursor.target = self
        cursor.state = settings.recordingShowsCursor ? .on : .off
        attachPersistentChoice(
            to: cursor,
            style: .checkbox,
            isSelected: { [weak self] in self?.settings.recordingShowsCursor ?? false })
        submenu.addItem(cursor)
        submenu.addItem(.separator())

        let audio = NSMenuItem(title: "Audio", action: nil, keyEquivalent: "")
        let audioMenu = NSMenu()
        for (title, mode) in [
            ("No Audio", RecordingAudioMode.off),
            ("System / App Audio", RecordingAudioMode.computer),
            ("Microphone", RecordingAudioMode.microphone),
            ("System / App + Microphone — Separate Tracks (.mov)", RecordingAudioMode.computerAndMicrophone)
        ] {
            let choice = NSMenuItem(
                title: title,
                action: #selector(selectRecordingAudioMode(_:)),
                keyEquivalent: "")
            choice.target = self
            choice.representedObject = mode.rawValue
            choice.state = settings.recordingAudioMode == mode ? .on : .off
            attachPersistentChoice(
                to: choice,
                style: .radio,
                isSelected: { [weak self] in self?.settings.recordingAudioMode == mode })
            audioMenu.addItem(choice)
        }
        audio.submenu = audioMenu
        submenu.addItem(audio)

        submenu.addItem(createMicrophoneDeviceMenu())

        submenu.addItem(createAudioGainMenu(
            title: "System / App Gain — \(settings.computerAudioGainPercent)%",
            source: "system",
            selectedGain: settings.computerAudioGainPercent))
        submenu.addItem(createAudioGainMenu(
            title: "Microphone Gain — \(settings.microphoneGainPercent)%",
            source: "microphone",
            selectedGain: settings.microphoneGainPercent))

        item.submenu = submenu
        return item
    }

    private func recordingMenuTitle() -> String {
        let frameRate = RecordingFrameRateSettings.summary(settings.recordingFrameRate)
        return "Recording — \(frameRate) + \(settings.recordingResolution.rawValue) + "
            + "\(settings.recordingQuality.displayName) + \(settings.recordingAudioMode.summary)"
    }

    @objc private func selectRecordingFrameRate(_ sender: NSMenuItem) {
        guard let frameRate = sender.representedObject as? Int,
              RecordingFrameRateSettings.isSupported(frameRate) else { return }
        let previous = settings.recordingFrameRate
        settings.recordingFrameRate = frameRate
        guard persist(rollback: { self.settings.recordingFrameRate = previous }) else { return }
        let selection = frameRate == 0 ? "Match Display Refresh" : "\(frameRate) FPS"
        showSettingNotification("Recording frame rate selected: \(selection)")
        refreshAfterSettingAction()
    }

    @objc private func selectRecordingResolution(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String,
              let resolution = RecordingResolution.decode(raw) else { return }
        let previous = settings.recordingResolution
        settings.recordingResolution = resolution
        guard persist(rollback: { self.settings.recordingResolution = previous }) else { return }
        showSettingNotification("Recording resolution selected: \(resolution.displayName)")
        refreshAfterSettingAction()
    }

    @objc private func selectRecordingQuality(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String,
              let quality = RecordingQuality.decode(raw) else { return }
        let previous = settings.recordingQuality
        settings.recordingQuality = quality
        guard persist(rollback: { self.settings.recordingQuality = previous }) else { return }
        showSettingNotification("Recording quality selected: \(quality.displayName)")
        refreshAfterSettingAction()
    }

    @objc private func toggleRecordingCursor() {
        let previous = settings.recordingShowsCursor
        settings.recordingShowsCursor.toggle()
        guard persist(rollback: { self.settings.recordingShowsCursor = previous }) else { return }
        let state = settings.recordingShowsCursor ? "shown" : "hidden"
        showSettingNotification("Cursor will be \(state) in new clips")
        refreshAfterSettingAction()
    }

    @objc private func selectRecordingAudioMode(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String,
              let mode = RecordingAudioMode(rawValue: raw) else { return }
        let previous = settings.recordingAudioMode
        settings.recordingAudioMode = mode
        guard persist(rollback: { self.settings.recordingAudioMode = previous }) else { return }
        showSettingNotification("Recording audio selected: \(mode.summary)")
        refreshAfterSettingAction()
    }

    private func createMicrophoneDeviceMenu() -> NSMenuItem {
        let devices = MicrophoneDeviceService.availableDevices()
        let defaultDevice = MicrophoneDeviceService.defaultDevice()
        let selection = MicrophoneDeviceService.resolve(
            savedDeviceID: settings.microphoneDeviceID,
            savedDeviceName: settings.microphoneDeviceName,
            devices: devices,
            defaultDevice: defaultDevice)
        let item = NSMenuItem(
            title: "Microphone — \(selection.displayName)",
            action: nil,
            keyEquivalent: "")
        microphoneSummaryItem = item
        let microphoneMenu = NSMenu()

        if selection.savedDeviceMissing {
            let missing = NSMenuItem(
                title: "\(settings.microphoneDeviceName ?? "Selected Microphone") — Not Connected",
                action: nil,
                keyEquivalent: "")
            missing.isEnabled = false
            missing.state = .mixed
            microphoneMenu.addItem(missing)
            microphoneMenu.addItem(.separator())
        }

        let defaultChoice = NSMenuItem(
            title: defaultDevice.map { "System Default — \($0.name)" } ?? "System Default",
            action: #selector(selectMicrophoneDevice(_:)),
            keyEquivalent: "")
        defaultChoice.target = self
        defaultChoice.representedObject = ""
        defaultChoice.state = settings.microphoneDeviceID == nil ? .on : .off
        attachPersistentChoice(
            to: defaultChoice,
            style: .radio,
            isSelected: { [weak self] in self?.settings.microphoneDeviceID == nil })
        microphoneMenu.addItem(defaultChoice)

        if !devices.isEmpty {
            microphoneMenu.addItem(.separator())
        }
        for device in devices {
            var title = device.name
            if device.id == defaultDevice?.id {
                title += " — Current Default"
            }
            let choice = NSMenuItem(
                title: title,
                action: #selector(selectMicrophoneDevice(_:)),
                keyEquivalent: "")
            choice.target = self
            choice.representedObject = device.id
            choice.state = settings.microphoneDeviceID == device.id ? .on : .off
            attachPersistentChoice(
                to: choice,
                style: .radio,
                isSelected: { [weak self] in self?.settings.microphoneDeviceID == device.id })
            microphoneMenu.addItem(choice)
        }

        item.submenu = microphoneMenu
        return item
    }

    @objc private func selectMicrophoneDevice(_ sender: NSMenuItem) {
        guard let deviceID = sender.representedObject as? String else { return }
        let previousID = settings.microphoneDeviceID
        let previousName = settings.microphoneDeviceName

        if deviceID.isEmpty {
            settings.microphoneDeviceID = nil
            settings.microphoneDeviceName = nil
        } else {
            guard let device = MicrophoneDeviceService.availableDevices()
                .first(where: { $0.id == deviceID }) else {
                CopyableAlert.show(
                    title: "Microphone is not connected",
                    detail: "That microphone is no longer available. Choose another device or System Default.")
                refreshAfterSettingAction()
                return
            }
            settings.microphoneDeviceID = device.id
            settings.microphoneDeviceName = device.name
        }

        guard persist(rollback: {
            self.settings.microphoneDeviceID = previousID
            self.settings.microphoneDeviceName = previousName
        }) else { return }

        let selected = MicrophoneDeviceService.selection(
            savedDeviceID: settings.microphoneDeviceID,
            savedDeviceName: settings.microphoneDeviceName)
        showSettingNotification("Microphone selected: \(selected.displayName)")
        refreshAfterSettingAction()
    }

    private func createAudioGainMenu(title: String, source: String, selectedGain: Int) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        if source == "system" {
            systemGainSummaryItem = item
        } else if source == "microphone" {
            microphoneGainSummaryItem = item
        }
        let gainMenu = NSMenu()
        for gain in SettingsStore.supportedAudioGainPercents {
            var title = gain == 0 ? "0% — Muted" : "\(gain)%"
            if gain == 100 { title += " — Normal" }
            let choice = NSMenuItem(
                title: title,
                action: #selector(selectRecordingAudioGain(_:)),
                keyEquivalent: "")
            choice.target = self
            choice.representedObject = "\(source)|\(gain)"
            choice.state = gain == selectedGain ? .on : .off
            attachPersistentChoice(
                to: choice,
                style: .radio,
                isSelected: { [weak self] in
                    guard let self else { return false }
                    return source == "system"
                        ? self.settings.computerAudioGainPercent == gain
                        : self.settings.microphoneGainPercent == gain
                })
            gainMenu.addItem(choice)
        }
        item.submenu = gainMenu
        return item
    }

    @objc private func selectRecordingAudioGain(_ sender: NSMenuItem) {
        guard let encoded = sender.representedObject as? String else { return }
        let parts = encoded.split(separator: "|", omittingEmptySubsequences: false)
        guard parts.count == 2,
              let gain = Int(parts[1]),
              SettingsStore.isSupportedAudioGainPercent(gain) else { return }

        if parts[0] == "system" {
            let previous = settings.computerAudioGainPercent
            settings.computerAudioGainPercent = gain
            guard persist(rollback: { self.settings.computerAudioGainPercent = previous }) else { return }
            showSettingNotification("System / App audio gain selected: \(gain)%")
        } else if parts[0] == "microphone" {
            let previous = settings.microphoneGainPercent
            settings.microphoneGainPercent = gain
            guard persist(rollback: { self.settings.microphoneGainPercent = previous }) else { return }
            showSettingNotification("Microphone gain selected: \(gain)%")
        } else {
            return
        }
        refreshAfterSettingAction()
    }

    // MARK: - Destinations

    @objc private func selectDestination(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String else { return }
        let previous = settings.activeDestinationId
        settings.activeDestinationId = id
        guard persist(rollback: {
            self.settings.activeDestinationId = previous
        }) else { return }

        showSettingNotification("Output selected: \(settings.activeDestination?.name ?? "output")")
    }

    @objc private func addDestination() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        panel.prompt = "Use Folder"
        panel.message = "Choose a folder for captures to land in."

        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK, let url = panel.url else { return }

        let previousId = settings.activeDestinationId
        var addedDestinationId: String?
        if let existing = settings.destination(withPath: url.path) {
            settings.activeDestinationId = existing.id
        } else {
            let defaultName = url.lastPathComponent.isEmpty ? url.path : url.lastPathComponent
            guard let name = promptForDestinationName(defaultName: defaultName) else { return }
            let destination = SettingsStore.createDestination(path: url.path, name: name)
            settings.destinations.append(destination)
            settings.activeDestinationId = destination.id
            addedDestinationId = destination.id
        }

        guard persist(rollback: {
            if let addedDestinationId {
                self.settings.destinations.removeAll { $0.id == addedDestinationId }
            }
            self.settings.activeDestinationId = previousId
        }) else { return }

        _ = registerShortcuts(reportingFailureFor: nil)
        showSettingNotification("Output selected: \(settings.activeDestination?.name ?? "output")")
    }

    @objc private func removeCurrentDestination() {
        guard settings.destinations.count > 1,
              let active = settings.activeDestination,
              let index = settings.destinations.firstIndex(where: { $0.id == active.id }) else { return }

        let detail = "Remove ‘\(active.name)’ from Huck’s Snip ’n’ Clip?\n\nThe folder and its files will not be deleted."
        guard CopyableAlert.confirm(
            title: "Remove Output",
            detail: detail,
            confirmButtonTitle: "Remove") else { return }

        let previousId = settings.activeDestinationId
        settings.destinations.remove(at: index)
        settings.activeDestinationId = settings.destinations.first?.id
        guard persist(rollback: {
            self.settings.destinations.insert(active, at: index)
            self.settings.activeDestinationId = previousId
        }) else { return }

        _ = registerShortcuts(reportingFailureFor: nil)
        showSettingNotification("Output removed: \(active.name)")
    }

    @objc private func revealDestination() {
        guard let path = settings.activeDestination?.path else { return }
        let url = URL(fileURLWithPath: path, isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        NSWorkspace.shared.open(url)
    }

    @objc private func openLastCapture() {
        guard let lastSavedPath,
              FileManager.default.fileExists(atPath: lastSavedPath) else { return }
        NSWorkspace.shared.open(URL(fileURLWithPath: lastSavedPath))
    }

    @objc private func toggleSettingChangeNotifications() {
        let previous = settings.routineNotificationsEnabled
        settings.routineNotificationsEnabled.toggle()
        persist(rollback: { self.settings.routineNotificationsEnabled = previous })
    }

    @objc private func toggleStartAtLogin() {
        do {
            switch LaunchAtLoginService.state {
            case .on:
                try LaunchAtLoginService.unregister()
                showSettingNotification("Start at Login turned off")
            case .off:
                try LaunchAtLoginService.register()
                if LaunchAtLoginService.state == .requiresApproval {
                    showLoginApprovalRequired()
                } else {
                    showSettingNotification("Start at Login turned on")
                }
            case .requiresApproval:
                LaunchAtLoginService.openSystemSettings()
            }
        } catch {
            CopyableAlert.show(
                title: "Start at Login could not be changed",
                detail: error.localizedDescription,
                extraButtonTitle: "Open Login Items Settings",
                extraAction: LaunchAtLoginService.openSystemSettings)
        }
        rebuildMenu()
    }

    private func showLoginApprovalRequired() {
        CopyableAlert.show(
            title: "Start at Login needs approval",
            detail: "macOS registered Huck’s Snip ’n’ Clip but requires you to allow it in System Settings > General > Login Items & Extensions.",
            extraButtonTitle: "Open Login Items Settings",
            extraAction: LaunchAtLoginService.openSystemSettings)
    }

    @objc private func checkForUpdates() {
        guard clipRecorder == nil, !isCapturing else { return }
        updateFlow?.start()
    }

    @objc private func openSettingsFile() {
        persist()
        NSWorkspace.shared.activateFileViewerSelecting([store.settingsURL])
    }

    @objc private func showAbout() {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
        CopyableAlert.show(
            title: "Huck’s Snip ’n’ Clip \(version)",
            detail: """
                By Quintin Huckaby.

                Snip Screen  \(settings.shortcut(for: .snipScreen).displayString)
                Snip Region  \(settings.shortcut(for: .snipRegion).displayString)
                Snip Window  \(settings.shortcut(for: .snipWindow).displayString)
                Clip Region  \(settings.shortcut(for: .clipRegion).displayString)
                Clip Window  \(settings.shortcut(for: .clipWindow).displayString)
                Clip Screen  \(settings.shortcut(for: .clipScreen).displayString)

                Installed: \(Bundle.main.bundlePath)
                Settings: \(store.settingsURL.path)
                Destination: \(settings.activeDestination?.path ?? "none")

                Clips record \(settings.recordingAudioMode.summary.lowercased()). While recording,
                the H meters show the selected audio input and total CPU usage. Combined recording
                keeps system/app and microphone audio on separate named tracks. Frame rate and
                quality are selectable independently. Future capture filenames, shortcuts, and
                Start at Login are available directly from the menu.
                """)
    }

    @objc private func openScreenRecordingSettings() {
        AppDelegate.openScreenRecordingPane()
    }

    /// macOS never prompts for Screen Recording the way it prompts for the microphone: the switch
    /// has to be turned on by hand, and the app has to be restarted afterwards.
    static func openScreenRecordingPane() {
        let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")
        if let url {
            NSWorkspace.shared.open(url)
        }
    }

    static func openMicrophonePane() {
        let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")
        if let url {
            NSWorkspace.shared.open(url)
        }
    }

    @objc private func quit() {
        if clipRecorder != nil {
            quitAfterClipFinishes = true
            stopClip()
        } else {
            NSApp.terminate(nil)
        }
    }

    @objc private func workspaceApplicationDidActivate(_ notification: Notification) {
        let application = notification.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
        rememberExternalApplication(application)
    }

    private func rememberExternalApplication(_ application: NSRunningApplication?) {
        guard let application,
              application.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return }
        lastExternalApplicationPID = application.processIdentifier
    }

    private func currentExternalApplicationPID() -> pid_t? {
        let current = NSWorkspace.shared.frontmostApplication
        if let current,
           current.processIdentifier != ProcessInfo.processInfo.processIdentifier {
            rememberExternalApplication(current)
        }
        return lastExternalApplicationPID
    }

    private func promptForDestinationName(defaultName: String) -> String? {
        while true {
            let alert = NSAlert()
            alert.messageText = "Name this output"
            alert.informativeText = "This name appears in the menu. The folder itself will not be renamed."
            alert.addButton(withTitle: "Add")
            alert.addButton(withTitle: "Cancel")

            let field = NSTextField(string: defaultName)
            field.frame = NSRect(x: 0, y: 0, width: 360, height: 24)
            field.selectText(nil)
            alert.accessoryView = field

            NSApp.activate(ignoringOtherApps: true)
            guard alert.runModal() == .alertFirstButtonReturn else { return nil }

            let name = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            if !name.isEmpty { return name }
            NSSound.beep()
        }
    }

    private func menuModifierMask(for binding: ShortcutBinding) -> NSEvent.ModifierFlags {
        var result: NSEvent.ModifierFlags = []
        if binding.modifiers & UInt32(controlKey) != 0 { result.insert(.control) }
        if binding.modifiers & UInt32(optionKey) != 0 { result.insert(.option) }
        if binding.modifiers & UInt32(shiftKey) != 0 { result.insert(.shift) }
        if binding.modifiers & UInt32(cmdKey) != 0 { result.insert(.command) }
        return result
    }

    private func restoreStatusToolTipAfterChord() {
        if let clipAction, clipRecorder != nil {
            statusItem?.button?.toolTip = "Recording \(clipAction.displayName.dropFirst(5)) — "
                + "\(settings.shortcut(for: clipAction).displayString) to stop"
        } else {
            statusItem?.button?.toolTip = "Huck’s Snip ’n’ Clip"
        }
    }

    private func showSettingNotification(_ message: String) {
        guard settings.routineNotificationsEnabled else { return }
        ToastPresenter.show(message)
    }

    @discardableResult
    private func persist(rollback: (() -> Void)? = nil) -> Bool {
        do {
            try store.save(settings)
            return true
        } catch {
            rollback?()
            CopyableAlert.show(
                title: "Settings could not be saved",
                detail: (error as? LocalizedError)?.errorDescription ?? error.localizedDescription)
            return false
        }
    }
}
