using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace QSnipAndClip
{
    /// <summary>
    /// Reaching a running recording when the tray is not reachable.
    ///
    /// Two routes, both alive only while a clip is actually recording: editable global
    /// Pause/Resume and Stop shortcuts, and the floating H controller. Neither exists while Huck's
    /// Snip 'n' Clip is idle, and neither survives into finalization - once a stop has been asked
    /// for there is nothing left to control.
    /// </summary>
    internal sealed partial class TrayApplicationContext
    {
        private const int RecordingControlHotkeyBase = 3000;

        private readonly Dictionary<int, RecordingControlAction> registeredRecordingControls =
            new Dictionary<int, RecordingControlAction>();
        private bool recordingControlsActive;
        private bool recordingControlEditorOpen;
        private FloatingRecordingController floatingController;
        private string controllerLayoutKey;

        /// <summary>
        /// Holds the recording-control keys registered exactly while a take is running.
        ///
        /// The comparison against <see cref="recordingControlsActive" /> matters: the menu is
        /// rebuilt many times during a recording, and re-registering on every rebuild would report
        /// the same failure over and over.
        /// </summary>
        private void RefreshRecordingControlShortcuts()
        {
            if (this.hotkeyWindow == null || this.settings == null) return;

            bool shouldBeActive = this.clipRecorder != null
                && this.clipRecorder.IsRunning
                && !this.clipStopRequested
                && !this.recordingControlEditorOpen;
            if (shouldBeActive == this.recordingControlsActive) return;

            foreach (int id in this.registeredRecordingControls.Keys) this.hotkeyWindow.Unregister(id);
            this.registeredRecordingControls.Clear();
            this.recordingControlsActive = shouldBeActive;
            if (!shouldBeActive) return;

            foreach (RecordingControlAction action in RecordingControlCatalog.Actions)
            {
                ShortcutBinding binding = this.settings.GetRecordingShortcut(action);
                int id = RecordingControlHotkeyBase + (int)action;
                if (this.hotkeyWindow.Register(
                        id, binding.Modifiers | NativeMethods.MOD_NOREPEAT, binding.Key))
                {
                    this.registeredRecordingControls[id] = action;
                }
                else
                {
                    // Visible, and the stored binding is left exactly as it was: the tray and the
                    // floating controller still work, so the take is never stranded.
                    ShowNotification(
                        "Recording shortcut unavailable",
                        binding.ToDisplayString() + " could not "
                            + (action == RecordingControlAction.Stop ? "stop" : "pause")
                            + " this recording. Windows or another program has reserved it. Use the "
                            + "tray or the floating controls, and choose another key in Settings.",
                        ToolTipIcon.Warning);
                }
            }
        }

        private void HandleRecordingControlShortcut(int id)
        {
            RecordingControlAction action;
            if (!this.registeredRecordingControls.TryGetValue(id, out action)) return;

            // Idle is not a state these keys act in. They are unregistered while idle, and this
            // second guard keeps a queued message from reaching a recording that has already gone.
            if (this.clipRecorder == null || !this.clipRecorder.IsRunning || this.clipStopRequested) return;

            CancelPendingShortcutChord();
            if (action == RecordingControlAction.Stop) StopClip();
            else ToggleClipPause();
        }

        // ---- Floating controller ------------------------------------------------------------

        private void ShowFloatingRecordingController(Screen display)
        {
            DismissFloatingRecordingController();
            if (this.settings == null || !this.settings.ShowFloatingRecordingControls) return;

            try
            {
                string layoutKey = DisplayLayoutIdentity.Current();
                this.controllerLayoutKey = layoutKey;

                Rectangle workingArea = display.WorkingArea;
                Size size = FloatingRecordingController.Measure(this.settings.FloatingControllerSize);
                IList<Rectangle> areas = GetWorkingAreas();

                Point desired;
                bool knownLayout = this.settings.ControllerLayouts.TryGetPosition(layoutKey, out desired);
                if (!knownLayout)
                {
                    // A layout nobody has taught yet starts safely on the display being recorded.
                    // It is not written down until the first right-drag teaches it.
                    desired = RecordingControllerPlacement.Default(workingArea, size);
                }

                Point placed = RecordingControllerPlacement.Clamp(desired, size, areas);
                FloatingRecordingController controller = FloatingRecordingController.TryShow(
                    placed,
                    this.settings.FloatingControllerSize,
                    this.settings.FloatingControllerOpacityPercent);
                if (controller == null)
                {
                    ShowNotification(
                        "Floating controls hidden",
                        "Windows would have recorded the floating controls into this clip, so they "
                            + "were not shown. Use the tray or the recording shortcuts.",
                        ToolTipIcon.Warning);
                    return;
                }

                controller.PauseResumeRequested += delegate { ToggleClipPause(); };
                controller.StopRequested += delegate { StopClip(); };
                controller.Moved += HandleFloatingControllerMoved;
                controller.Resized += HandleFloatingControllerResized;
                this.floatingController = controller;

                if (knownLayout && placed != desired)
                {
                    // A remembered point that no longer fits is corrected in place. Only this
                    // layout changes; every other desk keeps the position it was taught.
                    StoreControllerPosition(layoutKey, placed);
                }
                else if (knownLayout)
                {
                    this.settings.ControllerLayouts.Touch(layoutKey);
                }
            }
            catch (Exception exception)
            {
                // A controller is a convenience. It must never take a recording down with it.
                DismissFloatingRecordingController();
                ShowNotification(
                    "Floating controls unavailable", Shorten(exception.Message, 180), ToolTipIcon.Warning);
            }
        }

        private void HandleFloatingControllerMoved(object sender, EventArgs e)
        {
            FloatingRecordingController controller = this.floatingController;
            if (controller == null || String.IsNullOrEmpty(this.controllerLayoutKey)) return;

            Size size = controller.Size;
            Point clamped = RecordingControllerPlacement.Clamp(
                controller.Location, size, GetWorkingAreas());
            if (clamped != controller.Location) controller.MoveTo(clamped);
            StoreControllerPosition(this.controllerLayoutKey, clamped);
        }

        /// <summary>
        /// A resize is a deliberate choice, so it is kept - and so is where the resized controller
        /// ended up, because growing from the notch moves the window's own corner.
        /// </summary>
        private void HandleFloatingControllerResized(object sender, EventArgs e)
        {
            FloatingRecordingController controller = this.floatingController;
            if (controller == null) return;

            Point clamped = RecordingControllerPlacement.Clamp(
                controller.Location, controller.Size, GetWorkingAreas());
            if (clamped != controller.Location) controller.MoveTo(clamped);

            this.settings.FloatingControllerSize = controller.ControllerSize;
            if (!String.IsNullOrEmpty(this.controllerLayoutKey))
            {
                this.settings.ControllerLayouts.SetPosition(this.controllerLayoutKey, clamped);
            }

            try { this.settingsStore.Save(this.settings); }
            catch { /* Remembering a control's size is never worth failing a recording. */ }
        }

        private void StoreControllerPosition(string layoutKey, Point position)
        {
            this.settings.ControllerLayouts.SetPosition(layoutKey, position);
            try { this.settingsStore.Save(this.settings); }
            catch { /* Remembering where a control sits is never worth failing a recording. */ }
        }

        private static IList<Rectangle> GetWorkingAreas()
        {
            Screen[] screens = Screen.AllScreens;
            List<Rectangle> areas = new List<Rectangle>(screens.Length);
            foreach (Screen screen in screens) areas.Add(screen.WorkingArea);
            return areas;
        }

        private void DismissFloatingRecordingController()
        {
            if (this.floatingController == null) return;
            this.floatingController.Dispose();
            this.floatingController = null;
        }

        private void UpdateFloatingControllerMeters(float input, float strain)
        {
            if (this.floatingController == null) return;
            this.floatingController.UpdateMeters(input, strain);
        }

        // ---- Settings surface ---------------------------------------------------------------

        private ToolStripMenuItem CreateRecordingControlsMenu()
        {
            bool recording = this.clipRecorder != null;
            ToolStripMenuItem menu = new ToolStripMenuItem("Recording Controls");

            ToolStripMenuItem explanation = new ToolStripMenuItem("During recording: pause or stop…");
            explanation.Enabled = false;
            menu.DropDownItems.Add(explanation);
            menu.DropDownItems.Add(new ToolStripSeparator());

            foreach (RecordingControlAction action in RecordingControlCatalog.Actions)
            {
                RecordingControlAction selected = action;
                ToolStripMenuItem row = new ToolStripMenuItem(
                    RecordingControlCatalog.GetName(action) + " — "
                        + this.settings.GetRecordingShortcut(action).ToDisplayString());
                row.Enabled = !recording && !this.captureInProgress;
                row.Click += delegate { EditRecordingControlShortcut(selected); };
                menu.DropDownItems.Add(row);
            }

            ToolStripMenuItem reset = new ToolStripMenuItem("Reset Recording Shortcuts to Defaults");
            reset.Enabled = !recording && !this.captureInProgress;
            reset.Click += delegate { ResetRecordingControlShortcutsToDefaults(); };
            menu.DropDownItems.Add(reset);
            menu.DropDownItems.Add(new ToolStripSeparator());

            // These two stay reachable during a take on purpose. Everything else is locked while
            // recording because it could change where the clip goes or how it is encoded; these
            // only change how the controller itself looks, and the controller only exists during
            // the take, so locking them would hide them exactly when they matter.
            ToolStripMenuItem show = new PersistentChoice("Show Floating Recording Controls");
            show.Checked = this.settings.ShowFloatingRecordingControls;
            show.Click += delegate { ToggleFloatingRecordingControls(); };
            menu.DropDownItems.Add(show);

            ToolStripMenuItem opacity = new ToolStripMenuItem(
                "Controller Opacity — "
                    + FloatingRecordingController.NormalizeOpacityPercent(
                        this.settings.FloatingControllerOpacityPercent) + "%");
            for (int percent = 10; percent <= 100; percent += 10)
            {
                int chosen = percent;
                ToolStripMenuItem choice = new PersistentChoice(percent + "%");
                choice.Checked = FloatingRecordingController.NormalizeOpacityPercent(
                    this.settings.FloatingControllerOpacityPercent) == percent;
                choice.Click += delegate { SelectControllerOpacity(chosen); };
                opacity.DropDownItems.Add(choice);
            }

            menu.DropDownItems.Add(opacity);
            return menu;
        }

        private void ToggleFloatingRecordingControls()
        {
            bool previous = this.settings.ShowFloatingRecordingControls;
            this.settings.ShowFloatingRecordingControls = !previous;
            try
            {
                this.settingsStore.Save(this.settings);
            }
            catch (Exception exception)
            {
                this.settings.ShowFloatingRecordingControls = previous;
                CopyableDialog.Show(
                    "That choice could not be saved.\n\n" + Shorten(exception.Message, 300),
                    "Floating Recording Controls", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Turning it off hides the controller now. It never touches the recording shortcuts,
            // the tray controls, the saved opacity, or any learned layout position.
            if (!this.settings.ShowFloatingRecordingControls) DismissFloatingRecordingController();
            else if (this.clipRecorder != null && this.clipRecorder.IsRunning && !this.clipStopRequested
                && this.clipDisplay != null)
            {
                ShowFloatingRecordingController(this.clipDisplay);
            }

            ShowRoutineNotification(
                "Floating recording controls",
                this.settings.ShowFloatingRecordingControls ? "Shown during recording" : "Hidden");
            RebuildContextMenu();
        }

        private void SelectControllerOpacity(int percent)
        {
            int normalized = FloatingRecordingController.NormalizeOpacityPercent(percent);
            int previous = this.settings.FloatingControllerOpacityPercent;
            if (normalized == previous) return;

            this.settings.FloatingControllerOpacityPercent = normalized;
            try
            {
                this.settingsStore.Save(this.settings);
            }
            catch (Exception exception)
            {
                this.settings.FloatingControllerOpacityPercent = previous;
                CopyableDialog.Show(
                    "That choice could not be saved.\n\n" + Shorten(exception.Message, 300),
                    "Controller Opacity", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Immediate: a controller already on screen changes as the choice is made.
            if (this.floatingController != null) this.floatingController.SetOpacityPercent(normalized);
            RebuildContextMenu();
        }

        private void ResetRecordingControlShortcutsToDefaults()
        {
            if (this.captureInProgress || this.clipRecorder != null) return;

            Dictionary<RecordingControlAction, ShortcutBinding> previous =
                new Dictionary<RecordingControlAction, ShortcutBinding>();
            foreach (RecordingControlAction action in RecordingControlCatalog.Actions)
            {
                previous[action] = this.settings.GetRecordingShortcut(action);
                this.settings.SetRecordingShortcut(action, RecordingControlCatalog.GetDefault(action));
            }

            try
            {
                this.settingsStore.Save(this.settings);
                ShowRoutineNotification(
                    "Recording shortcuts reset", "Pause/Resume and Stop are back to their defaults.");
            }
            catch (Exception exception)
            {
                foreach (RecordingControlAction action in RecordingControlCatalog.Actions)
                {
                    this.settings.SetRecordingShortcut(action, previous[action]);
                }

                CopyableDialog.Show(
                    "The recording shortcuts could not be reset. Your previous shortcuts are still "
                        + "active.\n\n" + Shorten(exception.Message, 300),
                    "Recording Shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            RebuildContextMenu();
        }

        /// <summary>
        /// Rebinds one recording control, reusing the same conflict, registration and rollback
        /// behavior as the Output shortcut editor. A combination that Windows will not give up is
        /// rejected here, while the editor is open, rather than silently failing mid-recording.
        /// </summary>
        private void EditRecordingControlShortcut(RecordingControlAction action)
        {
            if (this.captureInProgress || this.clipRecorder != null) return;

            this.recordingControlEditorOpen = true;
            RefreshOutputShortcuts();
            CancelPendingShortcutChord();
            UnregisterAllShortcuts();
            try
            {
                using (OutputShortcutForm editor = new OutputShortcutForm(
                    RecordingControlCatalog.GetName(action),
                    delegate(ShortcutBinding candidate)
                    {
                        string conflict = this.settings.RecordingControlConflict(action, candidate);
                        if (conflict != null) return conflict;
                        if (!this.hotkeyWindow.Register(
                                RecordingControlHotkeyBase,
                                candidate.Modifiers | NativeMethods.MOD_NOREPEAT,
                                candidate.Key))
                        {
                            return "Windows or another application already uses that key.";
                        }

                        this.hotkeyWindow.Unregister(RecordingControlHotkeyBase);
                        return null;
                    }))
                {
                    editor.Text = "Recording Shortcut — " + RecordingControlCatalog.GetName(action);
                    if (editor.ShowDialog() != DialogResult.OK) return;

                    ShortcutBinding previous = this.settings.GetRecordingShortcut(action);
                    this.settings.SetRecordingShortcut(action, editor.Binding);
                    try { this.settingsStore.Save(this.settings); }
                    catch
                    {
                        this.settings.SetRecordingShortcut(action, previous);
                        throw;
                    }
                }
            }
            catch (Exception exception)
            {
                CopyableDialog.Show(
                    "The recording shortcut could not be saved. Your previous shortcut is still "
                        + "active.\n\n" + Shorten(exception.Message, 300),
                    "Recording Shortcut", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                this.recordingControlEditorOpen = false;
                RegisterConfiguredShortcuts();
                RebuildContextMenu();
            }
        }
    }
}
