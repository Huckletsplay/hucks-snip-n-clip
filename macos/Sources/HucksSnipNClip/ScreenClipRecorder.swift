import AVFoundation
import AudioToolbox
import CoreMedia
import CoreVideo
import Foundation
import ScreenCaptureKit

/// A ScreenCaptureKit recording slice. The selected audio source supplies the live tray meter and,
/// when enabled, AVAssetWriter encodes it as AAC beside the H.264 picture. The recording is finalized in
/// Application Support before CaptureSaveService copies it into a user-selected destination, so
/// an interrupted copy to an external drive can never expose a half-written final file.
final class ScreenClipRecorder: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    private let workingDirectory: URL
    private let framesPerSecond: Int
    private let audioMode: RecordingAudioMode
    private let recordingQuality: RecordingQuality
    private let recordingResolution: RecordingResolution
    private let showsCursor: Bool
    private let systemAudioGainPercent: Int
    private let microphoneGainPercent: Int
    private let microphoneDeviceID: String?
    private let sampleQueue = DispatchQueue(label: "com.projectplayground.huckssnipnclip.video")

    private var stream: SCStream?
    private var writer: AVAssetWriter?
    private var videoInput: AVAssetWriterInput?
    private var systemAudioInput: AVAssetWriterInput?
    private var microphoneAudioInput: AVAssetWriterInput?
    private var workingURL: URL?
    private var sessionStarted = false
    private var sessionStartTime: CMTime?
    private var lastVideoSampleBuffer: CMSampleBuffer?
    private var lastVideoTime: CMTime?
    private var lastAudioEndTime: CMTime?
    private var recordingError: Error?
    private var latestAudioPeak: Float = 0
    private var screenFramesSinceRead = 0
    private var droppedFramesSinceRead = 0
    // Whole-recording totals. The SinceRead counters above are cleared every 100 ms by the live
    // meter, so they cannot answer what the finished clip actually achieved.
    private var offeredFramesTotal = 0
    private var droppedFramesTotal = 0
    /// Survives clearState so the summary is still readable after the writer has been torn down.
    private var completedDelivery: FrameDelivery?
    private var paused = false
    private var pauseStartTime: CMTime?
    private var accumulatedPauseDuration = CMTime.zero

    init(
        workingDirectory: URL,
        framesPerSecond: Int = 60,
        audioMode: RecordingAudioMode = .computer,
        recordingQuality: RecordingQuality = .balanced,
        recordingResolution: RecordingResolution = .native,
        showsCursor: Bool = false,
        systemAudioGainPercent: Int = 100,
        microphoneGainPercent: Int = 100,
        microphoneDeviceID: String? = nil
    ) {
        self.workingDirectory = workingDirectory
        self.framesPerSecond = framesPerSecond
        self.audioMode = audioMode
        self.recordingQuality = recordingQuality
        self.recordingResolution = recordingResolution
        self.showsCursor = showsCursor
        self.systemAudioGainPercent = SettingsStore.normalizeAudioGainPercent(systemAudioGainPercent)
        self.microphoneGainPercent = SettingsStore.normalizeAudioGainPercent(microphoneGainPercent)
        self.microphoneDeviceID = microphoneDeviceID
    }

    func start(target: ScreenClipTarget) async throws {
        if audioMode.recordsMicrophone {
            guard #available(macOS 15.0, *) else {
                throw ScreenClipError.microphoneRequiresMacOS15
            }
            let authorized: Bool
            switch AVCaptureDevice.authorizationStatus(for: .audio) {
            case .authorized:
                authorized = true
            case .notDetermined:
                authorized = await AVCaptureDevice.requestAccess(for: .audio)
            case .denied, .restricted:
                authorized = false
            @unknown default:
                authorized = false
            }
            guard authorized else {
                throw ScreenClipError.microphonePermissionDenied
            }
        }

        try FileManager.default.createDirectory(at: workingDirectory, withIntermediateDirectories: true)
        let usesSeparateAudioTracks = audioMode == .computerAndMicrophone
        let url = makeWorkingURL(pathExtension: usesSeparateAudioTracks ? "mov" : "mp4")
        try? FileManager.default.removeItem(at: url)

        // AVAssetWriter's MP4 profile rejects a second AAC input. QuickTime MOV supports both
        // independently named tracks while remaining directly editable in Mac video editors.
        let writer = try AVAssetWriter(outputURL: url, fileType: usesSeparateAudioTracks ? .mov : .mp4)
        // ScreenCaptureKit scales on the GPU while it is already compositing the frame, so asking
        // it for a smaller picture costs nothing and is what actually buys back encoder headroom.
        // The writer, the bitrate, and the capture configuration all have to agree on this size.
        let output = RecordingResolution.outputSize(
            sourceWidth: target.pixelWidth,
            sourceHeight: target.pixelHeight,
            resolution: recordingResolution)
        let bitsPerSecond = Self.bitsPerSecond(
            quality: recordingQuality,
            width: output.width,
            height: output.height,
            framesPerSecond: framesPerSecond)
        let settings: [String: Any] = [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: output.width,
            AVVideoHeightKey: output.height,
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: bitsPerSecond,
                AVVideoExpectedSourceFrameRateKey: framesPerSecond,
                AVVideoMaxKeyFrameIntervalKey: framesPerSecond * 2,
                AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel
            ]
        ]
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: settings)
        input.expectsMediaDataInRealTime = true
        guard writer.canAdd(input) else {
            throw ScreenClipError.encoderUnavailable
        }
        writer.add(input)

        var encodedSystemAudioInput: AVAssetWriterInput?
        if audioMode.recordsSystemAudio {
            let candidate = Self.makeAudioInput(title: "System / App Audio")
            guard writer.canAdd(candidate) else {
                throw ScreenClipError.audioEncoderUnavailable
            }
            writer.add(candidate)
            encodedSystemAudioInput = candidate
        }

        var encodedMicrophoneAudioInput: AVAssetWriterInput?
        if audioMode.recordsMicrophone {
            let candidate = Self.makeAudioInput(title: "Microphone")
            guard writer.canAdd(candidate) else {
                throw audioMode.recordsSystemAudio
                    ? ScreenClipError.separateAudioTracksUnavailable
                    : ScreenClipError.audioEncoderUnavailable
            }
            writer.add(candidate)
            encodedMicrophoneAudioInput = candidate
        }

        let configuration = SCStreamConfiguration()
        configuration.width = output.width
        configuration.height = output.height
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(framesPerSecond))
        configuration.queueDepth = 8
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        Self.applyCursorPreference(to: configuration, showsCursor: showsCursor)
        configuration.captureResolution = .best
        if let sourceRect = target.sourceRect {
            configuration.sourceRect = sourceRect
        }
        configuration.ignoreShadowsSingleWindow = target.ignoresSingleWindowShadow
        configuration.capturesAudio = audioMode.recordsSystemAudio
        configuration.sampleRate = 48_000
        configuration.channelCount = 2
        configuration.excludesCurrentProcessAudio = true
        if #available(macOS 15.0, *) {
            configuration.captureMicrophone = audioMode.recordsMicrophone
            if audioMode.recordsMicrophone {
                configuration.microphoneCaptureDeviceID = microphoneDeviceID
            }
        }

        let stream = SCStream(filter: target.contentFilter, configuration: configuration, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: sampleQueue)
        if audioMode.recordsSystemAudio {
            try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: sampleQueue)
        }
        if #available(macOS 15.0, *), audioMode.recordsMicrophone {
            try stream.addStreamOutput(self, type: .microphone, sampleHandlerQueue: sampleQueue)
        }

        self.writer = writer
        self.videoInput = input
        self.systemAudioInput = encodedSystemAudioInput
        self.microphoneAudioInput = encodedMicrophoneAudioInput
        self.workingURL = url
        self.stream = stream
        self.sessionStarted = false
        self.sessionStartTime = nil
        self.lastVideoSampleBuffer = nil
        self.lastVideoTime = nil
        self.lastAudioEndTime = nil
        self.recordingError = nil
        self.latestAudioPeak = 0
        self.screenFramesSinceRead = 0
        self.droppedFramesSinceRead = 0
        self.offeredFramesTotal = 0
        self.droppedFramesTotal = 0
        self.completedDelivery = nil
        self.paused = false
        self.pauseStartTime = nil
        self.accumulatedPauseDuration = .zero

        do {
            try await stream.startCapture()
        } catch {
            clearState(removingWorkingFile: true)
            throw error
        }
    }

    static func applyCursorPreference(
        to configuration: SCStreamConfiguration,
        showsCursor: Bool
    ) {
        configuration.showsCursor = showsCursor
    }

    func stop() async throws -> URL {
        guard let stream else { throw ScreenClipError.notRecording }
        let rawStopTime = CMClockGetTime(CMClockGetHostTimeClock())
        let requestedStopTime = sampleQueue.sync { adjustedTime(rawStopTime) }
        do {
            try await stream.stopCapture()
        } catch {
            sampleQueue.sync {
                if recordingError == nil { recordingError = error }
            }
        }

        return try await withCheckedThrowingContinuation { continuation in
            sampleQueue.async { [self] in
                if let recordingError {
                    // An unfinished MP4/MOV has no usable movie atom. Keeping
                    // it looks like recovery, but it cannot be opened and only creates clutter.
                    clearState(removingWorkingFile: true)
                    continuation.resume(throwing: recordingError)
                    return
                }

                guard sessionStarted,
                      let writer,
                      let videoInput,
                      let workingURL else {
                    clearState(removingWorkingFile: true)
                    continuation.resume(throwing: ScreenClipError.noFrames)
                    return
                }

                let finalEndTime = Self.sessionEndTime(
                    requestedStopTime: requestedStopTime,
                    lastVideoTime: lastVideoTime,
                    lastAudioEndTime: lastAudioEndTime,
                    framesPerSecond: framesPerSecond)
                appendTailFrameIfNeeded(endTime: finalEndTime)
                completedDelivery = FrameDelivery(
                    requestedFramesPerSecond: framesPerSecond,
                    offeredFrames: offeredFramesTotal,
                    droppedFrames: droppedFramesTotal,
                    seconds: sessionStartTime.map {
                        CMTimeSubtract(finalEndTime, $0).seconds
                    } ?? 0)
                if let recordingError {
                    clearState(removingWorkingFile: true)
                    continuation.resume(throwing: recordingError)
                    return
                }

                videoInput.markAsFinished()
                systemAudioInput?.markAsFinished()
                microphoneAudioInput?.markAsFinished()
                // finishWriting otherwise infers the session end from the final encoded sample.
                // H.264 frame reordering can extend that sample across a removed pause, making a
                // seven-second adjusted timeline report the original fourteen-second wall time.
                writer.endSession(atSourceTime: finalEndTime)
                writer.finishWriting { [self] in
                    let error = writer.error
                    let status = writer.status
                    clearState(removingWorkingFile: status != .completed)

                    if status == .completed {
                        continuation.resume(returning: workingURL)
                    } else {
                        continuation.resume(throwing: error ?? ScreenClipError.finalizeFailed)
                    }
                }
            }
        }
    }

    func stream(
        _ stream: SCStream,
        didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
        of outputType: SCStreamOutputType
    ) {
        guard CMSampleBufferDataIsReady(sampleBuffer) else { return }
        let selectedAudioInput: AVAssetWriterInput?
        let selectedGainPercent: Int
        if outputType == .audio {
            selectedAudioInput = audioMode.recordsSystemAudio ? systemAudioInput : nil
            selectedGainPercent = systemAudioGainPercent
        } else if #available(macOS 15.0, *), outputType == .microphone {
            selectedAudioInput = audioMode.recordsMicrophone ? microphoneAudioInput : nil
            selectedGainPercent = microphoneGainPercent
        } else {
            selectedAudioInput = nil
            selectedGainPercent = 100
        }
        if let selectedAudioInput {
            guard !paused, let adjustedBuffer = sampleBufferRemovingPausedTime(sampleBuffer) else { return }
            Self.applyGainInPlace(to: adjustedBuffer, percent: selectedGainPercent)
            latestAudioPeak = max(latestAudioPeak, Self.audioPeak(in: adjustedBuffer))
            let endTime = Self.sampleEndTime(adjustedBuffer)
            if lastAudioEndTime.map({ CMTimeCompare(endTime, $0) > 0 }) ?? true {
                lastAudioEndTime = endTime
            }
            appendSelectedAudio(adjustedBuffer, to: selectedAudioInput)
            return
        }

        guard outputType == .screen,
              !paused,
              Self.isCompleteScreenFrame(sampleBuffer),
              let writer,
              let videoInput,
              recordingError == nil else { return }
        guard let adjustedBuffer = sampleBufferRemovingPausedTime(sampleBuffer) else { return }

        screenFramesSinceRead += 1
        offeredFramesTotal += 1

        if !sessionStarted {
            guard writer.startWriting() else {
                recordingError = Self.writerFailure(
                    writer.error,
                    fallback: .encoderUnavailable)
                return
            }
            writer.startSession(atSourceTime: adjustedBuffer.presentationTimeStamp)
            sessionStarted = true
            sessionStartTime = adjustedBuffer.presentationTimeStamp
        }

        guard writer.status == .writing else {
            recordingError = Self.writerFailure(
                writer.error,
                fallback: .encoderFailed(detail: nil))
            return
        }

        guard videoInput.isReadyForMoreMediaData else {
            droppedFramesSinceRead += 1
            droppedFramesTotal += 1
            return
        }
        if !videoInput.append(adjustedBuffer) {
            recordingError = Self.writerFailure(
                writer.error,
                fallback: .encoderFailed(detail: nil))
        } else {
            lastVideoSampleBuffer = adjustedBuffer
            lastVideoTime = adjustedBuffer.presentationTimeStamp
        }
    }

    /// ScreenCaptureKit is change-driven: a selected region that stays perfectly still may stop
    /// producing frames even while the recording and audio continue. Retiming one copy of the last
    /// real frame near stop gives the video track the honest recording duration; players hold that
    /// unchanged image naturally between the two samples.
    private func appendTailFrameIfNeeded(endTime: CMTime) {
        guard let writer,
              writer.status == .writing,
              let videoInput,
              let lastVideoSampleBuffer,
              let lastVideoTime else { return }

        let frameDuration = CMTime(value: 1, timescale: CMTimeScale(framesPerSecond))
        let tailTime = CMTimeSubtract(endTime, frameDuration)
        guard CMTimeCompare(tailTime, CMTimeAdd(lastVideoTime, frameDuration)) > 0 else { return }
        guard videoInput.isReadyForMoreMediaData else {
            recordingError = ScreenClipError.encoderFailed(
                detail: "The encoder could not accept the final held frame.")
            return
        }

        var timing = CMSampleTimingInfo(
            duration: frameDuration,
            presentationTimeStamp: tailTime,
            decodeTimeStamp: .invalid)
        var tailBuffer: CMSampleBuffer?
        let status = CMSampleBufferCreateCopyWithNewTiming(
            allocator: kCFAllocatorDefault,
            sampleBuffer: lastVideoSampleBuffer,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleBufferOut: &tailBuffer)
        guard status == noErr, let tailBuffer else {
            recordingError = ScreenClipError.encoderFailed(
                detail: "The final held frame could not be timed. [OSStatus \(status)]")
            return
        }

        if !videoInput.append(tailBuffer) {
            recordingError = Self.writerFailure(
                writer.error,
                fallback: .encoderFailed(detail: "The final held frame could not be written."))
        }
    }

    private func appendSelectedAudio(
        _ sampleBuffer: CMSampleBuffer,
        to audioInput: AVAssetWriterInput
    ) {
        guard audioMode.recordsAudio,
              sessionStarted,
              let startTime = sessionStartTime,
              CMTimeCompare(sampleBuffer.presentationTimeStamp, startTime) >= 0,
              let writer,
              recordingError == nil else { return }

        guard writer.status == .writing else {
            recordingError = Self.writerFailure(
                writer.error,
                fallback: .audioEncoderFailed(detail: nil),
                audio: true)
            return
        }
        guard audioInput.isReadyForMoreMediaData else { return }
        if !audioInput.append(sampleBuffer) {
            recordingError = Self.writerFailure(
                writer.error,
                fallback: .audioEncoderFailed(detail: nil),
                audio: true)
        }
    }

    /// Pausing leaves ScreenCaptureKit running so display/window identity and microphone ownership
    /// stay stable. Samples are discarded until resume; later timestamps are shifted backward by
    /// the total paused duration so the finished movie contains no frozen or silent pause gap.
    func pause() -> Bool {
        sampleQueue.sync {
            guard stream != nil, !paused else { return false }
            paused = true
            pauseStartTime = CMClockGetTime(CMClockGetHostTimeClock())
            latestAudioPeak = 0
            return true
        }
    }

    func resume() -> Bool {
        sampleQueue.sync {
            guard stream != nil, paused, let pauseStartTime else { return false }
            let now = CMClockGetTime(CMClockGetHostTimeClock())
            accumulatedPauseDuration = CMTimeAdd(
                accumulatedPauseDuration,
                CMTimeSubtract(now, pauseStartTime))
            self.pauseStartTime = nil
            paused = false
            latestAudioPeak = 0
            return true
        }
    }

    func isPaused() -> Bool {
        sampleQueue.sync { paused }
    }

    private func adjustedTime(_ rawTime: CMTime) -> CMTime {
        Self.timelineTime(
            rawTime: rawTime,
            accumulatedPauseDuration: accumulatedPauseDuration,
            activePauseStart: paused ? pauseStartTime : nil)
    }

    static func timelineTime(
        rawTime: CMTime,
        accumulatedPauseDuration: CMTime,
        activePauseStart: CMTime?
    ) -> CMTime {
        var removed = accumulatedPauseDuration
        if let activePauseStart {
            removed = CMTimeAdd(removed, CMTimeSubtract(rawTime, activePauseStart))
        }
        return CMTimeSubtract(rawTime, removed)
    }

    static func sessionEndTime(
        requestedStopTime: CMTime,
        lastVideoTime: CMTime?,
        lastAudioEndTime: CMTime?,
        framesPerSecond: Int
    ) -> CMTime {
        var endTime = requestedStopTime
        if let lastAudioEndTime, CMTimeCompare(lastAudioEndTime, endTime) > 0 {
            endTime = lastAudioEndTime
        }
        if let lastVideoTime {
            let videoEnd = CMTimeAdd(
                lastVideoTime,
                CMTime(value: 1, timescale: CMTimeScale(framesPerSecond)))
            if CMTimeCompare(videoEnd, endTime) > 0 {
                endTime = videoEnd
            }
        }
        return endTime
    }

    private func sampleBufferRemovingPausedTime(_ sampleBuffer: CMSampleBuffer) -> CMSampleBuffer? {
        guard CMTimeCompare(accumulatedPauseDuration, .zero) > 0 else { return sampleBuffer }

        var timing = CMSampleTimingInfo(
            duration: .invalid,
            presentationTimeStamp: .invalid,
            decodeTimeStamp: .invalid)
        let timingStatus = CMSampleBufferGetSampleTimingInfo(
            sampleBuffer,
            at: 0,
            timingInfoOut: &timing)
        guard timingStatus == noErr else {
            recordingError = ScreenClipError.pauseRetimingFailed(status: timingStatus)
            return nil
        }

        if timing.presentationTimeStamp.isValid {
            timing.presentationTimeStamp = CMTimeSubtract(
                timing.presentationTimeStamp,
                accumulatedPauseDuration)
        }
        if timing.decodeTimeStamp.isValid {
            timing.decodeTimeStamp = CMTimeSubtract(
                timing.decodeTimeStamp,
                accumulatedPauseDuration)
        }

        var adjustedBuffer: CMSampleBuffer?
        let copyStatus = CMSampleBufferCreateCopyWithNewTiming(
            allocator: kCFAllocatorDefault,
            sampleBuffer: sampleBuffer,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleBufferOut: &adjustedBuffer)
        guard copyStatus == noErr, let adjustedBuffer else {
            recordingError = ScreenClipError.pauseRetimingFailed(status: copyStatus)
            return nil
        }
        return adjustedBuffer
    }

    /// Read by the 10-fps status timer so an asynchronous encoder failure stops capture and reaches
    /// the user immediately instead of leaving the H in a false recording state until they stop it.
    func currentFailure() -> Error? {
        sampleQueue.sync { recordingError }
    }

    /// Read once after `stop()` succeeds, so a finished clip can report what the requested frame
    /// rate actually delivered instead of leaving the measurement unused.
    func takeCompletedDelivery() -> FrameDelivery? {
        sampleQueue.sync {
            let delivery = completedDelivery
            completedDelivery = nil
            return delivery
        }
    }

    func takeTelemetry() -> (audioPeak: Double, framePressure: Double) {
        sampleQueue.sync {
            let peak = Double(latestAudioPeak)
            let pressure = screenFramesSinceRead == 0
                ? 0.0
                : Double(droppedFramesSinceRead) / Double(screenFramesSinceRead)
            latestAudioPeak = 0
            screenFramesSinceRead = 0
            droppedFramesSinceRead = 0
            return (peak, pressure)
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        sampleQueue.async { [weak self] in
            guard let self, self.recordingError == nil else { return }
            self.recordingError = error
        }
    }

    static func makeAudioInput(title: String) -> AVAssetWriterInput {
        let audioSettings: [String: Any] = [
            AVFormatIDKey: kAudioFormatMPEG4AAC,
            AVSampleRateKey: 48_000,
            AVNumberOfChannelsKey: 2,
            AVEncoderBitRateKey: 192_000
        ]
        let input = AVAssetWriterInput(mediaType: .audio, outputSettings: audioSettings)
        input.expectsMediaDataInRealTime = true

        let titleItem = AVMutableMetadataItem()
        titleItem.identifier = .commonIdentifierTitle
        titleItem.value = title as NSString
        input.metadata = [titleItem]
        return input
    }

    static func bitsPerSecond(
        quality: RecordingQuality,
        width: Int,
        height: Int,
        framesPerSecond: Int
    ) -> Int {
        if quality == .legacy { return 8_000_000 }

        let referenceBitrate = quality == .high ? 24_000_000.0 : 16_000_000.0
        let minimumBitrate = quality == .high ? 3_000_000 : 2_000_000
        let scaled = referenceBitrate
            * (Double(max(1, width)) / 1920.0)
            * (Double(max(1, height)) / 1080.0)
            * (Double(min(240, max(1, framesPerSecond))) / 60.0)
        return min(80_000_000, max(minimumBitrate, Int(scaled.rounded())))
    }

    private static func isCompleteScreenFrame(_ sampleBuffer: CMSampleBuffer) -> Bool {
        guard sampleBuffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(
                sampleBuffer,
                createIfNecessary: false) as? [[SCStreamFrameInfo: Any]] else { return false }
        return ScreenFrameAdmission.shouldEncode(
            statusRawValue: attachments.first?[.status] as? Int,
            hasImageBuffer: CMSampleBufferGetImageBuffer(sampleBuffer) != nil)
    }

    private static func writerFailure(
        _ error: Error?,
        fallback: ScreenClipError,
        audio: Bool = false
    ) -> Error {
        guard let error else { return fallback }
        let top = error as NSError
        var detail = "\(top.localizedDescription) [\(top.domain) \(top.code)]"
        if let underlying = top.userInfo[NSUnderlyingErrorKey] as? NSError {
            detail += " — underlying [\(underlying.domain) \(underlying.code)]"
        }
        return audio
            ? ScreenClipError.audioEncoderFailed(detail: detail)
            : ScreenClipError.encoderFailed(detail: detail)
    }

    private static func sampleEndTime(_ sampleBuffer: CMSampleBuffer) -> CMTime {
        let start = sampleBuffer.presentationTimeStamp
        let duration = CMSampleBufferGetOutputDuration(sampleBuffer)
        guard duration.isValid, duration.isNumeric, CMTimeCompare(duration, .zero) > 0 else {
            return start
        }
        return CMTimeAdd(start, duration)
    }

    private static func audioPeak(in sampleBuffer: CMSampleBuffer) -> Float {
        guard let formatDescription = CMSampleBufferGetFormatDescription(sampleBuffer),
              let description = CMAudioFormatDescriptionGetStreamBasicDescription(formatDescription)
        else { return 0 }

        var sizeNeeded = 0
        var blockBuffer: CMBlockBuffer?
        CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer,
            bufferListSizeNeededOut: &sizeNeeded,
            bufferListOut: nil,
            bufferListSize: 0,
            blockBufferAllocator: kCFAllocatorDefault,
            blockBufferMemoryAllocator: kCFAllocatorDefault,
            flags: UInt32(kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment),
            blockBufferOut: &blockBuffer)
        guard sizeNeeded > 0 else { return 0 }

        let storage = UnsafeMutableRawPointer.allocate(
            byteCount: sizeNeeded,
            alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { storage.deallocate() }
        let audioBufferList = storage.bindMemory(to: AudioBufferList.self, capacity: 1)
        let status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer,
            bufferListSizeNeededOut: nil,
            bufferListOut: audioBufferList,
            bufferListSize: sizeNeeded,
            blockBufferAllocator: kCFAllocatorDefault,
            blockBufferMemoryAllocator: kCFAllocatorDefault,
            flags: UInt32(kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment),
            blockBufferOut: &blockBuffer)
        guard status == noErr else { return 0 }

        let format = description.pointee
        let isFloat = format.mFormatFlags & kAudioFormatFlagIsFloat != 0
        let isSignedInteger = format.mFormatFlags & kAudioFormatFlagIsSignedInteger != 0
        var peak: Float = 0

        for buffer in UnsafeMutableAudioBufferListPointer(audioBufferList) {
            guard let data = buffer.mData else { continue }
            if isFloat && format.mBitsPerChannel == 32 {
                let samples = data.assumingMemoryBound(to: Float.self)
                for index in 0..<(Int(buffer.mDataByteSize) / MemoryLayout<Float>.size) {
                    peak = max(peak, abs(samples[index]))
                }
            } else if isSignedInteger && format.mBitsPerChannel == 16 {
                let samples = data.assumingMemoryBound(to: Int16.self)
                for index in 0..<(Int(buffer.mDataByteSize) / MemoryLayout<Int16>.size) {
                    peak = max(peak, min(1, Float(abs(Int32(samples[index]))) / 32_768.0))
                }
            } else if isSignedInteger && format.mBitsPerChannel == 32 {
                let samples = data.assumingMemoryBound(to: Int32.self)
                for index in 0..<(Int(buffer.mDataByteSize) / MemoryLayout<Int32>.size) {
                    peak = max(peak, min(1, Float(abs(Int64(samples[index]))) / 2_147_483_648.0))
                }
            }
        }
        return min(1, peak)
    }

    /// ScreenCaptureKit supplies uncompressed PCM. Apply the selected per-source gain before the
    /// AAC writer sees the sample, saturating instead of allowing integer wrap or float overshoot.
    static func applyGainInPlace(to sampleBuffer: CMSampleBuffer, percent: Int) {
        let normalized = SettingsStore.normalizeAudioGainPercent(percent)
        guard normalized != 100,
              let formatDescription = CMSampleBufferGetFormatDescription(sampleBuffer),
              let description = CMAudioFormatDescriptionGetStreamBasicDescription(formatDescription)
        else { return }

        var sizeNeeded = 0
        var blockBuffer: CMBlockBuffer?
        CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer,
            bufferListSizeNeededOut: &sizeNeeded,
            bufferListOut: nil,
            bufferListSize: 0,
            blockBufferAllocator: kCFAllocatorDefault,
            blockBufferMemoryAllocator: kCFAllocatorDefault,
            flags: UInt32(kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment),
            blockBufferOut: &blockBuffer)
        guard sizeNeeded > 0 else { return }

        let storage = UnsafeMutableRawPointer.allocate(
            byteCount: sizeNeeded,
            alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { storage.deallocate() }
        let audioBufferList = storage.bindMemory(to: AudioBufferList.self, capacity: 1)
        let status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer,
            bufferListSizeNeededOut: nil,
            bufferListOut: audioBufferList,
            bufferListSize: sizeNeeded,
            blockBufferAllocator: kCFAllocatorDefault,
            blockBufferMemoryAllocator: kCFAllocatorDefault,
            flags: UInt32(kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment),
            blockBufferOut: &blockBuffer)
        guard status == noErr else { return }

        let format = description.pointee
        let multiplier = Float(normalized) / 100.0
        let isFloat = format.mFormatFlags & kAudioFormatFlagIsFloat != 0
        let isSignedInteger = format.mFormatFlags & kAudioFormatFlagIsSignedInteger != 0

        for buffer in UnsafeMutableAudioBufferListPointer(audioBufferList) {
            guard let data = buffer.mData else { continue }
            if isFloat && format.mBitsPerChannel == 32 {
                let samples = data.assumingMemoryBound(to: Float.self)
                for index in 0..<(Int(buffer.mDataByteSize) / MemoryLayout<Float>.size) {
                    samples[index] = scaledFloatSample(samples[index], multiplier: multiplier)
                }
            } else if isFloat && format.mBitsPerChannel == 64 {
                let samples = data.assumingMemoryBound(to: Double.self)
                let doubleMultiplier = Double(multiplier)
                for index in 0..<(Int(buffer.mDataByteSize) / MemoryLayout<Double>.size) {
                    samples[index] = max(-1, min(1, samples[index] * doubleMultiplier))
                }
            } else if isSignedInteger && format.mBitsPerChannel == 16 {
                let samples = data.assumingMemoryBound(to: Int16.self)
                for index in 0..<(Int(buffer.mDataByteSize) / MemoryLayout<Int16>.size) {
                    let scaled = (Float(samples[index]) * multiplier).rounded()
                    samples[index] = Int16(max(Float(Int16.min), min(Float(Int16.max), scaled)))
                }
            } else if isSignedInteger && format.mBitsPerChannel == 32 {
                let samples = data.assumingMemoryBound(to: Int32.self)
                for index in 0..<(Int(buffer.mDataByteSize) / MemoryLayout<Int32>.size) {
                    let scaled = (Double(samples[index]) * Double(multiplier)).rounded()
                    samples[index] = Int32(max(Double(Int32.min), min(Double(Int32.max), scaled)))
                }
            }
        }
    }

    static func scaledFloatSample(_ sample: Float, multiplier: Float) -> Float {
        return max(-1, min(1, sample * multiplier))
    }

    private func makeWorkingURL(pathExtension: String) -> URL {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd_HH-mm-ss-SSS"
        formatter.locale = Locale(identifier: "en_US_POSIX")
        let name = "HucksSnipNClip_Working_" + formatter.string(from: Date()) + "_" + UUID().uuidString + "." + pathExtension
        return workingDirectory.appendingPathComponent(name)
    }

    private func clearState(removingWorkingFile: Bool) {
        let file = workingURL
        stream = nil
        writer = nil
        videoInput = nil
        systemAudioInput = nil
        microphoneAudioInput = nil
        workingURL = nil
        sessionStarted = false
        sessionStartTime = nil
        lastVideoSampleBuffer = nil
        lastVideoTime = nil
        lastAudioEndTime = nil
        recordingError = nil
        latestAudioPeak = 0
        screenFramesSinceRead = 0
        droppedFramesSinceRead = 0
        offeredFramesTotal = 0
        droppedFramesTotal = 0
        // completedDelivery is deliberately left alone: stop() records it here, and the caller
        // reads it after the writer has already been torn down.
        paused = false
        pauseStartTime = nil
        accumulatedPauseDuration = .zero

        if removingWorkingFile, let file {
            try? FileManager.default.removeItem(at: file)
        }
    }
}

enum ScreenClipError: LocalizedError {
    case notRecording
    case encoderUnavailable
    case audioEncoderUnavailable
    case separateAudioTracksUnavailable
    case microphonePermissionDenied
    case microphoneRequiresMacOS15
    case pauseRetimingFailed(status: OSStatus)
    case encoderFailed(detail: String?)
    case audioEncoderFailed(detail: String?)
    case noFrames
    case finalizeFailed

    var errorDescription: String? {
        switch self {
        case .notRecording:
            return "No screen clip is currently recording."
        case .encoderUnavailable:
            return "The Mac could not create an H.264 video encoder."
        case .audioEncoderUnavailable:
            return "The Mac could not create an AAC audio encoder."
        case .separateAudioTracksUnavailable:
            return "The Mac could not create separate System / App Audio and Microphone tracks in this MOV."
        case .microphonePermissionDenied:
            return "Microphone access is off. Open System Settings > Privacy & Security > Microphone and allow Huck’s Snip ’n’ Clip."
        case .microphoneRequiresMacOS15:
            return "Microphone recording requires macOS 15 or newer."
        case .pauseRetimingFailed(let status):
            return "The recording could not remove paused time safely. [OSStatus \(status)]"
        case .encoderFailed(let detail):
            let message = "The H.264 encoder stopped before the clip could be saved."
            return detail.map { message + "\n\n" + $0 } ?? message
        case .audioEncoderFailed(let detail):
            let message = "The AAC audio encoder stopped before the clip could be saved."
            return detail.map { message + "\n\n" + $0 } ?? message
        case .noFrames:
            return "The clip ended before a video frame was captured."
        case .finalizeFailed:
            return "The video file could not be finalized."
        }
    }
}
