using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private readonly HashSet<int> registeredIds;
        private bool isDisposed;

        public HotkeyWindow()
        {
            this.registeredIds = new HashSet<int>();
            CreateHandle(new CreateParams());
        }

        public event EventHandler<HotkeyPressedEventArgs> HotkeyPressed;

        internal IntPtr WindowHandle { get { return this.Handle; } }

        public bool Register(int id, uint modifiers, Keys key)
        {
            if (this.isDisposed)
            {
                throw new ObjectDisposedException("HotkeyWindow");
            }

            if (this.registeredIds.Contains(id))
            {
                NativeMethods.UnregisterHotKey(this.Handle, id);
                this.registeredIds.Remove(id);
            }

            bool registered = NativeMethods.RegisterHotKey(
                this.Handle,
                id,
                modifiers,
                (uint)key);

            if (registered)
            {
                this.registeredIds.Add(id);
            }

            return registered;
        }

        public void Unregister(int id)
        {
            if (this.registeredIds.Contains(id))
            {
                NativeMethods.UnregisterHotKey(this.Handle, id);
                this.registeredIds.Remove(id);
            }
        }

        protected override void WndProc(ref Message message)
        {
            int id = message.WParam.ToInt32();
            if (message.Msg == NativeMethods.WM_HOTKEY && this.registeredIds.Contains(id))
            {
                EventHandler<HotkeyPressedEventArgs> handler = this.HotkeyPressed;
                if (handler != null)
                {
                    handler(this, new HotkeyPressedEventArgs(id));
                }
            }

            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (this.isDisposed)
            {
                return;
            }

            foreach (int id in this.registeredIds)
            {
                NativeMethods.UnregisterHotKey(this.Handle, id);
            }

            this.registeredIds.Clear();

            DestroyHandle();
            this.isDisposed = true;
        }
    }

    internal sealed class HotkeyPressedEventArgs : EventArgs
    {
        public HotkeyPressedEventArgs(int id)
        {
            this.Id = id;
        }

        public int Id { get; private set; }
    }
}
