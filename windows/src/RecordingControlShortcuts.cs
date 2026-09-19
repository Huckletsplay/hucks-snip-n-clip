using System;
using System.Windows.Forms;

namespace QSnipAndClip
{
    /// <summary>
    /// The two controls a recording needs while the taskbar and the tray are hidden.
    ///
    /// These are deliberately *not* <see cref="CaptureAction" /> members. A capture shortcut is
    /// registered the whole time Huck's Snip 'n' Clip is running; these are registered only while a
    /// clip is actually being recorded and are removed again while idle and during finalization, so
    /// they cannot replace a key the user needs for ordinary work.
    /// </summary>
    internal enum RecordingControlAction
    {
        PauseResume = 1,
        Stop = 2
    }

    internal static class RecordingControlCatalog
    {
        private static readonly RecordingControlAction[] actions = new[]
        {
            RecordingControlAction.PauseResume,
            RecordingControlAction.Stop
        };

        public static RecordingControlAction[] Actions
        {
            get { return (RecordingControlAction[])actions.Clone(); }
        }

        public static string GetName(RecordingControlAction action)
        {
            switch (action)
            {
                case RecordingControlAction.PauseResume: return "Pause/Resume Recording";
                case RecordingControlAction.Stop: return "Stop Recording";
                default: throw new ArgumentOutOfRangeException("action");
            }
        }

        public static ShortcutBinding GetDefault(RecordingControlAction action)
        {
            Keys key;
            switch (action)
            {
                case RecordingControlAction.PauseResume: key = Keys.P; break;
                case RecordingControlAction.Stop: key = Keys.X; break;
                default: throw new ArgumentOutOfRangeException("action");
            }

            return new ShortcutBinding
            {
                Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT,
                Key = key
            };
        }

        /// <summary>
        /// Escape is never a recording control. Accidentally cancelling a take is worse than
        /// requiring an explicit stop, so the key that dismisses every other surface must not
        /// reach a running recording.
        /// </summary>
        public static bool IsForbiddenKey(Keys key)
        {
            return key == Keys.Escape;
        }
    }
}
