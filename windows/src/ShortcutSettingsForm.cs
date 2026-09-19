using System;
using System.Collections.Generic;
using System.Drawing;
using System.Media;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed class ShortcutSettingsForm : Form
    {
        private readonly Dictionary<CaptureAction, ShortcutCaptureBox> editors;

        public ShortcutSettingsForm(AppSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            this.editors = new Dictionary<CaptureAction, ShortcutCaptureBox>();
            this.Text = "Huck’s Snip ’n’ Clip Shortcuts";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(470, 390);

            Label instructions = new Label();
            instructions.AutoSize = false;
            instructions.Location = new Point(18, 16);
            instructions.Size = new Size(434, 52);
            instructions.Text = "Click a field and press a shortcut. For a two-step chord, press the second shortcut within 1.5 seconds (example: Ctrl+Alt+C, then R).";
            this.Controls.Add(instructions);

            TableLayoutPanel table = new TableLayoutPanel();
            table.Location = new Point(18, 76);
            table.Size = new Size(434, 204);
            table.ColumnCount = 2;
            table.RowCount = ShortcutCatalog.Actions.Length;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42.0f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58.0f));
            table.RowStyles.Clear();

            int row = 0;
            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34.0f));

                Label label = new Label();
                label.Text = ShortcutCatalog.GetName(action);
                label.TextAlign = ContentAlignment.MiddleLeft;
                label.Dock = DockStyle.Fill;
                table.Controls.Add(label, 0, row);

                ShortcutCaptureBox editor = new ShortcutCaptureBox(settings.GetShortcut(action));
                editor.Dock = DockStyle.Fill;
                editor.Margin = new Padding(3, 4, 3, 4);
                this.editors[action] = editor;
                table.Controls.Add(editor, 1, row);
                row++;
            }

            this.Controls.Add(table);

            Label warning = new Label();
            warning.AutoSize = false;
            warning.Location = new Point(18, 288);
            warning.Size = new Size(434, 34);
            warning.ForeColor = Color.FromArgb(135, 75, 15);
            warning.Text = "Bare keys work globally, but Q will intercept normal typing of those keys while it runs.";
            this.Controls.Add(warning);

            Button reset = new Button();
            reset.Text = "Reset Defaults";
            reset.Location = new Point(18, 338);
            reset.Size = new Size(110, 32);
            reset.Click += delegate { ResetDefaults(); };
            this.Controls.Add(reset);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(276, 338);
            cancel.Size = new Size(84, 32);
            this.Controls.Add(cancel);

            Button save = new Button();
            save.Text = "Save";
            save.Location = new Point(368, 338);
            save.Size = new Size(84, 32);
            save.Click += HandleSave;
            this.Controls.Add(save);

            this.AcceptButton = save;
            this.CancelButton = cancel;
        }

        public Dictionary<CaptureAction, ShortcutBinding> GetBindings()
        {
            Dictionary<CaptureAction, ShortcutBinding> bindings = new Dictionary<CaptureAction, ShortcutBinding>();
            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                bindings[action] = this.editors[action].Binding.Clone();
            }

            return bindings;
        }

        private void ResetDefaults()
        {
            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                this.editors[action].Binding = ShortcutCatalog.GetDefault(action);
            }
        }

        private void HandleSave(object sender, EventArgs e)
        {
            CaptureAction[] actions = ShortcutCatalog.Actions;
            for (int index = 0; index < actions.Length; index++)
            {
                ShortcutBinding first = this.editors[actions[index]].Binding;
                for (int otherIndex = index + 1; otherIndex < actions.Length; otherIndex++)
                {
                    if (first.HasSameFirstStroke(this.editors[actions[otherIndex]].Binding))
                    {
                        ShortcutBinding other = this.editors[actions[otherIndex]].Binding;
                        CopyableDialog.Show(
                            this,
                            ShortcutCatalog.GetName(actions[index]) + " (" + first.ToDisplayString() + ") and "
                                + ShortcutCatalog.GetName(actions[otherIndex]) + " (" + other.ToDisplayString() + ")"
                                + " begin with the same shortcut. Chord prefixes must currently be unique.",
                            "Shortcut Prefix Conflict",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        this.editors[actions[otherIndex]].Focus();
                        return;
                    }
                }
            }

            bool hasBareKey = false;
            foreach (CaptureAction action in actions)
            {
                if (this.editors[action].Binding.Modifiers == 0)
                {
                    hasBareKey = true;
                    break;
                }
            }

            if (hasBareKey)
            {
                DialogResult warningResult = CopyableDialog.Show(
                    this,
                    "Modifier-free shortcuts will replace normal typing of those keys everywhere while Huck’s Snip ’n’ Clip is running. Save them anyway?",
                    "Global Bare-Key Shortcut",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (warningResult != DialogResult.Yes)
                {
                    return;
                }
            }

            this.DialogResult = DialogResult.OK;
            Close();
        }
    }

    internal sealed class ShortcutCaptureBox : Button
    {
        private ShortcutBinding binding;
        private ShortcutBinding pendingFirstStroke;
        private readonly Timer chordCaptureTimer;
        private bool captureArmed;

        public ShortcutCaptureBox(ShortcutBinding binding)
        {
            this.FlatStyle = FlatStyle.System;
            this.TextAlign = ContentAlignment.MiddleCenter;
            this.chordCaptureTimer = new Timer();
            this.chordCaptureTimer.Interval = 1500;
            this.chordCaptureTimer.Tick += delegate { CompletePendingCapture(); };
            this.Binding = binding;
        }

        public ShortcutBinding Binding
        {
            get { return this.binding; }
            set
            {
                CompletePendingCapture();
                if (value == null || !value.IsValid())
                {
                    throw new ArgumentException("A valid shortcut binding is required.", "value");
                }

                this.binding = value.Clone();
                this.Text = this.binding.ToDisplayString();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!this.captureArmed)
            {
                base.OnKeyDown(e);
                return;
            }

            if (!TryCaptureKeyData(e.KeyData, IsWinPressed()))
            {
                SystemSounds.Beep.Play();
            }

            e.Handled = true;
            e.SuppressKeyPress = true;
            base.OnKeyDown(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return true;
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (!this.captureArmed)
            {
                return base.ProcessCmdKey(ref message, keyData);
            }

            if (!TryCaptureKeyData(keyData, IsWinPressed()))
            {
                SystemSounds.Beep.Play();
            }

            return true;
        }

        internal bool TryCaptureKeyData(Keys keyData, bool winPressed)
        {
            ShortcutBinding candidate = ShortcutBinding.FromKeyData(keyData, winPressed);
            if (!candidate.IsValid())
            {
                return false;
            }

            if (this.pendingFirstStroke != null)
            {
                if (this.pendingFirstStroke.HasSameFirstStroke(candidate))
                {
                    return true;
                }

                ShortcutBinding first = this.pendingFirstStroke;
                CompletePendingCapture();
                this.Binding = first.WithSecondStroke(candidate);
                return true;
            }

            this.binding = candidate.Clone();
            this.pendingFirstStroke = candidate.Clone();
            this.Text = candidate.ToDisplayString() + ", …";
            this.chordCaptureTimer.Stop();
            this.chordCaptureTimer.Start();
            return true;
        }

        internal void CompletePendingCapture()
        {
            this.chordCaptureTimer.Stop();
            this.pendingFirstStroke = null;
            this.captureArmed = false;
            if (this.binding != null)
            {
                this.Text = this.binding.ToDisplayString();
            }
        }

        internal bool CaptureArmed
        {
            get { return this.captureArmed; }
        }

        internal void BeginCapture()
        {
            CompletePendingCapture();
            this.captureArmed = true;
            this.Text = "Press shortcut…";
        }

        protected override void OnClick(EventArgs e)
        {
            BeginCapture();
            base.OnClick(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            CompletePendingCapture();
            base.OnLostFocus(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.chordCaptureTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        private static bool IsWinPressed()
        {
            return (NativeMethods.GetAsyncKeyState((int)Keys.LWin) & 0x8000) != 0
                || (NativeMethods.GetAsyncKeyState((int)Keys.RWin) & 0x8000) != 0;
        }
    }
}
