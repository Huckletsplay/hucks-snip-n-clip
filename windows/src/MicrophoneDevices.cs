using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace QSnipAndClip
{
    internal sealed class MicrophoneDevice
    {
        internal string Id;
        internal string Name;
    }

    internal static class MicrophoneDevices
    {
        internal static List<MicrophoneDevice> Enumerate()
        {
            List<MicrophoneDevice> result = new List<MicrophoneDevice>();
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDeviceCollection devices = null;
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(1, 1, out devices));
                uint count; Marshal.ThrowExceptionForHR(devices.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device; Marshal.ThrowExceptionForHR(devices.Item(i, out device));
                    IAudioPropertyStore properties = null;
                    IntPtr id = IntPtr.Zero;
                    try
                    {
                        Marshal.ThrowExceptionForHR(device.GetId(out id));
                        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out properties));
                        AudioPropertyKey key = new AudioPropertyKey {
                            Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 14 };
                        AudioPropVariant value;
                        Marshal.ThrowExceptionForHR(properties.GetValue(ref key, out value));
                        try
                        {
                            string deviceId = Marshal.PtrToStringUni(id);
                            string name = value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : "Microphone";
                            result.Add(new MicrophoneDevice { Id = deviceId, Name = name });
                        }
                        finally { PropVariantClear(ref value); }
                    }
                    finally
                    {
                        if (id != IntPtr.Zero) Marshal.FreeCoTaskMem(id);
                        if (properties != null) Marshal.ReleaseComObject(properties);
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                if (devices != null) Marshal.ReleaseComObject(devices);
                Marshal.ReleaseComObject(enumerator);
            }
            return result;
        }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref AudioPropVariant value);
    }

    [StructLayout(LayoutKind.Sequential)] internal struct AudioPropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct AudioPropVariant
    { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [ComImport, Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    { [PreserveSig] int GetCount(out uint count); [PreserveSig] int Item(uint index, out IMMDevice device); }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out AudioPropertyKey key);
        [PreserveSig] int GetValue(ref AudioPropertyKey key, out AudioPropVariant value);
        [PreserveSig] int SetValue(ref AudioPropertyKey key, ref AudioPropVariant value);
        [PreserveSig] int Commit();
    }
}
