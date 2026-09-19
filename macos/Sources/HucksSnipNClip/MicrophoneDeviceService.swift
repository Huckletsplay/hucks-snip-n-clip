import AVFoundation
import Foundation

struct MicrophoneDeviceDescriptor: Equatable {
    let id: String
    let name: String
}

struct ResolvedMicrophoneSelection: Equatable {
    let captureDeviceID: String?
    let displayName: String
    let savedDeviceMissing: Bool
}

enum MicrophoneDeviceService {
    static func availableDevices() -> [MicrophoneDeviceDescriptor] {
        let discovery = AVCaptureDevice.DiscoverySession(
            deviceTypes: [.microphone],
            mediaType: .audio,
            position: .unspecified)
        var devices = discovery.devices.map(descriptor)

        // Some virtual/default routes can be returned by default(for:) without appearing in the
        // discovery session. Keep them selectable rather than hiding the route macOS is using.
        if let currentDefault = defaultDevice(),
           !devices.contains(where: { $0.id == currentDefault.id }) {
            devices.append(currentDefault)
        }

        var seen: Set<String> = []
        return devices
            .filter { seen.insert($0.id).inserted }
            .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    static func defaultDevice() -> MicrophoneDeviceDescriptor? {
        return AVCaptureDevice.default(for: .audio).map(descriptor)
    }

    static func resolve(
        savedDeviceID: String?,
        savedDeviceName: String?,
        devices: [MicrophoneDeviceDescriptor],
        defaultDevice: MicrophoneDeviceDescriptor?
    ) -> ResolvedMicrophoneSelection {
        guard let savedDeviceID, !savedDeviceID.isEmpty else {
            return ResolvedMicrophoneSelection(
                captureDeviceID: nil,
                displayName: systemDefaultName(defaultDevice),
                savedDeviceMissing: false)
        }

        if let selected = devices.first(where: { $0.id == savedDeviceID }) {
            return ResolvedMicrophoneSelection(
                captureDeviceID: selected.id,
                displayName: selected.name,
                savedDeviceMissing: false)
        }

        let name = savedDeviceName.flatMap { $0.isEmpty ? nil : $0 } ?? "Selected Microphone"
        return ResolvedMicrophoneSelection(
            captureDeviceID: nil,
            displayName: "\(name) — Not Connected; Using System Default",
            savedDeviceMissing: true)
    }

    static func selection(
        savedDeviceID: String?,
        savedDeviceName: String?
    ) -> ResolvedMicrophoneSelection {
        return resolve(
            savedDeviceID: savedDeviceID,
            savedDeviceName: savedDeviceName,
            devices: availableDevices(),
            defaultDevice: defaultDevice())
    }

    private static func systemDefaultName(_ device: MicrophoneDeviceDescriptor?) -> String {
        guard let device else { return "System Default" }
        return "System Default — \(device.name)"
    }

    private static func descriptor(_ device: AVCaptureDevice) -> MicrophoneDeviceDescriptor {
        return MicrophoneDeviceDescriptor(id: device.uniqueID, name: device.localizedName)
    }
}
