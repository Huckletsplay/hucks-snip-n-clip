using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace QSnipAndClip
{
    internal sealed class CaptureSaveResult
    {
        public bool Succeeded { get; set; }
        public bool UsedRecovery { get; set; }
        public string SavedPath { get; set; }
        public Exception Error { get; set; }
    }

    internal sealed class CaptureSaveService
    {
        private readonly string recoveryDirectory;

        private static string PublishWithoutOverwrite(string temporary, string directory, string stem, string extension)
        {
            for (int suffix = 0; ; suffix = checked(suffix + 1))
            {
                string candidate = Path.Combine(directory, stem + (suffix == 0 ? "" : "_" + suffix.ToString("00")) + extension);
                try { File.Move(temporary, candidate); return candidate; }
                catch (IOException)
                {
                    if (!File.Exists(candidate) && !Directory.Exists(candidate)) throw;
                }
            }
        }

        public CaptureSaveService(string recoveryDirectory)
        {
            if (String.IsNullOrWhiteSpace(recoveryDirectory))
            {
                throw new ArgumentException("A recovery directory is required.", "recoveryDirectory");
            }

            this.recoveryDirectory = recoveryDirectory;
        }

        public static CaptureSaveService CreateDefault()
        {
            string recovery = AppPaths.SubFolder("Recovery");

            return new CaptureSaveService(recovery);
        }

        public CaptureSaveResult SavePng(Bitmap image, string destinationDirectory) { return SavePng(image, destinationDirectory, CaptureFilename.Render("", "", "Snip", DateTime.Now, 1)); }
        public CaptureSaveResult SavePng(Bitmap image, string destinationDirectory, string stem)
        {
            if (image == null)
            {
                throw new ArgumentNullException("image");
            }

            Exception destinationError;
            string destinationPath;
            if (TrySave(image, destinationDirectory, stem, out destinationPath, out destinationError))
            {
                return new CaptureSaveResult
                {
                    Succeeded = true,
                    UsedRecovery = false,
                    SavedPath = destinationPath
                };
            }

            Exception recoveryError;
            string recoveryPath;
            if (TrySave(image, this.recoveryDirectory, stem, out recoveryPath, out recoveryError))
            {
                return new CaptureSaveResult
                {
                    Succeeded = true,
                    UsedRecovery = true,
                    SavedPath = recoveryPath,
                    Error = destinationError
                };
            }

            return new CaptureSaveResult
            {
                Succeeded = false,
                UsedRecovery = true,
                Error = new AggregateException(
                    "The capture could not be saved to its destination or recovery folder.",
                    destinationError,
                    recoveryError)
            };
        }

        public CaptureSaveResult RouteCompletedClip(string workingPath, string destinationDirectory) { return RouteCompletedClip(workingPath, destinationDirectory, CaptureFilename.Render("", "", "Clip", DateTime.Now, 1)); }
        public CaptureSaveResult RouteCompletedClip(string workingPath, string destinationDirectory, string stem)
        {
            if (String.IsNullOrWhiteSpace(workingPath) || !File.Exists(workingPath))
            {
                throw new FileNotFoundException("The completed working clip was not found.", workingPath);
            }

            Exception destinationError;
            string destinationPath;
            if (TryRouteClip(workingPath, destinationDirectory, stem, out destinationPath, out destinationError))
            {
                return new CaptureSaveResult
                {
                    Succeeded = true,
                    UsedRecovery = false,
                    SavedPath = destinationPath
                };
            }

            Exception recoveryError;
            string recoveryPath;
            if (TryRouteClip(workingPath, this.recoveryDirectory, stem, out recoveryPath, out recoveryError))
            {
                return new CaptureSaveResult
                {
                    Succeeded = true,
                    UsedRecovery = true,
                    SavedPath = recoveryPath,
                    Error = destinationError
                };
            }

            return new CaptureSaveResult
            {
                Succeeded = false,
                UsedRecovery = true,
                SavedPath = workingPath,
                Error = new AggregateException(
                    "The clip could not be routed to its destination or recovery folder. The working file was preserved.",
                    destinationError,
                    recoveryError)
            };
        }

        private static bool TrySave(
            Bitmap image,
            string directory,
            string stem,
            out string savedPath,
            out Exception error)
        {
            savedPath = null;
            error = null;
            string temporaryPath = null;

            try
            {
                if (String.IsNullOrWhiteSpace(directory))
                {
                    throw new InvalidOperationException("The destination folder is empty.");
                }

                Directory.CreateDirectory(directory);
                CaptureFilename.ValidateStem(stem);
                temporaryPath = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");

                using (FileStream output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write)) image.Save(output, ImageFormat.Png);
                savedPath = PublishWithoutOverwrite(temporaryPath, directory, stem, ".png");
                temporaryPath = null;
                return true;
            }
            catch (Exception exception)
            {
                error = exception;
                savedPath = null;
                return false;
            }
            finally
            {
                if (temporaryPath != null)
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                    catch
                    {
                        // The recovery path is more important than cleaning an incomplete temp file.
                    }
                }
            }
        }


        private static bool TryRouteClip(
            string workingPath,
            string directory,
            string stem,
            out string savedPath,
            out Exception error)
        {
            savedPath = null;
            error = null;
            string temporaryPath = null;

            try
            {
                if (String.IsNullOrWhiteSpace(directory))
                {
                    throw new InvalidOperationException("The destination folder is empty.");
                }

                Directory.CreateDirectory(directory);
                CaptureFilename.ValidateStem(stem);
                temporaryPath = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".routing");
                File.Copy(workingPath, temporaryPath, false);
                savedPath = PublishWithoutOverwrite(temporaryPath, directory, stem, Path.GetExtension(workingPath));
                temporaryPath = null;
                try
                {
                    File.Delete(workingPath);
                }
                catch
                {
                    // The routed copy is complete; a leftover working copy can be cleaned later.
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception;
                savedPath = null;
                return false;
            }
            finally
            {
                if (temporaryPath != null)
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                    catch
                    {
                        // Preserve the original file; an orphaned routing copy can be cleaned later.
                    }
                }
            }
        }

    }
}
