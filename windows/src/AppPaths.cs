using System;
using System.IO;

namespace QSnipAndClip
{
    /// <summary>
    /// Where the app keeps its own data, and the one-time move off the retired Q name.
    ///
    /// `QSNC` is what this product was called before it became Huck's Snip 'n' Clip. The macOS
    /// build already stores under `HucksSnipNClip`; Windows kept the old folder far too long and
    /// shipped it publicly, so a stranger installing the beta saw a retired brand in their own
    /// AppData. New data lives under `HucksSnipNClip`.
    ///
    /// An existing `QSNC` folder is **copied**, never moved: if anything about the migration goes
    /// wrong the original is still sitting there untouched, and an older build installed alongside
    /// keeps working from it.
    /// </summary>
    internal static class AppPaths
    {
        internal const string DataFolderName = "HucksSnipNClip";
        internal const string LegacyDataFolderName = "QSNC";
        internal const string FilePrefix = "HucksSnipNClip";

        private static bool migrationAttempted;

        internal static string DataRoot
        {
            get
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    DataFolderName);
                MigrateFromLegacyOnce(root);
                return root;
            }
        }

        internal static string SubFolder(string name)
        {
            return Path.Combine(DataRoot, name);
        }

        internal static string LegacyDataRoot
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    LegacyDataFolderName);
            }
        }

        private static void MigrateFromLegacyOnce(string root)
        {
            if (migrationAttempted) return;
            migrationAttempted = true;

            try
            {
                string legacy = LegacyDataRoot;
                if (!Directory.Exists(legacy)) return;

                // Only adopt the old data when there is nothing here yet. A settings file already
                // under the new name always wins.
                if (File.Exists(Path.Combine(root, "settings.ini"))) return;
                if (!File.Exists(Path.Combine(legacy, "settings.ini"))) return;

                Directory.CreateDirectory(root);
                CopyTree(legacy, root);
            }
            catch
            {
                // A failed migration must never stop the app from starting. Worst case the user
                // gets fresh defaults and their old folder is still on disk, untouched.
            }
        }

        private static void CopyTree(string source, string destination)
        {
            // Working files are scratch from an interrupted recording and are not worth carrying.
            foreach (string directory in Directory.GetDirectories(source))
            {
                string name = Path.GetFileName(directory);
                if (String.Equals(name, "Working", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(destination, name);
                Directory.CreateDirectory(target);
                CopyTree(directory, target);
            }

            foreach (string file in Directory.GetFiles(source))
            {
                string target = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(target)) File.Copy(file, target);
            }
        }
    }
}
