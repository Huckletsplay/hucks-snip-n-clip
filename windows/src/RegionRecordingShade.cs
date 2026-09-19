using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QSnipAndClip
{
    /// <summary>
    /// Keeps the live Region target obvious after selection: the recorded rectangle stays at true
    /// colour while the rest of the desktop is gently dimmed.
    ///
    /// Unlike ScreenCaptureKit on macOS, GDI <c>CopyFromScreen</c> has no window filter - it copies
    /// whatever is on screen - so this guide would be burned into the clip. The window is excluded
    /// with <c>WDA_EXCLUDEFROMCAPTURE</c>, measured on Windows 10 22H2 to hide it from
    /// <c>CopyFromScreen</c> completely. If that exclusion cannot be applied, the shade refuses to
    /// appear at all rather than risk contaminating the recording.
    /// </summary>
    internal sealed class RegionRecordingShade : IDisposable
    {
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x80;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

        private ShadeForm form;

        private RegionRecordingShade(ShadeForm form)
        {
            this.form = form;
        }

        /// <summary>
        /// Returns null when no shade should be shown - the region covers the whole desktop, or
        /// this Windows build will not exclude the window from capture.
        /// </summary>
        public static RegionRecordingShade TryShow(Rectangle recordedBounds)
        {
            Rectangle desktop = SystemInformation.VirtualScreen;
            if (recordedBounds.Width <= 0 || recordedBounds.Height <= 0) return null;
            if (recordedBounds.Contains(desktop)) return null;

            ShadeForm form = null;
            try
            {
                form = new ShadeForm(desktop, recordedBounds);
                form.Show();
                if (!SetWindowDisplayAffinity(form.Handle, WDA_EXCLUDEFROMCAPTURE))
                {
                    // Never show a guide this platform would record into the clip.
                    form.Dispose();
                    return null;
                }

                return new RegionRecordingShade(form);
            }
            catch
            {
                if (form != null) form.Dispose();
                return null;
            }
        }

        public void Dispose()
        {
            if (this.form == null) return;
            try { this.form.Close(); this.form.Dispose(); }
            catch { /* Closing a guide must never fail a finished recording. */ }
            this.form = null;
        }

        private sealed class ShadeForm : Form
        {
            public ShadeForm(Rectangle desktop, Rectangle recordedBounds)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                Bounds = desktop;
                BackColor = Color.Black;
                Opacity = 0.35;
                ShowInTaskbar = false;
                TopMost = true;
                Enabled = false;

                // Cut the recorded rectangle out entirely: the target keeps its true colour and
                // pointer input passes straight through the hole.
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddRectangle(new Rectangle(0, 0, desktop.Width, desktop.Height));
                    Rectangle hole = Rectangle.Intersect(recordedBounds, desktop);
                    hole.Offset(-desktop.X, -desktop.Y);
                    if (hole.Width > 0 && hole.Height > 0) path.AddRectangle(hole);
                    Region = new Region(path);
                }
            }

            protected override bool ShowWithoutActivation
            {
                get { return true; }
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.ExStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                    return parameters;
                }
            }
        }
    }
}
