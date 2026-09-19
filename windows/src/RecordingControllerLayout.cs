using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace QSnipAndClip
{
    /// <summary>
    /// One display as it contributes to a layout identity: which physical monitor it is, where it
    /// sits relative to the others, how big it is, how it is scaled, and which way up it is.
    /// </summary>
    internal sealed class DisplayLayoutEntry
    {
        public string MonitorId = "";
        public Rectangle Bounds;
        public int Dpi = 96;
        public int Orientation;
        public bool Primary;
    }

    /// <summary>
    /// Turns the connected displays into a stable key, so the floating controller can remember a
    /// separate position for each arrangement the user actually works in.
    ///
    /// There is no single universal position worth keeping: a point that sits comfortably beside a
    /// laptop's own screen is off the edge of a three-monitor desk, and a point that is perfect on
    /// the right-hand monitor lands in the middle of the recording once that monitor moves to the
    /// left. The identity therefore includes the monitors' own hardware identities, their
    /// arrangement relative to each other, their resolution, their DPI scaling and their
    /// orientation. Rearranging the same monitors is a different layout on purpose.
    ///
    /// This is backend behavior. The user never sees, names or manages a profile.
    /// </summary>
    internal static class DisplayLayoutIdentity
    {
        private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;
        private const int ENUM_CURRENT_SETTINGS = -1;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const int MDT_EFFECTIVE_DPI = 0;

        /// <summary>
        /// Builds the key from an already-gathered description. Kept separate from
        /// <see cref="Snapshot" /> so the rules can be exercised without real hardware.
        /// </summary>
        internal static string Compose(IList<DisplayLayoutEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException("entries");
            if (entries.Count == 0) return "none";

            // Windows enumerates displays in an order that can change on its own, so sort into a
            // canonical order first. Position still matters: it is carried by each entry's bounds,
            // which Windows expresses relative to the primary display's origin.
            List<string> parts = new List<string>();
            foreach (DisplayLayoutEntry entry in entries)
            {
                parts.Add(String.Format(
                    CultureInfo.InvariantCulture,
                    "{0}@{1},{2},{3},{4}|dpi{5}|rot{6}|{7}",
                    (entry.MonitorId ?? "").ToUpperInvariant(),
                    entry.Bounds.X,
                    entry.Bounds.Y,
                    entry.Bounds.Width,
                    entry.Bounds.Height,
                    entry.Dpi,
                    entry.Orientation,
                    entry.Primary ? "primary" : "secondary"));
            }

            parts.Sort(StringComparer.Ordinal);
            string descriptor = entries.Count + ";" + String.Join(";", parts.ToArray());
            using (SHA256 hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(Encoding.UTF8.GetBytes(descriptor));
                StringBuilder key = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    key.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return key.ToString();
            }
        }

        internal static string Current()
        {
            try { return Compose(Snapshot()); }
            catch { return "unknown"; }
        }

        internal static IList<DisplayLayoutEntry> Snapshot()
        {
            List<DisplayLayoutEntry> entries = new List<DisplayLayoutEntry>();
            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                Screen screen = screens[i];
                entries.Add(new DisplayLayoutEntry
                {
                    // The adapter name is positional and renames itself when monitors are
                    // replugged, so prefer the monitor's own interface path.
                    MonitorId = GetStableMonitorId(screen, i),
                    Bounds = screen.Bounds,
                    Dpi = GetDpi(screen),
                    Orientation = GetOrientation(screen),
                    Primary = screen.Primary
                });
            }

            return entries;
        }

        private static string GetStableMonitorId(Screen screen, int index)
        {
            try
            {
                DisplayDevice monitor = new DisplayDevice();
                monitor.Size = Marshal.SizeOf(typeof(DisplayDevice));
                if (EnumDisplayDevices(screen.DeviceName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME)
                    && !String.IsNullOrWhiteSpace(monitor.DeviceId))
                {
                    return monitor.DeviceId;
                }
            }
            catch { /* Fall through to a deterministic substitute. */ }

            // A monitor that will not identify itself still has to produce the same key every time
            // the same desk is used, so fall back on something deterministic rather than random.
            return (screen.DeviceName ?? "display") + "#" + index.ToString(CultureInfo.InvariantCulture);
        }

        private static int GetDpi(Screen screen)
        {
            try
            {
                NativePoint center;
                center.X = screen.Bounds.X + screen.Bounds.Width / 2;
                center.Y = screen.Bounds.Y + screen.Bounds.Height / 2;
                IntPtr monitor = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
                uint dpiX, dpiY;
                if (monitor != IntPtr.Zero
                    && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) == 0
                    && dpiX > 0)
                {
                    return (int)dpiX;
                }
            }
            catch { /* Windows 8.0 and earlier have no per-monitor DPI to report. */ }

            try
            {
                using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
                {
                    return (int)Math.Round(graphics.DpiX);
                }
            }
            catch { return 96; }
        }

        private static int GetOrientation(Screen screen)
        {
            try
            {
                DeviceMode mode = new DeviceMode();
                mode.Size = (short)Marshal.SizeOf(typeof(DeviceMode));
                if (EnumDisplaySettings(screen.DeviceName, ENUM_CURRENT_SETTINGS, ref mode))
                {
                    return mode.DisplayOrientation;
                }
            }
            catch { /* Fall through. */ }

            // Without a reported orientation, shape still separates portrait from landscape.
            return screen.Bounds.Height > screen.Bounds.Width ? 1 : 0;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(
            string device, uint deviceIndex, ref DisplayDevice target, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int mode, ref DeviceMode target);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DeviceMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public short SpecVersion;
            public short DriverVersion;
            public short Size;
            public short DriverExtra;
            public int Fields;
            public int PositionX;
            public int PositionY;
            public int DisplayOrientation;
            public int DisplayFixedOutput;
            public short Color;
            public short Duplex;
            public short YResolution;
            public short TrueTypeOption;
            public short Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public short LogPixels;
            public int BitsPerPel;
            public int PelsWidth;
            public int PelsHeight;
            public int DisplayFlags;
            public int DisplayFrequency;
            public int ICMMethod;
            public int ICMIntent;
            public int MediaType;
            public int DitherType;
            public int Reserved1;
            public int Reserved2;
            public int PanningWidth;
            public int PanningHeight;
        }
    }

    /// <summary>
    /// A small, bounded, most-recently-used set of controller positions, one per display layout.
    ///
    /// Bounded on purpose: a settings file that grows a line for every arrangement a laptop has
    /// ever been plugged into is a leak, not a memory. Twelve covers a desk, a dock, a meeting
    /// room and a sofa several times over, and the oldest layout is the one nobody misses.
    /// </summary>
    internal sealed class RecordingControllerLayouts
    {
        internal const int MaxProfiles = 12;

        private readonly List<string> order = new List<string>();
        private readonly Dictionary<string, Point> positions =
            new Dictionary<string, Point>(StringComparer.Ordinal);

        public int Count
        {
            get { return this.order.Count; }
        }

        /// <summary>Layout keys, most recently used first.</summary>
        public IList<string> Keys
        {
            get { return this.order.ToArray(); }
        }

        public bool TryGetPosition(string layoutKey, out Point position)
        {
            position = Point.Empty;
            return !String.IsNullOrEmpty(layoutKey) && this.positions.TryGetValue(layoutKey, out position);
        }

        /// <summary>
        /// Teaches one layout its position. Only that layout changes: a point corrected because it
        /// had drifted off a small screen must never move the controller on the big desk.
        /// </summary>
        public void SetPosition(string layoutKey, Point position)
        {
            if (String.IsNullOrEmpty(layoutKey)) return;
            this.positions[layoutKey] = position;
            Promote(layoutKey);
        }

        /// <summary>Marks a layout as current without changing where its controller sits.</summary>
        public void Touch(string layoutKey)
        {
            if (String.IsNullOrEmpty(layoutKey) || !this.positions.ContainsKey(layoutKey)) return;
            Promote(layoutKey);
        }

        public void Clear()
        {
            this.order.Clear();
            this.positions.Clear();
        }

        /// <summary>Restores stored layouts in the recency order they were written.</summary>
        public void Append(string layoutKey, Point position)
        {
            if (String.IsNullOrEmpty(layoutKey) || this.positions.ContainsKey(layoutKey)) return;
            if (this.order.Count >= MaxProfiles) return;
            this.positions[layoutKey] = position;
            this.order.Add(layoutKey);
        }

        private void Promote(string layoutKey)
        {
            this.order.Remove(layoutKey);
            this.order.Insert(0, layoutKey);
            while (this.order.Count > MaxProfiles)
            {
                string dropped = this.order[this.order.Count - 1];
                this.order.RemoveAt(this.order.Count - 1);
                this.positions.Remove(dropped);
            }
        }
    }

    /// <summary>
    /// Where the controller goes when a layout has never been seen, and how a remembered point is
    /// rescued when the display it was learned on has shrunk or gone away.
    /// </summary>
    internal static class RecordingControllerPlacement
    {
        internal const int EdgeMargin = 24;

        /// <summary>
        /// Keeps the whole controller inside one display's working area. A point that cannot be
        /// honored is pulled to the nearest usable place rather than discarded, so the layout keeps
        /// its learned corner instead of jumping back to a default.
        /// </summary>
        internal static Point Clamp(Point desired, Size controller, IList<Rectangle> workingAreas)
        {
            if (workingAreas == null || workingAreas.Count == 0) return desired;

            Rectangle wanted = new Rectangle(desired, controller);
            Rectangle best = workingAreas[0];
            long bestOverlap = -1;
            for (int i = 0; i < workingAreas.Count; i++)
            {
                Rectangle intersection = Rectangle.Intersect(wanted, workingAreas[i]);
                long overlap = (long)intersection.Width * intersection.Height;
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    best = workingAreas[i];
                }
            }

            if (bestOverlap <= 0)
            {
                // Completely off every display - the layout changed underneath a saved point.
                best = NearestArea(desired, workingAreas);
            }

            int x = Math.Min(Math.Max(desired.X, best.Left), Math.Max(best.Left, best.Right - controller.Width));
            int y = Math.Min(Math.Max(desired.Y, best.Top), Math.Max(best.Top, best.Bottom - controller.Height));
            return new Point(x, y);
        }

        /// <summary>
        /// A layout nobody has taught yet starts on the display being recorded, low and to the
        /// right, where a controller is least likely to sit on top of the subject.
        /// </summary>
        internal static Point Default(Rectangle workingArea, Size controller)
        {
            int x = workingArea.Right - controller.Width - EdgeMargin;
            int y = workingArea.Bottom - controller.Height - EdgeMargin;
            return new Point(
                Math.Max(workingArea.Left, x),
                Math.Max(workingArea.Top, y));
        }

        private static Rectangle NearestArea(Point desired, IList<Rectangle> workingAreas)
        {
            Rectangle nearest = workingAreas[0];
            long best = Int64.MaxValue;
            foreach (Rectangle area in workingAreas)
            {
                long dx = desired.X < area.Left ? area.Left - desired.X
                    : desired.X > area.Right ? desired.X - area.Right : 0;
                long dy = desired.Y < area.Top ? area.Top - desired.Y
                    : desired.Y > area.Bottom ? desired.Y - area.Bottom : 0;
                long distance = dx * dx + dy * dy;
                if (distance < best) { best = distance; nearest = area; }
            }

            return nearest;
        }
    }
}
