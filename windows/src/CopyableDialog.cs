using System;
using System.Drawing;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal static class CopyableDialog
    {
        public static DialogResult Show(
            string message,
            string caption,
            MessageBoxButtons buttons,
            MessageBoxIcon icon)
        {
            return Show(null, message, caption, buttons, icon, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(
            string message,
            string caption,
            MessageBoxButtons buttons,
            MessageBoxIcon icon,
            MessageBoxDefaultButton defaultButton)
        {
            return Show(null, message, caption, buttons, icon, defaultButton);
        }

        public static DialogResult Show(
            IWin32Window owner,
            string message,
            string caption,
            MessageBoxButtons buttons,
            MessageBoxIcon icon)
        {
            return Show(owner, message, caption, buttons, icon, MessageBoxDefaultButton.Button1);
        }

        public static DialogResult Show(
            IWin32Window owner,
            string message,
            string caption,
            MessageBoxButtons buttons,
            MessageBoxIcon icon,
            MessageBoxDefaultButton defaultButton)
        {
            using (CopyableMessageForm dialog = new CopyableMessageForm(
                message,
                caption,
                buttons,
                icon,
                defaultButton))
            {
                return owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            }
        }

        internal static string GetCopyAllText(string caption, string message)
        {
            if (String.IsNullOrWhiteSpace(caption))
            {
                return message ?? String.Empty;
            }

            return caption + Environment.NewLine + Environment.NewLine + (message ?? String.Empty);
        }
    }

    internal sealed class CopyableMessageForm : Form
    {
        private readonly RichTextBox messageBox;
        private readonly ContextMenuStrip messageMenu;
        private readonly string copyAllText;

        public CopyableMessageForm(
            string message,
            string caption,
            MessageBoxButtons buttons,
            MessageBoxIcon icon,
            MessageBoxDefaultButton defaultButton)
        {
            this.Text = caption ?? String.Empty;
            this.copyAllText = CopyableDialog.GetCopyAllText(caption, message);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(548, 228);
            this.KeyPreview = true;

            PictureBox iconBox = new PictureBox();
            iconBox.Location = new Point(18, 20);
            iconBox.Size = new Size(36, 36);
            iconBox.SizeMode = PictureBoxSizeMode.CenterImage;
            Icon dialogIcon = GetSystemIcon(icon);
            if (dialogIcon != null)
            {
                iconBox.Image = dialogIcon.ToBitmap();
            }
            this.Controls.Add(iconBox);

            this.messageBox = new RichTextBox();
            this.messageBox.Location = new Point(68, 18);
            this.messageBox.Size = new Size(462, 150);
            this.messageBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            this.messageBox.ReadOnly = true;
            this.messageBox.BackColor = SystemColors.Window;
            this.messageBox.BorderStyle = BorderStyle.FixedSingle;
            this.messageBox.DetectUrls = false;
            this.messageBox.ScrollBars = RichTextBoxScrollBars.Vertical;
            this.messageBox.Text = message ?? String.Empty;
            this.messageBox.TabStop = true;
            this.Controls.Add(this.messageBox);

            this.messageMenu = new ContextMenuStrip();
            ToolStripMenuItem copy = new ToolStripMenuItem("Copy");
            copy.ShortcutKeyDisplayString = "Ctrl+C";
            copy.Click += delegate { CopySelectionOrMessage(); };
            this.messageMenu.Items.Add(copy);

            ToolStripMenuItem copyAll = new ToolStripMenuItem("Copy All");
            copyAll.Click += delegate { CopyText(this.copyAllText); };
            this.messageMenu.Items.Add(copyAll);
            this.messageBox.ContextMenuStrip = this.messageMenu;
            this.ContextMenuStrip = this.messageMenu;

            ConfigureButtons(buttons, defaultButton);
            this.Shown += delegate
            {
                this.messageBox.Focus();
                this.messageBox.Select(0, 0);
            };
        }

        internal string MessageText
        {
            get { return this.messageBox.Text; }
        }

        internal int CopyMenuItemCount
        {
            get { return this.messageMenu.Items.Count; }
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.C))
            {
                CopySelectionOrMessage();
                return true;
            }

            return base.ProcessCmdKey(ref message, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (this.messageBox.ContextMenuStrip == this.messageMenu)
                {
                    this.messageBox.ContextMenuStrip = null;
                }

                if (this.ContextMenuStrip == this.messageMenu)
                {
                    this.ContextMenuStrip = null;
                }

                this.messageMenu.Dispose();
                if (this.Controls.Count > 0)
                {
                    PictureBox iconBox = this.Controls[0] as PictureBox;
                    if (iconBox != null && iconBox.Image != null)
                    {
                        iconBox.Image.Dispose();
                        iconBox.Image = null;
                    }
                }
            }

            base.Dispose(disposing);
        }

        private void ConfigureButtons(MessageBoxButtons buttons, MessageBoxDefaultButton defaultButton)
        {
            if (buttons == MessageBoxButtons.OK)
            {
                Button ok = CreateButton("OK", DialogResult.OK, 446);
                this.AcceptButton = ok;
                this.CancelButton = ok;
                ok.Focus();
                return;
            }

            if (buttons == MessageBoxButtons.YesNo)
            {
                Button no = CreateButton("No", DialogResult.No, 446);
                Button yes = CreateButton("Yes", DialogResult.Yes, 354);
                this.AcceptButton = defaultButton == MessageBoxDefaultButton.Button2 ? no : yes;
                this.CancelButton = no;
                if (defaultButton == MessageBoxDefaultButton.Button2)
                {
                    no.Focus();
                }
                else
                {
                    yes.Focus();
                }

                return;
            }

            throw new NotSupportedException("This copyable dialog button layout is not supported.");
        }

        private Button CreateButton(string text, DialogResult result, int left)
        {
            Button button = new Button();
            button.Text = text;
            button.DialogResult = result;
            button.Location = new Point(left, 182);
            button.Size = new Size(84, 30);
            this.Controls.Add(button);
            return button;
        }

        private void CopySelectionOrMessage()
        {
            string selected = this.messageBox.SelectedText;
            CopyText(String.IsNullOrEmpty(selected) ? this.messageBox.Text : selected);
        }

        private static void CopyText(string text)
        {
            if (!String.IsNullOrEmpty(text))
            {
                try
                {
                    Clipboard.SetText(text);
                }
                catch (ExternalException)
                {
                    SystemSounds.Beep.Play();
                }
            }
        }

        private static Icon GetSystemIcon(MessageBoxIcon icon)
        {
            switch (icon)
            {
                case MessageBoxIcon.Error: return SystemIcons.Error;
                case MessageBoxIcon.Warning: return SystemIcons.Warning;
                case MessageBoxIcon.Question: return SystemIcons.Question;
                case MessageBoxIcon.Information: return SystemIcons.Information;
                default: return null;
            }
        }
    }
}
