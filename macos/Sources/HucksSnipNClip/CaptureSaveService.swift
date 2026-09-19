import CoreGraphics
import Darwin
import Foundation
import ImageIO
import UniformTypeIdentifiers

struct CaptureSaveResult {
    var savedPath: String
    var usedRecovery: Bool
    /// Why the destination was skipped, when the capture landed in the recovery folder instead.
    var destinationError: Error?
}

/// Writes a snip to its destination, and to a recovery folder when the destination cannot be
/// written. A capture is never lost because a routed folder went missing. Mirrors the Windows
/// `CaptureSaveService`, including the default `HucksSnipNClip_Snip_<timestamp>.png` naming.
final class CaptureSaveService {
    private let recoveryDirectory: URL

    init(recoveryDirectory: URL) {
        self.recoveryDirectory = recoveryDirectory
    }

    static func createDefault() -> CaptureSaveService {
        return CaptureSaveService(recoveryDirectory: SettingsStore.recoveryDirectory)
    }

    func savePng(
        _ image: CGImage,
        destinationDirectory: String?,
        filenameConfiguration: CaptureFilenameConfiguration = CaptureFilenameConfiguration(),
        capturedAt: Date = Date(),
        counter: Int = 1
    ) throws -> CaptureSaveResult {
        var destinationError: Error?

        if let directory = destinationDirectory, !directory.trimmingCharacters(in: .whitespaces).isEmpty {
            do {
                let path = try write(
                    image,
                    into: URL(fileURLWithPath: directory, isDirectory: true),
                    filenameConfiguration: filenameConfiguration,
                    capturedAt: capturedAt,
                    counter: counter)
                return CaptureSaveResult(savedPath: path, usedRecovery: false, destinationError: nil)
            } catch {
                destinationError = error
            }
        } else {
            destinationError = SettingsError.noDestination
        }

        do {
            let path = try write(
                image,
                into: recoveryDirectory,
                filenameConfiguration: filenameConfiguration,
                capturedAt: capturedAt,
                counter: counter)
            return CaptureSaveResult(savedPath: path, usedRecovery: true, destinationError: destinationError)
        } catch {
            throw CaptureSaveError.bothFailed(destination: destinationError, recovery: error)
        }
    }

    /// Copies a finalized native working MP4 or MOV into the selected destination through a temporary
    /// sibling, then renames it into place. Copy-before-rename is required because Application
    /// Support and a routed destination may live on different volumes.
    func routeCompletedClip(
        _ workingURL: URL,
        destinationDirectory: String?,
        filenameConfiguration: CaptureFilenameConfiguration = CaptureFilenameConfiguration(),
        capturedAt: Date = Date(),
        counter: Int = 1
    ) throws -> CaptureSaveResult {
        guard FileManager.default.fileExists(atPath: workingURL.path) else {
            throw CaptureSaveError.workingClipMissing
        }

        var destinationError: Error?
        if let directory = destinationDirectory, !directory.trimmingCharacters(in: .whitespaces).isEmpty {
            do {
                let path = try routeClip(
                    workingURL,
                    into: URL(fileURLWithPath: directory, isDirectory: true),
                    filenameConfiguration: filenameConfiguration,
                    capturedAt: capturedAt,
                    counter: counter)
                return CaptureSaveResult(savedPath: path, usedRecovery: false, destinationError: nil)
            } catch {
                destinationError = error
            }
        } else {
            destinationError = SettingsError.noDestination
        }

        do {
            let path = try routeClip(
                workingURL,
                into: recoveryDirectory,
                filenameConfiguration: filenameConfiguration,
                capturedAt: capturedAt,
                counter: counter)
            return CaptureSaveResult(savedPath: path, usedRecovery: true, destinationError: destinationError)
        } catch {
            throw CaptureSaveError.bothFailed(destination: destinationError, recovery: error)
        }
    }

    private func write(
        _ image: CGImage,
        into directory: URL,
        filenameConfiguration: CaptureFilenameConfiguration,
        capturedAt: Date,
        counter: Int
    ) throws -> String {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

        let stem = try CaptureFilenameTemplate.stem(
            configuration: filenameConfiguration,
            kind: .snip,
            capturedAt: capturedAt,
            counter: counter)
        let temporaryURL = temporaryURL(in: directory, pathExtension: "png")

        guard let destination = CGImageDestinationCreateWithURL(
            temporaryURL as CFURL,
            UTType.png.identifier as CFString,
            1,
            nil) else {
            throw CaptureSaveError.encoderUnavailable
        }

        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else {
            try? FileManager.default.removeItem(at: temporaryURL)
            throw CaptureSaveError.encodeFailed
        }

        do {
            let finalURL = try installWithoutOverwrite(
                temporaryURL,
                in: directory,
                stem: stem,
                pathExtension: "png")
            return finalURL.path
        } catch {
            try? FileManager.default.removeItem(at: temporaryURL)
            throw error
        }
    }

    private func routeClip(
        _ workingURL: URL,
        into directory: URL,
        filenameConfiguration: CaptureFilenameConfiguration,
        capturedAt: Date,
        counter: Int
    ) throws -> String {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

        let sourceExtension = workingURL.pathExtension.lowercased()
        let finalExtension = sourceExtension == "mov" ? "mov" : "mp4"
        let stem = try CaptureFilenameTemplate.stem(
            configuration: filenameConfiguration,
            kind: .clip,
            capturedAt: capturedAt,
            counter: counter)
        let temporaryURL = temporaryURL(in: directory, pathExtension: finalExtension)

        do {
            try FileManager.default.copyItem(at: workingURL, to: temporaryURL)
            let finalURL = try installWithoutOverwrite(
                temporaryURL,
                in: directory,
                stem: stem,
                pathExtension: finalExtension)
            try FileManager.default.removeItem(at: workingURL)
            return finalURL.path
        } catch {
            try? FileManager.default.removeItem(at: temporaryURL)
            throw error
        }
    }

    private func temporaryURL(in directory: URL, pathExtension: String) -> URL {
        directory.appendingPathComponent(
            ".HucksSnipNClip-\(UUID().uuidString).\(pathExtension).tmp")
    }

    /// Claims the final name with O_EXCL before renaming the complete sibling over that reservation.
    /// Every writer therefore sees either "I own this name" or EEXIST; no check-then-write window
    /// can overwrite an older capture, including on the hub's exFAT drive.
    private func installWithoutOverwrite(
        _ temporaryURL: URL,
        in directory: URL,
        stem: String,
        pathExtension: String
    ) throws -> URL {
        for suffix in 0..<10_000 {
            let candidateStem = suffix == 0 ? stem : String(format: "%@_%02d", stem, suffix)
            let candidate = directory.appendingPathComponent(candidateStem + "." + pathExtension)
            let descriptor = candidate.path.withCString {
                Darwin.open($0, O_WRONLY | O_CREAT | O_EXCL, mode_t(S_IRUSR | S_IWUSR))
            }
            if descriptor < 0 {
                if errno == EEXIST { continue }
                throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
            }
            Darwin.close(descriptor)

            let renameResult = temporaryURL.path.withCString { oldPath in
                candidate.path.withCString { newPath in Darwin.rename(oldPath, newPath) }
            }
            if renameResult == 0 { return candidate }

            let renameError = errno
            candidate.path.withCString { _ = Darwin.unlink($0) }
            throw POSIXError(POSIXErrorCode(rawValue: renameError) ?? .EIO)
        }

        throw CaptureSaveError.tooManyFilenameCollisions
    }
}

enum CaptureSaveError: LocalizedError {
    case encoderUnavailable
    case encodeFailed
    case workingClipMissing
    case tooManyFilenameCollisions
    case bothFailed(destination: Error?, recovery: Error)

    var errorDescription: String? {
        switch self {
        case .encoderUnavailable:
            return "The PNG encoder could not be created."
        case .encodeFailed:
            return "The snip could not be encoded as a PNG."
        case .workingClipMissing:
            return "The finalized working video could not be found."
        case .tooManyFilenameCollisions:
            return "A unique capture filename could not be reserved. Change the filename template or destination."
        case .bothFailed(let destination, let recovery):
            let destinationText = destination.map { $0.localizedDescription } ?? "no destination was set"
            return "The snip could not be saved to its destination or the recovery folder.\n\n"
                + "Destination: \(destinationText)\n"
                + "Recovery: \(recovery.localizedDescription)"
        }
    }
}
