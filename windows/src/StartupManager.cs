using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QSnipAndClip
{
    internal enum StartupState
    {
        Disabled = 0,
        Enabled,
        EnabledForAnotherPath
    }

    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string ValueName = "HucksSnipNClip";
        internal const string LegacyValueName = "QSnipAndClip";

        public static bool IsEnabled()
        {
            return GetState() != StartupState.Disabled;
        }

        public static StartupState GetState()
        {
            string configured;
            StartupState current = GetState(ValueName, Application.ExecutablePath, out configured);
            if (current != StartupState.Disabled)
            {
                return current;
            }

            return GetState(LegacyValueName, Application.ExecutablePath, out configured);
        }

        public static void SetEnabled(bool enabled)
        {
            SetEnabled(enabled, ValueName, Application.ExecutablePath);
            SetEnabled(false, LegacyValueName, Application.ExecutablePath);
        }

        /// <summary>
        /// Points an existing startup entry at the executable that is running now. Moving the
        /// build to another drive otherwise leaves Windows launching the old copy while the tray
        /// reports Start with Windows as off, because the recorded command no longer matches.
        /// </summary>
        public static bool RepairStaleEntry()
        {
            if (RepairStaleEntry(ValueName, Application.ExecutablePath))
            {
                return true;
            }

            string legacyCommand;
            if (GetState(LegacyValueName, Application.ExecutablePath, out legacyCommand) == StartupState.Disabled)
            {
                return false;
            }

            try
            {
                SetEnabled(true, ValueName, Application.ExecutablePath);
                SetEnabled(false, LegacyValueName, Application.ExecutablePath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static StartupState GetState(
            string valueName,
            string executablePath,
            out string configuredCommand)
        {
            configuredCommand = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    configuredCommand = key == null ? null : key.GetValue(valueName) as string;
                }
            }
            catch
            {
                return StartupState.Disabled;
            }

            return ClassifyCommand(configuredCommand, executablePath);
        }

        internal static StartupState ClassifyCommand(string configuredCommand, string executablePath)
        {
            if (String.IsNullOrWhiteSpace(configuredCommand))
            {
                return StartupState.Disabled;
            }

            return String.Equals(
                configuredCommand.Trim(),
                BuildLaunchCommand(executablePath),
                StringComparison.OrdinalIgnoreCase)
                ? StartupState.Enabled
                : StartupState.EnabledForAnotherPath;
        }

        internal static void SetEnabled(bool enabled, string valueName, string executablePath)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Windows would not open the current-user startup settings.");
                }

                if (enabled)
                {
                    key.SetValue(
                        valueName,
                        BuildLaunchCommand(executablePath),
                        RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(valueName, false);
                }
            }
        }

        internal static bool RepairStaleEntry(string valueName, string executablePath)
        {
            string configured;
            if (GetState(valueName, executablePath, out configured) != StartupState.EnabledForAnotherPath)
            {
                return false;
            }

            try
            {
                SetEnabled(true, valueName, executablePath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static string BuildLaunchCommand(string executablePath)
        {
            if (String.IsNullOrWhiteSpace(executablePath))
            {
                throw new ArgumentException("An executable path is required.", "executablePath");
            }

            return "\"" + executablePath + "\"";
        }
    }
}
