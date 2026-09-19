using System.IO;
using System.Drawing;
using System.Drawing.Imaging;

namespace QSnipAndClip
{
    internal static class BuildIcon
    {
        private static void Main(string[] args)
        {
            File.WriteAllBytes(args[0], TrayIconFactory.CreateIconData(false, 0, 0));
            using (Bitmap sheet = new Bitmap(600, 120))
            using (Graphics graphics = Graphics.FromImage(sheet))
            {
                graphics.Clear(Color.FromArgb(30, 30, 30));
                int x = 12;
                foreach (int size in new[] { 16, 20, 24, 32, 40, 48 })
                    using (Bitmap mark = TrayIconFactory.CreateBitmap(false, 0, 0, size))
                    {
                        graphics.DrawImageUnscaled(mark, x, 12);
                        graphics.DrawString(size.ToString(), SystemFonts.DefaultFont, Brushes.White, x, 72);
                        x += 96;
                    }
                sheet.Save(Path.ChangeExtension(args[0], ".png"), ImageFormat.Png);
            }
        }
    }
}
