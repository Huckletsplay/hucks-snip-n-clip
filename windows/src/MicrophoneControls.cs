using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace QSnipAndClip
{
    internal sealed partial class TrayApplicationContext
    {
        private ToolStripMenuItem CreateMicrophoneMenu()
        {
            ToolStripMenuItem menu = new ToolStripMenuItem("Microphone — System Default");
            List<MicrophoneDevice> devices;
            try { devices = MicrophoneDevices.Enumerate(); }
            catch { devices = new List<MicrophoneDevice>(); }
            bool available = devices.Exists(delegate(MicrophoneDevice device) { return device.Id == this.settings.MicrophoneDeviceId; });
            PersistentChoice defaultItem = new PersistentChoice("System Default");
            defaultItem.Checked = String.IsNullOrEmpty(this.settings.MicrophoneDeviceId) || !available;
            defaultItem.Click += delegate { SelectMicrophone(""); };
            menu.DropDownItems.Add(defaultItem);
            foreach (MicrophoneDevice device in devices)
            {
                string id = device.Id;
                PersistentChoice item = new PersistentChoice(device.Name);
                item.Checked = id == this.settings.MicrophoneDeviceId;
                if (item.Checked) menu.Text = "Microphone — " + device.Name;
                item.Click += delegate { SelectMicrophone(id); };
                menu.DropDownItems.Add(item);
            }
            if (!String.IsNullOrEmpty(this.settings.MicrophoneDeviceId) && !available)
                menu.Text = "Microphone — System Default (selected device unavailable)";
            return menu;
        }

        private void SelectMicrophone(string id)
        {
            string previous = this.settings.MicrophoneDeviceId;
            this.settings.MicrophoneDeviceId = id;
            try { this.settingsStore.Save(this.settings); RebuildContextMenu(); }
            catch (Exception ex)
            {
                this.settings.MicrophoneDeviceId = previous;
                CopyableDialog.Show(ex.Message, "Microphone Setting", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
