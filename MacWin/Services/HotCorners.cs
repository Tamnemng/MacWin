using System;
using System.Windows.Threading;
using MacWin.Native;

namespace MacWin.Services
{
    /// <summary>Hot corners: park the pointer in a screen corner to trigger an action.</summary>
    public static class HotCorners
    {
        static DispatcherTimer _timer;
        static int _corner = -1;
        static DateTime _enteredAt;
        static bool _fired;

        public static void Start()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(90) };
            _timer.Tick += Tick;
            _timer.Start();
        }

        public static void Stop() => _timer?.Stop();

        static void Tick(object sender, EventArgs e)
        {
            var s = App.Settings;
            if (s.CornerTopLeft == CornerAction.None && s.CornerTopRight == CornerAction.None &&
                s.CornerBottomLeft == CornerAction.None && s.CornerBottomRight == CornerAction.None) return;
            if (App.FullscreenAppActive) return;
            if (!Win32.GetCursorPos(out var p)) return;
            int w = Win32.GetSystemMetrics(Win32.SM_CXSCREEN) - 1, h = Win32.GetSystemMetrics(Win32.SM_CYSCREEN) - 1;
            const int t = 1;
            int corner = p.X <= t && p.Y <= t ? 0 : p.X >= w - t && p.Y <= t ? 1 : p.X <= t && p.Y >= h - t ? 2 : p.X >= w - t && p.Y >= h - t ? 3 : -1;

            if (corner == -1)
            {
                if (_corner != -1 && (Math.Min(p.X, w - p.X) > 40 || Math.Min(p.Y, h - p.Y) > 40)) { _corner = -1; _fired = false; }
                return;
            }
            if (corner != _corner) { _corner = corner; _enteredAt = DateTime.Now; _fired = false; return; }
            if (_fired || (DateTime.Now - _enteredAt).TotalMilliseconds < 180) return;
            if ((Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0) return; // dragging
            _fired = true;
            var action = corner switch { 0 => s.CornerTopLeft, 1 => s.CornerTopRight, 2 => s.CornerBottomLeft, _ => s.CornerBottomRight };
            Shell.Run(action);
        }

        public static string Describe(CornerAction a) => a switch
        {
            CornerAction.None => "—",
            CornerAction.MissionControl => "Mission Control",
            CornerAction.Desktop => "Desktop",
            CornerAction.Launchpad => "Launchpad",
            CornerAction.Spotlight => "Spotlight",
            CornerAction.NotificationCenter => "Notification Center",
            CornerAction.ControlCenter => "Control Center",
            CornerAction.LockScreen => "Lock Screen",
            _ => a.ToString()
        };
    }
}
