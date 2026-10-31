using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MacWin.Native
{
    public enum AppBarEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

    /// <summary>
    /// Registers a WPF window as a Windows "application desktop toolbar" so maximised windows
    /// do not cover it (the same mechanism the Windows taskbar uses).
    /// </summary>
    internal sealed class AppBar : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public Win32.RECT rc;
            public IntPtr lParam;
        }

        [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);

        public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_ACTIVATE = 6,
            ABM_WINDOWPOSCHANGED = 9, ABM_SETSTATE = 10;
        public const int ABN_STATECHANGE = 0, ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
        public const int ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;

        readonly Window _window;
        IntPtr _hwnd;
        uint _callbackMsg;
        bool _registered;
        AppBarEdge _edge;
        double _thicknessDip;
        HwndSource _source;

        public event Action<bool> FullscreenAppChanged;
        public bool IsRegistered => _registered;

        public AppBar(Window window) { _window = window; }

        public void Register(AppBarEdge edge, double thicknessDip)
        {
            _hwnd = new WindowInteropHelper(_window).EnsureHandle();
            _edge = edge;
            _thicknessDip = thicknessDip;
            if (!_registered)
            {
                _callbackMsg = RegisterWindowMessage("MacWinAppBarMessage");
                _source = HwndSource.FromHwnd(_hwnd);
                _source?.AddHook(WndProc);
                var abd = NewData();
                abd.uCallbackMessage = _callbackMsg;
                SHAppBarMessage(ABM_NEW, ref abd);
                _registered = true;
            }
            UpdatePosition();
        }

        public void Update(AppBarEdge edge, double thicknessDip)
        {
            _edge = edge;
            _thicknessDip = thicknessDip;
            if (_registered) UpdatePosition();
        }

        APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

        double Scale()
        {
            try { return VisualTreeHelper.GetDpi(_window).DpiScaleX; } catch { return 1.0; }
        }

        void UpdatePosition()
        {
            int sw = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
            int sh = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
            int t = (int)Math.Round(_thicknessDip * Scale());
            var abd = NewData();
            abd.uEdge = (uint)_edge;
            abd.rc = new Win32.RECT { Left = 0, Top = 0, Right = sw, Bottom = sh };
            SHAppBarMessage(ABM_QUERYPOS, ref abd);
            switch (_edge)
            {
                case AppBarEdge.Top: abd.rc.Bottom = abd.rc.Top + t; break;
                case AppBarEdge.Bottom: abd.rc.Top = abd.rc.Bottom - t; break;
                case AppBarEdge.Left: abd.rc.Right = abd.rc.Left + t; break;
                case AppBarEdge.Right: abd.rc.Left = abd.rc.Right - t; break;
            }
            SHAppBarMessage(ABM_SETPOS, ref abd);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_registered && msg == _callbackMsg)
            {
                switch (wParam.ToInt32())
                {
                    case ABN_POSCHANGED:
                        UpdatePosition();
                        break;
                    case ABN_FULLSCREENAPP:
                        FullscreenAppChanged?.Invoke(lParam != IntPtr.Zero);
                        break;
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Unregister()
        {
            if (!_registered) return;
            var abd = NewData();
            SHAppBarMessage(ABM_REMOVE, ref abd);
            _registered = false;
            _source?.RemoveHook(WndProc);
        }

        public void Dispose() => Unregister();

        // ---------- Windows taskbar state ----------
        public static int GetTaskbarState()
        {
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = Win32.FindExplorerTray() };
            return (int)SHAppBarMessage(ABM_GETSTATE, ref abd).ToUInt32();
        }

        public static void SetTaskbarState(int state)
        {
            var abd = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                hWnd = Win32.FindExplorerTray(),
                lParam = new IntPtr(state)
            };
            SHAppBarMessage(ABM_SETSTATE, ref abd);
        }
    }
}
