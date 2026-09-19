using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;

namespace QSnipAndClip
{
    internal static class TrayIconFactory
    {
        private static readonly Color RestingColor = Color.FromArgb(150, 150, 150);
        private static readonly Color InputColor = Color.FromArgb(63, 157, 98);
        private static readonly Color InputWarningColor = Color.FromArgb(201, 138, 46);
        // The right post is amber for ordinary load and turns red only once the machine is genuinely
        // overloaded. A post that was red from its first pixel read as a fault at every level, so
        // the one state worth noticing had nothing left to announce itself with.
        private static readonly Color StrainColor = Color.FromArgb(201, 138, 46);
        private static readonly Color StrainWarningColor = Color.FromArgb(192, 80, 63);

        // Shared by both posts, so the icon has one idea of "into the warning band" rather than two.
        internal const float MeterWarningThreshold = 0.85f;

        // The tray mark's unfilled track is deliberately faint so the meters read against it.
        internal const int TrayTrackAlpha = 56;

        /// <summary>A mark that reads against the given body colour, for pause bars and dots.</summary>
        private static Color ContrastFor(Color body)
        {
            return body.R + body.G + body.B > 500
                ? Color.FromArgb(70, 70, 70)
                : body;
        }

        public static Icon Create()
        {
            return CreateIcon(false, 0.0f, 0.0f);
        }

        public static Icon CreateRecording()
        {
            return CreateRecording(0.0f, 0.0f);
        }

        public static Icon CreateRecording(bool audioActive)
        {
            return CreateRecording(audioActive ? 0.72f : 0.0f, 0.0f);
        }

        public static Icon CreateRecording(float inputLevel, float strainLevel)
        {
            return CreateIcon(true, inputLevel, strainLevel);
        }

        public static Icon CreateStatus(bool paused)
        {
            using (Bitmap bitmap = CreateStatusBitmap(paused, 32))
            {
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon icon = Icon.FromHandle(handle)) return (Icon)icon.Clone();
                }
                finally { DestroyIcon(handle); }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        internal static Bitmap CreateStatusBitmap(bool paused, int side)
        {
            return CreateStatusBitmap(paused, side, RestingColor, TrayTrackAlpha);
        }

        /// <summary>
        /// The same status H in another colour. The floating recording controller draws a white H
        /// with a solid track, because it sits on the user's own desktop rather than on a taskbar
        /// whose colour it has to survive.
        /// </summary>
        internal static Bitmap CreateStatusBitmap(bool paused, int side, Color restingColor, int trackAlpha)
        {
            Bitmap bitmap = CreateBitmap(true, 0, 0, side, restingColor, trackAlpha);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Brush brush = new SolidBrush(ContrastFor(restingColor)))
            {
                float scale = side / 64.0f;
                if (paused)
                {
                    graphics.FillRectangle(brush, 20 * scale, 18 * scale, 8 * scale, 28 * scale);
                    graphics.FillRectangle(brush, 36 * scale, 18 * scale, 8 * scale, 28 * scale);
                }
                else
                    foreach (int x in new[] { 17, 29, 41 })
                        graphics.FillRectangle(brush, x * scale, 29 * scale, 6 * scale, 6 * scale);
            }
            return bitmap;
        }

        internal static Bitmap CreateBitmap(bool recording)
        {
            return CreateBitmap(recording, 0.0f, 0.0f, 32);
        }

        internal static Bitmap CreateBitmap(bool recording, bool audioActive)
        {
            return CreateBitmap(recording, audioActive ? 0.72f : 0.0f, 0.0f, 32);
        }

        internal static Bitmap CreateBitmap(bool recording, float inputLevel, float strainLevel)
        {
            return CreateBitmap(recording, inputLevel, strainLevel, 32);
        }

        internal static Bitmap CreateBitmap(
            bool recording,
            float inputLevel,
            float strainLevel,
            int side)
        {
            return CreateBitmap(recording, inputLevel, strainLevel, side, RestingColor, TrayTrackAlpha);
        }

        /// <summary>
        /// The tray's H is grey with a faint track, because it is a 16 px mark competing with a
        /// taskbar. The floating recording controller asks for the same geometry in white with a
        /// solid track: it is drawn several times larger on the user's own desktop, and its window
        /// is shaped from these pixels, so a track at tray alpha would leave almost nothing to see
        /// or to grab.
        /// </summary>
        internal static Bitmap CreateBitmap(
            bool recording,
            float inputLevel,
            float strainLevel,
            int side,
            Color restingColor,
            int trackAlpha)
        {
            if (side < 16)
            {
                throw new ArgumentOutOfRangeException("side");
            }

            if (trackAlpha < 0 || trackAlpha > 255)
            {
                throw new ArgumentOutOfRangeException("trackAlpha");
            }

            inputLevel = Clamp(inputLevel);
            strainLevel = Clamp(strainLevel);

            Bitmap bitmap = new Bitmap(side, side);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (GraphicsPath hPath = CreateHPath(side))
            using (Brush restingBrush = new SolidBrush(restingColor))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = side <= 16 ? SmoothingMode.None : SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                if (!recording)
                {
                    graphics.FillPath(restingBrush, hPath);
                    KnockOutCutAndFilm(graphics, side);

                    return bitmap;
                }

                float scale = side / 64.0f;
                float top = 6.0f * scale;
                float bottom = 58.0f * scale;
                float fullHeight = bottom - top;
                using (Brush trackBrush = new SolidBrush(Color.FromArgb(trackAlpha, restingColor)))
                {
                    graphics.FillPath(trackBrush, hPath);
                }

                GraphicsState clippedState = graphics.Save();
                graphics.SetClip(hPath);
                DrawMeterFill(
                    graphics,
                    new RectangleF(6.0f * scale, top, 29.0f * scale, fullHeight),
                    inputLevel,
                    InputColor,
                    bottom);

                // The left post caps only the part above the threshold: loudness is a gradient and
                // how far into the loud band matters. The right post below flips entirely instead,
                // because overload is a yes or no and a one-pixel cap is invisible in a 16 px tray.
                if (inputLevel >= MeterWarningThreshold)
                {
                    float fillTop = bottom - fullHeight * inputLevel;
                    float warningBottom = bottom - fullHeight * MeterWarningThreshold;
                    using (Brush warningBrush = new SolidBrush(InputWarningColor))
                    {
                        graphics.FillRectangle(
                            warningBrush,
                            6.0f * scale,
                            fillTop,
                            29.0f * scale,
                            Math.Max(0.0f, warningBottom - fillTop));
                    }
                }

                RectangleF rightPost = new RectangleF(
                    35.0f * scale,
                    top,
                    23.0f * scale,
                    fullHeight);
                DrawMeterFill(
                    graphics,
                    rightPost,
                    strainLevel,
                    strainLevel >= MeterWarningThreshold ? StrainWarningColor : StrainColor,
                    bottom);

                graphics.Restore(clippedState);
            }

            return bitmap;
        }

        private static void DrawMeterFill(
            Graphics graphics,
            RectangleF bounds,
            float level,
            Color color,
            float bottom)
        {
            float height = bounds.Height * level;
            using (Brush brush = new SolidBrush(color))
            {
                graphics.FillRectangle(brush, bounds.X, bottom - height, bounds.Width, height);
            }
        }

        private static GraphicsPath CreateHPath(int side)
        {
            GraphicsPath path = new GraphicsPath();
            if (side <= 16)
            {
                path.AddRectangle(new RectangleF(1, 1, 6, 14));
                path.AddRectangle(new RectangleF(9, 1, 6, 14));
                path.AddRectangle(new RectangleF(7, 6, 2, 4));
                return path;
            }

            float scale = side / 64.0f;
            PointF[] points = new[]
            {
                Scale(6, 6, scale), Scale(29, 6, scale), Scale(29, 27, scale),
                Scale(35, 27, scale), Scale(35, 6, scale), Scale(58, 6, scale),
                Scale(58, 58, scale), Scale(35, 58, scale), Scale(35, 37, scale),
                Scale(29, 37, scale), Scale(29, 58, scale), Scale(6, 58, scale)
            };
            path.AddPolygon(points);
            return path;
        }

        private static void KnockOutCutAndFilm(Graphics graphics, int side)
        {
            float scale = side / 64.0f;
            GraphicsState state = graphics.Save();
            graphics.CompositingMode = CompositingMode.SourceCopy;
            using (Brush transparentBrush = new SolidBrush(Color.Transparent))
            {
                if (side == 16)
                {
                    graphics.PixelOffsetMode = PixelOffsetMode.None;
                    for (int step = 0; step < 5; step++)
                    {
                        graphics.FillRectangle(transparentBrush, 2 + step, 3 + step, 1, 1);
                        graphics.FillRectangle(transparentBrush, 2 + step, 12 - step, 1, 1);
                    }
                    foreach (int x in new[] { 10, 13 })
                        foreach (int y in new[] { 3, 7, 11 })
                            graphics.FillRectangle(transparentBrush, x, y, 1, 2);
                }
                else
                {
                    // Exact negative-space polygons from docs/icon/h-cut-film.svg.
                    graphics.FillPolygon(transparentBrush, new[] {
                        Scale(6.19f,15.28f,scale), Scale(8.73f,12.18f,scale),
                        Scale(28.81f,28.72f,scale), Scale(26.27f,31.82f,scale) });
                    graphics.FillPolygon(transparentBrush, new[] {
                        Scale(8.73f,51.82f,scale), Scale(6.19f,48.72f,scale),
                        Scale(26.27f,32.18f,scale), Scale(28.81f,35.28f,scale) });
                float[] xs = { 38.0f, 50.0f };
                float[] ys = { 13.0f, 28.5f, 44.0f };
                foreach (float x in xs)
                {
                    foreach (float y in ys)
                    {
                        graphics.FillRectangle(transparentBrush, x * scale, y * scale, 5 * scale, 7 * scale);
                    }
                }
                }
            }

            graphics.Restore(state);
        }

        private static PointF Scale(float x, float y, float scale)
        {
            return new PointF(x * scale, y * scale);
        }

        private static float Clamp(float value)
        {
            return Math.Max(0.0f, Math.Min(1.0f, value));
        }

        private static Icon CreateIcon(bool recording, float inputLevel, float strainLevel)
        {
            using (MemoryStream data = new MemoryStream(CreateIconData(recording, inputLevel, strainLevel)))
            using (Icon icon = new Icon(data, 32, 32))
            {
                return (Icon)icon.Clone();
            }
        }

        internal static byte[] CreateIconData(bool recording, float inputLevel, float cpuLevel)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48 };
            byte[][] images = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (Bitmap bitmap = CreateBitmap(recording, inputLevel, cpuLevel, sizes[i]))
                using (MemoryStream dib = new MemoryStream())
                using (BinaryWriter pixels = new BinaryWriter(dib))
                {
                    int side = sizes[i];
                    int maskStride = ((side + 31) / 32) * 4;
                    pixels.Write(40); pixels.Write(side); pixels.Write(side * 2);
                    pixels.Write((ushort)1); pixels.Write((ushort)32);
                    pixels.Write(0); pixels.Write(side * side * 4 + maskStride * side);
                    pixels.Write(0); pixels.Write(0); pixels.Write(0); pixels.Write(0);
                    for (int y = side - 1; y >= 0; y--)
                        for (int x = 0; x < side; x++)
                            pixels.Write(bitmap.GetPixel(x, y).ToArgb());
                    for (int y = side - 1; y >= 0; y--)
                    {
                        byte[] mask = new byte[maskStride];
                        for (int x = 0; x < side; x++)
                            if (bitmap.GetPixel(x, y).A == 0) mask[x / 8] |= (byte)(128 >> (x % 8));
                        pixels.Write(mask);
                    }
                    images[i] = dib.ToArray();
                }
            using (MemoryStream output = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(output))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)sizes[i]); writer.Write((byte)sizes[i]);
                    writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write(images[i].Length); writer.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] png in images) writer.Write(png);
                return output.ToArray();
            }
        }
    }

    internal sealed class MeterSmoother
    {
        private const float AttackMilliseconds = 150.0f;
        private const float ReleaseMilliseconds = 400.0f;
        private const int PeakHoldMilliseconds = 800;
        private int holdRemaining;

        public float Level { get; private set; }

        public float Update(float target, int elapsedMilliseconds)
        {
            target = Math.Max(0.0f, Math.Min(1.0f, target));
            elapsedMilliseconds = Math.Max(0, elapsedMilliseconds);
            if (target >= this.Level)
            {
                float amount = Math.Min(1.0f, elapsedMilliseconds / AttackMilliseconds);
                this.Level += (target - this.Level) * amount;
                this.holdRemaining = PeakHoldMilliseconds;
            }
            else if (this.holdRemaining > 0)
            {
                this.holdRemaining = Math.Max(0, this.holdRemaining - elapsedMilliseconds);
            }
            else
            {
                float amount = Math.Min(1.0f, elapsedMilliseconds / ReleaseMilliseconds);
                this.Level += (target - this.Level) * amount;
            }

            return this.Level;
        }

        public void Reset()
        {
            this.Level = 0.0f;
            this.holdRemaining = 0;
        }
    }

    internal sealed class SystemLoadSampler
    {
        private ulong previousIdle;
        private ulong previousKernel;
        private ulong previousUser;
        private bool initialized;

        public float Sample()
        {
            NativeFileTime idle;
            NativeFileTime kernel;
            NativeFileTime user;
            if (!GetSystemTimes(out idle, out kernel, out user))
            {
                return 0.0f;
            }

            ulong idleValue = idle.ToUInt64();
            ulong kernelValue = kernel.ToUInt64();
            ulong userValue = user.ToUInt64();
            if (!this.initialized)
            {
                this.previousIdle = idleValue;
                this.previousKernel = kernelValue;
                this.previousUser = userValue;
                this.initialized = true;
                return 0.0f;
            }

            ulong idleDelta = idleValue - this.previousIdle;
            ulong kernelDelta = kernelValue - this.previousKernel;
            ulong userDelta = userValue - this.previousUser;
            this.previousIdle = idleValue;
            this.previousKernel = kernelValue;
            this.previousUser = userValue;

            ulong total = kernelDelta + userDelta;
            if (total == 0 || idleDelta >= total)
            {
                return 0.0f;
            }

            return (float)(total - idleDelta) / total;
        }

        public void Reset()
        {
            this.initialized = false;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(
            out NativeFileTime idleTime,
            out NativeFileTime kernelTime,
            out NativeFileTime userTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            public uint Low;
            public uint High;

            public ulong ToUInt64()
            {
                return ((ulong)this.High << 32) | this.Low;
            }
        }
    }
}
