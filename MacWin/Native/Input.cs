using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MacWin.Native
{
    /// <summary>Synthesised keyboard input and robust window activation.</summary>
    internal static class Input
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);

        const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;

        /// <summary>Marker placed in dwExtraInfo so our own keyboard hook can ignore injected keys.</summary>
        public static readonly IntPtr Signature = new(0x4D414357); // "MACW"

        static bool IsExtended(ushort vk) =>
            vk is Win32.VK_LWIN or Win32.VK_RWIN or Win32.VK_LEFT or Win32.VK_RIGHT or Win32.VK_UP or Win32.VK_DOWN
                or Win32.VK_RCONTROL or Win32.VK_RMENU or 0x2D or 0x2E or 0x21 or 0x22 or 0x23 or 0x24;

        static INPUT Key(ushort vk, bool up) => new()
        {
            type = 1,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0),
                    extra = Signature
                }
            }
        };

        /// <summary>Press keys in order, then release in reverse order (a chord such as Ctrl+Shift+S).</summary>
        public static void Chord(params ushort[] vks)
        {
            var list = new List<INPUT>();
            foreach (var vk in vks) list.Add(Key(vk, false));
            for (int i = vks.Length - 1; i >= 0; i--) list.Add(Key(vks[i], true));
            SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
        }

        public static void KeyDown(ushort vk) => SendInput(1, new[] { Key(vk, false) }, Marshal.SizeOf<INPUT>());
        public static void KeyUp(ushort vk) => SendInput(1, new[] { Key(vk, true) }, Marshal.SizeOf<INPUT>());

        /// <summary>Brings a window to the foreground even when Windows' foreground lock would refuse.</summary>
        public static void Activate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            if (Win32.SetForegroundWindow(hwnd) && Win32.GetForegroundWindow() == hwnd) return;

            var fg = Win32.GetForegroundWindow();
            uint fgThread = Win32.GetWindowThreadProcessId(fg, out _);
            uint me = Win32.GetCurrentThreadId();
            bool attached = fgThread != me && Win32.AttachThreadInput(me, fgThread, true);
            try
            {
                // A synthetic Alt tap satisfies the "received last input" rule of SetForegroundWindow.
                KeyDown(Win32.VK_MENU);
                Win32.BringWindowToTop(hwnd);
                Win32.SetForegroundWindow(hwnd);
                KeyUp(Win32.VK_MENU);
            }
            finally
            {
                if (attached) Win32.AttachThreadInput(me, fgThread, false);
            }
        }
    }
}
