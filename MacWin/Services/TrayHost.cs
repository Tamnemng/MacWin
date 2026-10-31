using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacWin.Native;

namespace MacWin.Services
{
    public class TrayIcon
    {
        public uint HWnd, UID, CallbackMessage, Version;
        public Guid Guid;
        public ImageSource Icon;
        public string Tip = "";
        public bool Hidden;
        public string Owner;   // program file name ("Unikey.exe"), used for "Always show in menu bar"
        public string Key => Guid != Guid.Empty ? Guid.ToString() : $"{HWnd}:{UID}";
    }

    /// <summary>
    /// Shows the Windows notification-area ("tray") icons in the menu bar while the taskbar is hidden.
    ///
    /// Apps add their tray icons by sending WM_COPYDATA to the first window of class "Shell_TrayWnd".
    /// MacWin creates such a window (kept topmost so it is found first), reads the icons, and forwards every
    /// message unchanged to Explorer's real taskbar, so Windows keeps working exactly as before. Clicks on an
    /// icon are sent back to its app the same way the taskbar would. This is the approach used by
    /// alternative Windows shells (Cairo, RetroBar).
    /// </summary>
    public static class TrayHost
    {
        public static readonly List<TrayIcon> Icons = new();
        public static event Action Changed;

        delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

        // Shell tray data always uses 32-bit handle fields so 32- and 64-bit apps share one layout.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize;
            public uint hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public uint hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public uint hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHELLTRAYDATA { public int dwSignature; public uint dwMessage; public NOTIFYICONDATA nid; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string s);
        [DllImport("user32.dll")] static extern bool SendNotifyMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr info);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        const uint WM_COPYDATA = 0x004A;
        const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_STATE = 0x8, NIF_GUID = 0x20;
        const uint NIS_HIDDEN = 0x1;

        static IntPtr _hwnd;
        static WndProcDelegate _proc;
        static uint _taskbarCreated;
        static DispatcherTimer _timer;
        static IntPtr _explorerTray;
        static bool _changePending;

        public static bool Running => _hwnd != IntPtr.Zero;

        public static void Start()
        {
            if (_hwnd != IntPtr.Zero) return;
            try
            {
                _proc = WndProc;
                var inst = GetModuleHandle(null);
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                    hInstance = inst,
                    lpszClassName = "Shell_TrayWnd"
                };
                RegisterClassEx(ref wc); // fails harmlessly if already registered in this process
                _hwnd = CreateWindowEx(0x80 | 0x8 /*TOOLWINDOW|TOPMOST*/, "Shell_TrayWnd", "", 0x80000000 /*WS_POPUP*/, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero) return;
                _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
                ChangeWindowMessageFilterEx(_hwnd, WM_COPYDATA, 1, IntPtr.Zero);
                ChangeWindowMessageFilterEx(_hwnd, _taskbarCreated, 1, IntPtr.Zero);
                _explorerTray = Win32.FindExplorerTray();
                KeepOnTop();
                // ask every app to (re)add its tray icons: they now reach us first
                SendNotifyMessage(Win32.HWND_BROADCAST, _taskbarCreated, IntPtr.Zero, IntPtr.Zero);

                _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
                _timer.Tick += (_, _) => Maintain();
                _timer.Start();
            }
            catch (Exception e) { App.Log(e); }
        }

        public static void Stop()
        {
            _timer?.Stop();
            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            Icons.Clear();
            Changed?.Invoke();
        }

        static void KeepOnTop() =>
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        static void Maintain()
        {
            if (_hwnd == IntPtr.Zero) return;
            KeepOnTop();
            // Explorer restarted: apps re-register with whichever tray they find; ask them again
            var ex = Win32.FindExplorerTray();
            if (ex != _explorerTray)
            {
                _explorerTray = ex;
                SendNotifyMessage(Win32.HWND_BROADCAST, _taskbarCreated, IntPtr.Zero, IntPtr.Zero);
            }
            // drop icons whose app exited without removing them
            if (Icons.RemoveAll(i => !Win32.IsWindow(new IntPtr(unchecked((int)i.HWnd)))) > 0) Changed?.Invoke();
        }

        static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
        {
            if (msg != WM_COPYDATA) return DefWindowProc(hwnd, msg, w, l);
            IntPtr result = new IntPtr(1);
            try
            {
                var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(l);
                if (cds.dwData == (IntPtr)1 && cds.lpData != IntPtr.Zero) Handle(cds);
                // forward everything (tray, app-bar and icon-position messages) to Explorer's taskbar
                var ex = Win32.FindExplorerTray();
                if (ex != IntPtr.Zero)
                {
                    SendMessageTimeout(ex, WM_COPYDATA, w, l, 0x2 /*ABORTIFHUNG*/, 1500, out var r);
                    if (cds.dwData != (IntPtr)1) result = r; // app-bar / position answers come from Explorer
                }
            }
            catch (Exception e) { App.Log(e); }
            return result;
        }

        static void Handle(COPYDATASTRUCT cds)
        {
            // older apps send a shorter structure: copy into a zeroed buffer of the full size before reading
            int size = Marshal.SizeOf<SHELLTRAYDATA>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            SHELLTRAYDATA data;
            try
            {
                var zero = new byte[size];
                Marshal.Copy(zero, 0, buf, size);
                int n = Math.Min(cds.cbData, size);
                var tmp = new byte[n];
                Marshal.Copy(cds.lpData, tmp, 0, n);
                Marshal.Copy(tmp, 0, buf, n);
                data = Marshal.PtrToStructure<SHELLTRAYDATA>(buf);
            }
            finally { Marshal.FreeHGlobal(buf); }

            var nid = data.nid;
            bool byGuid = (nid.uFlags & NIF_GUID) != 0 && nid.guidItem != Guid.Empty;
            TrayIcon icon = Icons.FirstOrDefault(i => byGuid ? i.Guid == nid.guidItem : i.HWnd == nid.hWnd && i.UID == nid.uID);

            switch (data.dwMessage)
            {
                case NIM_ADD:
                case NIM_MODIFY:
                    if (icon == null)
                    {
                        icon = new TrayIcon { HWnd = nid.hWnd, UID = nid.uID, Guid = byGuid ? nid.guidItem : Guid.Empty };
                        try
                        {
                            Win32.GetWindowThreadProcessId(new IntPtr(unchecked((int)nid.hWnd)), out uint pid);
                            var path = Win32.GetProcessPath(pid);
                            icon.Owner = path != null ? System.IO.Path.GetFileName(path) : null;
                        }
                        catch { }
                        Icons.Add(icon);
                    }
                    if ((nid.uFlags & NIF_MESSAGE) != 0) icon.CallbackMessage = nid.uCallbackMessage;
                    if ((nid.uFlags & NIF_TIP) != 0) icon.Tip = nid.szTip ?? "";
                    if ((nid.uFlags & NIF_STATE) != 0 && (nid.dwStateMask & NIS_HIDDEN) != 0) icon.Hidden = (nid.dwState & NIS_HIDDEN) != 0;
                    if ((nid.uFlags & NIF_ICON) != 0 && nid.hIcon != 0) icon.Icon = ToImage(nid.hIcon) ?? icon.Icon;
                    if (icon.HWnd == 0) icon.HWnd = nid.hWnd;
                    break;
                case NIM_DELETE:
                    if (icon != null) Icons.Remove(icon);
                    break;
                case NIM_SETVERSION:
                    if (icon != null) icon.Version = nid.uVersion;
                    break;
                default:
                    return;
            }
            // many apps update their icon in bursts (animated icons): coalesce UI refreshes
            if (_changePending) return;
            _changePending = true;
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _changePending = false; Changed?.Invoke(); });
        }

        static ImageSource ToImage(uint hIcon)
        {
            try
            {
                // copy right away: the app may destroy the icon handle afterwards
                var src = Imaging.CreateBitmapSourceFromHIcon(new IntPtr(unchecked((int)hIcon)), Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ clicks

        const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203,
            WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_CONTEXTMENU = 0x7B, NIN_SELECT = 0x400;

        /// <summary>Delivers a click to the icon's app exactly like the Windows taskbar does.</summary>
        public static void Click(TrayIcon i, bool right, bool doubleClick)
        {
            var hwnd = new IntPtr(unchecked((int)i.HWnd));
            if (!Win32.IsWindow(hwnd)) { Icons.Remove(i); Changed?.Invoke(); return; }
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            AllowSetForegroundWindow(pid); // let the app bring its window / menu to the front
            Win32.GetCursorPos(out var p);

            void Send(uint mouse)
            {
                IntPtr wp, lp;
                if (i.Version >= 4)
                {
                    wp = new IntPtr((p.X & 0xFFFF) | ((p.Y & 0xFFFF) << 16));
                    lp = new IntPtr((int)(mouse & 0xFFFF) | (int)((i.UID & 0xFFFF) << 16));
                }
                else
                {
                    wp = new IntPtr(unchecked((int)i.UID));
                    lp = new IntPtr((int)mouse);
                }
                Win32.PostMessage(hwnd, (int)i.CallbackMessage, wp, lp);
            }

            if (i.CallbackMessage == 0) return;
            if (right)
            {
                Send(WM_RBUTTONDOWN);
                Send(WM_RBUTTONUP);
                if (i.Version >= 3) Send(WM_CONTEXTMENU);
            }
            else if (doubleClick)
            {
                Send(WM_LBUTTONDBLCLK);
                Send(WM_LBUTTONUP);
            }
            else
            {
                Send(WM_LBUTTONDOWN);
                Send(WM_LBUTTONUP);
                if (i.Version >= 3) Send(NIN_SELECT);
            }
        }

        /// <summary>Hover: some apps refresh their tooltip on mouse move.</summary>
        public static void Hover(TrayIcon i)
        {
            if (i.CallbackMessage == 0) return;
            var hwnd = new IntPtr(unchecked((int)i.HWnd));
            Win32.GetCursorPos(out var p);
            IntPtr wp = i.Version >= 4 ? new IntPtr((p.X & 0xFFFF) | ((p.Y & 0xFFFF) << 16)) : new IntPtr(unchecked((int)i.UID));
            IntPtr lp = i.Version >= 4 ? new IntPtr((int)WM_MOUSEMOVE | (int)((i.UID & 0xFFFF) << 16)) : new IntPtr((int)WM_MOUSEMOVE);
            Win32.PostMessage(hwnd, (int)i.CallbackMessage, wp, lp);
        }
    }
}
