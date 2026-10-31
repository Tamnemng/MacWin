using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MacWin.Native;
using MacWin.Services;

namespace MacWin.UI
{
    public enum WindowButtonsMode { Off, Right, Left }

    /// <summary>
    /// Red / yellow / green buttons for other apps' windows. Windows can't restyle another app's title bar,
    /// so a tiny overlay is attached (as an owned window) to the front window's caption: it follows the window,
    /// matches the caption colour and sends the real close / minimise / zoom commands.
    /// </summary>
    public static class WindowButtons
    {
        static Overlay _overlay;
        static IntPtr _target;
        static IntPtr _locHook;
        static WinEventProc _locProc;
        static DispatcherTimer _timer;

        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);

        const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        const long WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;

        static WindowButtonsMode Mode => App.Settings.WindowButtons;

        public static void Start()
        {
            WindowTracker.ForegroundChanged += Update;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
            _timer.Tick += (_, _) => { if (_target != IntPtr.Zero) Update(); };
            Apply();
        }

        public static void Apply()
        {
            if (Mode == WindowButtonsMode.Off)
            {
                _timer?.Stop();
                Unhook();
                Detach();
                return;
            }
            _timer?.Start();
            Update();
        }

        public static void Stop()
        {
            _timer?.Stop();
            Unhook();
            Detach();
            _overlay?.Close();
            _overlay = null;
        }

        static void Unhook()
        {
            if (_locHook != IntPtr.Zero) UnhookWinEvent(_locHook);
            _locHook = IntPtr.Zero;
        }

        static bool Eligible(IntPtr h)
        {
            if (h == IntPtr.Zero || !Win32.IsWindow(h) || !Win32.IsWindowVisible(h) || Win32.IsIconic(h) || Win32.IsCloaked(h)) return false;
            Win32.GetWindowThreadProcessId(h, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;
            if (Win32.GetClass(h) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
            long style = Win32.GetStyle(h), ex = Win32.GetExStyle(h);
            if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 || (style & Win32.WS_CHILD) != 0) return false;
            if ((style & WS_SYSMENU) == 0) return false;
            // only windows that really have minimise/maximise buttons (skips dialogs and message boxes)
            if ((style & (WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) == 0) return false;
            if (App.FullscreenAppActive && Win32.IsOnPrimaryMonitor(h)) return false;
            if (!Win32.GetVisibleRect(h, out var r) || r.Width < 320 || r.Height < 120) return false;
            return true;
        }

        public static void Update()
        {
            if (Mode == WindowButtonsMode.Off) return;
            var fg = WindowTracker.LastExternalForeground;
            if (!Eligible(fg)) { Detach(); return; }
            if (fg != _target) Attach(fg);
            Position();
        }

        static void Attach(IntPtr h)
        {
            Detach();
            _target = h;
            if (_overlay == null)
            {
                _overlay = new Overlay();
                var created = _overlay;
                // Windows destroys owned windows together with their owner: just make a new one next time
                created.Closed += (_, _) => { if (_overlay == created) _overlay = null; };
            }
            try { if (!_overlay.IsVisible) _overlay.Show(); }
            catch { _overlay = null; _target = IntPtr.Zero; return; }
            // owned by the target: stays right above it in the z-order and minimises with it
            Win32.SetWindowLongPtr(Ui.Hwnd(_overlay), -8 /*GWLP_HWNDPARENT*/, h);
            Win32.GetWindowThreadProcessId(h, out uint pid);
            uint tid = Win32.GetWindowThreadProcessId(h, out _);
            _locProc ??= (hook, ev, hwnd, obj, child, t, time) => { if (hwnd == _target && obj == 0) Position(); };
            _locHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _locProc, pid, tid, 0);
        }

        static void Detach()
        {
            Unhook();
            _target = IntPtr.Zero;
            try
            {
                if (_overlay != null && _overlay.IsVisible)
                {
                    Win32.SetWindowLongPtr(Ui.Hwnd(_overlay), -8, IntPtr.Zero);
                    _overlay.Hide();
                }
            }
            catch { _overlay = null; }
        }

        static void Position()
        {
            var h = _target;
            if (h == IntPtr.Zero || _overlay == null) return;
            if (!Win32.IsWindow(h) || Win32.IsIconic(h) || !Win32.GetVisibleRect(h, out var r)) { Detach(); return; }
            double dpi = Math.Max(1, GetDpiForWindow(h)) / 96.0;
            bool max = Win32.IsZoomed(h);
            int top = r.Top + (max ? 0 : 1);
            int capH = (int)Math.Round(32 * dpi);
            int x, w;
            if (Mode == WindowButtonsMode.Right)
            {
                w = (int)Math.Round(138 * dpi);          // the three native caption buttons
                x = r.Right - w - (max ? 0 : 1);
            }
            else
            {
                w = (int)Math.Round(78 * dpi);
                x = r.Left + (max ? 0 : 1);
            }
            // caption colour just beside our area
            int sx = Mode == WindowButtonsMode.Right ? x - (int)(6 * dpi) : x + w + (int)(6 * dpi);
            IntPtr dc = Win32.GetDC(IntPtr.Zero);
            uint px = GetPixel(dc, sx, top + (int)(5 * dpi));
            Win32.ReleaseDC(IntPtr.Zero, dc);
            if (px != 0xFFFFFFFF) _overlay.SetCaptionColor(Color.FromRgb((byte)(px & 0xFF), (byte)((px >> 8) & 0xFF), (byte)((px >> 16) & 0xFF)));
            _overlay.Layout(Mode == WindowButtonsMode.Right);
            Win32.SetWindowPos(Ui.Hwnd(_overlay), IntPtr.Zero, x, top, w, capH, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }

        static void Command(int sc)
        {
            var h = _target;
            if (h == IntPtr.Zero) return;
            if (sc == Win32.SC_MAXIMIZE && Win32.IsZoomed(h)) sc = Win32.SC_RESTORE;
            Win32.PostMessage(h, Win32.WM_SYSCOMMAND, new IntPtr(sc), IntPtr.Zero);
        }

        /// <summary>The little overlay window holding the three buttons.</summary>
        sealed class Overlay : Window
        {
            readonly Border _bg = new();
            readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            readonly TextBlock[] _glyphs = new TextBlock[3];
            static readonly string[] Colors = { "#FF5F57", "#FEBC2E", "#28C840" };
            static readonly string[] Glyphs = { "", "", "" };
            static readonly int[] Commands = { Win32.SC_CLOSE, Win32.SC_MINIMIZE, Win32.SC_MAXIMIZE };

            public Overlay()
            {
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                ShowInTaskbar = false;
                ShowActivated = false;
                ResizeMode = ResizeMode.NoResize;
                Width = 80; Height = 30; Left = -32000; Top = -32000;
                for (int i = 0; i < 3; i++)
                {
                    int idx = i;
                    var g = new TextBlock
                    {
                        Text = Glyphs[i], FontFamily = Ui.IconFont, FontSize = 7.5, FontWeight = FontWeights.Bold, Opacity = 0,
                        Foreground = new SolidColorBrush(Color.FromArgb(160, 60, 0, 0)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    };
                    _glyphs[i] = g;
                    var c = (Color)ColorConverter.ConvertFromString(Colors[i]);
                    var dot = new Border
                    {
                        Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0), Child = g,
                        Background = new LinearGradientBrush(Color.FromRgb((byte)Math.Min(255, c.R + 18), (byte)Math.Min(255, c.G + 18), (byte)Math.Min(255, c.B + 18)), c, 90),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), BorderThickness = new Thickness(0.5)
                    };
                    dot.MouseLeftButtonDown += (_, e) => { e.Handled = true; dot.Opacity = 0.7; };
                    dot.MouseLeftButtonUp += (_, e) => { e.Handled = true; dot.Opacity = 1; Command(Commands[idx]); };
                    dot.MouseLeave += (_, _) => dot.Opacity = 1;
                    _dots.Children.Add(dot);
                }
                _dots.MouseEnter += (_, _) => { foreach (var g in _glyphs) g.Opacity = 1; };
                _dots.MouseLeave += (_, _) => { foreach (var g in _glyphs) g.Opacity = 0; };
                _bg.Child = _dots;
                Content = _bg;
                SourceInitialized += (_, _) => Backdrop.MakeToolWindow(this, noActivate: true);
            }

            public void SetCaptionColor(Color c)
            {
                if (_bg.Background is SolidColorBrush b && b.Color == c) return;
                _bg.Background = new SolidColorBrush(c);
            }

            public void Layout(bool right)
            {
                _dots.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                _dots.Margin = right ? new Thickness(0, 0, 16, 0) : new Thickness(13, 0, 0, 0);
            }
        }
    }
}
