using System;
using System.Drawing;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed class FilenameSettingsForm : Form
    {
        private readonly TextBox label;
        private readonly TextBox template;
        private readonly TextBox preview;
        private readonly Button save;
        private readonly long counter;
        private readonly DateTime timestamp = DateTime.Now;
        internal string FilenameLabel { get { return label.Text.Trim(); } }
        internal string FilenameTemplate { get { return template.Text; } }

        internal FilenameSettingsForm(AppSettings settings)
        {
            counter = settings.NextFilenameCounter;
            Text = "Capture Filenames"; ClientSize = new Size(610, 330);
            AutoScaleMode = AutoScaleMode.Font; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(new Label { Text = "Blank values keep the default names. Extensions are added automatically.\n"
                + "{label} {kind} {date} {hour} {minute} {second} {milliseconds} {counter}",
                Location = new Point(16,16), Size = new Size(578,42) });
            Controls.Add(new Label { Text = "Label", Location = new Point(16,68), AutoSize = true });
            label = new TextBox { Text = settings.FilenameLabel, Location = new Point(16,88), Width = 578 };
            Controls.Add(label);
            Controls.Add(new Label { Text = "Template", Location = new Point(16,124), AutoSize = true });
            template = new TextBox { Text = settings.FilenameTemplate, Location = new Point(16,144), Width = 578 };
            Controls.Add(template);
            preview = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Horizontal,
                WordWrap = false, Location = new Point(16,184), Size = new Size(578,76) };
            Controls.Add(preview);
            Button reset = new Button { Text = "Use Defaults", Location = new Point(16,282), Size = new Size(112,30) };
            reset.Click += delegate { label.Text = ""; template.Text = ""; };
            Controls.Add(reset);
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel,
                Location = new Point(414,282), Size = new Size(84,30) };
            Controls.Add(cancel); CancelButton = cancel;
            save = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(510,282), Size = new Size(84,30) };
            Controls.Add(save); AcceptButton = save;
            label.TextChanged += delegate { UpdatePreview(); };
            template.TextChanged += delegate { UpdatePreview(); };
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            try
            {
                preview.Text = CaptureFilename.Render(label.Text, template.Text, "Snip", timestamp, counter) + ".png\r\n"
                    + CaptureFilename.Render(label.Text, template.Text, "Clip", timestamp, counter) + ".mp4";
                save.Enabled = true;
            }
            catch (ArgumentException ex) { preview.Text = ex.Message; save.Enabled = false; }
        }
    }
}
