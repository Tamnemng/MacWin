using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MacWin.Native
{
    public record AudioOutput(string Id, string Name, bool IsDefault);

    /// <summary>Lists playback devices and switches the default one (what the Sound section of Control Center shows).</summary>
    internal static class AudioDevices
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }
        [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p, p2; }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint c);
            [PreserveSig] int GetAt(uint i, out PROPERTYKEY k);
            [PreserveSig] int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
        }

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

        // Windows' own (undocumented but long-stable) interface used by the Sound settings to change the default device.
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class CPolicyConfigClient { }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat(string id, IntPtr fmt);
            [PreserveSig] int GetDeviceFormat(string id, int def, IntPtr fmt);
            [PreserveSig] int ResetDeviceFormat(string id);
            [PreserveSig] int SetDeviceFormat(string id, IntPtr a, IntPtr b);
            [PreserveSig] int GetProcessingPeriod(string id, int def, IntPtr a, IntPtr b);
            [PreserveSig] int SetProcessingPeriod(string id, IntPtr a);
            [PreserveSig] int GetShareMode(string id, IntPtr mode);
            [PreserveSig] int SetShareMode(string id, IntPtr mode);
            [PreserveSig] int GetPropertyValue(string id, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue(string id, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility(string id, int visible);
        }

        public static List<AudioOutput> Outputs()
        {
            var list = new List<AudioOutput>();
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                string def = null;
                if (en.GetDefaultAudioEndpoint(0, 1, out var d) == 0 && d != null) d.GetId(out def);
                if (en.EnumAudioEndpoints(0 /*eRender*/, 1 /*ACTIVE*/, out var col) != 0 || col == null) return list;
                col.GetCount(out int n);
                for (int i = 0; i < n; i++)
                {
                    if (col.Item(i, out var dev) != 0 || dev == null) continue;
                    dev.GetId(out string id);
                    string name = id;
                    if (dev.OpenPropertyStore(0, out var store) == 0 && store != null)
                    {
                        var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 }; // friendly name
                        if (store.GetValue(ref key, out var pv) == 0)
                        {
                            if (pv.vt == 31) name = Marshal.PtrToStringUni(pv.p);
                            PropVariantClear(ref pv);
                        }
                    }
                    list.Add(new AudioOutput(id, name, id == def));
                }
            }
            catch { }
            return list;
        }

        public static bool SetDefault(string id)
        {
            try
            {
                var pc = (IPolicyConfig)new CPolicyConfigClient();
                for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint(id, role); // console, multimedia, communications
                return true;
            }
            catch { return false; }
        }
    }
}
