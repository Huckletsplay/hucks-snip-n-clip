using System;
using System.Drawing;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed class CountdownForm : Form
    {
        private readonly Label numberLabel;
        private readonly Timer timer;
        private int number;

        public CountdownForm(Screen display)
            : this(GetDisplayBounds(display))
        {
        }

        public CountdownForm(Rectangle targetBounds)
        {
            if (targetBounds.Width <= 0 || targetBounds.Height <= 0)
            {
                throw new ArgumentOutOfRangeException("targetBounds");
            }

            this.number = 3;
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;
            this.ClientSize = new Size(180, 180);
            this.Location = new Point(
                targetBounds.Left + ((targetBounds.Width - this.Width) / 2),
                targetBounds.Top + ((targetBounds.Height - this.Height) / 2));
            this.BackColor = Color.FromArgb(22, 30, 38);
            this.Opacity = 0.92;

            this.numberLabel = new Label();
            this.numberLabel.Dock = DockStyle.Fill;
            this.numberLabel.TextAlign = ContentAlignment.MiddleCenter;
            this.numberLabel.Font = new Font("Segoe UI", 84.0f, FontStyle.Bold, GraphicsUnit.Pixel);
            this.numberLabel.ForeColor = Color.White;
            this.numberLabel.Text = "3";
            this.Controls.Add(this.numberLabel);

            this.timer = new Timer();
            this.timer.Interval = 1000;
            this.timer.Tick += HandleTick;
            this.Shown += delegate { this.timer.Start(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.timer.Dispose();
                this.numberLabel.Dispose();
            }

            base.Dispose(disposing);
        }

        private void HandleTick(object sender, EventArgs e)
        {
            this.number--;
            if (this.number <= 0)
            {
                this.timer.Stop();
                this.DialogResult = DialogResult.OK;
                this.Close();
                return;
            }

            this.numberLabel.Text = this.number.ToString();
        }

        private static Rectangle GetDisplayBounds(Screen display)
        {
            if (display == null)
            {
                throw new ArgumentNullException("display");
            }

            return display.Bounds;
        }
    }
}
