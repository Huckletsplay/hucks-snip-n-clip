using System;
using System.Collections.Specialized;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal static class CaptureClipboard
    {
        internal static bool TryCopyImage(Bitmap image, out Exception error)
        {
            if (image == null) throw new ArgumentNullException("image");
            return TrySet(delegate { Clipboard.SetImage(image); }, out error);
        }

        internal static bool TryCopyFile(string path, out Exception error)
        {
            StringCollection files = CreateFileDropList(path);
            return TrySet(delegate { Clipboard.SetFileDropList(files); }, out error);
        }

        internal static StringCollection CreateFileDropList(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A capture path is required.", "path");
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("The finished capture was not found.", fullPath);
            StringCollection files = new StringCollection();
            files.Add(fullPath);
            return files;
        }

        private static bool TrySet(Action setClipboard, out Exception error)
        {
            error = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    setClipboard();
                    return true;
                }
                catch (Exception exception)
                {
                    error = exception;
                    if (attempt < 4) Thread.Sleep(40 * (attempt + 1));
                }
            }

            return false;
        }
    }
}
