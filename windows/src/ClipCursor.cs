using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace QSnipAndClip
{
    internal static class ClipCursor
    {
        internal static void Draw(Graphics graphics, Point captureOrigin)
        {
            CursorInfo cursor = new CursorInfo { Size = Marshal.SizeOf(typeof(CursorInfo)) };
            if (!GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0) return;
            IntPtr copy = CopyIcon(cursor.Cursor);
            if (copy == IntPtr.Zero) return;
            IconInfo icon;
            try
            {
                if (!GetIconInfo(copy, out icon)) return;
                try
                {
                    IntPtr dc = graphics.GetHdc();
                    try { DrawIconEx(dc, cursor.Position.X - captureOrigin.X - icon.HotspotX,
                        cursor.Position.Y - captureOrigin.Y - icon.HotspotY, copy, 0, 0, 0, IntPtr.Zero, 3); }
                    finally { graphics.ReleaseHdc(dc); }
                }
                finally
                {
                    if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
                    if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
                }
            }
            finally { NativeMethods.DestroyIcon(copy); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public IntPtr Cursor; public Point Position; }
        [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public int IsIcon, HotspotX, HotspotY; public IntPtr Mask, Color; }
        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
        [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
        [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    }
}
