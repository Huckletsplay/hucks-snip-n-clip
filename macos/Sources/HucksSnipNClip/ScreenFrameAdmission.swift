import ScreenCaptureKit

/// ScreenCaptureKit emits lifecycle and idle callbacks through the same output as real video
/// frames. Only a complete callback backed by an image buffer is safe to give AVAssetWriter.
enum ScreenFrameAdmission {
    static func shouldEncode(statusRawValue: Int?, hasImageBuffer: Bool) -> Bool {
        guard let statusRawValue,
              let status = SCFrameStatus(rawValue: statusRawValue) else { return false }
        return status == .complete && hasImageBuffer
    }
}
