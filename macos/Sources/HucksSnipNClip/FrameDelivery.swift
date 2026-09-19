import Foundation

/// How much of the requested frame rate actually reached the finished file.
///
/// ScreenCaptureKit is change-driven and `minimumFrameInterval` is a maximum delivery rate, not a
/// promised schedule. The new-frame rate is therefore reported as an observed fact rather than
/// treated as a drop: 24 changing FPS can be correct for 24 FPS content, just as 2 can be correct
/// for a still screen. Only a complete frame AVAssetWriter actually refuses is labeled a drop.
struct FrameDelivery: Equatable {
    /// Below this, a stray dropped frame would turn ordinary recordings into warnings.
    static let strainThreshold = 0.02

    let requestedFramesPerSecond: Int
    /// Complete frames ScreenCaptureKit handed to the recorder.
    let offeredFrames: Int
    /// Offered frames the encoder was not ready to accept.
    let droppedFrames: Int
    /// Length of the finished, pause-adjusted timeline.
    let seconds: Double

    var writtenFrames: Int { max(0, offeredFrames - droppedFrames) }

    /// Share of offered frames the encoder could not take. This is the "the Mac is over-taxed"
    /// number, and it stays at 0 for a still screen no matter how few frames arrive.
    var droppedFraction: Double {
        offeredFrames <= 0 ? 0 : Double(droppedFrames) / Double(offeredFrames)
    }

    /// Frames per second actually written to the file. A still screen lowers this honestly without
    /// anything being wrong, so it is reported beside `droppedFraction` rather than instead of it.
    var achievedFramesPerSecond: Double {
        seconds <= 0 ? 0 : Double(writtenFrames) / seconds
    }

    var isStrained: Bool { droppedFraction >= FrameDelivery.strainThreshold }

    /// Always shown after a successful save. "New" is deliberate: this is the rate of changed
    /// frames ScreenCaptureKit supplied, not a claim that unchanged or upstream frames were lost.
    var deliverySummary: String {
        return String(
            format: "%.0f new FPS / %d max",
            achievedFramesPerSecond,
            requestedFramesPerSecond)
    }

    /// Appended to the clip-saved toast, and only when the encoder actually lost frames. A clip
    /// that merely recorded a still screen says nothing, because nothing went wrong.
    var strainNotice: String? {
        guard isStrained else { return nil }
        return String(format: "encoder dropped %.0f%%", droppedFraction * 100)
    }

    /// The live menu-bar reading. Kept separate from the saved-clip notice so the tooltip can stay
    /// quiet at rest and still name the cause the moment frames start being lost.
    static func tooltipFragment(droppedFraction: Double) -> String? {
        guard droppedFraction >= strainThreshold else { return nil }
        return String(format: "dropping %.0f%%", droppedFraction * 100)
    }
}
