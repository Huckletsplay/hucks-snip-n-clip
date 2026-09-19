using System;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed partial class TrayApplicationContext
    {
        private string ReserveFilename(string kind, DateTime started)
        {
            long counter = this.settings.NextFilenameCounter;
            string stem = CaptureFilename.Render(this.settings.FilenameLabel, this.settings.FilenameTemplate, kind, started, counter);
            this.settings.NextFilenameCounter = checked(counter + 1);
            try { this.settingsStore.Save(this.settings); }
            catch { this.settings.NextFilenameCounter = counter; throw; }
            return stem;
        }

        // The Mac shows the current naming as a submenu with live previews rather than hiding it
        // behind a dialog, so the format can be read without opening anything.
        private ToolStripMenuItem CreateFilenamesMenu()
        {
            bool isDefault = String.IsNullOrEmpty(this.settings.FilenameLabel)
                && (String.IsNullOrEmpty(this.settings.FilenameTemplate)
                    || this.settings.FilenameTemplate == CaptureFilename.DefaultTemplate);

            ToolStripMenuItem menu = new ToolStripMenuItem(
                "Filenames — " + (isDefault ? "Default" : "Custom"));
            menu.Enabled = this.clipRecorder == null && !this.captureInProgress;

            DateTime now = DateTime.Now;
            long counter = this.settings.NextFilenameCounter;
            AddFilenamePreview(menu, "Snip", ".png", now, counter);
            AddFilenamePreview(menu, "Clip", ".mp4", now, counter);
            menu.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem configure = new ToolStripMenuItem("Configure Filenames…");
            configure.Click += delegate { EditFilenames(); };
            menu.DropDownItems.Add(configure);

            ToolStripMenuItem reset = new ToolStripMenuItem("Reset to Default");
            reset.Enabled = !isDefault;
            reset.Click += delegate { ResetFilenames(); };
            menu.DropDownItems.Add(reset);
            return menu;
        }

        private void AddFilenamePreview(
            ToolStripMenuItem parent, string kind, string extension, DateTime now, long counter)
        {
            string preview;
            try { preview = CaptureFilename.Render(this.settings.FilenameLabel, this.settings.FilenameTemplate, kind, now, counter) + extension; }
            catch (Exception ex) { preview = "invalid — " + Shorten(ex.Message, 60); }
            ToolStripMenuItem row = new ToolStripMenuItem(kind + " — " + preview);
            row.Enabled = false;
            parent.DropDownItems.Add(row);
        }

        private void ResetFilenames()
        {
            string previousLabel = this.settings.FilenameLabel;
            string previousTemplate = this.settings.FilenameTemplate;
            this.settings.FilenameLabel = "";
            this.settings.FilenameTemplate = CaptureFilename.DefaultTemplate;
            try
            {
                this.settingsStore.Save(this.settings);
                ShowRoutineNotification("Filenames reset", "Capture filenames are back to the default format.");
                RebuildContextMenu();
            }
            catch (Exception ex)
            {
                this.settings.FilenameLabel = previousLabel;
                this.settings.FilenameTemplate = previousTemplate;
                CopyableDialog.Show(ex.Message, "Filenames", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void EditFilenames()
        {
            if (this.clipRecorder != null || this.captureInProgress) return;
            // Text entry must never trigger one of the user's global bare-key capture bindings.
            this.outputEditorOpen = true;
            RefreshOutputShortcuts();
            UnregisterAllShortcuts();
            try
            {
                using (FilenameSettingsForm editor = new FilenameSettingsForm(this.settings))
                {
                    if (editor.ShowDialog() != DialogResult.OK) return;
                    string oldLabel = this.settings.FilenameLabel;
                    string oldTemplate = this.settings.FilenameTemplate;
                    this.settings.FilenameLabel = editor.FilenameLabel;
                    this.settings.FilenameTemplate = editor.FilenameTemplate;
                    try { this.settingsStore.Save(this.settings); }
                    catch
                    {
                        this.settings.FilenameLabel = oldLabel; this.settings.FilenameTemplate = oldTemplate; throw;
                    }
                }
            }
            catch (Exception ex) { CopyableDialog.Show(ex.Message, "Filenames", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { this.outputEditorOpen = false; RegisterConfiguredShortcuts(); RebuildContextMenu(); }
        }
    }
}
