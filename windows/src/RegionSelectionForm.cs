using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed class RegionSelectionForm : Form
    {
        private readonly Bitmap desktopImage;
        private readonly string confirmInstruction;
        private readonly bool requiresConfirmation;
        private Point dragStart;
        private Rectangle selection;
        private bool dragging;

        public RegionSelectionForm(Bitmap desktopImage)
            : this(desktopImage, null)
        {
        }

        public RegionSelectionForm(Bitmap desktopImage, string confirmInstruction)
        {
            if (desktopImage == null)
            {
                throw new ArgumentNullException("desktopImage");
            }

            this.desktopImage = desktopImage;
            this.requiresConfirmation = !String.IsNullOrWhiteSpace(confirmInstruction);
            this.confirmInstruction = String.IsNullOrWhiteSpace(confirmInstruction)
                ? "Enter confirms"
                : confirmInstruction;
            this.AutoScaleMode = AutoScaleMode.None;
            this.Bounds = SystemInformation.VirtualScreen;
            this.Cursor = Cursors.Cross;
            this.DoubleBuffered = true;
            this.FormBorderStyle = FormBorderStyle.None;
            this.KeyPreview = true;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.Manual;
            this.TopMost = true;

            this.Shown += delegate
            {
                this.Activate();
                this.Focus();
            };
        }

        public Rectangle SelectedRegion
        {
            get { return this.selection; }
        }

        internal bool RequiresConfirmation
        {
            get { return this.requiresConfirmation; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            this.dragStart = e.Location;
            this.selection = Rectangle.Empty;
            this.dragging = true;
            this.Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!this.dragging)
            {
                return;
            }

            this.selection = NormalizeRectangle(this.dragStart, e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!this.dragging || e.Button != MouseButtons.Left)
            {
                return;
            }

            this.dragging = false;
            this.Capture = false;
            this.selection = NormalizeRectangle(this.dragStart, e.Location);

            if (this.selection.Width < 3 || this.selection.Height < 3)
            {
                this.selection = Rectangle.Empty;
            }

            else if (!this.requiresConfirmation)
            {
                this.DialogResult = DialogResult.OK;
                Close();
                return;
            }

            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.Escape)
            {
                this.DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            if (e.KeyCode == Keys.R)
            {
                this.selection = Rectangle.Empty;
                this.dragging = false;
                this.Capture = false;
                Invalidate();
                return;
            }

            if (e.KeyCode == Keys.Enter && !this.selection.IsEmpty)
            {
                this.DialogResult = DialogResult.OK;
                Close();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.DrawImageUnscaled(this.desktopImage, 0, 0);
            using (Brush shade = new SolidBrush(Color.FromArgb(118, 0, 0, 0)))
            {
                e.Graphics.FillRectangle(shade, this.ClientRectangle);
            }

            if (!this.selection.IsEmpty)
            {
                e.Graphics.DrawImage(
                    this.desktopImage,
                    this.selection,
                    this.selection,
                    GraphicsUnit.Pixel);

                using (Pen outline = new Pen(Color.FromArgb(255, 55, 196, 255), 2.0f))
                {
                    outline.Alignment = PenAlignment.Inset;
                    e.Graphics.DrawRectangle(outline, this.selection);
                }
            }

            if (this.requiresConfirmation)
            {
                DrawInstructions(e.Graphics);
            }
        }

        private void DrawInstructions(Graphics graphics)
        {
            string message = this.selection.IsEmpty
                ? "Drag to select a region   •   Esc cancels"
                : this.confirmInstruction + "   •   R redraws   •   Esc cancels   •   "
                    + this.selection.Width + " × " + this.selection.Height;

            using (Font font = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point))
            {
                SizeF measured = graphics.MeasureString(message, font);
                int paddingX = 14;
                int paddingY = 8;
                RectangleF panel = new RectangleF(
                    (this.ClientSize.Width - measured.Width) / 2.0f - paddingX,
                    18,
                    measured.Width + (paddingX * 2),
                    measured.Height + (paddingY * 2));

                using (GraphicsPath path = CreateRoundedRectangle(panel, 8.0f))
                using (Brush background = new SolidBrush(Color.FromArgb(220, 24, 28, 34)))
                using (Brush foreground = new SolidBrush(Color.White))
                {
                    graphics.FillPath(background, path);
                    graphics.DrawString(
                        message,
                        font,
                        foreground,
                        panel.Left + paddingX,
                        panel.Top + paddingY);
                }
            }
        }

        private static Rectangle NormalizeRectangle(Point first, Point second)
        {
            int left = Math.Min(first.X, second.X);
            int top = Math.Min(first.Y, second.Y);
            int right = Math.Max(first.X, second.X);
            int bottom = Math.Max(first.Y, second.Y);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
        {
            float diameter = radius * 2.0f;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
