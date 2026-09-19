using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed class ShortcutChordWaitEventArgs : EventArgs
    {
        public ShortcutChordWaitEventArgs(ShortcutBinding firstStroke, int secondsRemaining)
        {
            this.FirstStroke = firstStroke;
            this.SecondsRemaining = secondsRemaining;
        }

        public ShortcutBinding FirstStroke { get; private set; }
        public int SecondsRemaining { get; private set; }
    }

    internal sealed class ShortcutCaptureCompletedEventArgs : EventArgs
    {
        public ShortcutCaptureCompletedEventArgs(ShortcutBinding binding, bool canceled)
        {
            this.Binding = binding;
            this.Canceled = canceled;
        }

        public ShortcutBinding Binding { get; private set; }
        public bool Canceled { get; private set; }
    }

    internal sealed class KeyboardShortcutCaptureSession : IDisposable
    {
        internal const int DefaultFirstStrokeTimeoutMilliseconds = 10000;
        internal const int DefaultSecondStrokeWindowMilliseconds = 3000;
        private const int ChordTickIntervalMilliseconds = 100;

        private readonly int firstStrokeTimeoutMilliseconds;
        private readonly int secondStrokeWindowMilliseconds;
        private readonly Timer timer;
        private readonly Timer dispatchTimer;
        private readonly Queue<Action> pendingEvents;
        private readonly HashSet<Keys> pressedKeys;
        private readonly NativeMethods.LowLevelKeyboardProc hookCallback;
        private IntPtr hook;
        private ShortcutBinding firstStroke;
        private uint activeModifiers;
        private int chordDeadlineTick;
        private int reportedSecondsRemaining;
        private bool isComplete;
        private bool isDisposed;

        public KeyboardShortcutCaptureSession()
            : this(DefaultFirstStrokeTimeoutMilliseconds, DefaultSecondStrokeWindowMilliseconds)
        {
        }

        internal KeyboardShortcutCaptureSession(
            int firstStrokeTimeoutMilliseconds,
            int secondStrokeWindowMilliseconds)
        {
            if (firstStrokeTimeoutMilliseconds <= 0 || secondStrokeWindowMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException("firstStrokeTimeoutMilliseconds");
            }

            this.firstStrokeTimeoutMilliseconds = firstStrokeTimeoutMilliseconds;
            this.secondStrokeWindowMilliseconds = secondStrokeWindowMilliseconds;
            this.timer = new Timer();
            this.timer.Interval = firstStrokeTimeoutMilliseconds;
            this.timer.Tick += HandleTimer;
            this.dispatchTimer = new Timer();
            this.dispatchTimer.Interval = 1;
            this.dispatchTimer.Tick += HandleDispatch;
            this.pendingEvents = new Queue<Action>();
            this.pressedKeys = new HashSet<Keys>();
            this.hookCallback = HandleKeyboardMessage;
        }

        /// <summary>
        /// Raised when a first stroke is captured and again whenever the visible number of
        /// seconds left to add a second stroke changes. Delivered from the message loop, never
        /// from inside the keyboard hook.
        /// </summary>
        public event EventHandler<ShortcutChordWaitEventArgs> ChordWaitChanged;

        public event EventHandler<ShortcutCaptureCompletedEventArgs> Completed;

        internal int SecondStrokeWindowMilliseconds
        {
            get { return this.secondStrokeWindowMilliseconds; }
        }

        public void Start()
        {
            if (this.hook != IntPtr.Zero || this.isComplete)
            {
                throw new InvalidOperationException("The shortcut capture session has already started.");
            }

            this.hook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                this.hookCallback,
                NativeMethods.GetModuleHandle(null),
                0);
            if (this.hook == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not start shortcut capture.");
            }

            this.timer.Interval = this.firstStrokeTimeoutMilliseconds;
            this.timer.Start();
        }

        public void Dispose()
        {
            if (this.isDisposed)
            {
                return;
            }

            this.isDisposed = true;
            this.timer.Stop();
            this.timer.Dispose();
            this.dispatchTimer.Stop();
            this.dispatchTimer.Dispose();
            this.pendingEvents.Clear();
            ReleaseHook();
        }

        private IntPtr HandleKeyboardMessage(int code, IntPtr message, IntPtr data)
        {
            if (code < 0 || this.isComplete)
            {
                return NativeMethods.CallNextHookEx(this.hook, code, message, data);
            }

            int messageId = message.ToInt32();
            bool keyDown = messageId == NativeMethods.WM_KEYDOWN
                || messageId == NativeMethods.WM_SYSKEYDOWN;
            bool keyUp = messageId == NativeMethods.WM_KEYUP
                || messageId == NativeMethods.WM_SYSKEYUP;
            if (!keyDown && !keyUp)
            {
                return NativeMethods.CallNextHookEx(this.hook, code, message, data);
            }

            LowLevelKeyboardData keyboardData = (LowLevelKeyboardData)Marshal.PtrToStructure(
                data,
                typeof(LowLevelKeyboardData));
            Keys key = (Keys)keyboardData.VirtualKeyCode;

            uint modifier = GetModifier(key);
            if (modifier != 0)
            {
                if (keyDown)
                {
                    this.activeModifiers |= modifier;
                }
                else
                {
                    this.activeModifiers &= ~modifier;
                }

                return new IntPtr(1);
            }

            if (keyUp)
            {
                this.pressedKeys.Remove(key);
                return new IntPtr(1);
            }

            if (!this.pressedKeys.Add(key))
            {
                return new IntPtr(1);
            }

            if (key == Keys.Escape && this.activeModifiers == 0)
            {
                Complete(null, true);
                return new IntPtr(1);
            }

            ShortcutBinding stroke = new ShortcutBinding
            {
                Modifiers = this.activeModifiers,
                Key = key
            };
            if (!stroke.IsValid())
            {
                return new IntPtr(1);
            }

            if (this.firstStroke == null)
            {
                BeginChordWait(stroke);
            }
            else
            {
                Complete(this.firstStroke.WithSecondStroke(stroke), false);
            }

            return new IntPtr(1);
        }

        private void BeginChordWait(ShortcutBinding stroke)
        {
            this.firstStroke = stroke;
            this.timer.Stop();
            this.chordDeadlineTick = unchecked(Environment.TickCount + this.secondStrokeWindowMilliseconds);
            this.reportedSecondsRemaining = GetSecondsRemaining();
            this.timer.Interval = ChordTickIntervalMilliseconds;
            this.timer.Start();
            AnnounceChordWait();
        }

        private void AnnounceChordWait()
        {
            ShortcutBinding announced = this.firstStroke.Clone();
            int secondsRemaining = this.reportedSecondsRemaining;
            Post(delegate
            {
                EventHandler<ShortcutChordWaitEventArgs> handler = this.ChordWaitChanged;
                if (handler != null)
                {
                    handler(this, new ShortcutChordWaitEventArgs(announced, secondsRemaining));
                }
            });
        }

        private int GetSecondsRemaining()
        {
            int remaining = unchecked(this.chordDeadlineTick - Environment.TickCount);
            if (remaining <= 0)
            {
                return 0;
            }

            return (int)Math.Ceiling(remaining / 1000.0);
        }

        private void HandleTimer(object sender, EventArgs e)
        {
            if (this.firstStroke == null)
            {
                Complete(null, true);
                return;
            }

            int secondsRemaining = GetSecondsRemaining();
            if (secondsRemaining <= 0)
            {
                Complete(this.firstStroke.Clone(), false);
                return;
            }

            if (secondsRemaining != this.reportedSecondsRemaining)
            {
                this.reportedSecondsRemaining = secondsRemaining;
                AnnounceChordWait();
            }
        }

        private void Complete(ShortcutBinding binding, bool canceled)
        {
            if (this.isComplete)
            {
                return;
            }

            this.isComplete = true;
            this.timer.Stop();
            ReleaseHook();

            Post(delegate
            {
                EventHandler<ShortcutCaptureCompletedEventArgs> handler = this.Completed;
                if (handler != null)
                {
                    handler(this, new ShortcutCaptureCompletedEventArgs(binding, canceled));
                }
            });
        }

        /// <summary>
        /// Queues work for the message loop. Nothing that touches the tray, its menu, or a modal
        /// dialog may run inside the low-level keyboard hook: Windows silently removes a hook that
        /// takes too long, which loses the second stroke of a chord.
        /// </summary>
        private void Post(Action action)
        {
            if (this.isDisposed)
            {
                return;
            }

            this.pendingEvents.Enqueue(action);
            this.dispatchTimer.Start();
        }

        private void HandleDispatch(object sender, EventArgs e)
        {
            while (!this.isDisposed && this.pendingEvents.Count > 0)
            {
                Action action = this.pendingEvents.Dequeue();
                action();
            }

            if (!this.isDisposed)
            {
                this.dispatchTimer.Stop();
            }
        }

        private void ReleaseHook()
        {
            if (this.hook == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.UnhookWindowsHookEx(this.hook);
            this.hook = IntPtr.Zero;
        }

        private static uint GetModifier(Keys key)
        {
            if (key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey)
            {
                return NativeMethods.MOD_CONTROL;
            }

            if (key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu)
            {
                return NativeMethods.MOD_ALT;
            }

            if (key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey)
            {
                return NativeMethods.MOD_SHIFT;
            }

            if (key == Keys.LWin || key == Keys.RWin)
            {
                return NativeMethods.MOD_WIN;
            }

            return 0;
        }
    }
}
