using System;
using System.Drawing;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed class TrayHelpFlyout : Form
    {
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int FlyoutWidth = 370;
        private const int FlyoutHeight = 156;

        public TrayHelpFlyout()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(248, 248, 248);
            ClientSize = new Size(FlyoutWidth, FlyoutHeight);
            ControlBox = false;
            FormBorderStyle = FormBorderStyle.None;
            KeyPreview = true;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;

            Label title = new Label();
            title.AutoSize = false;
            title.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 16.0f, FontStyle.Regular);
            title.Location = new Point(22, 19);
            title.Size = new Size(FlyoutWidth - 44, 34);
            title.Text = "Huck's Snip 'n' Clip";

            Label message = new Label();
            message.AutoSize = false;
            message.Font = SystemFonts.MessageBoxFont;
            message.Location = new Point(23, 64);
            message.Size = new Size(FlyoutWidth - 46, 75);
            message.Text =
                "Thanks for using Huck's Snip 'n' Clip for all your snipping and clipping needs.\r\n\r\n"
                + "Right-click the H for captures and settings.";

            Controls.Add(title);
            Controls.Add(message);
            Deactivate += delegate { Hide(); };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Hide();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WS_EX_TOOLWINDOW;
                parameters.ExStyle &= ~WS_EX_APPWINDOW;
                return parameters;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen border = new Pen(Color.FromArgb(205, 205, 205)))
            {
                e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        public void ShowAboveNotificationArea(Point trayPoint, IntPtr ownerHandle)
        {
            Rectangle work = Screen.FromPoint(trayPoint).WorkingArea;
            Location = new Point(work.Right - Width - 8, work.Bottom - Height - 8);

            if (Visible)
            {
                Activate();
                BringToFront();
                return;
            }

            Show(new HandleOwner(ownerHandle));
            Activate();
            BringToFront();
        }

        private sealed class HandleOwner : IWin32Window
        {
            public HandleOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; private set; }
        }
    }
}
