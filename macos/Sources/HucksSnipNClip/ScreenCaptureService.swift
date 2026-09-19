import AppKit
import CoreGraphics
import Foundation
import ScreenCaptureKit

struct ScreenDisplayTarget {
    let display: SCDisplay
    let scale: CGFloat
    let excludedApplications: [SCRunningApplication]

    var excludesOwnApplication: Bool {
        excludedApplications.contains { $0.processID == ProcessInfo.processInfo.processIdentifier }
    }

    var pixelWidth: Int {
        max(2, Int((CGFloat(display.width) * scale).rounded()) / 2 * 2)
    }

    var pixelHeight: Int {
        max(2, Int((CGFloat(display.height) * scale).rounded()) / 2 * 2)
    }
}

/// Everything the recorder needs after the user has chosen a display, region, or window. The
/// filter decides which pixels and audio ScreenCaptureKit supplies; sourceRect crops a display
/// target, while a direct-window filter needs no crop.
struct ScreenClipTarget {
    let displayID: CGDirectDisplayID
    let contentFilter: SCContentFilter
    let sourceRect: CGRect?
    let displaySpaceRegion: CGRect?
    let pixelWidth: Int
    let pixelHeight: Int
    let ignoresSingleWindowShadow: Bool
    /// True only when the filter itself keeps this app's own windows out of the recording: a
    /// direct-window filter, or a display filter that names this process as excluded. Anything the
    /// app shows during a take - the floating controller above all - may appear only when true.
    let excludesOwnWindows: Bool
}

/// Takes still captures through ScreenCaptureKit, the only supported route on modern macOS.
///
/// Every rectangle crossing this type is in display space: the global, top-left-origin
/// coordinate system `CGDisplayBounds` uses, not AppKit's bottom-left-origin screen space.
/// `displaySpacePoint(from:)` is the one place the two meet.
final class ScreenCaptureService {

    /// Captures the frontmost ordinary application window directly. Unlike the first Windows
    /// backend, ScreenCaptureKit can isolate the window instead of copying whatever pixels happen
    /// to overlap its screen rectangle.
    @MainActor
    func captureWindow(forApplicationPID targetPID: pid_t?) async throws -> CGImage {
        let resolved = try await resolveWindow(forApplicationPID: targetPID)
        let window = resolved.window
        let scale = backingScale(containing: window.frame, displays: resolved.displays)
        let configuration = SCStreamConfiguration()
        configuration.width = ScreenCaptureService.evenPixelCount(window.frame.width, scale: scale)
        configuration.height = ScreenCaptureService.evenPixelCount(window.frame.height, scale: scale)
        configuration.showsCursor = false
        configuration.captureResolution = .best
        configuration.ignoreShadowsSingleWindow = true

        let filter = SCContentFilter(desktopIndependentWindow: window)
        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
    }

    @MainActor
    private func resolveWindow(
        forApplicationPID targetPID: pid_t?
    ) async throws -> (window: SCWindow, displays: [SCDisplay]) {
        let ownPID = ProcessInfo.processInfo.processIdentifier
        guard let targetPID, targetPID != ownPID else {
            throw ScreenCaptureError.noWindow
        }

        let content: SCShareableContent
        do {
            content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        } catch {
            throw ScreenCaptureError.permissionDenied(underlying: error)
        }

        let windowPairs: [(CGWindowID, SCWindow)] = content.windows.compactMap { window in
            guard window.isOnScreen,
                  window.windowLayer == 0,
                  window.frame.width >= 2,
                  window.frame.height >= 2,
                  let application = window.owningApplication,
                  application.processID == targetPID else { return nil }
            return (window.windowID, window)
        }
        let ordinaryWindows: [CGWindowID: SCWindow] = Dictionary(uniqueKeysWithValues: windowPairs)

        // Core Graphics documents this list as front-to-back. ScreenCaptureKit's windows do not
        // promise a z-order, so intersect the ordered IDs with the windows it can actually share.
        let window = ScreenCaptureService.orderedWindowIDs(forApplicationPID: targetPID)
            .lazy
            .compactMap { ordinaryWindows[$0] }
            .first

        guard let window else {
            throw ScreenCaptureError.noWindow
        }
        return (window, content.displays)
    }

    private static func orderedWindowIDs(forApplicationPID targetPID: pid_t) -> [CGWindowID] {
        guard let entries = CGWindowListCopyWindowInfo(
            [.optionOnScreenOnly, .excludeDesktopElements],
            kCGNullWindowID) as? [[String: Any]] else { return [] }

        return entries.compactMap { entry in
            guard let owner = entry[kCGWindowOwnerPID as String] as? NSNumber,
                  owner.int32Value == targetPID,
                  let layer = entry[kCGWindowLayer as String] as? NSNumber,
                  layer.intValue == 0,
                  let number = entry[kCGWindowNumber as String] as? NSNumber else { return nil }
            return CGWindowID(number.uint32Value)
        }
    }

    func captureDisplay(containing point: CGPoint) async throws -> CGImage {
        let target = try await resolveDisplayTarget(containing: point)
        let display = target.display
        let configuration = SCStreamConfiguration()
        configuration.width = target.pixelWidth
        configuration.height = target.pixelHeight
        configuration.showsCursor = false
        configuration.captureResolution = .best

        let filter = SCContentFilter(display: display, excludingWindows: [])
        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
    }

    func captureRegion(_ region: CGRect) async throws -> CGImage {
        guard region.width >= 1, region.height >= 1 else {
            throw ScreenCaptureError.regionTooSmall
        }

        let target = try await resolveDisplayTarget(containing: CGPoint(x: region.midX, y: region.midY))
        let display = target.display
        let bounds = CGDisplayBounds(display.displayID)
        let clipped = region.intersection(bounds)
        guard !clipped.isNull, clipped.width >= 1, clipped.height >= 1 else {
            throw ScreenCaptureError.regionTooSmall
        }

        let configuration = SCStreamConfiguration()
        configuration.sourceRect = CGRect(
            x: clipped.minX - bounds.minX,
            y: clipped.minY - bounds.minY,
            width: clipped.width,
            height: clipped.height)
        configuration.width = Int((clipped.width * target.scale).rounded())
        configuration.height = Int((clipped.height * target.scale).rounded())
        configuration.showsCursor = false
        configuration.captureResolution = .best

        let filter = SCContentFilter(display: display, excludingWindows: [])
        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
    }

    func resolveDisplayClipTarget(containing point: CGPoint) async throws -> ScreenClipTarget {
        let target = try await resolveDisplayTarget(containing: point, waitingForOwnApplication: true)
        return ScreenClipTarget(
            displayID: target.display.displayID,
            contentFilter: SCContentFilter(
                display: target.display,
                excludingApplications: target.excludedApplications,
                exceptingWindows: []),
            sourceRect: nil,
            displaySpaceRegion: nil,
            pixelWidth: target.pixelWidth,
            pixelHeight: target.pixelHeight,
            ignoresSingleWindowShadow: false,
            excludesOwnWindows: target.excludesOwnApplication)
    }

    func resolveRegionClipTarget(_ region: CGRect) async throws -> ScreenClipTarget {
        guard region.width >= 2, region.height >= 2 else {
            throw ScreenCaptureError.regionTooSmall
        }

        let target = try await resolveDisplayTarget(
            containing: CGPoint(x: region.midX, y: region.midY),
            waitingForOwnApplication: true)
        let bounds = CGDisplayBounds(target.display.displayID)
        let clipped = region.intersection(bounds)
        guard !clipped.isNull, clipped.width >= 2, clipped.height >= 2 else {
            throw ScreenCaptureError.regionTooSmall
        }

        return ScreenClipTarget(
            displayID: target.display.displayID,
            contentFilter: SCContentFilter(
                display: target.display,
                excludingApplications: target.excludedApplications,
                exceptingWindows: []),
            sourceRect: CGRect(
                x: clipped.minX - bounds.minX,
                y: clipped.minY - bounds.minY,
                width: clipped.width,
                height: clipped.height),
            displaySpaceRegion: clipped,
            pixelWidth: ScreenCaptureService.evenPixelCount(clipped.width, scale: target.scale),
            pixelHeight: ScreenCaptureService.evenPixelCount(clipped.height, scale: target.scale),
            ignoresSingleWindowShadow: false,
            excludesOwnWindows: target.excludesOwnApplication)
    }

    @MainActor
    func resolveWindowClipTarget(forApplicationPID targetPID: pid_t?) async throws -> ScreenClipTarget {
        let resolved = try await resolveWindow(forApplicationPID: targetPID)
        let window = resolved.window
        guard let display = display(containing: window.frame, displays: resolved.displays) else {
            throw ScreenCaptureError.noDisplay
        }
        let scale = ScreenCaptureService.backingScale(for: display.displayID)

        return ScreenClipTarget(
            displayID: display.displayID,
            contentFilter: SCContentFilter(desktopIndependentWindow: window),
            sourceRect: nil,
            displaySpaceRegion: nil,
            pixelWidth: ScreenCaptureService.evenPixelCount(window.frame.width, scale: scale),
            pixelHeight: ScreenCaptureService.evenPixelCount(window.frame.height, scale: scale),
            ignoresSingleWindowShadow: true,
            excludesOwnWindows: true)
    }

    /// With `waitingForOwnApplication`, briefly re-reads shareable content until this app is listed,
    /// so a recording filter can exclude it. A window this app has just ordered front can take a
    /// moment to reach ScreenCaptureKit. If it never arrives the target says so and the caller
    /// keeps its own windows off screen.
    func resolveDisplayTarget(
        containing point: CGPoint,
        waitingForOwnApplication: Bool = false
    ) async throws -> ScreenDisplayTarget {
        var content: SCShareableContent
        do {
            content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            let ownPID = ProcessInfo.processInfo.processIdentifier
            var attempts = 0
            while waitingForOwnApplication, attempts < 10,
                  !content.applications.contains(where: { $0.processID == ownPID }) {
                attempts += 1
                try await Task.sleep(nanoseconds: 100_000_000)
                content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            }
        } catch {
            throw ScreenCaptureError.permissionDenied(underlying: error)
        }

        guard let display = content.displays.first(where: { CGDisplayBounds($0.displayID).contains(point) })
            ?? content.displays.first else {
            throw ScreenCaptureError.noDisplay
        }

        return ScreenDisplayTarget(
            display: display,
            scale: ScreenCaptureService.backingScale(for: display.displayID),
            excludedApplications: content.applications.filter {
                $0.processID == ProcessInfo.processInfo.processIdentifier
            })
    }

    static func backingScale(for displayID: CGDirectDisplayID) -> CGFloat {
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        let screen = NSScreen.screens.first { ($0.deviceDescription[key] as? NSNumber)?.uint32Value == displayID }
        return screen?.backingScaleFactor ?? 1
    }

    /// Match Display targets the screen's advertised maximum refresh rate. This is a capture
    /// target, not vertical-blank synchronization; adaptive-refresh displays may still vary.
    @MainActor
    static func refreshRate(for displayID: CGDirectDisplayID) -> Int {
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        if let screen = NSScreen.screens.first(where: {
            ($0.deviceDescription[key] as? NSNumber)?.uint32Value == displayID
        }), screen.maximumFramesPerSecond > 0 {
            return min(240, screen.maximumFramesPerSecond)
        }

        if let rate = CGDisplayCopyDisplayMode(displayID)?.refreshRate, rate > 0 {
            return min(240, max(1, Int(rate.rounded())))
        }
        return 60
    }

    private func backingScale(containing frame: CGRect, displays: [SCDisplay]) -> CGFloat {
        let display = display(containing: frame, displays: displays)
        return display.map { ScreenCaptureService.backingScale(for: $0.displayID) } ?? 1
    }

    private func display(containing frame: CGRect, displays: [SCDisplay]) -> SCDisplay? {
        let point = CGPoint(x: frame.midX, y: frame.midY)
        return displays.first { CGDisplayBounds($0.displayID).contains(point) } ?? displays.first
    }

    static func evenPixelCount(_ points: CGFloat, scale: CGFloat) -> Int {
        max(2, Int((points * scale).rounded()) / 2 * 2)
    }

    /// AppKit reports the mouse in bottom-left-origin screen space; captures need display space.
    @MainActor
    static func displaySpacePoint(from screenPoint: NSPoint) -> CGPoint {
        guard let primary = NSScreen.screens.first else {
            return CGPoint(x: screenPoint.x, y: screenPoint.y)
        }

        return CGPoint(x: screenPoint.x, y: primary.frame.maxY - screenPoint.y)
    }

    @MainActor
    static func currentMouseInDisplaySpace() -> CGPoint {
        return displaySpacePoint(from: NSEvent.mouseLocation)
    }

    /// True when the app already holds Screen Recording permission. When it does not, this asks
    /// for it once; macOS then requires a relaunch before the grant takes effect.
    static func ensurePermission() -> Bool {
        if CGPreflightScreenCaptureAccess() {
            return true
        }

        return CGRequestScreenCaptureAccess()
    }
}

enum ScreenCaptureError: LocalizedError {
    case noDisplay
    case noWindow
    case regionTooSmall
    case permissionDenied(underlying: Error)

    var errorDescription: String? {
        switch self {
        case .noDisplay:
            return "No display was available to capture."
        case .noWindow:
            return "No visible application window was available to capture."
        case .regionTooSmall:
            return "The selected region was too small to capture."
        case .permissionDenied(let underlying):
            return "macOS did not allow the screen to be read.\n\n"
                + "Open System Settings > Privacy & Security > Screen & System Audio Recording, "
                + "switch on Huck’s Snip ’n’ Clip, then quit and reopen it.\n\n"
                + "Details: \(underlying.localizedDescription)"
        }
    }
}
