using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace QSnipAndClip
{
    internal sealed class ScreenClipRecorder : IDisposable
    {
        private const int AudioWriteDelayMilliseconds = 100;

        private readonly object stateLock;
        private readonly int framesPerSecond;
        private readonly int bitsPerSecond;
        private readonly RecordingAudioMode audioMode;
        private readonly int computerAudioGainPercent;
        private readonly int microphoneGainPercent;
        private Thread worker;
        private volatile bool stopRequested;
        private volatile bool pauseRequested;
        private volatile bool isPaused;
        private long durationTicks;
        private volatile bool isFinished;
        private int pendingAudioPeak;
        private int pendingFramePressurePermille;
        private string workingPath;
        private string diagnosticsPath;
        private RecordingDiagnostics diagnostics;
        private Exception error;

        public ScreenClipRecorder(int framesPerSecond)
            : this(framesPerSecond, RecordingAudioMode.Off)
        {
        }

        public ScreenClipRecorder(int framesPerSecond, RecordingAudioMode audioMode)
            : this(framesPerSecond, audioMode, 100, 100)
        {
        }

        public ScreenClipRecorder(
            int framesPerSecond,
            RecordingAudioMode audioMode,
            int computerAudioGainPercent,
            int microphoneGainPercent)
            : this(framesPerSecond, audioMode, computerAudioGainPercent, microphoneGainPercent,
                RecordingQualitySettings.LegacyBitsPerSecond)
        {
        }

        public ScreenClipRecorder(
            int framesPerSecond,
            RecordingAudioMode audioMode,
            int computerAudioGainPercent,
            int microphoneGainPercent,
            int bitsPerSecond)
        {
            if (framesPerSecond < 1 || framesPerSecond > 240)
            {
                throw new ArgumentOutOfRangeException("framesPerSecond");
            }

            if (bitsPerSecond < 1 || bitsPerSecond > RecordingQualitySettings.MaximumBitsPerSecond)
            {
                throw new ArgumentOutOfRangeException("bitsPerSecond");
            }

            this.stateLock = new object();
            this.framesPerSecond = framesPerSecond;
            this.bitsPerSecond = bitsPerSecond;
            this.audioMode = audioMode;
            this.computerAudioGainPercent = SettingsStore.NormalizeAudioGainPercent(computerAudioGainPercent);
            this.microphoneGainPercent = SettingsStore.NormalizeAudioGainPercent(microphoneGainPercent);
        }

        public bool IsRunning
        {
            get { return this.worker != null && !this.isFinished; }
        }

        public bool IsFinished
        {
            get { return this.isFinished; }
        }

        public bool IsPaused { get { return this.isPaused; } }
        public bool ShowCursor { get; set; }
        public RecordingResolutionCeiling ResolutionCeiling { get; set; }
        public string MicrophoneDeviceId { get; set; }
        public bool MicrophoneFallback { get; private set; }
        public bool PauseRequested { get { return this.pauseRequested; } }
        public double DurationSeconds { get { return Interlocked.Read(ref this.durationTicks) / (double)Stopwatch.Frequency; } }

        public void SetPaused(bool paused)
        {
            if (IsRunning && !this.stopRequested) this.pauseRequested = paused;
        }

        public string WorkingPath
        {
            get { lock (this.stateLock) { return this.workingPath; } }
        }

        public Exception Error
        {
            get { lock (this.stateLock) { return this.error; } }
        }

        public string DiagnosticsPath
        {
            get { lock (this.stateLock) { return this.diagnosticsPath; } }
        }

        public RecordingDiagnostics Diagnostics
        {
            get { lock (this.stateLock) { return this.diagnostics; } }
        }

        public bool HasAudio
        {
            get { return this.audioMode != RecordingAudioMode.Off; }
        }

        public int TakeAudioPeak()
        {
            return Interlocked.Exchange(ref this.pendingAudioPeak, 0);
        }

        public float TakeFramePressure()
        {
            return Interlocked.Exchange(ref this.pendingFramePressurePermille, 0) / 1000.0f;
        }

        public void Start(Rectangle bounds)
        {
            Start(bounds, null);
        }

        public void Start(Rectangle bounds, Func<Point> captureOriginProvider)
        {
            if (this.worker != null)
            {
                throw new InvalidOperationException("This recorder has already been used.");
            }

            Rectangle safeBounds = NormalizeVideoBounds(bounds);

            string workingDirectory = AppPaths.SubFolder("Working");
            Directory.CreateDirectory(workingDirectory);

            string diagnosticsDirectory = AppPaths.SubFolder("Diagnostics");
            Directory.CreateDirectory(diagnosticsDirectory);

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");

            lock (this.stateLock)
            {
                this.workingPath = Path.Combine(
                    workingDirectory,
                    AppPaths.FilePrefix + "_Working_" + timestamp + ".mp4");
                this.diagnosticsPath = Path.Combine(
                    diagnosticsDirectory,
                    AppPaths.FilePrefix + "_Recording_" + timestamp + ".txt");
            }

            this.worker = new Thread(new ThreadStart(delegate { Record(safeBounds, captureOriginProvider); }));
            this.worker.Name = "Huck's Snip 'n' Clip Screen Recorder";
            this.worker.IsBackground = true;
            this.worker.SetApartmentState(ApartmentState.MTA);
            this.worker.Start();
        }

        internal static Rectangle NormalizeVideoBounds(Rectangle bounds)
        {
            if (bounds.Width < 16 || bounds.Height < 16)
            {
                throw new ArgumentOutOfRangeException(
                    "bounds",
                    "A video region must be at least 16 by 16 pixels.");
            }

            return new Rectangle(
                bounds.X,
                bounds.Y,
                bounds.Width - (bounds.Width % 2),
                bounds.Height - (bounds.Height % 2));
        }

        internal static long AdvanceSchedule(
            long currentSlot,
            long nowTicks,
            int framesPerSecond,
            long clockFrequency,
            out long targetTicks,
            out long skippedSlots)
        {
            if (framesPerSecond < 1 || clockFrequency < 1)
            {
                throw new ArgumentOutOfRangeException();
            }

            long nextSlot = currentSlot + 1;
            targetTicks = nextSlot * clockFrequency / framesPerSecond;
            skippedSlots = 0;
            while (targetTicks <= nowTicks)
            {
                nextSlot++;
                skippedSlots++;
                targetTicks = nextSlot * clockFrequency / framesPerSecond;
            }

            return nextSlot;
        }

        internal static bool IsTemporaryProtectedScreenFailure(Exception exception)
        {
            Win32Exception win32 = exception as Win32Exception;
            return win32 != null
                && (win32.NativeErrorCode == 5 || win32.NativeErrorCode == 6);
        }

        public void Stop()
        {
            this.stopRequested = true;
        }

        public void Dispose()
        {
            Stop();
            Thread runningWorker = this.worker;
            if (runningWorker != null && runningWorker.IsAlive)
            {
                runningWorker.Join(10000);
            }
        }

        private void Record(Rectangle bounds, Func<Point> captureOriginProvider)
        {
            string path = this.WorkingPath;
            Size encodedSize = RecordingResolutionSettings.Resolve(
                bounds.Width, bounds.Height, this.ResolutionCeiling);
            bool scaling = encodedSize.Width != bounds.Width || encodedSize.Height != bounds.Height;
            RecordingDiagnostics diagnostics = new RecordingDiagnostics(
                this.framesPerSecond,
                encodedSize.Width,
                encodedSize.Height,
                this.bitsPerSecond);
            lock (this.stateLock) { this.diagnostics = diagnostics; }
            Stopwatch clock = null;
            try
            {
                using (WasapiAudioCapture computerAudioCapture = CreateComputerAudioCapture())
                using (WasapiAudioCapture microphoneCapture = CreateMicrophoneCapture())
                using (Bitmap frame = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
                using (Graphics graphics = Graphics.FromImage(frame))
                using (Bitmap scaledFrame = scaling
                    ? new Bitmap(encodedSize.Width, encodedSize.Height, PixelFormat.Format32bppArgb)
                    : null)
                using (Graphics scaledGraphics = scaledFrame == null ? null : Graphics.FromImage(scaledFrame))
                using (MediaFoundationVideoWriter writer = new MediaFoundationVideoWriter(
                    path,
                    encodedSize.Width,
                    encodedSize.Height,
                    this.framesPerSecond,
                    this.bitsPerSecond,
                    GetAudioFormat(computerAudioCapture, microphoneCapture),
                    computerAudioCapture != null && microphoneCapture != null ? 2 : 1))
                {
                    if (computerAudioCapture != null)
                    {
                        computerAudioCapture.Start();
                    }

                    if (microphoneCapture != null)
                    {
                        this.MicrophoneFallback = microphoneCapture.UsedDefaultFallback;
                        microphoneCapture.Start();
                    }

                    PcmAudioFormat audioFormat = GetAudioFormat(computerAudioCapture, microphoneCapture);
                    PcmAudioMixer audioMixer = audioFormat == null
                        ? null
                        : new PcmAudioMixer(
                            audioFormat,
                            computerAudioCapture != null,
                            microphoneCapture != null,
                            this.computerAudioGainPercent,
                            this.microphoneGainPercent,
                            computerAudioCapture != null && microphoneCapture != null);

                    if (scaledGraphics != null)
                    {
                        scaledGraphics.InterpolationMode = InterpolationMode.Bilinear;
                        scaledGraphics.PixelOffsetMode = PixelOffsetMode.Half;
                        scaledGraphics.CompositingQuality = CompositingQuality.HighSpeed;
                        scaledGraphics.SmoothingMode = SmoothingMode.None;
                    }

                    clock = Stopwatch.StartNew();
                    long frameNumber = 0;
                    long nextScheduledSlot = 0;
                    double frameBudgetTicks = Stopwatch.Frequency / (double)this.framesPerSecond;
                    Point captureOrigin = bounds.Location;
                    do
                    {
                        if (this.pauseRequested)
                        {
                            clock.Stop();
                            DrainAudio(computerAudioCapture, microphoneCapture, audioMixer, writer,
                                GetAudioTargetFrame(clock, audioFormat, false));
                            this.isPaused = true;
                            Interlocked.Exchange(ref this.durationTicks, clock.ElapsedTicks);
                            // Keep draining the endpoint buffers while paused, but discard their samples.
                            // Both sources and video resume against the same stopped media clock.
                            do
                            {
                                DiscardAudio(computerAudioCapture, microphoneCapture, audioMixer);
                                if (!this.pauseRequested || this.stopRequested) break;
                                Thread.Sleep(10);
                            } while (true);
                            DiscardAudio(computerAudioCapture, microphoneCapture, audioMixer);
                            this.isPaused = false;
                            if (this.stopRequested) break;
                            clock.Start();
                        }
                        long iterationStarted = clock.ElapsedTicks;
                        DrainAudio(
                            computerAudioCapture,
                            microphoneCapture,
                            audioMixer,
                            writer,
                            GetAudioTargetFrame(clock, audioFormat, true));

                        if (captureOriginProvider != null)
                        {
                            try
                            {
                                captureOrigin = captureOriginProvider();
                            }
                            catch
                            {
                                // Keep the last known position if the target window temporarily disappears.
                            }
                        }

                        long captureStarted = clock.ElapsedTicks;
                        try
                        {
                            graphics.CopyFromScreen(
                                captureOrigin.X,
                                captureOrigin.Y,
                                0,
                                0,
                                bounds.Size,
                                CopyPixelOperation.SourceCopy);
                            if (this.ShowCursor) ClipCursor.Draw(graphics, captureOrigin);
                        }
                        catch (Win32Exception exception)
                        {
                            if (!IsTemporaryProtectedScreenFailure(exception))
                            {
                                throw;
                            }

                            long unavailableAtTicks = clock.ElapsedTicks;
                            long retryTargetTicks;
                            long protectedSkippedSlots;
                            nextScheduledSlot = AdvanceSchedule(
                                nextScheduledSlot,
                                unavailableAtTicks,
                                this.framesPerSecond,
                                Stopwatch.Frequency,
                                out retryTargetTicks,
                                out protectedSkippedSlots);
                            diagnostics.ObserveProtectedScreenRetry(protectedSkippedSlots);

                            if (!this.stopRequested)
                            {
                                // Ctrl+Alt+Delete and UAC prompts run on a protected desktop that GDI
                                // cannot capture. Back off rather than throwing or retrying at 60 Hz.
                                int retryDelay = (int)Math.Max(
                                    100,
                                    (retryTargetTicks - unavailableAtTicks) * 1000L
                                        / Stopwatch.Frequency);
                                Thread.Sleep(Math.Min(retryDelay, 250));
                            }

                            continue;
                        }
                        long captureFinished = clock.ElapsedTicks;
                        long sampleTime = frameNumber == 0
                            ? 0
                            : (clock.ElapsedTicks * 10000000L / Stopwatch.Frequency);
                        Bitmap encodedFrame = frame;
                        if (scaling)
                        {
                            scaledGraphics.DrawImage(
                                frame, 0, 0, encodedSize.Width, encodedSize.Height);
                            encodedFrame = scaledFrame;
                        }

                        long writeStarted = clock.ElapsedTicks;
                        writer.WriteFrame(encodedFrame, sampleTime);
                        long writeFinished = clock.ElapsedTicks;
                        DrainAudio(
                            computerAudioCapture,
                            microphoneCapture,
                            audioMixer,
                            writer,
                            GetAudioTargetFrame(clock, audioFormat, true));
                        long loopFinished = clock.ElapsedTicks;
                        frameNumber++;
                        Interlocked.Exchange(ref this.durationTicks, clock.ElapsedTicks);

                        long nextFrameDeadline = (nextScheduledSlot + 1) * Stopwatch.Frequency
                            / this.framesPerSecond;
                        diagnostics.ObserveFrame(
                            captureFinished - captureStarted,
                            writeFinished - writeStarted,
                            loopFinished,
                            nextFrameDeadline);
                        long nowTicks = clock.ElapsedTicks;
                        long targetTicks;
                        long skippedSlots;
                        nextScheduledSlot = AdvanceSchedule(
                            nextScheduledSlot,
                            nowTicks,
                            this.framesPerSecond,
                            Stopwatch.Frequency,
                            out targetTicks,
                            out skippedSlots);

                        diagnostics.AddSkippedFrameSlots(skippedSlots);
                        PublishFramePressure(
                            loopFinished - iterationStarted,
                            frameBudgetTicks,
                            skippedSlots > 0);

                        int delay = (int)((targetTicks - nowTicks) * 1000L / Stopwatch.Frequency);
                        if (delay > 0 && !this.stopRequested)
                        {
                            Thread.Sleep(Math.Min(delay, 67));
                        }
                    }
                    while (!this.stopRequested);

                    DrainAudio(
                        computerAudioCapture,
                        microphoneCapture,
                        audioMixer,
                        writer,
                        GetAudioTargetFrame(clock, audioFormat, false));

                    writer.FinalizeVideo();
                }
            }
            catch (Exception exception)
            {
                lock (this.stateLock)
                {
                    this.error = exception;
                }
            }
            finally
            {
                    if (clock != null)
                {
                    clock.Stop();
                    Interlocked.Exchange(ref this.durationTicks, clock.ElapsedTicks);
                    diagnostics.Complete(clock.ElapsedTicks);
                }

                TrySaveDiagnostics(diagnostics);
                this.isFinished = true;
            }
        }

        private void TrySaveDiagnostics(RecordingDiagnostics diagnostics)
        {
            try
            {
                string outcome;
                lock (this.stateLock)
                {
                    outcome = this.error == null ? "Completed" : "Failed: " + this.error.Message;
                }

                File.WriteAllText(
                    this.DiagnosticsPath,
                    diagnostics.ToReportText(outcome),
                    new UTF8Encoding(false));
            }
            catch
            {
                // A diagnostic write must never turn a valid recording into a failed recording.
            }
        }

        private WasapiAudioCapture CreateComputerAudioCapture()
        {
            return this.audioMode == RecordingAudioMode.Computer
                || this.audioMode == RecordingAudioMode.ComputerAndMicrophone
                ? new WasapiLoopbackCapture()
                : null;
        }

        private WasapiAudioCapture CreateMicrophoneCapture()
        {
            return this.audioMode == RecordingAudioMode.Microphone
                || this.audioMode == RecordingAudioMode.ComputerAndMicrophone
                ? new WasapiMicrophoneCapture(this.MicrophoneDeviceId)
                : null;
        }

        private static PcmAudioFormat GetAudioFormat(
            WasapiAudioCapture computerAudioCapture,
            WasapiAudioCapture microphoneCapture)
        {
            return computerAudioCapture != null
                ? computerAudioCapture.Format
                : (microphoneCapture == null ? null : microphoneCapture.Format);
        }

        private void DrainAudio(
            WasapiAudioCapture computerAudioCapture,
            WasapiAudioCapture microphoneCapture,
            PcmAudioMixer mixer,
            MediaFoundationVideoWriter writer,
            long targetFrame)
        {
            if (mixer == null)
            {
                return;
            }

            if (computerAudioCapture != null)
            {
                computerAudioCapture.DrainTo(mixer.ComputerSamples);
            }

            if (microphoneCapture != null)
            {
                microphoneCapture.DrainTo(mixer.MicrophoneSamples);
            }

            mixer.WriteThrough(writer, targetFrame);
            PublishAudioPeak(mixer.TakePeak());
        }

        private static void DiscardAudio(WasapiAudioCapture computer, WasapiAudioCapture microphone,
            PcmAudioMixer mixer)
        {
            if (mixer == null) return;
            if (computer != null) computer.DrainTo(mixer.ComputerSamples);
            if (microphone != null) microphone.DrainTo(mixer.MicrophoneSamples);
            mixer.ComputerSamples.Clear();
            mixer.MicrophoneSamples.Clear();
        }

        private void PublishAudioPeak(int peak)
        {
            int current = this.pendingAudioPeak;
            while (peak > current)
            {
                int previous = Interlocked.CompareExchange(ref this.pendingAudioPeak, peak, current);
                if (previous == current)
                {
                    return;
                }

                current = previous;
            }
        }

        private void PublishFramePressure(long workTicks, double frameBudgetTicks, bool missedSlot)
        {
            int pressure = frameBudgetTicks <= 0.0
                ? 0
                : (int)Math.Min(1000.0, Math.Round(workTicks * 1000.0 / frameBudgetTicks));
            if (missedSlot)
            {
                pressure = 1000;
            }
            int current = this.pendingFramePressurePermille;
            while (pressure > current)
            {
                int previous = Interlocked.CompareExchange(
                    ref this.pendingFramePressurePermille,
                    pressure,
                    current);
                if (previous == current)
                {
                    return;
                }

                current = previous;
            }
        }

        private static long GetAudioTargetFrame(
            Stopwatch clock,
            PcmAudioFormat format,
            bool allowCaptureDelay)
        {
            if (clock == null || format == null)
            {
                return 0;
            }

            long elapsedFrames = clock.ElapsedTicks * format.SampleRate / Stopwatch.Frequency;
            if (!allowCaptureDelay)
            {
                return elapsedFrames;
            }

            long delayFrames = (long)format.SampleRate * AudioWriteDelayMilliseconds / 1000L;
            return Math.Max(0, elapsedFrames - delayFrames);
        }
    }

    internal sealed class RecordingDiagnostics
    {
        private readonly int requestedFramesPerSecond;
        private readonly int width;
        private readonly int height;
        private readonly int bitsPerSecond;
        private long totalCaptureTicks;
        private long maximumCaptureTicks;
        private long totalWriteTicks;
        private long maximumWriteTicks;
        private long longestLatenessTicks;
        private long elapsedTicks;

        public RecordingDiagnostics(int requestedFramesPerSecond, int width, int height, int bitsPerSecond)
        {
            this.requestedFramesPerSecond = requestedFramesPerSecond;
            this.width = width;
            this.height = height;
            this.bitsPerSecond = bitsPerSecond;
        }

        public long FrameCount { get; private set; }

        public long MissedDeadlineCount { get; private set; }

        public long SkippedFrameSlots { get; private set; }

        public long ProtectedScreenRetryCount { get; private set; }

        public long ProtectedScreenSkippedFrameSlots { get; private set; }

        public double AchievedFramesPerSecond
        {
            get
            {
                return this.elapsedTicks <= 0
                    ? 0.0
                    : this.FrameCount * (double)Stopwatch.Frequency / this.elapsedTicks;
            }
        }

        public double AverageCaptureMilliseconds
        {
            get { return AverageMilliseconds(this.totalCaptureTicks, this.FrameCount); }
        }

        public double MaximumCaptureMilliseconds
        {
            get { return TicksToMilliseconds(this.maximumCaptureTicks); }
        }

        public double AverageWriteMilliseconds
        {
            get { return AverageMilliseconds(this.totalWriteTicks, this.FrameCount); }
        }

        public double MaximumWriteMilliseconds
        {
            get { return TicksToMilliseconds(this.maximumWriteTicks); }
        }

        public double LongestLatenessMilliseconds
        {
            get { return TicksToMilliseconds(this.longestLatenessTicks); }
        }

        public int RequestedFramesPerSecond
        {
            get { return this.requestedFramesPerSecond; }
        }

        // Only frames the encoder actually rejected. Capture-side pacing that delivers fewer
        // frames than requested is not a drop and must never be reported as one.
        public long EncoderDropCount { get; private set; }

        public void ObserveEncoderDrop()
        {
            this.EncoderDropCount++;
        }

        // The wording the user sees: what was actually delivered against what was requested.
        public string DeliverySummary
        {
            get
            {
                return FormatDelivered(this.AchievedFramesPerSecond)
                    + " new FPS / " + this.requestedFramesPerSecond + " max";
            }
        }

        internal static string FormatDelivered(double achievedFramesPerSecond)
        {
            double value = achievedFramesPerSecond < 0.0 ? 0.0 : achievedFramesPerSecond;
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }

        // A clip reports what was actually delivered. Fewer new frames than requested is pacing,
        // not an encoder drop, so only a real writer rejection is ever called a drop.
        internal static string DescribeDelivery(RecordingDiagnostics diagnostics)
        {
            if (diagnostics == null)
            {
                return null;
            }

            string summary = diagnostics.DeliverySummary;
            if (diagnostics.EncoderDropCount > 0)
            {
                summary += " — " + diagnostics.EncoderDropCount + " encoder drop"
                    + (diagnostics.EncoderDropCount == 1 ? "" : "s");
            }

            return summary;
        }

        public void ObserveFrame(
            long captureTicks,
            long writeTicks,
            long completedAtTicks,
            long nextFrameDeadlineTicks)
        {
            this.FrameCount++;
            this.totalCaptureTicks += captureTicks;
            this.totalWriteTicks += writeTicks;
            this.maximumCaptureTicks = Math.Max(this.maximumCaptureTicks, captureTicks);
            this.maximumWriteTicks = Math.Max(this.maximumWriteTicks, writeTicks);

            long lateness = completedAtTicks - nextFrameDeadlineTicks;
            if (lateness > 0)
            {
                this.MissedDeadlineCount++;
                this.longestLatenessTicks = Math.Max(this.longestLatenessTicks, lateness);
            }
        }

        public void Complete(long elapsedTicks)
        {
            this.elapsedTicks = Math.Max(0, elapsedTicks);
        }

        public void AddSkippedFrameSlots(long count)
        {
            this.SkippedFrameSlots += Math.Max(0, count);
        }

        public void ObserveProtectedScreenRetry(long skippedSlots)
        {
            // AdvanceSchedule counts expired future slots, but the unavailable capture attempt
            // itself also occupies one output-timeline slot.
            long safeSkippedSlots = Math.Max(0, skippedSlots) + 1;
            this.ProtectedScreenRetryCount++;
            this.ProtectedScreenSkippedFrameSlots += safeSkippedSlots;
            this.SkippedFrameSlots += safeSkippedSlots;
        }

        public string ToReportText(string outcome)
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("Huck's Snip 'n' Clip recording diagnostics");
            report.AppendLine("Outcome: " + outcome);
            report.AppendLine("Capture backend: GDI CopyFromScreen (CPU bitmap)");
            report.AppendLine("Encoder request: Windows Media Foundation H.264; selected transform identity unavailable");
            report.AppendLine("Frame input: System-memory RGB32");
            report.AppendLine("Resolution: " + this.width + "x" + this.height);
            report.AppendLine("Target bitrate: " + Format(this.bitsPerSecond / 1000000.0) + " Mbps");
            report.AppendLine("Requested FPS: " + this.requestedFramesPerSecond);
            report.AppendLine("Captured input frames: " + this.FrameCount);
            report.AppendLine("Capture-loop duration: " + Format(TicksToMilliseconds(this.elapsedTicks) / 1000.0) + " s");
            report.AppendLine("Achieved input FPS: " + Format(this.AchievedFramesPerSecond));
            report.AppendLine("Delivered: " + this.DeliverySummary);
            report.AppendLine("Encoder drops (writer rejections): " + this.EncoderDropCount);
            report.AppendLine("Missed next-frame deadlines: " + this.MissedDeadlineCount);
            report.AppendLine("Skipped catch-up frame slots: " + this.SkippedFrameSlots);
            report.AppendLine("Protected-screen capture retries: " + this.ProtectedScreenRetryCount);
            report.AppendLine("Protected-screen skipped slots: " + this.ProtectedScreenSkippedFrameSlots);
            report.AppendLine("Longest deadline lateness: " + Format(this.LongestLatenessMilliseconds) + " ms");
            report.AppendLine("Screen copy average / maximum: "
                + Format(this.AverageCaptureMilliseconds) + " / "
                + Format(this.MaximumCaptureMilliseconds) + " ms");
            report.AppendLine("Encoder write average / maximum: "
                + Format(this.AverageWriteMilliseconds) + " / "
                + Format(this.MaximumWriteMilliseconds) + " ms");
            return report.ToString();
        }

        private static double AverageMilliseconds(long totalTicks, long count)
        {
            return count <= 0 ? 0.0 : TicksToMilliseconds(totalTicks) / count;
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static string Format(double value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }
    }
}
