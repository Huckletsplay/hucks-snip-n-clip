using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal enum CaptureAction
    {
        SnipRegion = 1,
        SnipScreen = 2,
        ClipScreen = 3,
        ClipRegion = 4,
        SnipWindow = 5,
        ClipWindow = 6
    }

    internal sealed class ShortcutBinding
    {
        public uint Modifiers { get; set; }
        public Keys Key { get; set; }
        public uint SecondModifiers { get; set; }
        public Keys SecondKey { get; set; }

        public bool HasSecondStroke
        {
            get { return this.SecondKey != Keys.None; }
        }

        public ShortcutBinding Clone()
        {
            return new ShortcutBinding
            {
                Modifiers = this.Modifiers,
                Key = this.Key,
                SecondModifiers = this.SecondModifiers,
                SecondKey = this.SecondKey
            };
        }

        public bool IsValid()
        {
            return IsStrokeValid(this.Modifiers, this.Key)
                && ((!this.HasSecondStroke && this.SecondModifiers == 0)
                    || (this.HasSecondStroke && IsStrokeValid(this.SecondModifiers, this.SecondKey)));
        }

        public string ToDisplayString()
        {
            string first = GetStrokeDisplayString(this.Modifiers, this.Key);
            return this.HasSecondStroke
                ? first + ", " + GetStrokeDisplayString(this.SecondModifiers, this.SecondKey)
                : first;
        }

        private static string GetStrokeDisplayString(uint modifiers, Keys key)
        {
            List<string> parts = new List<string>();
            if ((modifiers & NativeMethods.MOD_CONTROL) != 0)
            {
                parts.Add("Ctrl");
            }

            if ((modifiers & NativeMethods.MOD_ALT) != 0)
            {
                parts.Add("Alt");
            }

            if ((modifiers & NativeMethods.MOD_SHIFT) != 0)
            {
                parts.Add("Shift");
            }

            if ((modifiers & NativeMethods.MOD_WIN) != 0)
            {
                parts.Add("Win");
            }

            parts.Add(GetKeyName(key));
            return String.Join("+", parts.ToArray());
        }

        public bool EqualsBinding(ShortcutBinding other)
        {
            return other != null
                && this.Modifiers == other.Modifiers
                && this.Key == other.Key
                && this.SecondModifiers == other.SecondModifiers
                && this.SecondKey == other.SecondKey;
        }

        public bool HasSameFirstStroke(ShortcutBinding other)
        {
            return other != null
                && this.Modifiers == other.Modifiers
                && this.Key == other.Key;
        }

        /// <summary>
        /// True when this binding's second step is the same combination that starts
        /// <paramref name="other" />. Windows can only reserve a combination once, so such a chord
        /// could never listen for its own second step.
        /// </summary>
        public bool SecondStrokeStarts(ShortcutBinding other)
        {
            return other != null
                && this.HasSecondStroke
                && this.SecondModifiers == other.Modifiers
                && this.SecondKey == other.Key;
        }

        public ShortcutBinding GetSecondStroke()
        {
            if (!this.HasSecondStroke)
            {
                return null;
            }

            return new ShortcutBinding
            {
                Modifiers = this.SecondModifiers,
                Key = this.SecondKey
            };
        }

        public ShortcutBinding GetFirstStroke()
        {
            return new ShortcutBinding
            {
                Modifiers = this.Modifiers,
                Key = this.Key
            };
        }

        public ShortcutBinding WithSecondStroke(ShortcutBinding second)
        {
            if (second == null || !IsStrokeValid(second.Modifiers, second.Key))
            {
                throw new ArgumentException("A valid second shortcut stroke is required.", "second");
            }

            ShortcutBinding chord = Clone();
            chord.SecondModifiers = second.Modifiers;
            chord.SecondKey = second.Key;
            return chord;
        }

        public static ShortcutBinding FromKeyEvent(KeyEventArgs e)
        {
            bool winPressed = (NativeMethods.GetAsyncKeyState((int)Keys.LWin) & 0x8000) != 0
                || (NativeMethods.GetAsyncKeyState((int)Keys.RWin) & 0x8000) != 0;
            return FromKeyData(e.KeyData, winPressed);
        }

        public static ShortcutBinding FromKeyData(Keys keyData, bool winPressed)
        {
            uint modifiers = 0;
            if ((keyData & Keys.Control) != 0)
            {
                modifiers |= NativeMethods.MOD_CONTROL;
            }

            if ((keyData & Keys.Alt) != 0)
            {
                modifiers |= NativeMethods.MOD_ALT;
            }

            if ((keyData & Keys.Shift) != 0)
            {
                modifiers |= NativeMethods.MOD_SHIFT;
            }

            if (winPressed)
            {
                modifiers |= NativeMethods.MOD_WIN;
            }

            Keys key = keyData & Keys.KeyCode;
            if (IsModifierKey(key))
            {
                key = Keys.None;
            }

            return new ShortcutBinding { Modifiers = modifiers, Key = key };
        }

        private static bool IsStrokeValid(uint modifiers, Keys key)
        {
            uint supported = NativeMethods.MOD_ALT
                | NativeMethods.MOD_CONTROL
                | NativeMethods.MOD_SHIFT
                | NativeMethods.MOD_WIN;
            return key != Keys.None
                && !IsModifierKey(key)
                && (modifiers & ~supported) == 0;
        }

        private static string GetKeyName(Keys key)
        {
            if (key >= Keys.D0 && key <= Keys.D9)
            {
                return ((int)(key - Keys.D0)).ToString();
            }

            if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            {
                return "Num " + ((int)(key - Keys.NumPad0)).ToString();
            }

            switch (key)
            {
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemPipe: return "\\";
                case Keys.Oemplus: return "+";
                case Keys.OemMinus: return "-";
                case Keys.Oemtilde: return "`";
                case Keys.Space: return "Space";
                case Keys.Return: return "Enter";
                case Keys.Escape: return "Esc";
                case Keys.Back: return "Backspace";
                case Keys.Next: return "Page Down";
                case Keys.Prior: return "Page Up";
            }

            return key.ToString();
        }

        private static bool IsModifierKey(Keys key)
        {
            return key == Keys.ControlKey
                || key == Keys.LControlKey
                || key == Keys.RControlKey
                || key == Keys.Menu
                || key == Keys.LMenu
                || key == Keys.RMenu
                || key == Keys.ShiftKey
                || key == Keys.LShiftKey
                || key == Keys.RShiftKey
                || key == Keys.LWin
                || key == Keys.RWin;
        }
    }

    internal static class ShortcutCatalog
    {
        private static readonly CaptureAction[] actions = new[]
        {
            CaptureAction.SnipRegion,
            CaptureAction.SnipWindow,
            CaptureAction.SnipScreen,
            CaptureAction.ClipRegion,
            CaptureAction.ClipWindow,
            CaptureAction.ClipScreen
        };

        public static CaptureAction[] Actions
        {
            get { return (CaptureAction[])actions.Clone(); }
        }

        public static string GetName(CaptureAction action)
        {
            switch (action)
            {
                case CaptureAction.SnipRegion: return "Snip Region";
                case CaptureAction.SnipScreen: return "Snip Screen";
                case CaptureAction.ClipScreen: return "Clip Screen";
                case CaptureAction.ClipRegion: return "Clip Region";
                case CaptureAction.SnipWindow: return "Snip Window";
                case CaptureAction.ClipWindow: return "Clip Window";
                default: throw new ArgumentOutOfRangeException("action");
            }
        }

        public static ShortcutBinding GetDefault(CaptureAction action)
        {
            Keys key;
            switch (action)
            {
                case CaptureAction.SnipRegion: key = Keys.S; break;
                case CaptureAction.SnipScreen: key = Keys.F; break;
                case CaptureAction.ClipScreen: key = Keys.R; break;
                case CaptureAction.ClipRegion: key = Keys.C; break;
                case CaptureAction.SnipWindow: key = Keys.W; break;
                case CaptureAction.ClipWindow: key = Keys.V; break;
                default: throw new ArgumentOutOfRangeException("action");
            }

            return new ShortcutBinding
            {
                Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT,
                Key = key
            };
        }
    }

    internal enum ShortcutConflictKind
    {
        None = 0,
        SameFirstStroke,
        SecondStrokeStartsAnotherAction,
        FirstStrokeCompletesAnotherChord
    }

    /// <summary>
    /// Finds the ways one proposed binding can collide with the other capture actions. Every kind
    /// here is a collision Huck's Snip 'n' Clip creates for itself, so the explanation must name the
    /// other action rather than blaming Windows or another program.
    /// </summary>
    internal static class ShortcutConflicts
    {
        public static ShortcutConflictKind Find(
            CaptureAction action,
            ShortcutBinding candidate,
            IDictionary<CaptureAction, ShortcutBinding> bindings,
            out CaptureAction otherAction)
        {
            if (candidate == null)
            {
                throw new ArgumentNullException("candidate");
            }

            if (bindings == null)
            {
                throw new ArgumentNullException("bindings");
            }

            otherAction = action;
            foreach (CaptureAction other in ShortcutCatalog.Actions)
            {
                if (other == action)
                {
                    continue;
                }

                ShortcutBinding existing;
                if (!bindings.TryGetValue(other, out existing) || existing == null)
                {
                    continue;
                }

                if (candidate.HasSameFirstStroke(existing))
                {
                    otherAction = other;
                    return ShortcutConflictKind.SameFirstStroke;
                }

                if (candidate.SecondStrokeStarts(existing))
                {
                    otherAction = other;
                    return ShortcutConflictKind.SecondStrokeStartsAnotherAction;
                }

                if (existing.SecondStrokeStarts(candidate))
                {
                    otherAction = other;
                    return ShortcutConflictKind.FirstStrokeCompletesAnotherChord;
                }
            }

            return ShortcutConflictKind.None;
        }

        public static string Describe(
            ShortcutConflictKind kind,
            CaptureAction action,
            ShortcutBinding candidate,
            CaptureAction otherAction,
            ShortcutBinding otherBinding)
        {
            string actionName = ShortcutCatalog.GetName(action);
            string otherName = ShortcutCatalog.GetName(otherAction);
            switch (kind)
            {
                case ShortcutConflictKind.SameFirstStroke:
                    return actionName + " (" + candidate.ToDisplayString() + ") and "
                        + otherName + " (" + otherBinding.ToDisplayString() + ") begin with the same "
                        + "shortcut. Chord prefixes must currently be unique. Your previous shortcuts "
                        + "are still active.";
                case ShortcutConflictKind.SecondStrokeStartsAnotherAction:
                    return actionName + " cannot use " + candidate.ToDisplayString()
                        + " because its second step, " + candidate.GetSecondStroke().ToDisplayString()
                        + ", is already the shortcut for " + otherName + ". Windows can reserve a "
                        + "combination only once, so a chord's second step cannot be another action's "
                        + "shortcut. Your previous shortcuts are still active.";
                case ShortcutConflictKind.FirstStrokeCompletesAnotherChord:
                    return actionName + " cannot use " + candidate.ToDisplayString()
                        + " because that combination is already the second step of " + otherName
                        + " (" + otherBinding.ToDisplayString() + "). Your previous shortcuts are "
                        + "still active.";
                default:
                    throw new ArgumentOutOfRangeException("kind");
            }
        }

        /// <summary>
        /// Explains a combination Windows itself refused. Only this message may point at Windows
        /// or another program, and it names the stroke that was rejected rather than the whole
        /// chord.
        /// </summary>
        public static string DescribeRegistrationFailure(
            CaptureAction action,
            ShortcutBinding binding,
            bool failedOnSecondStroke)
        {
            string name = ShortcutCatalog.GetName(action);
            if (failedOnSecondStroke)
            {
                return name + " could not use " + binding.ToDisplayString()
                    + " because Windows or another program has reserved its second step, "
                    + binding.GetSecondStroke().ToDisplayString()
                    + ". Your previous shortcuts are still active.";
            }

            return name + " could not use " + binding.GetFirstStroke().ToDisplayString()
                + ". Windows or another program has reserved that combination. Your previous "
                + "shortcuts are still active.";
        }
    }
}
