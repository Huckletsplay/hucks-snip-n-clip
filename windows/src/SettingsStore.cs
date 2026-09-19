using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed class SettingsStore
    {
        private const string VersionKey = "Version";
        private const string ActiveDestinationKey = "ActiveDestinationId";
        private const string DestinationKey = "Destination";
        private const string RecordingFrameRateKey = "RecordingFrameRate";
        private const string RecordingAudioModeKey = "RecordingAudioMode";
        private const string RecordingQualityKey = "RecordingQuality";
        private const string RecordingResolutionKey = "RecordingResolution";
        private const string ComputerAudioGainPercentKey = "ComputerAudioGainPercent";
        private const string MicrophoneGainPercentKey = "MicrophoneGainPercent";
        private const string RoutineNotificationsEnabledKey = "RoutineNotificationsEnabled";
        private const string ShortcutKeyPrefix = "Shortcut.";
        private const string RecordingShortcutKeyPrefix = "RecordingShortcut.";
        private const string ControllerPositionKeyPrefix = "ControllerPosition.";
        private const string ShowFloatingRecordingControlsKey = "ShowFloatingRecordingControls";
        private const string FloatingControllerOpacityKey = "FloatingControllerOpacityPercent";
        private const string FloatingControllerSizeKey = "FloatingControllerSize";
        private const string LegacyDestinationKey = "DestinationPath";
        private const string CurrentVersion = "15";
        private readonly string settingsDirectory;
        private readonly string settingsPath;
        private readonly Func<string> defaultDestinationFactory;
        internal string SettingsPath { get { return this.settingsPath; } }

        public SettingsStore(string settingsDirectory, Func<string> defaultDestinationFactory)
        {
            if (String.IsNullOrWhiteSpace(settingsDirectory))
            {
                throw new ArgumentException("A settings directory is required.", "settingsDirectory");
            }

            if (defaultDestinationFactory == null)
            {
                throw new ArgumentNullException("defaultDestinationFactory");
            }

            this.settingsDirectory = settingsDirectory;
            this.settingsPath = Path.Combine(settingsDirectory, "settings.ini");
            this.defaultDestinationFactory = defaultDestinationFactory;
        }

        public static SettingsStore CreateDefault()
        {
            string root = AppPaths.DataRoot;

            return new SettingsStore(root, FindDefaultDestination);
        }

        public AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            string legacyDestinationPath = null;
            // A missing key means two different things: a brand-new install that should get the
            // accepted modern default, or an older settings file whose behavior must be preserved.
            bool hasExistingSettings = File.Exists(this.settingsPath);
            bool needsMigration = false;
            bool frameRateSeen = false;
            bool recordingQualitySeen = false;
            bool recordingAudioModeSeen = false;
            bool computerAudioGainSeen = false;
            bool microphoneGainSeen = false;
            bool routineNotificationsSeen = false;
            bool showFloatingControlsSeen = false;
            bool controllerOpacitySeen = false;
            bool controllerSizeSeen = false;
            HashSet<CaptureAction> shortcutsSeen = new HashSet<CaptureAction>();
            HashSet<RecordingControlAction> recordingShortcutsSeen = new HashSet<RecordingControlAction>();

            if (File.Exists(this.settingsPath))
            {
                string[] lines = File.ReadAllLines(this.settingsPath, Encoding.UTF8);
                foreach (string line in lines)
                {
                    int separatorIndex = line.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, separatorIndex).Trim();
                    string value = line.Substring(separatorIndex + 1).Trim();
                    if (String.Equals(key, ActiveDestinationKey, StringComparison.OrdinalIgnoreCase))
                    {
                        settings.ActiveDestinationId = value;
                    }
                    else if (String.Equals(key, DestinationKey, StringComparison.OrdinalIgnoreCase))
                    {
                        CaptureDestination destination = DecodeDestination(value);
                        if (destination != null)
                        {
                            settings.Destinations.Add(destination);
                        }
                    }
                    else if (String.Equals(key, LegacyDestinationKey, StringComparison.OrdinalIgnoreCase))
                    {
                        legacyDestinationPath = value;
                        needsMigration = true;
                    }
                    else if (String.Equals(key, RecordingFrameRateKey, StringComparison.OrdinalIgnoreCase))
                    {
                        int parsedFrameRate;
                        if (Int32.TryParse(value, out parsedFrameRate)
                            && IsSupportedFrameRate(parsedFrameRate))
                        {
                            settings.RecordingFrameRate = parsedFrameRate;
                            frameRateSeen = true;
                        }
                    }
                    else if (String.Equals(key, RecordingQualityKey, StringComparison.OrdinalIgnoreCase))
                    {
                        RecordingQuality parsedQuality;
                        if (Enum.TryParse<RecordingQuality>(value, true, out parsedQuality)
                            && Enum.IsDefined(typeof(RecordingQuality), parsedQuality))
                        {
                            settings.RecordingQuality = parsedQuality;
                            recordingQualitySeen = true;
                        }
                    }
                    else if (String.Equals(key, RecordingResolutionKey, StringComparison.OrdinalIgnoreCase))
                    {
                        settings.RecordingResolution = RecordingResolutionSettings.Parse(value);
                    }
                    else if (String.Equals(key, RecordingAudioModeKey, StringComparison.OrdinalIgnoreCase))
                    {
                        RecordingAudioMode parsedAudioMode;
                        if (Enum.TryParse<RecordingAudioMode>(value, true, out parsedAudioMode)
                            && Enum.IsDefined(typeof(RecordingAudioMode), parsedAudioMode))
                        {
                            settings.RecordingAudioMode = parsedAudioMode;
                            recordingAudioModeSeen = true;
                        }
                    }
                    else if (String.Equals(key, ComputerAudioGainPercentKey, StringComparison.OrdinalIgnoreCase))
                    {
                        int parsedGain;
                        if (Int32.TryParse(value, out parsedGain) && IsSupportedAudioGainPercent(parsedGain))
                        {
                            settings.ComputerAudioGainPercent = parsedGain;
                            computerAudioGainSeen = true;
                        }
                    }
                    else if (String.Equals(key, MicrophoneGainPercentKey, StringComparison.OrdinalIgnoreCase))
                    {
                        int parsedGain;
                        if (Int32.TryParse(value, out parsedGain) && IsSupportedAudioGainPercent(parsedGain))
                        {
                            settings.MicrophoneGainPercent = parsedGain;
                            microphoneGainSeen = true;
                        }
                    }
                    else if (String.Equals(key, RoutineNotificationsEnabledKey, StringComparison.OrdinalIgnoreCase))
                    {
                        bool parsedEnabled;
                        if (Boolean.TryParse(value, out parsedEnabled))
                        {
                            settings.RoutineNotificationsEnabled = parsedEnabled;
                            routineNotificationsSeen = true;
                        }
                    }
                    else if (key == "ShowCursorInClips")
                    {
                        bool enabled; if (Boolean.TryParse(value, out enabled)) settings.ShowCursorInClips = enabled;
                    }
                    else if (key == "MicrophoneDeviceId")
                    {
                        try { settings.MicrophoneDeviceId = DecodeText(value); }
                        catch (FormatException) { needsMigration = true; }
                    }
                    else if (key == "FilenameLabel" || key == "FilenameTemplate")
                    {
                        try
                        {
                            if (key == "FilenameLabel") settings.FilenameLabel = DecodeText(value);
                            else settings.FilenameTemplate = DecodeText(value);
                        }
                        catch (FormatException) { needsMigration = true; }
                    }
                    else if (key == "NextFilenameCounter")
                    {
                        long counter;
                        if (Int64.TryParse(value, out counter) && counter > 0) settings.NextFilenameCounter = counter;
                    }
                    else if (key.StartsWith("OutputShortcut.", StringComparison.OrdinalIgnoreCase))
                    {
                        ShortcutBinding output;
                        if (TryDecodeShortcut(value, out output))
                            settings.OutputShortcuts[key.Substring("OutputShortcut.".Length)] = output;
                    }
                    else if (key.StartsWith(RecordingShortcutKeyPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        RecordingControlAction control;
                        ShortcutBinding controlBinding;
                        if (Enum.TryParse<RecordingControlAction>(
                                key.Substring(RecordingShortcutKeyPrefix.Length), true, out control)
                            && Enum.IsDefined(typeof(RecordingControlAction), control)
                            && TryDecodeShortcut(value, out controlBinding))
                        {
                            settings.SetRecordingShortcut(control, controlBinding);
                            recordingShortcutsSeen.Add(control);
                        }
                    }
                    else if (key.StartsWith(ControllerPositionKeyPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // Written most-recently-used first, so reading in file order preserves
                        // which layout profile is the oldest and therefore the first to be dropped.
                        Point position;
                        if (TryDecodePoint(value, out position))
                        {
                            settings.ControllerLayouts.Append(
                                key.Substring(ControllerPositionKeyPrefix.Length), position);
                        }
                    }
                    else if (String.Equals(key, ShowFloatingRecordingControlsKey, StringComparison.OrdinalIgnoreCase))
                    {
                        bool showController;
                        if (Boolean.TryParse(value, out showController))
                        {
                            settings.ShowFloatingRecordingControls = showController;
                            showFloatingControlsSeen = true;
                        }
                    }
                    else if (String.Equals(key, FloatingControllerSizeKey, StringComparison.OrdinalIgnoreCase))
                    {
                        int controllerSize;
                        if (Int32.TryParse(value, out controllerSize)
                            && controllerSize >= FloatingRecordingController.MinimumSize
                            && controllerSize <= FloatingRecordingController.MaximumSize)
                        {
                            settings.FloatingControllerSize = controllerSize;
                            controllerSizeSeen = true;
                        }
                    }
                    else if (String.Equals(key, FloatingControllerOpacityKey, StringComparison.OrdinalIgnoreCase))
                    {
                        int opacity;
                        if (Int32.TryParse(value, out opacity)
                            && opacity >= FloatingRecordingController.MinimumOpacityPercent
                            && opacity <= FloatingRecordingController.MaximumOpacityPercent)
                        {
                            settings.FloatingControllerOpacityPercent = opacity;
                            controllerOpacitySeen = true;
                        }
                    }
                    else if (key.StartsWith(ShortcutKeyPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        CaptureAction action;
                        ShortcutBinding binding;
                        if (Enum.TryParse<CaptureAction>(
                                key.Substring(ShortcutKeyPrefix.Length),
                                true,
                                out action)
                            && Enum.IsDefined(typeof(CaptureAction), action)
                            && TryDecodeShortcut(value, out binding))
                        {
                            settings.SetShortcut(action, binding);
                            shortcutsSeen.Add(action);
                        }
                    }
                }
            }

            if (!frameRateSeen)
            {
                settings.RecordingFrameRate = 15;
                needsMigration = true;
            }

            if (!recordingQualitySeen)
            {
                // A fresh install starts on Balanced, matching the accepted Mac default. An
                // existing settings file without the key was recording at the original fixed
                // bitrate, so migration must keep giving it exactly that.
                settings.RecordingQuality = hasExistingSettings
                    ? RecordingQuality.Legacy
                    : RecordingQuality.Balanced;
                needsMigration = true;
            }

            if (!recordingAudioModeSeen)
            {
                settings.RecordingAudioMode = RecordingAudioMode.Off;
                needsMigration = true;
            }

            if (!computerAudioGainSeen)
            {
                settings.ComputerAudioGainPercent = 100;
                needsMigration = true;
            }

            if (!microphoneGainSeen)
            {
                settings.MicrophoneGainPercent = 100;
                needsMigration = true;
            }

            if (!routineNotificationsSeen)
            {
                settings.RoutineNotificationsEnabled = true;
                needsMigration = true;
            }

            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                if (!shortcutsSeen.Contains(action))
                {
                    needsMigration = true;
                }
            }

            // A settings file written before recording controls existed gets the documented
            // defaults: the controls bound to Ctrl+Alt+Shift+P and Ctrl+Alt+Shift+X, the floating
            // controller on, and 50% opacity. None of the user's existing choices are touched.
            foreach (RecordingControlAction control in RecordingControlCatalog.Actions)
            {
                if (!recordingShortcutsSeen.Contains(control))
                {
                    needsMigration = true;
                }
            }

            if (!showFloatingControlsSeen)
            {
                settings.ShowFloatingRecordingControls = true;
                needsMigration = true;
            }

            if (!controllerOpacitySeen)
            {
                settings.FloatingControllerOpacityPercent =
                    FloatingRecordingController.DefaultOpacityPercent;
                needsMigration = true;
            }

            if (!controllerSizeSeen)
            {
                settings.FloatingControllerSize = FloatingRecordingController.DefaultSize;
                needsMigration = true;
            }

            if (settings.Destinations.Count == 0)
            {
                string initialPath = String.IsNullOrWhiteSpace(legacyDestinationPath)
                    ? this.defaultDestinationFactory()
                    : legacyDestinationPath;

                CaptureDestination initialDestination = CreateDestination(
                    initialPath,
                    GetSuggestedName(initialPath));
                settings.Destinations.Add(initialDestination);
                settings.ActiveDestinationId = initialDestination.Id;
                needsMigration = true;
            }

            if (settings.GetActiveDestination() == null
                || !ContainsDestinationId(settings, settings.ActiveDestinationId))
            {
                settings.ActiveDestinationId = settings.Destinations[0].Id;
                needsMigration = true;
            }

            try { CaptureFilename.Render(settings.FilenameLabel, settings.FilenameTemplate, "Snip", DateTime.Now, settings.NextFilenameCounter); }
            catch (ArgumentException) { settings.FilenameLabel = ""; settings.FilenameTemplate = ""; needsMigration = true; }
            needsMigration |= settings.EnsureOutputShortcuts();
            needsMigration |= settings.EnsureRecordingShortcuts();
            if (needsMigration)
            {
                Save(settings);
            }

            return settings;
        }

        public void Save(AppSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            if (settings.Destinations.Count == 0)
            {
                throw new InvalidOperationException("At least one destination is required.");
            }

            Directory.CreateDirectory(this.settingsDirectory);
            StringBuilder contents = new StringBuilder();
            ValidateShortcuts(settings);
            foreach (CaptureDestination destination in settings.Destinations)
            {
                ShortcutBinding output;
                if (settings.OutputShortcuts.TryGetValue(destination.Id, out output))
                {
                    string conflict = settings.OutputConflict(destination.Id, output);
                    if (conflict != null) throw new InvalidOperationException(conflict);
                }
            }
            foreach (RecordingControlAction control in RecordingControlCatalog.Actions)
            {
                string conflict = settings.RecordingControlConflict(
                    control, settings.GetRecordingShortcut(control));
                if (conflict != null) throw new InvalidOperationException(conflict);
            }
            settings.EnsureOutputShortcuts();
            CaptureFilename.Render(settings.FilenameLabel, settings.FilenameTemplate, "Snip", DateTime.Now, settings.NextFilenameCounter);
            contents.AppendLine(VersionKey + "=" + CurrentVersion);
            contents.AppendLine("ShowCursorInClips=" + settings.ShowCursorInClips);
            contents.AppendLine("MicrophoneDeviceId=" + EncodeText(settings.MicrophoneDeviceId ?? ""));
            contents.AppendLine("FilenameLabel=" + EncodeText(settings.FilenameLabel ?? ""));
            contents.AppendLine("FilenameTemplate=" + EncodeText(settings.FilenameTemplate ?? ""));
            contents.AppendLine("NextFilenameCounter=" + settings.NextFilenameCounter);
            contents.AppendLine(ActiveDestinationKey + "=" + settings.ActiveDestinationId);
            contents.AppendLine(RecordingFrameRateKey + "=" + NormalizeFrameRate(settings.RecordingFrameRate));
            contents.AppendLine(RecordingQualityKey + "=" + RecordingQualitySettings.Normalize(settings.RecordingQuality));
            contents.AppendLine(RecordingResolutionKey + "=" + RecordingResolutionSettings.Normalize(settings.RecordingResolution));
            contents.AppendLine(RecordingAudioModeKey + "=" + settings.RecordingAudioMode);
            contents.AppendLine(
                ComputerAudioGainPercentKey + "="
                + NormalizeAudioGainPercent(settings.ComputerAudioGainPercent));
            contents.AppendLine(
                MicrophoneGainPercentKey + "="
                + NormalizeAudioGainPercent(settings.MicrophoneGainPercent));
            contents.AppendLine(
                RoutineNotificationsEnabledKey + "=" + settings.RoutineNotificationsEnabled);

            foreach (CaptureDestination destination in settings.Destinations)
            {
                ValidateDestination(destination);
                contents.AppendLine(DestinationKey + "=" + EncodeDestination(destination));
                ShortcutBinding output;
                if (settings.OutputShortcuts.TryGetValue(destination.Id, out output))
                    contents.AppendLine("OutputShortcut." + destination.Id + "=" + EncodeShortcut(output));
            }

            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                contents.AppendLine(
                    ShortcutKeyPrefix + action + "=" + EncodeShortcut(settings.GetShortcut(action)));
            }

            foreach (RecordingControlAction control in RecordingControlCatalog.Actions)
            {
                contents.AppendLine(
                    RecordingShortcutKeyPrefix + control + "="
                        + EncodeShortcut(settings.GetRecordingShortcut(control)));
            }

            contents.AppendLine(
                ShowFloatingRecordingControlsKey + "=" + settings.ShowFloatingRecordingControls);
            contents.AppendLine(
                FloatingControllerOpacityKey + "="
                    + FloatingRecordingController.NormalizeOpacityPercent(
                        settings.FloatingControllerOpacityPercent));
            contents.AppendLine(
                FloatingControllerSizeKey + "="
                    + FloatingRecordingController.NormalizeSize(settings.FloatingControllerSize));

            // Most recently used first: the read path restores this order, so the bound stays
            // meaningful across restarts instead of dropping whichever layout happened to sort low.
            foreach (string layoutKey in settings.ControllerLayouts.Keys)
            {
                Point position;
                if (!settings.ControllerLayouts.TryGetPosition(layoutKey, out position)) continue;
                contents.AppendLine(
                    ControllerPositionKeyPrefix + layoutKey + "=" + position.X + "," + position.Y);
            }

            string temporary = Path.Combine(this.settingsDirectory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(contents.ToString());
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(this.settingsPath)) File.Replace(temporary, this.settingsPath, null);
                else File.Move(temporary, this.settingsPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal void SaveRecordingPreferences(AppSettings settings, int frameRate, RecordingQuality quality)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            int previousFrameRate = settings.RecordingFrameRate;
            RecordingQuality previousQuality = settings.RecordingQuality;
            settings.RecordingFrameRate = NormalizeFrameRate(frameRate);
            settings.RecordingQuality = RecordingQualitySettings.Normalize(quality);
            try
            {
                Save(settings);
            }
            catch
            {
                settings.RecordingFrameRate = previousFrameRate;
                settings.RecordingQuality = previousQuality;
                throw;
            }
        }

        internal static CaptureDestination CreateDestination(string path, string name)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A destination path is required.", "path");
            }

            return new CaptureDestination
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = String.IsNullOrWhiteSpace(name) ? GetSuggestedName(path) : name.Trim(),
                Path = path
            };
        }

        internal static string GetSuggestedName(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                return "Destination";
            }

            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            if (!String.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            DirectoryInfo directory = new DirectoryInfo(path);
            return String.IsNullOrWhiteSpace(directory.Name) ? "Destination" : directory.Name;
        }

        private static bool ContainsDestinationId(AppSettings settings, string id)
        {
            foreach (CaptureDestination destination in settings.Destinations)
            {
                if (String.Equals(destination.Id, id, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string EncodeDestination(CaptureDestination destination)
        {
            return destination.Id
                + "|" + EncodeText(destination.Name)
                + "|" + EncodeText(destination.Path);
        }

        private static CaptureDestination DecodeDestination(string value)
        {
            try
            {
                string[] parts = value.Split(new[] { '|' }, 3);
                if (parts.Length != 3 || String.IsNullOrWhiteSpace(parts[0]))
                {
                    return null;
                }

                CaptureDestination destination = new CaptureDestination
                {
                    Id = parts[0],
                    Name = DecodeText(parts[1]),
                    Path = DecodeText(parts[2])
                };

                ValidateDestination(destination);
                return destination;
            }
            catch
            {
                return null;
            }
        }

        private static string EncodeText(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string DecodeText(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private static void ValidateDestination(CaptureDestination destination)
        {
            if (destination == null
                || String.IsNullOrWhiteSpace(destination.Id)
                || String.IsNullOrWhiteSpace(destination.Name)
                || String.IsNullOrWhiteSpace(destination.Path))
            {
                throw new InvalidOperationException("A destination is incomplete.");
            }
        }

        /// <summary>
        /// Where a brand-new install saves until the user picks somewhere else.
        ///
        /// This is shipped software: it must never hunt the filesystem for a development folder.
        /// An earlier version walked up to ten parent directories from the executable looking for
        /// an "Ingest" folder, which can be a development-workspace convenience. On someone else's machine
        /// that walk reaches their user folder and the drive root, so an unrelated "Ingest" folder
        /// would silently capture their files. Named destinations are added from the tray instead.
        /// </summary>
        internal static string FindDefaultDestination()
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (String.IsNullOrWhiteSpace(pictures))
            {
                pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            return Path.Combine(pictures, "Huck's Snip 'n' Clip");
        }

        internal static bool IsSupportedFrameRate(int frameRate)
        {
            return frameRate == 0 || frameRate == 15 || frameRate == 30 || frameRate == 60;
        }

        internal static int NormalizeFrameRate(int frameRate)
        {
            return IsSupportedFrameRate(frameRate) ? frameRate : 15;
        }

        internal static bool IsSupportedAudioGainPercent(int gainPercent)
        {
            return gainPercent >= 0 && gainPercent <= 300;
        }

        internal static int NormalizeAudioGainPercent(int gainPercent)
        {
            return IsSupportedAudioGainPercent(gainPercent) ? gainPercent : 100;
        }

        private static string EncodeShortcut(ShortcutBinding binding)
        {
            string encoded = binding.Modifiers + "," + (int)binding.Key;
            if (binding.HasSecondStroke)
            {
                encoded += ";" + binding.SecondModifiers + "," + (int)binding.SecondKey;
            }

            return encoded;
        }

        private static bool TryDecodeShortcut(string value, out ShortcutBinding binding)
        {
            binding = null;
            string[] strokes = value.Split(';');
            uint modifiers;
            int key;
            if (strokes.Length < 1
                || strokes.Length > 2
                || !TryDecodeShortcutStroke(strokes[0], out modifiers, out key))
            {
                return false;
            }

            ShortcutBinding candidate = new ShortcutBinding
            {
                Modifiers = modifiers,
                Key = (Keys)key
            };

            if (strokes.Length == 2)
            {
                uint secondModifiers;
                int secondKey;
                if (!TryDecodeShortcutStroke(strokes[1], out secondModifiers, out secondKey))
                {
                    return false;
                }

                candidate.SecondModifiers = secondModifiers;
                candidate.SecondKey = (Keys)secondKey;
            }

            if (!candidate.IsValid())
            {
                return false;
            }

            binding = candidate;
            return true;
        }

        private static bool TryDecodePoint(string value, out Point point)
        {
            point = Point.Empty;
            string[] parts = (value ?? "").Split(',');
            int x, y;
            if (parts.Length != 2 || !Int32.TryParse(parts[0], out x) || !Int32.TryParse(parts[1], out y))
            {
                return false;
            }

            point = new Point(x, y);
            return true;
        }

        private static bool TryDecodeShortcutStroke(string value, out uint modifiers, out int key)
        {
            modifiers = 0;
            key = 0;
            string[] parts = value.Split(',');
            return parts.Length == 2
                && UInt32.TryParse(parts[0], out modifiers)
                && Int32.TryParse(parts[1], out key);
        }

        private static void ValidateShortcuts(AppSettings settings)
        {
            Dictionary<CaptureAction, ShortcutBinding> bindings = settings.CloneShortcuts();
            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                ShortcutBinding binding = bindings[action];
                if (!binding.IsValid())
                {
                    throw new InvalidOperationException("A shortcut binding is invalid.");
                }

                CaptureAction otherAction;
                ShortcutConflictKind conflict = ShortcutConflicts.Find(
                    action,
                    binding,
                    bindings,
                    out otherAction);
                if (conflict != ShortcutConflictKind.None)
                {
                    throw new InvalidOperationException(
                        ShortcutConflicts.Describe(
                            conflict,
                            action,
                            binding,
                            otherAction,
                            bindings[otherAction]));
                }
            }
        }
    }
}
