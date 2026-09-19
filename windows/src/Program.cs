using System;
using System.Threading;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;

            using (Mutex mutex = new Mutex(true, @"Local\QSnipAndClip.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApplicationContext());
            }
        }
    }
}

