using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed partial class TrayApplicationContext
    {
        private const int OutputHotkeyBase = 2000;
        private readonly Dictionary<int, string> registeredOutputs = new Dictionary<int, string>();
        private bool outputEditorOpen;

        private void RefreshOutputShortcuts()
        {
            if (this.hotkeyWindow == null || this.settings == null) return;
            foreach (int id in this.registeredOutputs.Keys) this.hotkeyWindow.Unregister(id);
            this.registeredOutputs.Clear();
            if (this.captureInProgress || this.clipRecorder != null || this.outputEditorOpen
                || this.shortcutCaptureSession != null) return;
            for (int i = 0; i < this.settings.Destinations.Count && i < 1000; i++)
            {
                CaptureDestination destination = this.settings.Destinations[i];
                ShortcutBinding binding;
                if (!this.settings.OutputShortcuts.TryGetValue(destination.Id, out binding)) continue;
                if (this.hotkeyWindow.Register(OutputHotkeyBase + i,
                    binding.Modifiers | NativeMethods.MOD_NOREPEAT, binding.Key))
                    this.registeredOutputs[OutputHotkeyBase + i] = destination.Id;
                else ShowNotification("Output shortcut unavailable",
                    binding.ToDisplayString() + " could not select " + destination.Name + ". Choose another key in Settings.",
                    ToolTipIcon.Warning);
            }
        }

        private void HandleOutputShortcut(int id)
        {
            if (this.captureInProgress || this.clipRecorder != null || this.outputEditorOpen
                || this.shortcutCaptureSession != null) return;
            string destinationId;
            if (!this.registeredOutputs.TryGetValue(id, out destinationId)) return;
            CancelPendingShortcutChord();
            try
            {
                SelectDestination(destinationId);
                ShowNotification("Output selected", GetActiveDestination().Name, ToolTipIcon.Info);
            }
            catch (Exception ex) { ShowNotification("Output could not change", ex.Message, ToolTipIcon.Error); }
        }

        private ToolStripMenuItem CreateOutputShortcutsMenu()
        {
            ToolStripMenuItem menu = new ToolStripMenuItem("Output Shortcuts");
            menu.Enabled = this.clipRecorder == null && !this.captureInProgress;

            // Without this line the submenu reads as an unexplained list of keys. These select the
            // route for the *next* capture; they are deliberately dead during one.
            ToolStripMenuItem explanation = new ToolStripMenuItem("Before capture: select output…");
            explanation.Enabled = false;
            menu.DropDownItems.Add(explanation);
            menu.DropDownItems.Add(new ToolStripSeparator());

            foreach (CaptureDestination destination in this.settings.Destinations)
            {
                CaptureDestination selected = destination;
                ShortcutBinding binding;
                string text = this.settings.OutputShortcuts.TryGetValue(destination.Id, out binding)
                    ? binding.ToDisplayString() : "Unassigned";
                ToolStripMenuItem row = new ToolStripMenuItem(destination.Name + " — " + text);
                row.Click += delegate { EditOutputShortcut(selected); };
                menu.DropDownItems.Add(row);
            }

            menu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem reset = new ToolStripMenuItem("Reset Output Shortcuts to Defaults");
            reset.Click += delegate { ResetOutputShortcutsToDefaults(); };
            menu.DropDownItems.Add(reset);
            return menu;
        }

        private void ResetOutputShortcutsToDefaults()
        {
            if (this.captureInProgress || this.clipRecorder != null) return;
            Dictionary<string, ShortcutBinding> previous =
                new Dictionary<string, ShortcutBinding>(this.settings.OutputShortcuts);
            this.settings.OutputShortcuts.Clear();
            this.settings.EnsureOutputShortcuts();
            try
            {
                this.settingsStore.Save(this.settings);
                RefreshOutputShortcuts();
                ShowRoutineNotification("Output shortcuts reset", "Every output is back to its default key.");
                RebuildContextMenu();
            }
            catch (Exception ex)
            {
                this.settings.OutputShortcuts.Clear();
                foreach (KeyValuePair<string, ShortcutBinding> entry in previous)
                    this.settings.OutputShortcuts[entry.Key] = entry.Value;
                RefreshOutputShortcuts();
                CopyableDialog.Show(ex.Message, "Output Shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void EditOutputShortcut(CaptureDestination destination)
        {
            if (this.captureInProgress || this.clipRecorder != null) return;
            this.outputEditorOpen = true;
            RefreshOutputShortcuts();
            CancelPendingShortcutChord();
            UnregisterAllShortcuts();
            try
            {
                using (OutputShortcutForm editor = new OutputShortcutForm(destination.Name, delegate(ShortcutBinding candidate)
                {
                    string conflict = this.settings.OutputConflict(destination.Id, candidate);
                    if (conflict != null) return conflict;
                    if (!this.hotkeyWindow.Register(OutputHotkeyBase, candidate.Modifiers | NativeMethods.MOD_NOREPEAT, candidate.Key))
                        return "Windows or another application already uses that key.";
                    this.hotkeyWindow.Unregister(OutputHotkeyBase);
                    return null;
                }))
                {
                    if (editor.ShowDialog() != DialogResult.OK) return;
                    ShortcutBinding previous;
                    this.settings.OutputShortcuts.TryGetValue(destination.Id, out previous);
                    this.settings.OutputShortcuts[destination.Id] = editor.Binding;
                    try { this.settingsStore.Save(this.settings); }
                    catch
                    {
                        if (previous == null) this.settings.OutputShortcuts.Remove(destination.Id);
                        else this.settings.OutputShortcuts[destination.Id] = previous;
                        throw;
                    }
                }
            }
            catch (Exception ex) { CopyableDialog.Show(ex.Message, "Output Shortcut", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally
            {
                this.outputEditorOpen = false;
                RegisterConfiguredShortcuts();
                RebuildContextMenu();
            }
        }

        private void FinishCapturePreparation()
        {
            this.captureInProgress = false;
            RebuildContextMenu();
        }
    }

    internal sealed class OutputShortcutForm : Form
    {
        private readonly Func<ShortcutBinding, string> validate;
        private readonly Label prompt;
        internal ShortcutBinding Binding;

        internal OutputShortcutForm(string destination, Func<ShortcutBinding, string> validate)
        {
            this.validate = validate;
            Text = "Output Shortcut — " + destination;
            ClientSize = new Size(440, 140);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            prompt = new Label { Text = "Press one key with Ctrl, Alt, Shift, or Windows.\nEscape cancels.",
                AutoSize = false, Location = new Point(16,16), Size = new Size(408,100) };
            Controls.Add(prompt);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            ShortcutBinding candidate = ShortcutBinding.FromKeyData(keyData,
                (NativeMethods.GetAsyncKeyState((int)Keys.LWin) & 0x8000) != 0
                || (NativeMethods.GetAsyncKeyState((int)Keys.RWin) & 0x8000) != 0);
            if (candidate.Key == Keys.None) return true;
            string error = validate(candidate);
            if (error != null) { prompt.Text = error + "\nPress another combination, or Escape to cancel."; return true; }
            Binding = candidate; DialogResult = DialogResult.OK; Close(); return true;
        }
    }
}
