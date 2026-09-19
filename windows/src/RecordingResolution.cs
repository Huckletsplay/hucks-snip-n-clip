using System;
using System.Drawing;

namespace QSnipAndClip
{
    // A ceiling, not a target. It caps how tall the encoded picture may be and never
    // enlarges a source that is already smaller.
    internal enum RecordingResolutionCeiling
    {
        Native = 0,
        P1440 = 1440,
        P1080 = 1080,
        P720 = 720
    }

    internal static class RecordingResolutionSettings
    {
        public static RecordingResolutionCeiling Normalize(RecordingResolutionCeiling ceiling)
        {
            switch (ceiling)
            {
                case RecordingResolutionCeiling.P1440:
                case RecordingResolutionCeiling.P1080:
                case RecordingResolutionCeiling.P720:
                    return ceiling;
                default:
                    return RecordingResolutionCeiling.Native;
            }
        }

        public static RecordingResolutionCeiling Parse(string value)
        {
            if (!String.IsNullOrWhiteSpace(value))
            {
                try
                {
                    return Normalize((RecordingResolutionCeiling)Enum.Parse(
                        typeof(RecordingResolutionCeiling), value.Trim(), true));
                }
                catch (ArgumentException)
                {
                    // An unreadable ceiling falls back to Native rather than failing a recording.
                }
            }

            return RecordingResolutionCeiling.Native;
        }

        public static string GetLabel(RecordingResolutionCeiling ceiling)
        {
            switch (Normalize(ceiling))
            {
                case RecordingResolutionCeiling.P1440: return "1440p";
                case RecordingResolutionCeiling.P1080: return "1080p";
                case RecordingResolutionCeiling.P720: return "720p";
                default: return "Native";
            }
        }

        // Preserves aspect ratio, never enlarges, and keeps both dimensions even for H.264.
        public static Size Resolve(int width, int height, RecordingResolutionCeiling ceiling)
        {
            if (width < 2 || height < 2)
            {
                throw new ArgumentOutOfRangeException("width", "A recording must be at least 2 by 2 pixels.");
            }

            int sourceWidth = width - (width % 2);
            int sourceHeight = height - (height % 2);
            int limit = (int)Normalize(ceiling);
            if (limit <= 0 || sourceHeight <= limit)
            {
                return new Size(sourceWidth, sourceHeight);
            }

            long scaledWidth = (long)sourceWidth * limit / sourceHeight;
            int targetWidth = (int)Math.Max(2, scaledWidth - (scaledWidth % 2));
            int targetHeight = limit - (limit % 2);
            return new Size(targetWidth, targetHeight);
        }
    }
}
