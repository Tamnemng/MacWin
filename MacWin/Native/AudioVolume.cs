using System;
using System.Runtime.InteropServices;

namespace MacWin.Native
{
    /// <summary>Master volume of the default playback device (Core Audio).</summary>
    internal static class AudioVolume
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            int NotImpl1();
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr notify);
            int UnregisterControlChangeNotify(IntPtr notify);
            int GetChannelCount(out int count);
            int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
            int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            int GetMasterVolumeLevel(out float levelDb);
            int GetMasterVolumeLevelScalar(out float level);
            int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
            int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
            int GetChannelVolumeLevel(uint channel, out float levelDb);
            int GetChannelVolumeLevelScalar(uint channel, out float level);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        static IAudioEndpointVolume Endpoint()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var device) != 0 || device == null) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out object o);
            return o as IAudioEndpointVolume;
        }

        public static float Get()
        {
            try { var e = Endpoint(); if (e == null) return 0; e.GetMasterVolumeLevelScalar(out float v); return v; }
            catch { return 0; }
        }

        public static void Set(float v)
        {
            try { var e = Endpoint(); var g = Guid.Empty; e?.SetMasterVolumeLevelScalar(Math.Clamp(v, 0f, 1f), ref g); }
            catch { }
        }

        public static bool GetMute()
        {
            try { var e = Endpoint(); if (e == null) return false; e.GetMute(out bool m); return m; }
            catch { return false; }
        }

        public static void SetMute(bool m)
        {
            try { var e = Endpoint(); var g = Guid.Empty; e?.SetMute(m, ref g); }
            catch { }
        }
    }
}
