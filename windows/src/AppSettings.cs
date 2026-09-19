using System;
using System.Collections.Generic;

namespace QSnipAndClip
{
    internal enum RecordingQuality
    {
        Legacy = 0,
        Balanced = 1,
        High = 2
    }

    internal static class RecordingQualitySettings
    {
        internal const int LegacyBitsPerSecond = 8000000;
        internal const int MaximumBitsPerSecond = 80000000;

        internal static RecordingQuality Normalize(RecordingQuality quality)
        {
            return Enum.IsDefined(typeof(RecordingQuality), quality) ? quality : RecordingQuality.Legacy;
        }

        internal static int CalculateBitsPerSecond(
            RecordingQuality quality, int width, int height, int framesPerSecond)
        {
            if (width < 16 || height < 16)
            {
                throw new ArgumentOutOfRangeException("width", "Video dimensions must be at least 16 pixels.");
            }

            // Match Display must be resolved to a real capture rate before calculating quality.
            if (framesPerSecond < 1 || framesPerSecond > 240)
            {
                throw new ArgumentOutOfRangeException("framesPerSecond");
            }

            quality = Normalize(quality);
            if (quality == RecordingQuality.Legacy)
            {
                return LegacyBitsPerSecond;
            }

            int referenceBitrate = quality == RecordingQuality.High ? 24000000 : 16000000;
            int minimumBitrate = quality == RecordingQuality.High ? 3000000 : 2000000;
            // Scale in floating point before multiplying dimensions to avoid integer overflow.
            double scaled = referenceBitrate * (width / 1920.0) * (height / 1080.0)
                * (framesPerSecond / 60.0);
            return (int)Math.Round(Math.Max(minimumBitrate, Math.Min(MaximumBitsPerSecond, scaled)));
        }
    }

    internal enum RecordingAudioMode
    {
        Off = 0,
        Computer = 1,
        Microphone = 2,
        ComputerAndMicrophone = 3
    }

    internal sealed class CaptureDestination
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
    }

    internal sealed class AppSettings
    {
        private readonly List<CaptureDestination> destinations;
        private readonly Dictionary<CaptureAction, ShortcutBinding> shortcuts;
        private readonly Dictionary<RecordingControlAction, ShortcutBinding> recordingShortcuts;

        public AppSettings()
        {
            this.destinations = new List<CaptureDestination>();
            this.shortcuts = new Dictionary<CaptureAction, ShortcutBinding>();
            this.recordingShortcuts = new Dictionary<RecordingControlAction, ShortcutBinding>();
            this.RecordingFrameRate = 15;
            this.RecordingQuality = RecordingQuality.Balanced;
            this.RecordingAudioMode = RecordingAudioMode.Off;
            this.ComputerAudioGainPercent = 100;
            this.MicrophoneGainPercent = 100;
            this.RoutineNotificationsEnabled = true;

            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                this.shortcuts[action] = ShortcutCatalog.GetDefault(action);
            }

            foreach (RecordingControlAction action in RecordingControlCatalog.Actions)
            {
                this.recordingShortcuts[action] = RecordingControlCatalog.GetDefault(action);
            }
        }

        public List<CaptureDestination> Destinations
        {
            get { return this.destinations; }
        }

        public string ActiveDestinationId { get; set; }

        // Zero means match the refresh rate of the display selected for the recording.
        public int RecordingFrameRate { get; set; }

        public RecordingQuality RecordingQuality { get; set; }

        public RecordingResolutionCeiling RecordingResolution { get; set; }

        public RecordingAudioMode RecordingAudioMode { get; set; }

        public int ComputerAudioGainPercent { get; set; }

        public int MicrophoneGainPercent { get; set; }

        public bool RoutineNotificationsEnabled { get; set; }
        public string FilenameLabel = "";
        public string FilenameTemplate = "";
        public long NextFilenameCounter = 1;
        public bool ShowCursorInClips;
        public string MicrophoneDeviceId = "";
        public readonly Dictionary<string, ShortcutBinding> OutputShortcuts = new Dictionary<string, ShortcutBinding>();

        /// <summary>The floating recording H is shown unless the user turns it off.</summary>
        public bool ShowFloatingRecordingControls = true;

        public int FloatingControllerOpacityPercent = FloatingRecordingController.DefaultOpacityPercent;

        /// <summary>Width of the controller's H square in pixels, set by dragging its notch.</summary>
        public int FloatingControllerSize = FloatingRecordingController.DefaultSize;

        /// <summary>
        /// Remembered controller positions, one per recognized display layout. Turning the
        /// controller off never clears these: switching it back on must land where it was left.
        /// </summary>
        public readonly RecordingControllerLayouts ControllerLayouts = new RecordingControllerLayouts();

        internal string OutputConflict(string destinationId, ShortcutBinding candidate)
        {
            if (candidate == null || !candidate.IsValid() || candidate.Modifiers == 0 || candidate.HasSecondStroke)
                return "Output shortcuts require one key with Ctrl, Alt, Shift, or Windows.";
            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                ShortcutBinding capture = GetShortcut(action);
                if (candidate.HasSameFirstStroke(capture) || capture.SecondStrokeStarts(candidate))
                    return "That key is already used by " + ShortcutCatalog.GetName(action) + ".";
            }
            foreach (RecordingControlAction control in RecordingControlCatalog.Actions)
            {
                if (candidate.HasSameFirstStroke(GetRecordingShortcut(control)))
                    return "That key is already used by " + RecordingControlCatalog.GetName(control) + ".";
            }
            foreach (CaptureDestination destination in Destinations)
            {
                ShortcutBinding existing;
                if (destination.Id != destinationId && OutputShortcuts.TryGetValue(destination.Id, out existing)
                    && candidate.HasSameFirstStroke(existing)) return "That key selects " + destination.Name + ".";
            }
            return null;
        }

        /// <summary>
        /// Why a proposed recording control key cannot be used, or null when it is free.
        ///
        /// A recording control is only alive during a take, but it still has to be checked against
        /// everything else Huck's Snip 'n' Clip reserves: both strokes of every capture shortcut,
        /// every Output shortcut, and the other recording control. Windows can reserve a
        /// combination only once, so an overlap would mean the control silently fails to register
        /// at the exact moment it is needed.
        /// </summary>
        internal string RecordingControlConflict(RecordingControlAction action, ShortcutBinding candidate)
        {
            if (candidate == null || !candidate.IsValid() || candidate.Modifiers == 0 || candidate.HasSecondStroke)
                return "Recording controls require one key with Ctrl, Alt, Shift, or Windows.";
            if (RecordingControlCatalog.IsForbiddenKey(candidate.Key))
                return "Escape never stops a recording. Choose another key.";
            foreach (CaptureAction capture in ShortcutCatalog.Actions)
            {
                ShortcutBinding binding = GetShortcut(capture);
                if (candidate.HasSameFirstStroke(binding) || binding.SecondStrokeStarts(candidate))
                    return "That key is already used by " + ShortcutCatalog.GetName(capture) + ".";
            }
            foreach (RecordingControlAction control in RecordingControlCatalog.Actions)
            {
                if (control != action && candidate.HasSameFirstStroke(GetRecordingShortcut(control)))
                    return "That key is already used by " + RecordingControlCatalog.GetName(control) + ".";
            }
            foreach (CaptureDestination destination in Destinations)
            {
                ShortcutBinding existing;
                if (OutputShortcuts.TryGetValue(destination.Id, out existing)
                    && candidate.HasSameFirstStroke(existing)) return "That key selects " + destination.Name + ".";
            }
            return null;
        }

        public ShortcutBinding GetRecordingShortcut(RecordingControlAction action)
        {
            ShortcutBinding binding;
            return this.recordingShortcuts.TryGetValue(action, out binding)
                ? binding
                : RecordingControlCatalog.GetDefault(action);
        }

        public void SetRecordingShortcut(RecordingControlAction action, ShortcutBinding binding)
        {
            if (binding == null || !binding.IsValid())
            {
                throw new ArgumentException("A valid shortcut binding is required.", "binding");
            }

            this.recordingShortcuts[action] = binding.Clone();
        }

        /// <summary>
        /// Repairs recording controls that a rebind elsewhere has collided with, so a take always
        /// has working Pause/Resume and Stop keys. Returns true when something had to change.
        /// </summary>
        internal bool EnsureRecordingShortcuts()
        {
            bool changed = false;
            foreach (RecordingControlAction action in RecordingControlCatalog.Actions)
            {
                if (RecordingControlConflict(action, GetRecordingShortcut(action)) == null) continue;

                ShortcutBinding fallback = RecordingControlCatalog.GetDefault(action);
                if (RecordingControlConflict(action, fallback) != null)
                {
                    for (int index = 0; index < 26; index++)
                    {
                        ShortcutBinding candidate = new ShortcutBinding
                        {
                            Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT,
                            Key = System.Windows.Forms.Keys.A + index
                        };
                        if (RecordingControlConflict(action, candidate) != null) continue;
                        fallback = candidate;
                        break;
                    }
                }

                // When every candidate is taken there is nothing better to offer. Leave the binding
                // alone rather than rewriting the settings file on every load; the registration
                // failure is reported to the user when the recording starts.
                if (fallback.EqualsBinding(GetRecordingShortcut(action))) continue;
                this.recordingShortcuts[action] = fallback;
                changed = true;
            }

            return changed;
        }

        internal bool EnsureOutputShortcuts()
        {
            bool changed = false;
            foreach (CaptureDestination destination in Destinations)
            {
                ShortcutBinding existing;
                if (OutputShortcuts.TryGetValue(destination.Id, out existing)
                    && OutputConflict(destination.Id, existing) == null) continue;
                OutputShortcuts.Remove(destination.Id);
                for (int index = 0; index < 36; index++)
                {
                    System.Windows.Forms.Keys key = index < 9
                        ? System.Windows.Forms.Keys.D1 + index
                        : index == 9 ? System.Windows.Forms.Keys.D0
                        : System.Windows.Forms.Keys.A + index - 10;
                    ShortcutBinding candidate = new ShortcutBinding {
                        Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, Key = key };
                    if (OutputConflict(destination.Id, candidate) == null)
                    {
                        OutputShortcuts[destination.Id] = candidate;
                        changed = true;
                        break;
                    }
                }
            }
            return changed;
        }

        public ShortcutBinding GetShortcut(CaptureAction action)
        {
            ShortcutBinding binding;
            return this.shortcuts.TryGetValue(action, out binding)
                ? binding
                : ShortcutCatalog.GetDefault(action);
        }

        public void SetShortcut(CaptureAction action, ShortcutBinding binding)
        {
            if (binding == null || !binding.IsValid())
            {
                throw new ArgumentException("A valid shortcut binding is required.", "binding");
            }

            this.shortcuts[action] = binding.Clone();
        }

        public Dictionary<CaptureAction, ShortcutBinding> CloneShortcuts()
        {
            Dictionary<CaptureAction, ShortcutBinding> copy = new Dictionary<CaptureAction, ShortcutBinding>();
            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                copy[action] = GetShortcut(action).Clone();
            }

            return copy;
        }

        public CaptureDestination GetActiveDestination()
        {
            foreach (CaptureDestination destination in this.destinations)
            {
                if (String.Equals(destination.Id, this.ActiveDestinationId, StringComparison.Ordinal))
                {
                    return destination;
                }
            }

            return this.destinations.Count == 0 ? null : this.destinations[0];
        }

        public CaptureDestination FindDestinationByPath(string path)
        {
            foreach (CaptureDestination destination in this.destinations)
            {
                if (String.Equals(destination.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return destination;
                }
            }

            return null;
        }
    }
}
