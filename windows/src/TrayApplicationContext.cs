using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed partial class TrayApplicationContext : ApplicationContext
    {
        private const int SnipRegionHotkeyId = (int)CaptureAction.SnipRegion;
        private const int SnipScreenHotkeyId = (int)CaptureAction.SnipScreen;
        private const int ClipScreenHotkeyId = (int)CaptureAction.ClipScreen;
        private const int ClipRegionHotkeyId = (int)CaptureAction.ClipRegion;
        private const int SnipWindowHotkeyId = (int)CaptureAction.SnipWindow;
        private const int ClipWindowHotkeyId = (int)CaptureAction.ClipWindow;
        private const int ChordSecondHotkeyId = 1000;
        private const int ChordValidationHotkeyId = 1001;
        private readonly SettingsStore settingsStore;
        private readonly CaptureSaveService saveService;
        private readonly HotkeyWindow hotkeyWindow;
        private readonly NotifyIcon notifyIcon;
        private readonly Icon trayIcon;
        private readonly MeterSmoother inputMeter;
        private readonly MeterSmoother strainMeter;
        private readonly SystemLoadSampler systemLoadSampler;
        private readonly Timer clipStatusTimer;
        private readonly Timer shortcutChordTimer;
        private ContextMenuStrip trayMenu;
        private TrayHelpFlyout trayHelpFlyout;
        private AppSettings settings;
        private bool captureInProgress;
        private ScreenClipRecorder clipRecorder;
        private RegionRecordingShade regionShade;
        private CaptureDestination clipDestination;
        private Screen clipDisplay;
        private bool clipStopRequested;
        private Icon recordingMeterIcon;
        private int activeClipHotkeyId;
        private string lastSavedPath;
        private string clipFilenameStem;
        private CaptureAction? pendingChordAction;
        private KeyboardShortcutCaptureSession shortcutCaptureSession;
        private CaptureActionMenuRow shortcutCaptureRow;
        private ContextMenuStrip shortcutCaptureMenu;
        private Dictionary<CaptureAction, ShortcutBinding> shortcutCapturePrevious;
        private CaptureAction? shortcutCaptureAction;

        public TrayApplicationContext()
        {
            this.settingsStore = SettingsStore.CreateDefault();
            this.settings = this.settingsStore.Load();
            StartupManager.RepairStaleEntry();
            this.saveService = CaptureSaveService.CreateDefault();
            this.hotkeyWindow = new HotkeyWindow();
            this.hotkeyWindow.HotkeyPressed += HandleHotkeyPressed;

            this.trayIcon = TrayIconFactory.Create();
            this.inputMeter = new MeterSmoother();
            this.strainMeter = new MeterSmoother();
            this.systemLoadSampler = new SystemLoadSampler();
            this.notifyIcon = new NotifyIcon();
            this.notifyIcon.Icon = this.trayIcon;
            this.notifyIcon.Text = "Huck’s Snip ’n’ Clip";
            this.notifyIcon.Visible = true;
            this.notifyIcon.MouseClick += HandleNotifyIconMouseClick;
            this.clipStatusTimer = new Timer();
            this.clipStatusTimer.Interval = 100;
            this.clipStatusTimer.Tick += HandleClipStatusTick;
            this.shortcutChordTimer = new Timer();
            this.shortcutChordTimer.Interval = 1500;
            this.shortcutChordTimer.Tick += delegate { CancelPendingShortcutChord(); };
            RebuildContextMenu();

            RegisterConfiguredShortcuts();
            RefreshOutputShortcuts();
        }

        private void HandleNotifyIconMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            if (this.trayHelpFlyout == null || this.trayHelpFlyout.IsDisposed)
            {
                this.trayHelpFlyout = new TrayHelpFlyout();
            }
            this.trayHelpFlyout.ShowAboveNotificationArea(
                Cursor.Position,
                this.hotkeyWindow.WindowHandle);
        }

        protected override void ExitThreadCore()
        {
            this.clipStatusTimer.Stop();
            CancelPendingShortcutChord();
            CancelTrayShortcutCapture(false);
            this.shortcutChordTimer.Dispose();
            DismissFloatingRecordingController();
            if (this.clipRecorder != null)
            {
                this.clipRecorder.Dispose();
                this.clipRecorder = null;
            }

            this.notifyIcon.Visible = false;
            if (this.trayMenu != null)
            {
                this.trayMenu.Dispose();
                this.trayMenu = null;
            }
            if (this.trayHelpFlyout != null)
            {
                this.trayHelpFlyout.Dispose();
                this.trayHelpFlyout = null;
            }
            this.notifyIcon.Dispose();
            this.clipStatusTimer.Dispose();
            this.hotkeyWindow.Dispose();
            this.trayIcon.Dispose();
            if (this.recordingMeterIcon != null)
            {
                this.recordingMeterIcon.Dispose();
            }
            base.ExitThreadCore();
        }

        private void RegisterConfiguredShortcuts()
        {
            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                ShortcutBinding binding = this.settings.GetShortcut(action);
                if (!RegisterShortcut(action, binding))
                {
                    ShowNotification(
                        "Shortcut unavailable",
                        binding.ToDisplayString() + " is already being used. "
                            + ShortcutCatalog.GetName(action) + " is still available from the tray menu.",
                        ToolTipIcon.Warning);
                }
            }

            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                ShortcutBinding binding = this.settings.GetShortcut(action);
                if (binding.HasSecondStroke && !CanRegisterSecondStroke(binding))
                {
                    this.hotkeyWindow.Unregister((int)action);
                    ShowNotification(
                        "Shortcut unavailable",
                        binding.ToDisplayString() + " could not reserve its second step. "
                            + ShortcutCatalog.GetName(action) + " is still available from the tray menu.",
                        ToolTipIcon.Warning);
                }
            }
        }

        private bool RegisterShortcut(CaptureAction action, ShortcutBinding binding)
        {
            return this.hotkeyWindow.Register(
                (int)action,
                binding.Modifiers | NativeMethods.MOD_NOREPEAT,
                binding.Key);
        }

        private void HandleHotkeyPressed(object sender, HotkeyPressedEventArgs e)
        {
            if (e.Id >= OutputHotkeyBase && e.Id < OutputHotkeyBase + 1000)
            {
                HandleOutputShortcut(e.Id);
                return;
            }
            if (e.Id >= RecordingControlHotkeyBase && e.Id < RecordingControlHotkeyBase + 1000)
            {
                HandleRecordingControlShortcut(e.Id);
                return;
            }
            if (e.Id == ChordSecondHotkeyId && this.pendingChordAction.HasValue)
            {
                CaptureAction completedAction = this.pendingChordAction.Value;
                CancelPendingShortcutChord();
                InvokeCaptureAction(completedAction);
                return;
            }

            CancelPendingShortcutChord();
            CaptureAction action = (CaptureAction)e.Id;
            ShortcutBinding binding = this.settings.GetShortcut(action);
            if (binding.HasSecondStroke)
            {
                BeginShortcutChord(action, binding);
                return;
            }

            InvokeCaptureAction(action);
        }

        private void InvokeCaptureAction(CaptureAction action)
        {
            if (action == CaptureAction.SnipRegion)
            {
                BeginRegionSnip();
            }
            else if (action == CaptureAction.SnipScreen)
            {
                BeginScreenSnip();
            }
            else if (action == CaptureAction.ClipScreen)
            {
                ToggleScreenClip();
            }
            else if (action == CaptureAction.ClipRegion)
            {
                ToggleRegionClip();
            }
            else if (action == CaptureAction.SnipWindow)
            {
                BeginWindowSnip();
            }
            else if (action == CaptureAction.ClipWindow)
            {
                ToggleWindowClip();
            }
        }

        private void BeginShortcutChord(CaptureAction action, ShortcutBinding binding)
        {
            if (!this.hotkeyWindow.Register(
                    ChordSecondHotkeyId,
                    binding.SecondModifiers | NativeMethods.MOD_NOREPEAT,
                    binding.SecondKey))
            {
                ShowNotification(
                    "Shortcut chord unavailable",
                    binding.ToDisplayString() + " could not listen for its second step.",
                    ToolTipIcon.Warning);
                return;
            }

            this.pendingChordAction = action;
            this.shortcutChordTimer.Stop();
            this.shortcutChordTimer.Start();
            ShortcutBinding second = new ShortcutBinding
            {
                Modifiers = binding.SecondModifiers,
                Key = binding.SecondKey
            };
            this.notifyIcon.Text = Shorten("Huck’s Snip ’n’ Clip - press " + second.ToDisplayString(), 63);
        }

        private void CancelPendingShortcutChord()
        {
            this.shortcutChordTimer.Stop();
            this.hotkeyWindow.Unregister(ChordSecondHotkeyId);
            this.pendingChordAction = null;
            this.notifyIcon.Text = "Huck’s Snip ’n’ Clip";
        }

        private bool CanRegisterSecondStroke(ShortcutBinding binding)
        {
            if (!binding.HasSecondStroke)
            {
                return true;
            }

            bool registered = this.hotkeyWindow.Register(
                ChordValidationHotkeyId,
                binding.SecondModifiers | NativeMethods.MOD_NOREPEAT,
                binding.SecondKey);
            if (registered)
            {
                this.hotkeyWindow.Unregister(ChordValidationHotkeyId);
            }

            return registered;
        }

        private void ToggleScreenClip()
        {
            if (this.clipRecorder != null && this.clipRecorder.IsRunning)
            {
                if (this.activeClipHotkeyId == ClipScreenHotkeyId)
                {
                    StopClip();
                }

                return;
            }

            BeginScreenClip();
        }

        private void ToggleRegionClip()
        {
            if (this.clipRecorder != null && this.clipRecorder.IsRunning)
            {
                if (this.activeClipHotkeyId == ClipRegionHotkeyId)
                {
                    StopClip();
                }

                return;
            }

            BeginRegionClip();
        }

        private void ToggleWindowClip()
        {
            if (this.clipRecorder != null && this.clipRecorder.IsRunning)
            {
                if (this.activeClipHotkeyId == ClipWindowHotkeyId)
                {
                    StopClip();
                }

                return;
            }

            BeginWindowClip();
        }

        private void BeginScreenClip()
        {
            if (!TryBeginCapture())
            {
                return;
            }

            try
            {
                Screen display = ScreenCaptureService.GetDisplayAtCursor();
                using (CountdownForm countdown = new CountdownForm(display))
                {
                    if (countdown.ShowDialog() != DialogResult.OK)
                    {
                        return;
                    }
                }

                StartClip(display.Bounds, display, ClipScreenHotkeyId);
            }
            catch (Exception exception)
            {
                if (this.clipRecorder != null)
                {
                    this.clipRecorder.Dispose();
                    this.clipRecorder = null;
                }

                DismissFloatingRecordingController();
                this.clipDestination = null;
                this.clipDisplay = null;
                this.activeClipHotkeyId = 0;

                ShowNotification("Clip could not start", Shorten(exception.Message, 180), ToolTipIcon.Error);
            }
            finally
            {
                FinishCapturePreparation();
            }
        }

        private void BeginRegionClip()
        {
            if (!TryBeginCapture())
            {
                return;
            }

            try
            {
                Rectangle selectedRegion;
                using (Bitmap desktop = ScreenCaptureService.CaptureVirtualDesktop())
                using (RegionSelectionForm selector = new RegionSelectionForm(desktop, "Enter continues"))
                {
                    DialogResult result = selector.ShowDialog();
                    if (result != DialogResult.OK || selector.SelectedRegion.IsEmpty)
                    {
                        return;
                    }

                    Rectangle virtualDesktop = SystemInformation.VirtualScreen;
                    selectedRegion = new Rectangle(
                        virtualDesktop.Left + selector.SelectedRegion.Left,
                        virtualDesktop.Top + selector.SelectedRegion.Top,
                        selector.SelectedRegion.Width,
                        selector.SelectedRegion.Height);
                }

                selectedRegion = ScreenClipRecorder.NormalizeVideoBounds(selectedRegion);
                Screen display = Screen.FromRectangle(selectedRegion);
                using (CountdownForm countdown = new CountdownForm(selectedRegion))
                {
                    if (countdown.ShowDialog() != DialogResult.OK)
                    {
                        return;
                    }
                }

                StartClip(selectedRegion, display, ClipRegionHotkeyId);
            }
            catch (Exception exception)
            {
                if (this.clipRecorder != null)
                {
                    this.clipRecorder.Dispose();
                    this.clipRecorder = null;
                }

                DismissRegionShade();
                DismissFloatingRecordingController();
                this.clipDestination = null;
                this.clipDisplay = null;
                this.activeClipHotkeyId = 0;

                ShowNotification("Region clip could not start", Shorten(exception.Message, 180), ToolTipIcon.Error);
            }
            finally
            {
                FinishCapturePreparation();
            }
        }

        private void BeginWindowClip()
        {
            if (!TryBeginCapture())
            {
                return;
            }

            try
            {
                IntPtr windowHandle = ScreenCaptureService.GetForegroundCapturableWindow();
                Rectangle initialBounds;
                if (!ScreenCaptureService.TryGetWindowBounds(windowHandle, out initialBounds))
                {
                    throw new InvalidOperationException("The active window cannot be captured.");
                }

                initialBounds = ScreenClipRecorder.NormalizeVideoBounds(initialBounds);
                Screen display = Screen.FromRectangle(initialBounds);
                using (CountdownForm countdown = new CountdownForm(initialBounds))
                {
                    if (countdown.ShowDialog() != DialogResult.OK)
                    {
                        return;
                    }
                }

                Point lastOrigin = initialBounds.Location;
                Func<Point> originProvider = delegate
                {
                    Rectangle currentBounds;
                    if (ScreenCaptureService.TryGetWindowBounds(windowHandle, out currentBounds))
                    {
                        lastOrigin = currentBounds.Location;
                    }

                    return lastOrigin;
                };

                StartClip(initialBounds, display, ClipWindowHotkeyId, originProvider);
            }
            catch (Exception exception)
            {
                if (this.clipRecorder != null)
                {
                    this.clipRecorder.Dispose();
                    this.clipRecorder = null;
                }

                DismissFloatingRecordingController();
                this.clipDestination = null;
                this.clipDisplay = null;
                this.activeClipHotkeyId = 0;
                ShowNotification("Window clip could not start", Shorten(exception.Message, 180), ToolTipIcon.Error);
            }
            finally
            {
                FinishCapturePreparation();
            }
        }

        private void StartClip(Rectangle bounds, Screen display, int hotkeyId)
        {
            StartClip(bounds, display, hotkeyId, null);
        }

        private void StartClip(
            Rectangle bounds,
            Screen display,
            int hotkeyId,
            Func<Point> captureOriginProvider)
        {
            this.clipDestination = ResolveAvailableDestination();
            this.clipFilenameStem = ReserveFilename("Clip", DateTime.Now);
            this.clipStopRequested = false;
            this.activeClipHotkeyId = hotkeyId;
            this.clipDisplay = display;
            int frameRate = ResolveRecordingFrameRate(display);
            RecordingAudioMode audioMode = this.settings.RecordingAudioMode;
            Rectangle videoBounds = ScreenClipRecorder.NormalizeVideoBounds(bounds);
            int bitsPerSecond = RecordingQualitySettings.CalculateBitsPerSecond(
                this.settings.RecordingQuality, videoBounds.Width, videoBounds.Height, frameRate);
            this.clipRecorder = new ScreenClipRecorder(
                frameRate,
                audioMode,
                this.settings.ComputerAudioGainPercent,
                this.settings.MicrophoneGainPercent,
                bitsPerSecond);
            this.clipRecorder.ShowCursor = this.settings.ShowCursorInClips;
            this.clipRecorder.ResolutionCeiling = RecordingResolutionSettings.Normalize(this.settings.RecordingResolution);
            this.clipRecorder.MicrophoneDeviceId = this.settings.MicrophoneDeviceId;
            this.clipRecorder.Start(bounds, captureOriginProvider);
            // Only a Region clip needs the guide: Screen and Window targets are already obvious.
            if (hotkeyId == ClipRegionHotkeyId)
            {
                this.regionShade = RegionRecordingShade.TryShow(bounds);
            }

            // Both recording routes come up only now that a clip is genuinely running.
            ShowFloatingRecordingController(display);
            this.notifyIcon.Text = "Huck’s Snip ’n’ Clip — Recording " + frameRate + " FPS"
                + GetRecordingAudioTrayLabel(audioMode);
            this.inputMeter.Reset();
            this.strainMeter.Reset();
            this.systemLoadSampler.Reset();
            ReplaceRecordingMeterIcon(0.0f, 0.0f);
            this.clipStatusTimer.Start();
            RebuildContextMenu();
        }

        private void DismissRegionShade()
        {
            if (this.regionShade == null) return;
            this.regionShade.Dispose();
            this.regionShade = null;
        }

        private void StopClip()
        {
            if (this.clipRecorder == null || !this.clipRecorder.IsRunning || this.clipStopRequested)
            {
                return;
            }

            this.clipStopRequested = true;
            this.clipRecorder.Stop();
            SetRecordingStatusIcon(false);
            // Finalization is not a state anything can be asked to do twice: the controls go
            // disabled and the recording-only shortcuts are released immediately.
            if (this.floatingController != null) this.floatingController.SetFinishing();
            RebuildContextMenu();
        }

        private void ToggleClipPause()
        {
            if (this.clipRecorder == null || !this.clipRecorder.IsRunning || this.clipStopRequested) return;
            this.clipRecorder.SetPaused(!this.clipRecorder.PauseRequested);
            this.inputMeter.Reset();
            if (this.floatingController != null)
            {
                this.floatingController.SetPaused(this.clipRecorder.PauseRequested);
            }

            RebuildContextMenu();
        }

        private void SetRecordingStatusIcon(bool paused)
        {
            Icon previous = this.recordingMeterIcon;
            this.recordingMeterIcon = TrayIconFactory.CreateStatus(paused);
            this.notifyIcon.Icon = this.recordingMeterIcon;
            this.notifyIcon.Text = paused ? "Huck’s Snip ’n’ Clip — Paused" : "Huck’s Snip ’n’ Clip — Finishing";
            if (previous != null) previous.Dispose();
        }

        private void HandleClipStatusTick(object sender, EventArgs e)
        {
            ScreenClipRecorder recorder = this.clipRecorder;
            if (recorder == null)
            {
                return;
            }

            if (!recorder.IsFinished)
            {
                if (this.clipStopRequested || recorder.PauseRequested)
                {
                    SetRecordingStatusIcon(!this.clipStopRequested);
                    return;
                }
                UpdateRecordingMeters(recorder);
                return;
            }

            this.clipStatusTimer.Stop();
            this.clipRecorder = null;
            DismissRegionShade();
            DismissFloatingRecordingController();
            this.clipDisplay = null;
            this.notifyIcon.Text = "Huck’s Snip ’n’ Clip";
            this.notifyIcon.Icon = this.trayIcon;
            if (this.recordingMeterIcon != null)
            {
                this.recordingMeterIcon.Dispose();
                this.recordingMeterIcon = null;
            }

            Exception recordingError = recorder.Error;
            string workingPath = recorder.WorkingPath;
            RecordingDiagnostics clipDiagnostics = recorder.Diagnostics;
            recorder.Dispose();

            try
            {
                if (recordingError != null)
                {
                    throw recordingError;
                }

                CaptureSaveResult saveResult = this.saveService.RouteCompletedClip(
                    workingPath,
                    this.clipDestination.Path,
                    this.clipFilenameStem);
                string clipboardNote = CopyFileToClipboard(saveResult);
                HandleSaveResult(
                    saveResult,
                    this.clipDestination,
                    "Clip",
                    JoinNotes(RecordingDiagnostics.DescribeDelivery(clipDiagnostics), clipboardNote));
                if (recorder.MicrophoneFallback)
                    ShowNotification("Microphone fallback", "The selected microphone was unavailable. This clip used System Default.", ToolTipIcon.Warning);
            }
            catch (Exception exception)
            {
                ShowNotification("Clip failed", Shorten(exception.Message, 180), ToolTipIcon.Error);
            }
            finally
            {
                this.clipDestination = null;
                this.clipStopRequested = false;
                this.activeClipHotkeyId = 0;
                RebuildContextMenu();
            }
        }

        private void UpdateRecordingMeters(ScreenClipRecorder recorder)
        {
            float rawInput = recorder.HasAudio
                ? Math.Min(1.0f, recorder.TakeAudioPeak() / 32767.0f)
                : 0.0f;
            // The right H post is a real CPU gauge. Recorder deadline pressure is useful
            // diagnostics, but it must not impersonate CPU usage in the public meter.
            float rawStrain = this.systemLoadSampler.Sample();
            recorder.TakeFramePressure();
            float input = this.inputMeter.Update(rawInput, this.clipStatusTimer.Interval);
            float strain = this.strainMeter.Update(rawStrain, this.clipStatusTimer.Interval);
            ReplaceRecordingMeterIcon(input, strain);
            UpdateFloatingControllerMeters(input, strain);
            this.notifyIcon.Text = Shorten(
                "Huck’s Snip ’n’ Clip — Mic " + (int)Math.Round(input * 100.0f)
                    + "% — CPU " + (int)Math.Round(strain * 100.0f) + "%",
                63);
        }

        private void ReplaceRecordingMeterIcon(float input, float strain)
        {
            Icon replacement = TrayIconFactory.CreateRecording(input, strain);
            this.notifyIcon.Icon = replacement;
            Icon previous = this.recordingMeterIcon;
            this.recordingMeterIcon = replacement;
            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private void BeginRegionSnip()
        {
            if (!TryBeginCapture())
            {
                return;
            }

            try
            {
                using (Bitmap desktop = ScreenCaptureService.CaptureVirtualDesktop())
                using (RegionSelectionForm selector = new RegionSelectionForm(desktop))
                {
                    DialogResult result = selector.ShowDialog();
                    if (result != DialogResult.OK || selector.SelectedRegion.IsEmpty)
                    {
                        return;
                    }

                    using (Bitmap snip = ScreenCaptureService.Crop(desktop, selector.SelectedRegion))
                    {
                        SaveSnip(snip);
                    }
                }
            }
            catch (Exception exception)
            {
                ShowNotification("Snip failed", Shorten(exception.Message, 180), ToolTipIcon.Error);
            }
            finally
            {
                FinishCapturePreparation();
            }
        }

        private void BeginScreenSnip()
        {
            if (!TryBeginCapture())
            {
                return;
            }

            try
            {
                using (Bitmap snip = ScreenCaptureService.CaptureDisplayAtCursor())
                {
                    SaveSnip(snip);
                }
            }
            catch (Exception exception)
            {
                ShowNotification("Screen snip failed", Shorten(exception.Message, 180), ToolTipIcon.Error);
            }
            finally
            {
                FinishCapturePreparation();
            }
        }

        private void BeginWindowSnip()
        {
            if (!TryBeginCapture())
            {
                return;
            }

            try
            {
                IntPtr windowHandle = ScreenCaptureService.GetForegroundCapturableWindow();
                Rectangle bounds;
                if (!ScreenCaptureService.TryGetWindowBounds(windowHandle, out bounds))
                {
                    throw new InvalidOperationException("The active window cannot be captured.");
                }

                using (Bitmap snip = ScreenCaptureService.CaptureBounds(bounds))
                {
                    SaveSnip(snip);
                }
            }
            catch (Exception exception)
            {
                ShowNotification("Window snip failed", Shorten(exception.Message, 180), ToolTipIcon.Error);
            }
            finally
            {
                FinishCapturePreparation();
            }
        }

        private bool TryBeginCapture()
        {
            if (this.captureInProgress || (this.clipRecorder != null && this.clipRecorder.IsRunning))
            {
                return false;
            }

            this.captureInProgress = true;
            RefreshOutputShortcuts();
            RebuildContextMenu();
            return true;
        }

        private void SaveSnip(Bitmap snip)
        {
            CaptureDestination destination = ResolveAvailableDestination();
            CaptureSaveResult saveResult = this.saveService.SavePng(snip, destination.Path, ReserveFilename("Snip", DateTime.Now));
            string clipboardNote = null;
            if (saveResult.Succeeded)
            {
                Exception clipboardError;
                if (CaptureClipboard.TryCopyImage(snip, out clipboardError))
                    clipboardNote = "Copied image to clipboard.";
                else
                    ShowNotification("Snip saved, clipboard unavailable", Shorten(clipboardError.Message, 180), ToolTipIcon.Warning);
            }
            HandleSaveResult(saveResult, destination, "Snip", clipboardNote);
        }

        private string CopyFileToClipboard(CaptureSaveResult result)
        {
            if (result == null || !result.Succeeded) return null;
            Exception clipboardError;
            if (CaptureClipboard.TryCopyFile(result.SavedPath, out clipboardError))
                return "Copied finished file to clipboard.";
            ShowNotification("Clip saved, clipboard unavailable", Shorten(clipboardError.Message, 180), ToolTipIcon.Warning);
            return null;
        }

        private static string JoinNotes(string first, string second)
        {
            if (String.IsNullOrWhiteSpace(first)) return second;
            if (String.IsNullOrWhiteSpace(second)) return first;
            return first + Environment.NewLine + second;
        }

        private CaptureDestination GetActiveDestination()
        {
            CaptureDestination destination = this.settings.GetActiveDestination();
            if (destination == null)
            {
                throw new InvalidOperationException("No capture destination is configured.");
            }

            return destination;
        }

        /// <summary>
        /// The destination a capture should actually use. If the active output has gone away - an
        /// external drive ejected, a network share offline - move to another configured output
        /// rather than quietly routing the capture into the hidden Recovery folder. Recovery is the
        /// last resort for when nothing is reachable, not the first answer to a missing drive.
        /// </summary>
        private CaptureDestination ResolveAvailableDestination()
        {
            CaptureDestination active = GetActiveDestination();
            if (IsDestinationAvailable(active))
            {
                return active;
            }

            foreach (CaptureDestination candidate in this.settings.Destinations)
            {
                if (candidate.Id == active.Id || !IsDestinationAvailable(candidate))
                {
                    continue;
                }

                string unavailable = active.Name;
                try
                {
                    SelectDestination(candidate.Id);
                }
                catch
                {
                    // If the switch cannot be persisted, still route this capture somewhere real.
                    return candidate;
                }

                // Always announce this one, even with routine notices off: the user did not ask
                // for it and needs to know where the capture actually landed.
                ShowNotification(
                    "Output switched to " + candidate.Name,
                    unavailable + " is unavailable, so captures now go to " + candidate.Name + ".",
                    ToolTipIcon.Warning);
                return candidate;
            }

            // Nothing is reachable. Let the save service fall back to Recovery and say so.
            return active;
        }

        private static bool IsDestinationAvailable(CaptureDestination destination)
        {
            if (destination == null || String.IsNullOrWhiteSpace(destination.Path))
            {
                return false;
            }

            try
            {
                return Directory.Exists(destination.Path);
            }
            catch
            {
                return false;
            }
        }

        private void HandleSaveResult(CaptureSaveResult result, CaptureDestination destination, string captureName)
        {
            HandleSaveResult(result, destination, captureName, null);
        }

        private void HandleSaveResult(
            CaptureSaveResult result,
            CaptureDestination destination,
            string captureName,
            string deliveryNote)
        {
            if (!result.Succeeded)
            {
                ShowNotification(
                    captureName + " could not be saved",
                    result.Error == null
                        ? "The destination and recovery folder were unavailable."
                        : Shorten(result.Error.Message, 180),
                    ToolTipIcon.Error);
                return;
            }

            this.lastSavedPath = result.SavedPath;
            RebuildContextMenu();

            if (result.UsedRecovery)
            {
                ShowNotification(
                    captureName + " saved to Recovery",
                    destination.Name + " was unavailable. Your " + captureName.ToLowerInvariant() + " is safe at " + result.SavedPath,
                    ToolTipIcon.Warning);
            }
            else
            {
                ShowNotification(
                    captureName + " saved to " + destination.Name,
                    String.IsNullOrEmpty(deliveryNote)
                        ? result.SavedPath
                        : result.SavedPath + Environment.NewLine + deliveryNote,
                    ToolTipIcon.Info);
            }
        }

        private void AddDestination()
        {
            using (FolderBrowserDialog folderDialog = new FolderBrowserDialog())
            {
                CaptureDestination activeDestination = GetActiveDestination();
                folderDialog.Description = "Choose a folder to add as a named destination.";
                folderDialog.ShowNewFolderButton = true;
                if (Directory.Exists(activeDestination.Path))
                {
                    folderDialog.SelectedPath = activeDestination.Path;
                }

                if (folderDialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                CaptureDestination existing = this.settings.FindDestinationByPath(folderDialog.SelectedPath);
                if (existing != null)
                {
                    SelectDestination(existing.Id);
                    ShowRoutineNotification("Destination selected", existing.Name);
                    return;
                }

                string suggestedName = CreateUniqueDestinationName(
                    SettingsStore.GetSuggestedName(folderDialog.SelectedPath));

                using (DestinationNameForm nameDialog = new DestinationNameForm(suggestedName))
                {
                    if (nameDialog.ShowDialog() != DialogResult.OK)
                    {
                        return;
                    }

                    CaptureDestination destination = SettingsStore.CreateDestination(
                        folderDialog.SelectedPath,
                        CreateUniqueDestinationName(nameDialog.DestinationName));

                    this.settings.Destinations.Add(destination);
                    this.settings.ActiveDestinationId = destination.Id;
                    this.settingsStore.Save(this.settings);
                    RebuildContextMenu();
                    ShowRoutineNotification(
                        "Destination added",
                        destination.Name + " — " + destination.Path);
                }
            }
        }

        private string CreateUniqueDestinationName(string requestedName)
        {
            string baseName = String.IsNullOrWhiteSpace(requestedName) ? "Destination" : requestedName.Trim();
            string candidate = baseName;
            int suffix = 2;

            while (DestinationNameExists(candidate))
            {
                candidate = baseName + " (" + suffix + ")";
                suffix++;
            }

            return candidate;
        }

        private bool DestinationNameExists(string name)
        {
            foreach (CaptureDestination destination in this.settings.Destinations)
            {
                if (String.Equals(destination.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void SelectDestination(string destinationId)
        {
            this.settings.ActiveDestinationId = destinationId;
            this.settingsStore.Save(this.settings);
            RebuildContextMenu();
        }

        private int ResolveRecordingFrameRate(Screen display)
        {
            int configured = SettingsStore.NormalizeFrameRate(this.settings.RecordingFrameRate);
            return configured == 0
                ? ScreenCaptureService.GetDisplayRefreshRate(display)
                : configured;
        }

        private void SelectRecordingFrameRate(int frameRate)
        {
            if (!TrySaveRecordingPreferences(frameRate, this.settings.RecordingQuality))
            {
                return;
            }

            string selection = frameRate == 0
                ? "Match Display Refresh"
                : frameRate + " FPS";
            ShowRoutineNotification("Recording frame rate selected", selection);
        }

        private bool TrySaveRecordingPreferences(int frameRate, RecordingQuality quality)
        {
            if (this.captureInProgress || this.clipRecorder != null)
            {
                return false;
            }

            try
            {
                this.settingsStore.SaveRecordingPreferences(this.settings, frameRate, quality);
            }
            catch (Exception exception)
            {
                CopyableDialog.Show(
                    "Recording settings could not be saved. Your previous settings are still active.\n\n"
                        + Shorten(exception.Message, 300),
                    "Recording Settings Could Not Be Saved",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }

            RebuildContextMenu();
            return true;
        }

        private void SelectRecordingQuality(RecordingQuality quality)
        {
            if (TrySaveRecordingPreferences(this.settings.RecordingFrameRate, quality))
            {
            ShowRoutineNotification("Recording quality selected", GetRecordingQualityLabel(quality));
            }
        }

        private void SelectRecordingAudioMode(RecordingAudioMode audioMode)
        {
            this.settings.RecordingAudioMode = audioMode;
            this.settingsStore.Save(this.settings);
            RebuildContextMenu();
            ShowRoutineNotification(
                "Recording audio selected",
                GetRecordingAudioLabel(audioMode));
        }

        private void SelectRecordingAudioGain(bool computerAudio, int gainPercent)
        {
            int normalized = SettingsStore.NormalizeAudioGainPercent(gainPercent);
            if (computerAudio)
            {
                this.settings.ComputerAudioGainPercent = normalized;
            }
            else
            {
                this.settings.MicrophoneGainPercent = normalized;
            }

            this.settingsStore.Save(this.settings);
            RebuildContextMenu();
            ShowRoutineNotification(
                computerAudio ? "Computer audio gain selected" : "Microphone gain selected",
                normalized + "%");
        }

        private void BeginTrayShortcutCapture(
            CaptureAction action,
            CaptureActionMenuRow row,
            ContextMenuStrip menu)
        {
            if ((this.clipRecorder != null && this.clipRecorder.IsRunning)
                || this.shortcutCaptureSession != null)
            {
                return;
            }

            Dictionary<CaptureAction, ShortcutBinding> previous = this.settings.CloneShortcuts();
            UnregisterAllShortcuts();

            KeyboardShortcutCaptureSession session = new KeyboardShortcutCaptureSession();
            this.shortcutCaptureSession = session;
            RefreshOutputShortcuts();
            this.shortcutCaptureRow = row;
            this.shortcutCaptureMenu = menu;
            this.shortcutCapturePrevious = previous;
            this.shortcutCaptureAction = action;
            session.ChordWaitChanged += delegate(object sender, ShortcutChordWaitEventArgs e)
            {
                this.notifyIcon.Text = Shorten(
                    "Huck’s Snip ’n’ Clip - " + e.FirstStroke.ToDisplayString()
                        + ", second step within " + e.SecondsRemaining + "s",
                    63);
                if (!row.IsDisposed)
                {
                    row.ShowPendingBinding(e.FirstStroke, e.SecondsRemaining);
                }
            };
            session.Completed += delegate(object sender, ShortcutCaptureCompletedEventArgs e)
            {
                HandleTrayShortcutCaptured(action, previous, session, e);
            };

            try
            {
                session.Start();
                row.BeginBindingCapture();
                SetShortcutButtonsEnabled(menu, false, row);
                this.notifyIcon.Text = Shorten(
                    "Huck’s Snip ’n’ Clip - press shortcut for " + ShortcutCatalog.GetName(action),
                    63);
            }
            catch (Exception exception)
            {
                CancelTrayShortcutCapture(true);
                CopyableDialog.Show(
                    "Shortcut capture could not start. Your current shortcuts are still active.\n\n"
                        + Shorten(exception.Message, 300),
                    "Shortcut Capture Could Not Start",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void HandleTrayShortcutCaptured(
            CaptureAction action,
            Dictionary<CaptureAction, ShortcutBinding> previous,
            KeyboardShortcutCaptureSession session,
            ShortcutCaptureCompletedEventArgs result)
        {
            if (!Object.ReferenceEquals(this.shortcutCaptureSession, session))
            {
                return;
            }

            ReleaseTrayShortcutSession();
            if (result.Canceled || result.Binding == null)
            {
                RestorePreviousShortcuts(previous, action);
                return;
            }

            ShortcutBinding candidate = result.Binding;
            foreach (CaptureDestination destination in this.settings.Destinations)
            {
                ShortcutBinding output;
                if (this.settings.OutputShortcuts.TryGetValue(destination.Id, out output)
                    && (candidate.HasSameFirstStroke(output) || candidate.SecondStrokeStarts(output)))
                {
                    RestorePreviousShortcuts(previous, action);
                    CopyableDialog.Show("That key selects output " + destination.Name + ". Change its Output Shortcut first.",
                        "Shortcut Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            // A recording control is only registered during a take, but it still has to stay out
            // of a capture shortcut's way: Windows reserves a combination once, so an overlap would
            // fail silently at the moment the recording needed it.
            foreach (RecordingControlAction control in RecordingControlCatalog.Actions)
            {
                ShortcutBinding recordingBinding = this.settings.GetRecordingShortcut(control);
                if (candidate.HasSameFirstStroke(recordingBinding)
                    || candidate.SecondStrokeStarts(recordingBinding))
                {
                    RestorePreviousShortcuts(previous, action);
                    CopyableDialog.Show(
                        "That key is " + RecordingControlCatalog.GetName(control)
                            + ". Change it under Settings ▸ Recording Controls first.",
                        "Shortcut Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            Dictionary<CaptureAction, ShortcutBinding> proposed = this.settings.CloneShortcuts();
            proposed[action] = candidate.Clone();

            CaptureAction conflictingAction;
            ShortcutConflictKind conflict = ShortcutConflicts.Find(
                action,
                candidate,
                proposed,
                out conflictingAction);
            if (conflict != ShortcutConflictKind.None)
            {
                RestorePreviousShortcuts(previous, action);
                CopyableDialog.Show(
                    ShortcutConflicts.Describe(
                        conflict,
                        action,
                        candidate,
                        conflictingAction,
                        proposed[conflictingAction]),
                    conflict == ShortcutConflictKind.SameFirstStroke
                        ? "Shortcut Prefix Conflict"
                        : "Shortcut Step Conflict",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (candidate.Modifiers == 0)
            {
                DialogResult warningResult = CopyableDialog.Show(
                    "A modifier-free shortcut will replace normal typing of "
                        + candidate.ToDisplayString()
                        + " everywhere while Huck’s Snip ’n’ Clip is running. Save it anyway?",
                    "Global Bare-Key Shortcut",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (warningResult != DialogResult.Yes)
                {
                    RestorePreviousShortcuts(previous, action);
                    return;
                }
            }

            CaptureAction failedAction;
            bool failedOnSecondStroke;
            if (!TryRegisterBindings(proposed, out failedAction, out failedOnSecondStroke))
            {
                UnregisterAllShortcuts();
                RestorePreviousShortcuts(previous, action);
                CopyableDialog.Show(
                    ShortcutConflicts.DescribeRegistrationFailure(
                        failedAction,
                        proposed[failedAction],
                        failedOnSecondStroke),
                    "Shortcut Could Not Be Applied",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            try
            {
                this.settings.SetShortcut(action, candidate);
                this.settingsStore.Save(this.settings);
                FinishTrayShortcutCapture(candidate.ToDisplayString());
            }
            catch (Exception exception)
            {
                UnregisterAllShortcuts();
                foreach (CaptureAction previousAction in ShortcutCatalog.Actions)
                {
                    this.settings.SetShortcut(previousAction, previous[previousAction]);
                }

                RestorePreviousShortcuts(previous, action);
                CopyableDialog.Show(
                    "The shortcut could not be saved. Your previous shortcuts are still active.\n\n"
                        + Shorten(exception.Message, 300),
                    "Shortcut Was Not Saved",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void CancelTrayShortcutCapture(bool restoreShortcuts)
        {
            Dictionary<CaptureAction, ShortcutBinding> previous = this.shortcutCapturePrevious;
            string previousText = null;
            if (previous != null && this.shortcutCaptureAction.HasValue)
            {
                previousText = previous[this.shortcutCaptureAction.Value].ToDisplayString();
            }

            ReleaseTrayShortcutSession();
            if (restoreShortcuts && previous != null)
            {
                UnregisterAllShortcuts();
                CaptureAction ignored;
                TryRegisterBindings(previous, out ignored);
            }

            FinishTrayShortcutCapture(previousText);
        }

        private void ReleaseTrayShortcutSession()
        {
            KeyboardShortcutCaptureSession session = this.shortcutCaptureSession;
            this.shortcutCaptureSession = null;
            if (session != null)
            {
                session.Dispose();
            }

            this.notifyIcon.Text = "Huck’s Snip ’n’ Clip";
        }

        private void FinishTrayShortcutCapture(string bindingText)
        {
            CaptureActionMenuRow row = this.shortcutCaptureRow;
            ContextMenuStrip menu = this.shortcutCaptureMenu;
            this.shortcutCaptureRow = null;
            this.shortcutCaptureMenu = null;
            this.shortcutCapturePrevious = null;
            this.shortcutCaptureAction = null;
            RefreshOutputShortcuts();

            if (row != null && !row.IsDisposed && !String.IsNullOrEmpty(bindingText))
            {
                row.CompleteBindingCapture(bindingText);
            }

            if (menu != null && !menu.IsDisposed)
            {
                SetShortcutButtonsEnabled(menu, true, null);
            }
        }

        private static void SetShortcutButtonsEnabled(
            ContextMenuStrip menu,
            bool enabled,
            CaptureActionMenuRow activeRow)
        {
            foreach (ToolStripItem item in menu.Items)
            {
                ToolStripControlHost host = item as ToolStripControlHost;
                CaptureActionMenuRow row = host == null ? null : host.Control as CaptureActionMenuRow;
                if (row != null && !Object.ReferenceEquals(row, activeRow))
                {
                    row.SetBindingEnabled(enabled);
                }

                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem != null && String.Equals(menuItem.Name, "ResetShortcuts", StringComparison.Ordinal))
                {
                    menuItem.Enabled = enabled;
                }
            }
        }

        private void ResetShortcutsToDefaults()
        {
            if ((this.clipRecorder != null && this.clipRecorder.IsRunning)
                || this.shortcutCaptureSession != null)
            {
                return;
            }

            DialogResult confirmation = CopyableDialog.Show(
                "Reset all six capture shortcuts to the Huck’s Snip ’n’ Clip defaults?",
                "Reset Shortcuts",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            Dictionary<CaptureAction, ShortcutBinding> previous = this.settings.CloneShortcuts();
            Dictionary<CaptureAction, ShortcutBinding> defaults =
                new Dictionary<CaptureAction, ShortcutBinding>();
            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                defaults[action] = ShortcutCatalog.GetDefault(action);
            }

            UnregisterAllShortcuts();
            CaptureAction failedAction;
            bool failedOnSecondStroke;
            if (!TryRegisterBindings(defaults, out failedAction, out failedOnSecondStroke))
            {
                UnregisterAllShortcuts();
                CaptureAction ignored;
                TryRegisterBindings(previous, out ignored);
                CopyableDialog.Show(
                    ShortcutConflicts.DescribeRegistrationFailure(
                        failedAction,
                        defaults[failedAction],
                        failedOnSecondStroke),
                    "Defaults Could Not Be Applied",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            try
            {
                foreach (CaptureAction action in ShortcutCatalog.Actions)
                {
                    this.settings.SetShortcut(action, defaults[action]);
                }

                this.settingsStore.Save(this.settings);
                RebuildContextMenu();
            }
            catch (Exception exception)
            {
                UnregisterAllShortcuts();
                foreach (CaptureAction action in ShortcutCatalog.Actions)
                {
                    this.settings.SetShortcut(action, previous[action]);
                }

                CaptureAction ignored;
                TryRegisterBindings(previous, out ignored);
                CopyableDialog.Show(
                    "The default shortcuts could not be saved. Your previous shortcuts are still active.\n\n"
                        + Shorten(exception.Message, 300),
                    "Defaults Were Not Saved",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private bool TryRegisterBindings(
            Dictionary<CaptureAction, ShortcutBinding> bindings,
            out CaptureAction failedAction)
        {
            bool ignored;
            return TryRegisterBindings(bindings, out failedAction, out ignored);
        }

        private bool TryRegisterBindings(
            Dictionary<CaptureAction, ShortcutBinding> bindings,
            out CaptureAction failedAction,
            out bool failedOnSecondStroke)
        {
            failedOnSecondStroke = false;
            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                if (!RegisterShortcut(action, bindings[action]))
                {
                    failedAction = action;
                    return false;
                }
            }

            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                if (!CanRegisterSecondStroke(bindings[action]))
                {
                    failedAction = action;
                    failedOnSecondStroke = true;
                    return false;
                }
            }

            failedAction = 0;
            return true;
        }

        private void RestorePreviousShortcuts(
            Dictionary<CaptureAction, ShortcutBinding> previous,
            CaptureAction action)
        {
            CaptureAction ignored;
            TryRegisterBindings(previous, out ignored);
            FinishTrayShortcutCapture(previous[action].ToDisplayString());
        }

        private void UnregisterAllShortcuts()
        {
            CancelPendingShortcutChord();
            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                this.hotkeyWindow.Unregister((int)action);
            }
        }

        private string GetShortcutText(CaptureAction action)
        {
            return this.settings.GetShortcut(action).ToDisplayString();
        }

        private void QueueWindowAction(Action action)
        {
            Timer delay = new Timer();
            delay.Interval = 150;
            delay.Tick += delegate
            {
                delay.Stop();
                delay.Dispose();
                action();
            };
            delay.Start();
        }

        private void RemoveActiveDestination()
        {
            if (this.settings.Destinations.Count <= 1)
            {
                return;
            }

            CaptureDestination active = GetActiveDestination();
            DialogResult confirmation = CopyableDialog.Show(
                "Remove ‘" + active.Name + "’ from Huck’s Snip ’n’ Clip?\n\nThe folder and its files will not be deleted.",
                "Remove Destination",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            this.settings.Destinations.Remove(active);
            this.settings.ActiveDestinationId = this.settings.Destinations[0].Id;
            this.settingsStore.Save(this.settings);
            RebuildContextMenu();
            ShowRoutineNotification("Destination removed", active.Name);
        }

        private void OpenActiveDestination()
        {
            try
            {
                CaptureDestination destination = GetActiveDestination();
                Directory.CreateDirectory(destination.Path);
                OpenPath(destination.Path);
            }
            catch (Exception exception)
            {
                ShowNotification("Could not open destination", Shorten(exception.Message, 180), ToolTipIcon.Error);
            }
        }

        private void OpenLastCapture()
        {
            if (String.IsNullOrWhiteSpace(this.lastSavedPath) || !File.Exists(this.lastSavedPath))
            {
                return;
            }

            try
            {
                OpenPath(this.lastSavedPath);
            }
            catch (Exception exception)
            {
                ShowNotification("Could not open capture", Shorten(exception.Message, 180), ToolTipIcon.Error);
            }
        }

        private static void OpenPath(string path)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = path;
            startInfo.UseShellExecute = true;
            Process.Start(startInfo);
        }

        private void RebuildContextMenu()
        {
            RefreshOutputShortcuts();
            RefreshRecordingControlShortcuts();
            ContextMenuStrip oldMenu = this.notifyIcon.ContextMenuStrip;
            PersistentTrayMenu menu = new PersistentTrayMenu();

            ToolStripMenuItem heading = new ToolStripMenuItem("Huck’s Snip ’n’ Clip");
            heading.Enabled = false;
            menu.Items.Add(heading);
            ToolStripMenuItem brandLine = new ToolStripMenuItem("By Quintin Huckaby");
            brandLine.Enabled = false;
            brandLine.ToolTipText = "Here for all your snippin’ and clippin’ needs.";
            menu.Items.Add(brandLine);
            menu.Items.Add(new ToolStripSeparator());

            bool clipIsRunning = this.clipRecorder != null && this.clipRecorder.IsRunning;
            bool busy = this.clipRecorder != null || this.captureInProgress;
            AddCaptureActionRow(
                menu,
                CaptureAction.SnipRegion,
                "Snip Region",
                !busy,
                "Selects and captures part of the virtual desktop.",
                delegate { BeginRegionSnip(); });
            AddCaptureActionRow(
                menu,
                CaptureAction.SnipWindow,
                "Snip Window",
                !busy,
                "Captures the visible pixels of the active window.",
                delegate { QueueWindowAction(delegate { BeginWindowSnip(); }); });
            AddCaptureActionRow(
                menu,
                CaptureAction.SnipScreen,
                "Snip Screen",
                !busy,
                "Captures the display under the mouse pointer.",
                delegate { BeginScreenSnip(); });

            AddCaptureActionRow(
                menu,
                CaptureAction.ClipRegion,
                "Clip Region",
                !busy,
                "Selects and records part of the virtual desktop after a 3–2–1 countdown.",
                delegate { ToggleRegionClip(); });

            AddCaptureActionRow(
                menu,
                CaptureAction.ClipWindow,
                "Clip Window",
                !busy,
                "Records the visible pixels at the active window and follows its position.",
                delegate { QueueWindowAction(delegate { ToggleWindowClip(); }); });

            AddCaptureActionRow(
                menu,
                CaptureAction.ClipScreen,
                "Clip Screen",
                !busy,
                "Records the display under the mouse pointer after a 3–2–1 countdown.",
                delegate { ToggleScreenClip(); });

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem stop = new ToolStripMenuItem("Stop Recording");
            stop.Name = "StopRecording";
            stop.Enabled = clipIsRunning && !this.clipStopRequested;
            stop.Click += delegate { StopClip(); };
            menu.Items.Add(stop);
            ToolStripMenuItem pause = new ToolStripMenuItem(
                this.clipRecorder != null && this.clipRecorder.PauseRequested ? "Resume Recording" : "Pause Recording");
            pause.Name = "PauseRecording";
            pause.Enabled = stop.Enabled;
            pause.Click += delegate { ToggleClipPause(); };
            menu.Items.Add(pause);

            ToolStripMenuItem resetShortcuts = new ToolStripMenuItem("Reset Shortcuts to Defaults…");
            resetShortcuts.Name = "ResetShortcuts";
            resetShortcuts.Enabled = !clipIsRunning && this.shortcutCaptureSession == null;
            resetShortcuts.Click += delegate { ResetShortcutsToDefaults(); };

            // Menu grouping mirrors the accepted Mac layout: recording controls, then Open Last
            // Capture, then Recording and Output, then Settings, About and Quit.
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem openLast = new ToolStripMenuItem("Open Last Capture");
            openLast.Enabled = !String.IsNullOrWhiteSpace(this.lastSavedPath) && File.Exists(this.lastSavedPath);
            openLast.Click += delegate { OpenLastCapture(); };
            menu.Items.Add(openLast);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(CreateRecordingSettingsMenu());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(CreateDestinationsMenu());
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem settingsMenu = new ToolStripMenuItem("Settings");
            ToolStripMenuItem recordingControlsMenu = CreateRecordingControlsMenu();
            settingsMenu.DropDownItems.Add(recordingControlsMenu);
            settingsMenu.DropDownItems.Add(CreateOutputShortcutsMenu());
            settingsMenu.DropDownItems.Add(CreateFilenamesMenu());
            settingsMenu.DropDownItems.Add(resetShortcuts);
            settingsMenu.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem settingNotifications = new PersistentChoice("Setting Change Notifications");
            settingNotifications.Checked = this.settings.RoutineNotificationsEnabled;
            settingNotifications.Click += delegate { ToggleRoutineNotifications(); };
            settingsMenu.DropDownItems.Add(settingNotifications);

            ToolStripMenuItem startWithWindows = new PersistentChoice("Start with Windows");
            startWithWindows.Checked = StartupManager.IsEnabled();
            startWithWindows.Click += delegate { ToggleStartWithWindows(); };
            settingsMenu.DropDownItems.Add(startWithWindows);
            settingsMenu.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem settingsFile = new ToolStripMenuItem("Open Settings File");
            settingsFile.Click += delegate { OpenPath(this.settingsStore.SettingsPath); };
            settingsMenu.DropDownItems.Add(settingsFile);
            ToolStripMenuItem checkForUpdates = new ToolStripMenuItem(
                this.updateCheckRunning ? "Checking for Updates…" : "Check for Updates…");
            checkForUpdates.Enabled = !this.updateCheckRunning;
            checkForUpdates.Click += delegate { CheckForUpdates(); };
            settingsMenu.DropDownItems.Add(checkForUpdates);
            menu.Items.Add(settingsMenu);

            ToolStripMenuItem about = new ToolStripMenuItem("About Huck’s Snip ’n’ Clip");
            about.Click += delegate { ShowAbout(); };
            menu.Items.Add(about);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("Quit");
            exit.Click += delegate { ExitThread(); };
            menu.Items.Add(exit);

            if (busy)
            {
                // Everything that could redirect, re-encode or interrupt a take is locked while one
                // is running. Settings stays reachable for exactly one branch: Recording Controls,
                // whose show/hide and opacity choices only affect the floating controller - which
                // exists only during a recording, so locking them would hide them precisely when
                // they are wanted. Its shortcut rows disable themselves while recording.
                foreach (ToolStripItem item in menu.Items)
                    if (item != stop && item != pause && item != settingsMenu) item.Enabled = false;
                foreach (ToolStripItem item in settingsMenu.DropDownItems)
                    if (item != recordingControlsMenu) item.Enabled = false;
            }

            menu.Closed += delegate
            {
                if (Object.ReferenceEquals(this.shortcutCaptureMenu, menu)
                    && this.shortcutCaptureSession != null)
                {
                    CancelTrayShortcutCapture(true);
                }
            };

            PersistentTrayMenu persistent = oldMenu as PersistentTrayMenu;
            if (persistent != null && persistent.PreserveInteraction)
            {
                PersistentTrayMenu.CopyState(menu.Items, persistent.Items);
                menu.Dispose();
                return;
            }
            menu.Configure();
            this.trayMenu = menu;
            this.notifyIcon.ContextMenuStrip = menu;
            if (oldMenu != null)
            {
                oldMenu.Dispose();
            }
        }

        private void AddCaptureActionRow(
            ContextMenuStrip menu,
            CaptureAction action,
            string actionText,
            bool actionEnabled,
            string actionToolTip,
            Action invokeAction)
        {
            CaptureActionMenuRow row = new CaptureActionMenuRow(
                actionText,
                GetShortcutText(action),
                actionEnabled,
                (this.clipRecorder == null || !this.clipRecorder.IsRunning)
                    && this.shortcutCaptureSession == null,
                actionToolTip);
            row.ActionInvoked += delegate
            {
                menu.Close();
                invokeAction();
            };
            row.RebindRequested += delegate
            {
                BeginTrayShortcutCapture(action, row, menu);
            };

            ToolStripControlHost host = new ToolStripControlHost(row);
            host.AutoSize = false;
            host.Margin = Padding.Empty;
            host.Padding = Padding.Empty;
            host.Size = row.Size;
            menu.Items.Add(host);
        }

        private ToolStripMenuItem CreateDestinationsMenu()
        {
            CaptureDestination active = GetActiveDestination();
            ToolStripMenuItem destinationsMenu = new ToolStripMenuItem(
                "Output — " + Shorten(active.Name, 30));

            foreach (CaptureDestination destination in this.settings.Destinations)
            {
                string destinationId = destination.Id;
                ToolStripMenuItem destinationItem = new PersistentChoice(destination.Name);
                destinationItem.Checked = String.Equals(
                    destination.Id,
                    active.Id,
                    StringComparison.Ordinal);
                destinationItem.ToolTipText = destination.Path;
                destinationItem.Click += delegate
                {
                    SelectDestination(destinationId);
                    ShowRoutineNotification("Destination selected", GetActiveDestination().Name);
                };
                destinationsMenu.DropDownItems.Add(destinationItem);
            }

            destinationsMenu.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem addDestination = new ToolStripMenuItem("Add Output...");
            addDestination.Click += delegate { AddDestination(); };
            destinationsMenu.DropDownItems.Add(addDestination);

            ToolStripMenuItem removeDestination = new ToolStripMenuItem("Remove Current Output...");
            removeDestination.Enabled = this.settings.Destinations.Count > 1;
            removeDestination.Click += delegate { RemoveActiveDestination(); };
            destinationsMenu.DropDownItems.Add(removeDestination);

            ToolStripMenuItem openDestination = new ToolStripMenuItem("Open Current Output");
            openDestination.Click += delegate { OpenActiveDestination(); };
            destinationsMenu.DropDownItems.Add(openDestination);

            return destinationsMenu;
        }

        internal static string GetAboutText()
        {
            System.Reflection.Assembly assembly = typeof(TrayApplicationContext).Assembly;
            System.Reflection.AssemblyInformationalVersionAttribute informational =
                (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            string version = informational == null
                ? assembly.GetName().Version.ToString()
                : informational.InformationalVersion;
            return "Huck’s Snip ’n’ Clip " + version + "\n"
                + "By Quintin Huckaby\n\n"
                + "Installed at:\n" + assembly.Location;
        }

        private void ShowAbout()
        {
            CopyableDialog.Show(
                GetAboutText(),
                "About Huck’s Snip ’n’ Clip",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void ToggleStartWithWindows()
        {
            bool enable = !StartupManager.IsEnabled();
            try
            {
                StartupManager.SetEnabled(enable);
                RebuildContextMenu();
                ShowRoutineNotification(
                    enable ? "Starts with Windows" : "Windows startup disabled",
                    enable
                        ? "Huck’s Snip ’n’ Clip will start automatically when you sign in."
                        : "Huck’s Snip ’n’ Clip will no longer start automatically.");
            }
            catch (Exception exception)
            {
                CopyableDialog.Show(
                    "Windows startup could not be changed.\n\n" + Shorten(exception.Message, 300),
                    "Startup Setting Could Not Be Changed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void ToggleRoutineNotifications()
        {
            this.settings.RoutineNotificationsEnabled = !this.settings.RoutineNotificationsEnabled;
            this.settingsStore.Save(this.settings);
            RebuildContextMenu();
        }

        private ToolStripMenuItem CreateRecordingSettingsMenu()
        {
            int configuredFrameRate = SettingsStore.NormalizeFrameRate(this.settings.RecordingFrameRate);
            RecordingQuality configuredQuality = RecordingQualitySettings.Normalize(this.settings.RecordingQuality);
            string summary = configuredFrameRate == 0
                ? "Match Display"
                : configuredFrameRate + " FPS";
            RecordingResolutionCeiling configuredResolution =
                RecordingResolutionSettings.Normalize(this.settings.RecordingResolution);
            summary += " + " + GetRecordingQualityLabel(configuredQuality)
                + " + " + RecordingResolutionSettings.GetLabel(configuredResolution)
                + " + " + GetRecordingAudioSummary(this.settings.RecordingAudioMode);

            ToolStripMenuItem recordingMenu = new ToolStripMenuItem("Recording — " + summary);
            recordingMenu.Enabled = this.clipRecorder == null && !this.captureInProgress;

            ToolStripMenuItem frameRateMenu = new ToolStripMenuItem("Frame Rate");
            frameRateMenu.ToolTipText = "Match Display follows its refresh rate; it is not vertical-blank synchronization.";
            AddFrameRateItem(frameRateMenu, "15 FPS — Smallest / Lightest", 15, configuredFrameRate);
            AddFrameRateItem(frameRateMenu, "30 FPS — Balanced", 30, configuredFrameRate);
            AddFrameRateItem(frameRateMenu, "60 FPS — Smoothest", 60, configuredFrameRate);
            AddFrameRateItem(frameRateMenu, "Match Display Refresh", 0, configuredFrameRate);
            recordingMenu.DropDownItems.Add(frameRateMenu);
            PersistentChoice cursor = new PersistentChoice("Show Cursor in Clips");
            cursor.Name = "ShowCursorInClips";
            cursor.Checked = this.settings.ShowCursorInClips;
            cursor.Click += delegate
            {
                bool previous = this.settings.ShowCursorInClips;
                this.settings.ShowCursorInClips = !previous;
                try { this.settingsStore.Save(this.settings); RebuildContextMenu(); }
                catch (Exception ex)
                {
                    this.settings.ShowCursorInClips = previous;
                    CopyableDialog.Show(ex.Message, "Cursor Setting", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            ToolStripMenuItem resolutionMenu = new ToolStripMenuItem(
                "Resolution — " + RecordingResolutionSettings.GetLabel(configuredResolution));
            AddResolutionItem(resolutionMenu, RecordingResolutionCeiling.Native, configuredResolution,
                "Native — Full Captured Size");
            AddResolutionItem(resolutionMenu, RecordingResolutionCeiling.P1440, configuredResolution, "1440p");
            AddResolutionItem(resolutionMenu, RecordingResolutionCeiling.P1080, configuredResolution, "1080p");
            AddResolutionItem(resolutionMenu, RecordingResolutionCeiling.P720, configuredResolution, "720p");
            recordingMenu.DropDownItems.Add(resolutionMenu);

            ToolStripMenuItem qualityMenu = new ToolStripMenuItem(
                "Quality — " + GetRecordingQualityLabel(configuredQuality));
            qualityMenu.ToolTipText = "Quality controls video bitrate independently of frame rate; capture size stays native (odd dimensions trim one pixel).";
            AddQualityItem(qualityMenu, RecordingQuality.Legacy, configuredQuality,
                "Legacy — Fixed 8 Mbps",
                "Original video bitrate at every size and frame rate; about 60 MB/min before audio. Preserves existing recording behavior.");
            AddQualityItem(qualityMenu, RecordingQuality.Balanced, configuredQuality,
                "Balanced — Detail / File Size",
                "Scales with resolution and FPS: 16 Mbps at 1080p60 (about 120 MB/min before audio), 8 Mbps at 1080p30. Bounded to 2–80 Mbps.");
            AddQualityItem(qualityMenu, RecordingQuality.High, configuredQuality,
                "High — More Motion Detail",
                "More bitrate for fast motion and larger files: 24 Mbps at 1080p60 (about 180 MB/min before audio), 12 Mbps at 1080p30. Scales with resolution and FPS, bounded to 3–80 Mbps.");
            recordingMenu.DropDownItems.Add(qualityMenu);
            recordingMenu.DropDownItems.Add(cursor);
            recordingMenu.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem audioMenu = new ToolStripMenuItem("Audio");
            ToolStripMenuItem audioOff = new PersistentChoice("Off — Video Only");
            audioOff.Checked = this.settings.RecordingAudioMode == RecordingAudioMode.Off;
            audioOff.Click += delegate { SelectRecordingAudioMode(RecordingAudioMode.Off); };
            audioMenu.DropDownItems.Add(audioOff);

            ToolStripMenuItem computerAudio = new PersistentChoice("Computer Audio — Games / Apps");
            computerAudio.Checked = this.settings.RecordingAudioMode == RecordingAudioMode.Computer;
            computerAudio.ToolTipText = "Captures the sound playing through the default Windows output device.";
            computerAudio.Click += delegate { SelectRecordingAudioMode(RecordingAudioMode.Computer); };
            audioMenu.DropDownItems.Add(computerAudio);

            ToolStripMenuItem microphone = new PersistentChoice("Microphone - Default Input");
            microphone.Checked = this.settings.RecordingAudioMode == RecordingAudioMode.Microphone;
            microphone.ToolTipText = "Captures the default Windows microphone.";
            microphone.Click += delegate { SelectRecordingAudioMode(RecordingAudioMode.Microphone); };
            audioMenu.DropDownItems.Add(microphone);

            ToolStripMenuItem mixedAudio = new PersistentChoice("Computer + Microphone — Separate Tracks");
            mixedAudio.Checked = this.settings.RecordingAudioMode == RecordingAudioMode.ComputerAndMicrophone;
            mixedAudio.ToolTipText = "Records computer audio as track 1 and the microphone as track 2 in one file.";
            mixedAudio.Click += delegate { SelectRecordingAudioMode(RecordingAudioMode.ComputerAndMicrophone); };
            audioMenu.DropDownItems.Add(mixedAudio);
            recordingMenu.DropDownItems.Add(audioMenu);
            recordingMenu.DropDownItems.Add(CreateMicrophoneMenu());

            ToolStripMenuItem computerGainMenu = new ToolStripMenuItem(
                "Computer Gain - " + this.settings.ComputerAudioGainPercent + "%");
            AddGainItems(computerGainMenu, true, this.settings.ComputerAudioGainPercent);
            recordingMenu.DropDownItems.Add(computerGainMenu);

            ToolStripMenuItem microphoneGainMenu = new ToolStripMenuItem(
                "Microphone Gain - " + this.settings.MicrophoneGainPercent + "%");
            AddGainItems(microphoneGainMenu, false, this.settings.MicrophoneGainPercent);
            recordingMenu.DropDownItems.Add(microphoneGainMenu);

            return recordingMenu;
        }

        private void AddQualityItem(
            ToolStripMenuItem parent,
            RecordingQuality quality,
            RecordingQuality configuredQuality,
            string label,
            string explanation)
        {
            ToolStripMenuItem item = new PersistentChoice(label);
            item.Checked = quality == configuredQuality;
            item.ToolTipText = explanation;
            item.Click += delegate { SelectRecordingQuality(quality); };
            parent.DropDownItems.Add(item);
        }

        private void AddResolutionItem(
            ToolStripMenuItem parent,
            RecordingResolutionCeiling ceiling,
            RecordingResolutionCeiling configuredCeiling,
            string label)
        {
            PersistentChoice item = new PersistentChoice(label);
            item.Checked = ceiling == configuredCeiling;
            item.Click += delegate { SelectRecordingResolution(ceiling); };
            parent.DropDownItems.Add(item);
        }

        private void SelectRecordingResolution(RecordingResolutionCeiling ceiling)
        {
            RecordingResolutionCeiling previous = this.settings.RecordingResolution;
            this.settings.RecordingResolution = RecordingResolutionSettings.Normalize(ceiling);
            try
            {
                this.settingsStore.Save(this.settings);
                RebuildContextMenu();
            }
            catch (Exception exception)
            {
                this.settings.RecordingResolution = previous;
                CopyableDialog.Show(
                    exception.Message,
                    "Recording Settings Could Not Be Saved",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static string GetRecordingQualityLabel(RecordingQuality quality)
        {
            return RecordingQualitySettings.Normalize(quality) == RecordingQuality.Legacy
                ? "Original"
                : RecordingQualitySettings.Normalize(quality).ToString();
        }

        private void AddGainItems(ToolStripMenuItem parent, bool computerAudio, int configuredGain)
        {
            int[] gains = new[] { 0, 50, 75, 100, 125, 150, 200, 300 };
            foreach (int gain in gains)
            {
                int selectedGain = gain;
                string label = gain == 0 ? "0% - Muted" : gain + "%";
                if (gain == 100)
                {
                    label += " - Normal";
                }

                ToolStripMenuItem item = new PersistentChoice(label);
                item.Checked = gain == configuredGain;
                item.Click += delegate { SelectRecordingAudioGain(computerAudio, selectedGain); };
                parent.DropDownItems.Add(item);
            }
        }

        private static string GetRecordingAudioLabel(RecordingAudioMode audioMode)
        {
            switch (audioMode)
            {
                case RecordingAudioMode.Computer:
                    return "Computer Audio";
                case RecordingAudioMode.Microphone:
                    return "Microphone";
                case RecordingAudioMode.ComputerAndMicrophone:
                    return "Computer + Microphone — Separate Tracks";
                default:
                    return "Off";
            }
        }

        private string GetRecordingAudioSummary(RecordingAudioMode audioMode)
        {
            switch (audioMode)
            {
                case RecordingAudioMode.Computer:
                    return "Computer " + this.settings.ComputerAudioGainPercent + "%";
                case RecordingAudioMode.Microphone:
                    return "Mic " + this.settings.MicrophoneGainPercent + "%";
                case RecordingAudioMode.ComputerAndMicrophone:
                    return "Computer " + this.settings.ComputerAudioGainPercent
                        + "% + Mic " + this.settings.MicrophoneGainPercent + "%";
                default:
                    return "No Audio";
            }
        }

        private static string GetRecordingAudioTrayLabel(RecordingAudioMode audioMode)
        {
            switch (audioMode)
            {
                case RecordingAudioMode.Computer:
                    return " + Computer";
                case RecordingAudioMode.Microphone:
                    return " + Mic";
                case RecordingAudioMode.ComputerAndMicrophone:
                    return " + Computer + Mic";
                default:
                    return String.Empty;
            }
        }

        private void AddFrameRateItem(
            ToolStripMenuItem parent,
            string label,
            int frameRate,
            int configuredFrameRate)
        {
            ToolStripMenuItem item = new PersistentChoice(label);
            item.Checked = frameRate == configuredFrameRate;
            item.Click += delegate { SelectRecordingFrameRate(frameRate); };
            parent.DropDownItems.Add(item);
        }

        private void ShowNotification(string title, string message, ToolTipIcon icon)
        {
            this.notifyIcon.BalloonTipTitle = title;
            this.notifyIcon.BalloonTipText = message;
            this.notifyIcon.BalloonTipIcon = icon;
            this.notifyIcon.ShowBalloonTip(3500);
        }

        private void ShowRoutineNotification(string title, string message)
        {
            if (this.settings.RoutineNotificationsEnabled)
            {
                ShowNotification(title, message, ToolTipIcon.Info);
            }
        }

        private static string Shorten(string value, int maximumLength)
        {
            if (String.IsNullOrEmpty(value) || value.Length <= maximumLength)
            {
                return value;
            }

            return value.Substring(0, maximumLength - 1) + "…";
        }
    }
}
