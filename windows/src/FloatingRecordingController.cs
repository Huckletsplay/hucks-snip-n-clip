using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QSnipAndClip
{
    /// <summary>
    /// The recording-only floating H.
    ///
    /// Huck's Snip 'n' Clip has no persistent main window, and this does not become one: it exists
    /// only while a clip is actually recording and is destroyed when the take finishes. It is a
    /// borderless always-on-top surface carrying the same live H the tray shows - in white, at a
    /// size the user sets - with Pause/Resume and Stop sitting directly under the H's two posts, so
    /// a recording can be controlled with the taskbar hidden.
    ///
    /// **There is no panel behind it.** The window is shaped from the drawn pixels themselves, so
    /// what is on screen is the H, its two buttons and a resize notch, and nothing else. Everything
    /// around and between them is not part of the window at all, which also means clicks there go
    /// straight to whatever the user is really working on.
    ///
    /// It deliberately stays out of the way: no taskbar button, no Alt-Tab entry, no normal focus,
    /// and no keyboard interception. Right-button dragging the H moves it, which leaves ordinary
    /// left clicks free for its own controls.
    ///
    /// GDI <c>CopyFromScreen</c> has no window filter, so this window would otherwise be burned
    /// into the finished video. <c>WDA_EXCLUDEFROMCAPTURE</c> is applied before it is ever shown
    /// and verified afterwards; if the exclusion cannot be guaranteed the controller does not
    /// appear at all. A missing controller is safer than a controller in the recording.
    /// </summary>
    internal sealed class FloatingRecordingController : IDisposable
    {
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_TOPMOST = 0x8;

        internal const int DefaultOpacityPercent = 50;
        internal const int MinimumOpacityPercent = 10;
        internal const int MaximumOpacityPercent = 100;

        /// <summary>
        /// Size is the width of the H's own 64-unit square, in pixels. Everything else in the
        /// controller is laid out from it, so one number is the whole scale.
        /// </summary>
        internal const int DefaultSize = 72;
        internal const int MinimumSize = 44;
        internal const int MaximumSize = 220;

        // The design grid. The H bitmap is 64x64; the two buttons sit under the H's two posts and
        // are exactly as wide as they are. The resize notch lives *inside* the H, in the top-right
        // corner of the right post - which is what lets it stay hidden until the pointer arrives.
        // A notch outside the H could not do that: hiding it would take it out of the window's
        // shape, and a shape you are not over cannot tell you the pointer is near.
        internal const int DesignWidth = 64;
        internal const int DesignHeight = 78;
        private const int HTop = 0;
        private const int HBodyTop = 6;
        private const int HBodyBottom = 58;
        private const int LeftPostX = 6;
        private const int PostWidth = 23;
        private const int RightPostX = 35;
        private const int ButtonTop = 60;
        private const int ButtonHeight = 16;
        private const int GripSide = 12;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

        private ControllerForm form;

        private FloatingRecordingController(ControllerForm form)
        {
            this.form = form;
            this.form.PauseResumeRequested += delegate { Raise(this.PauseResumeRequested); };
            this.form.StopRequested += delegate { Raise(this.StopRequested); };
            this.form.Moved += delegate { Raise(this.Moved); };
            this.form.Resized += delegate { Raise(this.Resized); };
        }

        /// <summary>Raised when the user asks to pause or resume from the controller.</summary>
        public event EventHandler PauseResumeRequested;

        /// <summary>Raised once when the user asks to stop from the controller.</summary>
        public event EventHandler StopRequested;

        /// <summary>Raised after a right-drag finishes, so the layout can learn the new point.</summary>
        public event EventHandler Moved;

        /// <summary>Raised after a resize finishes, so the chosen size can be remembered.</summary>
        public event EventHandler Resized;

        public Point Location
        {
            get { return this.form == null ? Point.Empty : this.form.Location; }
        }

        public Size Size
        {
            get { return this.form == null ? Size.Empty : this.form.Size; }
        }

        /// <summary>The H square's width in pixels - the one number the whole layout scales from.</summary>
        public int ControllerSize
        {
            get { return this.form == null ? DefaultSize : this.form.ControllerSize; }
        }

        public void MoveTo(Point location)
        {
            if (this.form == null || this.form.IsDisposed) return;
            this.form.Location = location;
        }

        internal static int NormalizeOpacityPercent(int percent)
        {
            if (percent < MinimumOpacityPercent) return MinimumOpacityPercent;
            if (percent > MaximumOpacityPercent) return MaximumOpacityPercent;
            return percent;
        }

        internal static int NormalizeSize(int size)
        {
            if (size < MinimumSize) return MinimumSize;
            if (size > MaximumSize) return MaximumSize;
            return size;
        }

        /// <summary>The window's pixel size for a given H size.</summary>
        internal static Size Measure(int size)
        {
            size = NormalizeSize(size);
            float scale = size / 64.0f;
            return new Size(
                (int)Math.Round(DesignWidth * scale),
                (int)Math.Round(DesignHeight * scale));
        }

        /// <summary>
        /// Returns null for exactly one reason: this Windows build will not keep the controller out
        /// of the captured pixels, so it must not appear at all. Anything else that goes wrong
        /// throws, because a silent null would be reported to the user as a capture problem it was
        /// not.
        /// </summary>
        public static FloatingRecordingController TryShow(Point location, int size, int opacityPercent)
        {
            ControllerForm form = new ControllerForm(NormalizeSize(size));
            try
            {
                form.Location = location;
                form.Opacity = NormalizeOpacityPercent(opacityPercent) / 100.0;

                // Force the window into existence and exclude it from capture before it is shown,
                // so there is no frame in which it could be copied into a recording.
                if (!ApplyCaptureExclusion(form.Handle))
                {
                    form.Dispose();
                    return null;
                }

                form.Show();

                // Showing a window can recreate its handle; the exclusion is only worth anything
                // if it is still in force on the window the user can actually see.
                if (!ApplyCaptureExclusion(form.Handle))
                {
                    form.Dispose();
                    return null;
                }

                return new FloatingRecordingController(form);
            }
            catch
            {
                form.Dispose();
                throw;
            }
        }

        /// <summary>
        /// The controller's exact pixels, without putting a window on screen. The live window is
        /// excluded from screen capture by design, so this is the only way to look at what it draws
        /// - and it is the same routine, so a check against it is a check against the real thing.
        /// </summary>
        internal static Bitmap RenderPreview(
            int size, float inputLevel, float strainLevel, bool paused, bool finishing)
        {
            return RenderPreview(size, inputLevel, strainLevel, paused, finishing, false);
        }

        internal static Bitmap RenderPreview(
            int size, float inputLevel, float strainLevel, bool paused, bool finishing, bool showGrip)
        {
            Size window = Measure(size);
            Bitmap preview = new Bitmap(window.Width, window.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(preview))
            {
                graphics.Clear(Color.Transparent);
                ControllerForm.Render(
                    graphics, NormalizeSize(size), inputLevel, strainLevel, paused, finishing,
                    false, false, showGrip, showGrip);
            }

            return preview;
        }

        /// <summary>Design-space rectangles, scaled to a given controller size.</summary>
        internal static Rectangle MeasureLeftButton(int size)
        {
            return ScaleRect(LeftPostX, ButtonTop, PostWidth, ButtonHeight, NormalizeSize(size));
        }

        internal static Rectangle MeasureRightButton(int size)
        {
            return ScaleRect(RightPostX, ButtonTop, PostWidth, ButtonHeight, NormalizeSize(size));
        }

        /// <summary>Where the H's own posts sit, so a check can confirm the buttons line up.</summary>
        internal static Rectangle MeasureLeftPost(int size)
        {
            return ScaleRect(
                LeftPostX, HBodyTop, PostWidth, HBodyBottom - HBodyTop, NormalizeSize(size));
        }

        internal static Rectangle MeasureRightPost(int size)
        {
            return ScaleRect(
                RightPostX, HBodyTop, PostWidth, HBodyBottom - HBodyTop, NormalizeSize(size));
        }

        /// <summary>
        /// The resize notch, in the top-right corner of the H's right post. Square bounds; the
        /// notch itself is the upper-right triangle of it.
        /// </summary>
        internal static Rectangle MeasureGrip(int size)
        {
            return ScaleRect(
                RightPostX + PostWidth - GripSide, HBodyTop, GripSide, GripSide, NormalizeSize(size));
        }

        /// <summary>
        /// Rounds the edges, not the origin and the size separately. Rounding a width on its own
        /// lets two shapes that share an edge in the design grid miss each other by a pixel once
        /// scaled - and a button that is one pixel wider than the post above it is exactly the
        /// thing these measurements exist to rule out.
        /// </summary>
        private static Rectangle ScaleRect(int x, int y, int width, int height, int size)
        {
            float scale = size / 64.0f;
            return Rectangle.FromLTRB(
                (int)Math.Round(x * scale),
                (int)Math.Round(y * scale),
                (int)Math.Round((x + width) * scale),
                (int)Math.Round((y + height) * scale));
        }

        private static bool ApplyCaptureExclusion(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return false;
            if (!SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE)) return false;

            uint affinity;
            return GetWindowDisplayAffinity(handle, out affinity) && affinity == WDA_EXCLUDEFROMCAPTURE;
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void UpdateMeters(float inputLevel, float strainLevel)
        {
            if (this.form == null || this.form.IsDisposed) return;
            this.form.UpdateMeters(inputLevel, strainLevel);
        }

        public void SetPaused(bool paused)
        {
            if (this.form == null || this.form.IsDisposed) return;
            this.form.SetPaused(paused);
        }

        /// <summary>
        /// Disables both controls while the file is being finished, so a second click cannot ask
        /// for a stop that is already happening.
        /// </summary>
        public void SetFinishing()
        {
            if (this.form == null || this.form.IsDisposed) return;
            this.form.SetFinishing();
        }

        public void SetOpacityPercent(int percent)
        {
            if (this.form == null || this.form.IsDisposed) return;
            this.form.Opacity = NormalizeOpacityPercent(percent) / 100.0;
        }

        public void Dispose()
        {
            if (this.form == null) return;
            try { this.form.Close(); this.form.Dispose(); }
            catch { /* Closing the controller must never fail a finished recording. */ }
            this.form = null;
        }

        private sealed class ControllerForm : Form
        {
            private static readonly Color Body = Color.White;
            private static readonly Color Glyph = Color.FromArgb(70, 70, 70);
            private static readonly Color DimBody = Color.FromArgb(168, 168, 168);
            // The notch sits on the white H, so it reads as a corner cut out of it.
            private static readonly Color GripResting = Color.FromArgb(120, 120, 120);
            private static readonly Color GripActive = Color.FromArgb(40, 40, 40);

            private int size;
            private float inputLevel;
            private float strainLevel;
            private bool paused;
            private bool finishing;
            private bool draggingWindow;
            private bool draggingSize;
            private Point dragOrigin;
            private int dragStartSize;
            private Point dragStartBottomLeft;
            private bool hoverLeft;
            private bool hoverRight;
            private bool hoverGrip;
            private bool pointerInside;

            internal ControllerForm(int size)
            {
                this.size = size;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                MinimizeBox = false;
                MaximizeBox = false;
                ControlBox = false;
                AutoScaleMode = AutoScaleMode.None;
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

                // Never reached by the eye: the window is shaped to the drawn pixels, so this only
                // covers the instant before the first shape is applied.
                BackColor = Color.Black;
                ApplySize(size, false);
            }

            internal event EventHandler PauseResumeRequested;
            internal event EventHandler StopRequested;
            internal event EventHandler Moved;
            internal event EventHandler Resized;

            internal int ControllerSize
            {
                get { return this.size; }
            }

            private float PixelScale
            {
                get { return this.size / 64.0f; }
            }

            internal void UpdateMeters(float input, float strain)
            {
                if (this.paused || this.finishing) return;
                this.inputLevel = input;
                this.strainLevel = strain;
                Invalidate();
            }

            internal void SetPaused(bool value)
            {
                this.paused = value;
                Invalidate();
            }

            internal void SetFinishing()
            {
                this.finishing = true;
                Invalidate();
            }

            // ---- geometry ------------------------------------------------------------------

            private RectangleF LeftButton
            {
                get { return ButtonBounds(this.size, true); }
            }

            private RectangleF RightButton
            {
                get { return ButtonBounds(this.size, false); }
            }

            // ---- painting ------------------------------------------------------------------

            /// <summary>
            /// Draws the whole controller. The same routine produces the window's shape, so what is
            /// drawn and what the window *is* can never drift apart.
            /// </summary>
            private void Render(Graphics graphics)
            {
                Render(
                    graphics, this.size, this.inputLevel, this.strainLevel, this.paused,
                    this.finishing, this.hoverLeft, this.hoverRight, this.hoverGrip,
                    this.pointerInside || this.draggingSize);
            }

            internal static void Render(
                Graphics graphics, int size, float inputLevel, float strainLevel,
                bool paused, bool finishing, bool hoverLeft, bool hoverRight, bool hoverGrip,
                bool pointerInside)
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float scale = size / 64.0f;

                using (Bitmap h = paused || finishing
                    ? TrayIconFactory.CreateStatusBitmap(paused, size, Body, 255)
                    : TrayIconFactory.CreateBitmap(true, inputLevel, strainLevel, size, Body, 255))
                {
                    if (pointerInside)
                    {
                        TintGripPixels(h, size, hoverGrip ? GripActive : GripResting);
                    }
                    graphics.DrawImageUnscaled(h, 0, (int)Math.Round(HTop * scale));
                }

                DrawButton(graphics, ButtonBounds(size, true), scale, hoverLeft, finishing, true, paused);
                DrawButton(graphics, ButtonBounds(size, false), scale, hoverRight, finishing, false, paused);

            }

            /// <summary>
            /// Colours the resize notch without adding pixels to the H's silhouette. This matters
            /// because the same bitmap also defines the shaped window; painting a polygon over the
            /// transparent film holes would otherwise make the window subtly change on hover.
            /// </summary>
            private static void TintGripPixels(Bitmap h, int size, Color color)
            {
                using (GraphicsPath grip = new GraphicsPath())
                {
                    grip.AddPolygon(Grip(size));
                    Rectangle bounds = Rectangle.Intersect(
                        Rectangle.Ceiling(grip.GetBounds()),
                        new Rectangle(Point.Empty, h.Size));

                    for (int y = bounds.Top; y < bounds.Bottom; y++)
                    {
                        for (int x = bounds.Left; x < bounds.Right; x++)
                        {
                            Color existing = h.GetPixel(x, y);
                            if (existing.A > 0 && grip.IsVisible(x + 0.5f, y + 0.5f))
                            {
                                h.SetPixel(x, y, Color.FromArgb(existing.A, color));
                            }
                        }
                    }
                }
            }

            private static RectangleF ButtonBounds(int size, bool left)
            {
                float scale = size / 64.0f;
                return new RectangleF(
                    (left ? LeftPostX : RightPostX) * scale,
                    ButtonTop * scale,
                    PostWidth * scale,
                    ButtonHeight * scale);
            }

            /// <summary>
            /// The upper-right triangle of the right post's top corner: a corner visibly cut away,
            /// which is the shape a resize handle has everywhere else.
            /// </summary>
            private static PointF[] Grip(int size)
            {
                float scale = size / 64.0f;
                float right = (RightPostX + PostWidth) * scale;
                float top = HBodyTop * scale;
                float span = GripSide * scale;
                return new[]
                {
                    new PointF(right - span, top),
                    new PointF(right, top),
                    new PointF(right, top + span)
                };
            }

            private static void DrawButton(
                Graphics graphics, RectangleF bounds, float scale, bool hover, bool disabled,
                bool isPauseButton, bool paused)
            {
                float radius = 3.0f * scale;
                using (GraphicsPath path = RoundedRectangle(bounds, radius))
                using (Brush body = new SolidBrush(disabled ? DimBody : Body))
                {
                    graphics.FillPath(body, path);
                }

                // The glyph is cut out of the button, the way the H's own cut and film edge are
                // negative space rather than added marks.
                using (Brush mark = new SolidBrush(hover && !disabled ? Color.FromArgb(40, 40, 40) : Glyph))
                {
                    float cx = bounds.X + bounds.Width / 2.0f;
                    float cy = bounds.Y + bounds.Height / 2.0f;
                    if (!isPauseButton)
                    {
                        float side = 7.0f * scale;
                        graphics.FillRectangle(mark, cx - side / 2, cy - side / 2, side, side);
                    }
                    else if (paused)
                    {
                        float width = 7.0f * scale, height = 8.0f * scale;
                        graphics.FillPolygon(mark, new[]
                        {
                            new PointF(cx - width / 2, cy - height / 2),
                            new PointF(cx + width / 2, cy),
                            new PointF(cx - width / 2, cy + height / 2)
                        });
                    }
                    else
                    {
                        float barWidth = 2.5f * scale, barHeight = 8.0f * scale, gap = 2.5f * scale;
                        graphics.FillRectangle(mark, cx - gap / 2 - barWidth, cy - barHeight / 2, barWidth, barHeight);
                        graphics.FillRectangle(mark, cx + gap / 2, cy - barHeight / 2, barWidth, barHeight);
                    }
                }
            }

            private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
            {
                GraphicsPath path = new GraphicsPath();
                float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
                if (diameter <= 1)
                {
                    path.AddRectangle(bounds);
                    return path;
                }

                path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
                path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
                path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                return path;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Render(e.Graphics);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // No panel. Whatever is not drawn is not part of this window.
            }

            // ---- window shape --------------------------------------------------------------

            /// <summary>
            /// Rebuilds the window shape from what the controller actually draws, so there is no
            /// rectangle behind it. Only run when the drawn silhouette can have changed - a size
            /// change or a button-state change - never for a meter update, which moves colour
            /// around inside a shape that stays the same.
            /// </summary>
            private void ApplyShape()
            {
                Size window = Measure(this.size);
                using (Bitmap mask = new Bitmap(window.Width, window.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics graphics = Graphics.FromImage(mask))
                    {
                        graphics.Clear(Color.Transparent);
                        Render(graphics);
                    }

                    using (GraphicsPath path = TraceOpaque(mask))
                    {
                        Region replacement = new Region(path);
                        Region previous = Region;
                        Region = replacement;
                        if (previous != null) previous.Dispose();
                    }
                }
            }

            /// <summary>
            /// Turns the drawn pixels into a shape by unioning the opaque runs of each row. Cheap
            /// enough to run on a resize, and it needs no second copy of the H's geometry - which
            /// is the point, because a hand-written outline would drift the first time the mark
            /// changed.
            /// </summary>
            private static GraphicsPath TraceOpaque(Bitmap mask)
            {
                GraphicsPath path = new GraphicsPath();
                BitmapData data = mask.LockBits(
                    new Rectangle(0, 0, mask.Width, mask.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                try
                {
                    byte[] row = new byte[data.Stride];
                    for (int y = 0; y < mask.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, data.Stride);
                        int runStart = -1;
                        for (int x = 0; x < mask.Width; x++)
                        {
                            bool solid = row[x * 4 + 3] >= 128;
                            if (solid && runStart < 0) runStart = x;
                            else if (!solid && runStart >= 0)
                            {
                                path.AddRectangle(new Rectangle(runStart, y, x - runStart, 1));
                                runStart = -1;
                            }
                        }

                        if (runStart >= 0)
                        {
                            path.AddRectangle(new Rectangle(runStart, y, mask.Width - runStart, 1));
                        }
                    }
                }
                finally { mask.UnlockBits(data); }

                return path;
            }

            private void ApplySize(int newSize, bool anchorBottomLeft)
            {
                int clamped = NormalizeSize(newSize);
                Size window = Measure(clamped);
                int bottom = Top + Height;
                this.size = clamped;
                if (anchorBottomLeft)
                {
                    // The notch is at the top right, so the far corner is what stays put.
                    SetBounds(Left, bottom - window.Height, window.Width, window.Height);
                }
                else
                {
                    ClientSize = window;
                }

                ApplyShape();
                Invalidate();
            }

            // ---- input ---------------------------------------------------------------------

            protected override void OnMouseDown(MouseEventArgs e)
            {
                // The right button does both jobs: on the notch it resizes, anywhere else it
                // moves. Left stays free for Pause/Resume and Stop.
                if (e.Button == MouseButtons.Right && InGrip(e.Location))
                {
                    this.draggingSize = true;
                    this.dragOrigin = Control.MousePosition;
                    this.dragStartSize = this.size;
                    this.dragStartBottomLeft = new Point(Left, Top + Height);
                    HoldMouse();
                }
                else if (e.Button == MouseButtons.Right)
                {
                    this.draggingWindow = true;
                    this.dragOrigin = Control.MousePosition;
                    this.dragOrigin.Offset(-Left, -Top);
                    HoldMouse();
                }

                base.OnMouseDown(e);
            }

            /// <summary>
            /// Takes the mouse for the length of a drag.
            ///
            /// Without this the controller visibly lags behind the pointer and then snaps to it.
            /// WinForms only captures the mouse itself for a left button on a UserMouse control, so
            /// a right-button drag here got move messages only while the pointer happened to still
            /// be over the window - and this window is *shaped*, so a pointer moving faster than
            /// the window leaves it almost immediately and the moves stop arriving.
            /// </summary>
            private void HoldMouse()
            {
                Capture = true;
            }

            private void ReleaseMouse()
            {
                if (Capture) Capture = false;
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                // Something else took the mouse - end the drag where it stands rather than leaving
                // the controller half-dragged.
                if (!Capture && (this.draggingWindow || this.draggingSize)) FinishDrag();
                base.OnMouseCaptureChanged(e);
            }

            private void FinishDrag()
            {
                bool wasMoving = this.draggingWindow;
                bool wasSizing = this.draggingSize;
                this.draggingWindow = false;
                this.draggingSize = false;
                if (wasSizing)
                {
                    EventHandler resized = this.Resized;
                    if (resized != null) resized(this, EventArgs.Empty);
                }
                else if (wasMoving)
                {
                    EventHandler moved = this.Moved;
                    if (moved != null) moved(this, EventArgs.Empty);
                }

                Invalidate();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (this.draggingSize)
                {
                    Point cursor = Control.MousePosition;
                    // Out to the right or up makes it bigger, which is the direction the notch
                    // itself points.
                    int delta = (cursor.X - this.dragOrigin.X) - (cursor.Y - this.dragOrigin.Y);
                    int wanted = this.dragStartSize + (int)Math.Round(delta * 64.0 / DesignWidth);
                    if (wanted != this.size)
                    {
                        Size window = Measure(NormalizeSize(wanted));
                        // Out to the right or up makes it bigger, which is the direction the notch
                        // itself points.
                        this.size = NormalizeSize(wanted);
                        SetBounds(
                            this.dragStartBottomLeft.X,
                            this.dragStartBottomLeft.Y - window.Height,
                            window.Width,
                            window.Height);
                        ApplyShape();
                        Invalidate();
                    }
                }
                else if (this.draggingWindow)
                {
                    Point cursor = Control.MousePosition;
                    Location = new Point(cursor.X - this.dragOrigin.X, cursor.Y - this.dragOrigin.Y);
                }
                else
                {
                    bool left = !this.finishing && LeftButton.Contains(e.Location);
                    bool right = !this.finishing && RightButton.Contains(e.Location);
                    bool grip = InGrip(e.Location);
                    if (left != this.hoverLeft || right != this.hoverRight || grip != this.hoverGrip
                        || !this.pointerInside)
                    {
                        this.hoverLeft = left;
                        this.hoverRight = right;
                        this.hoverGrip = grip;
                        this.pointerInside = true;
                        Invalidate();
                    }
                }

                base.OnMouseMove(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if ((this.draggingSize || this.draggingWindow) && e.Button == MouseButtons.Right)
                {
                    ReleaseMouse();
                    FinishDrag();
                }
                else if (e.Button == MouseButtons.Left && !this.finishing)
                {
                    if (LeftButton.Contains(e.Location))
                    {
                        EventHandler pause = this.PauseResumeRequested;
                        if (pause != null) pause(this, EventArgs.Empty);
                    }
                    else if (RightButton.Contains(e.Location))
                    {
                        EventHandler stop = this.StopRequested;
                        if (stop != null) stop(this, EventArgs.Empty);
                    }
                }

                base.OnMouseUp(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                // Keep the notch up while a resize is in progress: the pointer leaves the window
                // constantly while dragging it larger.
                if (this.draggingSize || this.draggingWindow) { base.OnMouseLeave(e); return; }

                if (this.hoverLeft || this.hoverRight || this.hoverGrip || this.pointerInside)
                {
                    this.hoverLeft = this.hoverRight = this.hoverGrip = false;
                    this.pointerInside = false;
                    Invalidate();
                }

                base.OnMouseLeave(e);
            }

            private bool InGrip(Point client)
            {
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddPolygon(Grip(this.size));
                    return path.IsVisible(client);
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
                    // NOACTIVATE keeps the keyboard with whatever the user is really doing;
                    // TOOLWINDOW keeps this out of the taskbar and Alt-Tab.
                    parameters.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                    return parameters;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                ApplyShape();
            }
        }
    }
}
