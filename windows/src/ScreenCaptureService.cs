using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal static class ScreenCaptureService
    {
        private const int VREFRESH = 116;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public static Bitmap CaptureVirtualDesktop()
        {
            Rectangle bounds = SystemInformation.VirtualScreen;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                throw new InvalidOperationException("Windows did not report a usable desktop area.");
            }

            Bitmap image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(image))
                {
                    graphics.CopyFromScreen(
                        bounds.Left,
                        bounds.Top,
                        0,
                        0,
                        bounds.Size,
                        CopyPixelOperation.SourceCopy);
                }

                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        public static Bitmap CaptureDisplayAtCursor()
        {
            Screen display = Screen.FromPoint(Cursor.Position);
            Rectangle bounds = display.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                throw new InvalidOperationException("Windows did not report a usable display area.");
            }

            Bitmap image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(image))
                {
                    graphics.CopyFromScreen(
                        bounds.Left,
                        bounds.Top,
                        0,
                        0,
                        bounds.Size,
                        CopyPixelOperation.SourceCopy);
                }

                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        public static Bitmap CaptureBounds(Rectangle bounds)
        {
            Rectangle safeBounds = Rectangle.Intersect(SystemInformation.VirtualScreen, bounds);
            if (safeBounds.Width <= 0 || safeBounds.Height <= 0)
            {
                throw new ArgumentOutOfRangeException("bounds", "The capture area is outside the virtual desktop.");
            }

            Bitmap image = new Bitmap(safeBounds.Width, safeBounds.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(image))
                {
                    graphics.CopyFromScreen(
                        safeBounds.Left,
                        safeBounds.Top,
                        0,
                        0,
                        safeBounds.Size,
                        CopyPixelOperation.SourceCopy);
                }

                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        public static IntPtr GetForegroundCapturableWindow()
        {
            IntPtr windowHandle = NativeMethods.GetForegroundWindow();
            Rectangle bounds;
            if (!TryGetWindowBounds(windowHandle, out bounds))
            {
                throw new InvalidOperationException("The active window cannot be captured. Restore it and try again.");
            }

            return windowHandle;
        }

        public static bool TryGetWindowBounds(IntPtr windowHandle, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (windowHandle == IntPtr.Zero
                || !NativeMethods.IsWindow(windowHandle)
                || !NativeMethods.IsWindowVisible(windowHandle)
                || NativeMethods.IsIconic(windowHandle))
            {
                return false;
            }

            NativeRect nativeBounds;
            int result = NativeMethods.DwmGetWindowAttribute(
                windowHandle,
                DWMWA_EXTENDED_FRAME_BOUNDS,
                out nativeBounds,
                System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeRect)));
            if (result < 0 && !NativeMethods.GetWindowRect(windowHandle, out nativeBounds))
            {
                return false;
            }

            Rectangle candidate = Rectangle.FromLTRB(
                nativeBounds.Left,
                nativeBounds.Top,
                nativeBounds.Right,
                nativeBounds.Bottom);
            bounds = Rectangle.Intersect(SystemInformation.VirtualScreen, candidate);
            return bounds.Width >= 16 && bounds.Height >= 16;
        }

        public static Screen GetDisplayAtCursor()
        {
            return Screen.FromPoint(Cursor.Position);
        }

        public static int GetDisplayRefreshRate(Screen display)
        {
            if (display == null)
            {
                throw new ArgumentNullException("display");
            }

            IntPtr deviceContext = NativeMethods.CreateDC(
                "DISPLAY",
                display.DeviceName,
                null,
                IntPtr.Zero);
            if (deviceContext == IntPtr.Zero)
            {
                return 60;
            }

            try
            {
                int refreshRate = NativeMethods.GetDeviceCaps(deviceContext, VREFRESH);
                return refreshRate >= 15 && refreshRate <= 240 ? refreshRate : 60;
            }
            finally
            {
                NativeMethods.DeleteDC(deviceContext);
            }
        }

        public static Bitmap Crop(Bitmap source, Rectangle region)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            Rectangle sourceBounds = new Rectangle(0, 0, source.Width, source.Height);
            Rectangle safeRegion = Rectangle.Intersect(sourceBounds, region);
            if (safeRegion.Width <= 0 || safeRegion.Height <= 0)
            {
                throw new ArgumentOutOfRangeException("region", "The selected region is empty.");
            }

            Bitmap cropped = new Bitmap(safeRegion.Width, safeRegion.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(cropped))
            {
                graphics.DrawImage(
                    source,
                    new Rectangle(0, 0, cropped.Width, cropped.Height),
                    safeRegion,
                    GraphicsUnit.Pixel);
            }

            return cropped;
        }
    }
}
