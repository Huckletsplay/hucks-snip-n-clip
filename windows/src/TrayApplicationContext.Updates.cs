using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed partial class TrayApplicationContext
    {
        private bool updateCheckRunning;

        private void CheckForUpdates()
        {
            if (this.updateCheckRunning) return;
            this.updateCheckRunning = true;
            RebuildContextMenu();

            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs args)
            {
                Version current = Assembly.GetExecutingAssembly().GetName().Version;
                args.Result = UpdateChecker.Check(current);
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs args)
            {
                this.updateCheckRunning = false;
                RebuildContextMenu();
                if (args.Error != null)
                {
                    CopyableDialog.Show(args.Error.Message, "Update Check Failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                UpdateCheckResult update = (UpdateCheckResult)args.Result;
                if (!update.UpdateAvailable)
                {
                    CopyableDialog.Show("You’re up to date.\n\nInstalled: " + update.CurrentVersion.ToString(3)
                        + "\nLatest: " + update.LatestVersion.ToString(3), "Check for Updates",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (String.IsNullOrWhiteSpace(update.InstallerUrl) || String.IsNullOrWhiteSpace(update.ChecksumUrl))
                {
                    CopyableDialog.Show("Version " + update.LatestVersion.ToString(3)
                        + " is available, but its verified Windows installer is not published yet.\n\n"
                        + update.ReleasePageUrl, "Update Available", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                DialogResult choice = CopyableDialog.Show("Version " + update.LatestVersion.ToString(3)
                    + " is available. Download, verify, and install it now?",
                    "Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (choice == DialogResult.Yes) DownloadUpdate(update);
            };
            worker.RunWorkerAsync();
        }

        private void DownloadUpdate(UpdateCheckResult update)
        {
            this.updateCheckRunning = true;
            RebuildContextMenu();
            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs args)
            {
                args.Result = UpdateChecker.DownloadAndVerify(update);
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs args)
            {
                this.updateCheckRunning = false;
                if (args.Error != null)
                {
                    RebuildContextMenu();
                    CopyableDialog.Show(args.Error.Message, "Update Download Failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                string installer = (string)args.Result;
                Process.Start(new ProcessStartInfo(installer, "/SILENT /CLOSEAPPLICATIONS")
                {
                    UseShellExecute = true
                });
                ExitThread();
            };
            worker.RunWorkerAsync();
        }
    }
}
