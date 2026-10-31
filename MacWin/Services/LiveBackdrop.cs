using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MacWin.Services
{
    /// <summary>
    /// Keeps a glass surface in sync with what is really behind it. A few times a second the window is excluded
    /// from screen capture (Windows then composes the capture without it), the area behind it is grabbed and
    /// blurred, and the window is made capturable again — so screenshots still show it. The screen itself never
    /// changes during the swap. Large areas refresh a little slower so the cost stays small.
    /// </summary>
    public sealed class LiveBackdrop
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;

        readonly Window _window;
        readonly Func<bool> _wanted;     // visible, not animating out, …
        readonly Action _capture;        // grabs + applies the backdrop (runs while the window is excluded)
        readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
        bool _excluded, _running, _lastMode;
        static bool? _supported;

        public static bool Enabled => App.Settings is { RealBlur: true, LiveGlass: true } && _supported != false;
        /// <summary>Every frame: the window stays excluded from capture while live (so it is missing from screenshots).</summary>
        public static bool Smooth => App.Settings.LiveGlassSmooth;
        /// <summary>Capture downscale for live frames: coarser when refreshing every frame.</summary>
        public static int Downscale => Smooth ? 4 : 2;

        public LiveBackdrop(Window window, Func<bool> wanted, Action capture)
        {
            _window = window;
            _wanted = wanted;
            _capture = capture;
            _timer.Tick += Tick;
            window.Closed += (_, _) => Stop();
        }

        /// <summary>Refresh period from the captured area: ~10 fps for a Dock or popover, ~4 fps for a big window.</summary>
        public double IntervalMs { get; set; } = 100;

        public void Start()
        {
            if (_running || !Enabled) return;
            _running = true;
            _timer.Interval = TimeSpan.FromMilliseconds(Smooth ? 15 : IntervalMs);
            _timer.Start();
        }

        public void Stop()
        {
            _running = false;
            _timer.Stop();
            Exclude(false);
        }

        void Tick(object sender, EventArgs e)
        {
            if (!_running) return;
            if (!Enabled) { Stop(); return; }
            if (_lastMode != Smooth) { _lastMode = Smooth; _timer.Interval = TimeSpan.FromMilliseconds(Smooth ? 15 : IntervalMs); Exclude(false); }
            try
            {
                if (!_window.IsVisible || !_wanted())
                {
                    Exclude(false);
                    return;
                }
                if (Smooth)
                {
                    // every frame: stay excluded, grab, done (one compositor frame of latency)
                    if (!_excluded) { if (!Exclude(true)) Stop(); return; }   // grab from the next frame on
                    _capture();
                    return;
                }
                if (!_excluded)
                {
                    // phase 1: drop out of the capture; the compositor needs a frame to apply it
                    if (!Exclude(true)) { Stop(); return; }
                    _timer.Interval = TimeSpan.FromMilliseconds(30);
                    return;
                }
                // phase 2: grab what is behind, then become capturable again (screenshots keep showing the window)
                _capture();
                Exclude(false);
                _timer.Interval = TimeSpan.FromMilliseconds(IntervalMs);
                _lastMode = false;
            }
            catch (Exception ex) { App.Log(ex); Stop(); }
        }

        bool Exclude(bool on)
        {
            if (_excluded == on) return true;
            var h = new WindowInteropHelper(_window).Handle;
            if (h == IntPtr.Zero) return false;
            bool ok = SetWindowDisplayAffinity(h, on ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
            if (on && !ok)
            {
                // Windows before 10 2004 has no capture exclusion: live glass isn't possible there
                _supported = false;
                App.LogInfo("Live glass unavailable (SetWindowDisplayAffinity failed)");
                return false;
            }
            if (on) _supported = true;
            _excluded = on && ok;
            return ok;
        }
    }
}
