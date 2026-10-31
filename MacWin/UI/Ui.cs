using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MacWin.Native;
using MacWin.Services;

namespace MacWin.UI
{
    /// <summary>Small helpers for building UI in code with theme-aware brushes.</summary>
    public static class Ui
    {
        public static FontFamily Font => (FontFamily)Application.Current.Resources["UiFont"];
        public static FontFamily DisplayFont => (FontFamily)Application.Current.Resources["DisplayFont"];
        public static FontFamily IconFont => (FontFamily)Application.Current.Resources["IconFont"];

        public static T Res<T>(this T el, DependencyProperty dp, string key) where T : FrameworkElement
        {
            el.SetResourceReference(dp, key);
            return el;
        }

        public static TextBlock Text(string s, double size = 13, FontWeight? weight = null, string brush = "Text.Primary")
        {
            var t = new TextBlock { Text = s, FontSize = size, FontFamily = Font, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
            if (brush != null) t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        public static TextBlock Glyph(string g, double size = 14, string brush = "Text.Primary")
        {
            var t = new TextBlock { Text = g, FontSize = size, FontFamily = IconFont, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            if (brush != null) t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        public static Style Style(string key) => (Style)Application.Current.Resources[key];

        public static double Scale(Visual v)
        {
            try { return VisualTreeHelper.GetDpi(v).DpiScaleX; } catch { return 1.0; }
        }

        public static IntPtr Hwnd(Window w) => new WindowInteropHelper(w).Handle;

        public static Duration Ms(double ms) => new(TimeSpan.FromMilliseconds(App.Settings.ReduceMotion ? 1 : ms));

        public static void Animate(DependencyObject target, DependencyProperty dp, double to, double ms, IEasingFunction ease = null, Action done = null, double? from = null)
        {
            var a = new DoubleAnimation { To = to, Duration = Ms(ms), EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut } };
            if (from.HasValue) a.From = from.Value;
            if (done != null) a.Completed += (_, _) => done();
            if (target is IAnimatable an) an.BeginAnimation(dp, a);
        }

        public static void Stop(DependencyObject target, DependencyProperty dp)
        {
            if (target is IAnimatable an) an.BeginAnimation(dp, null);
        }

        /// <summary>
        /// Gives a panel border the glass treatment: specular rim + top sheen (both theme resources,
        /// so they switch between classic and liquid glass live).
        /// </summary>
        public static Border Glassify(Border b)
        {
            b.SetResourceReference(Border.BorderBrushProperty, "Glass.Frame");
            b.SetResourceReference(Border.BorderThicknessProperty, "Glass.Thickness");
            var content = b.Child;
            b.Child = null;
            var grid = new Grid();
            var sheen = new Border { IsHitTestVisible = false, Margin = new Thickness(-b.Padding.Left, -b.Padding.Top, -b.Padding.Right, -b.Padding.Bottom) };
            sheen.SetResourceReference(Border.BackgroundProperty, "Glass.Sheen");
            var cr = b.CornerRadius;
            sheen.CornerRadius = new CornerRadius(Math.Max(0, cr.TopLeft - 1), Math.Max(0, cr.TopRight - 1), Math.Max(0, cr.BottomRight - 1), Math.Max(0, cr.BottomLeft - 1));
            grid.Children.Add(sheen);
            if (content != null) grid.Children.Add(content);
            b.Child = grid;
            return b;
        }

        /// <summary>Brings one of our own windows to the foreground (needed when opened from a global hotkey).</summary>
        public static void ForceActivate(Window w)
        {
            w.Activate();
            var h = Hwnd(w);
            if (Win32.GetForegroundWindow() != h) Input.Activate(h);
            w.Focus();
        }
    }

    /// <summary>Builders for macOS-style menus.</summary>
    public static class Menus
    {
        public static MenuItem Item(string header, Action click, string gesture = null, bool enabled = true, bool isChecked = false)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled, IsChecked = isChecked };
            if (click != null) mi.Click += (_, _) => click();
            return mi;
        }

        public static MenuItem Header(string text)
        {
            var mi = new MenuItem { Header = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold }, IsEnabled = false };
            return mi;
        }

        public static MenuItem Sub(string header, params object[] items)
        {
            var mi = new MenuItem { Header = header };
            foreach (var i in items) mi.Items.Add(i);
            return mi;
        }

        public static Separator Sep() => new();

        public static ContextMenu Build(params object[] items)
        {
            var cm = new ContextMenu();
            foreach (var i in items) if (i != null) cm.Items.Add(i);
            return cm;
        }
    }

    /// <summary>
    /// Base for frosted floating panels (Spotlight, Control Center, Launchpad…):
    /// borderless, blurred backdrop, rounded clip, hidden from Alt+Tab.
    /// </summary>
    public class PanelWindow : Window
    {
        readonly double _radius;
        readonly bool _noActivate;
        readonly byte _tintAlpha;

        public PanelWindow(double radius, bool noActivate = false, byte tintAlpha = 0x30)
        {
            _radius = radius;
            _noActivate = noActivate;
            _tintAlpha = tintAlpha;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowActivated = !noActivate;
            FontFamily = Ui.Font;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            // Rectangular panels (menu bar, Launchpad) use the live DWM blur. Rounded panels must not: DWM always
            // blurs the full window rectangle, which showed as square corners. They get a blurred snapshot instead.
            SourceInitialized += (_, _) =>
            {
                Backdrop.MakeToolWindow(this, _noActivate);
                ApplyBlur();
            };
            ThemeManager.Changed += ApplyBlur;
            Closed += (_, _) => ThemeManager.Changed -= ApplyBlur;
        }

        public void ApplyBlur()
        {
            var h = Ui.Hwnd(this);
            if (h == IntPtr.Zero) return;
            if (_radius > 0) Backdrop.Disable(h);
            else Backdrop.Enable(h, ThemeManager.BlurTint(_tintAlpha));
        }

        /// <summary>
        /// Captures and blurs what is behind the panel into <paramref name="frame"/>'s backdrop. Call it while the
        /// panel is still fully transparent (right after Show with Opacity 0). <paramref name="extraHeightDip"/>
        /// covers panels that grow downwards later (Spotlight results).
        /// </summary>
        LiveBackdrop _live;
        ContinuousBorder _liveFrame; double _liveExtra; bool _liveApply, _liveCapturing;

        public void RefreshBackdrop(ContinuousBorder frame, double extraHeightDip = 0, bool applyToFrame = true)
        {
            if (!App.Settings.RealBlur) { if (frame != null) frame.Backdrop = null; Snapshot = null; SnapshotChanged?.Invoke(); _live?.Stop(); return; }
            _liveFrame = frame; _liveExtra = extraHeightDip; _liveApply = applyToFrame;
            try
            {
                UpdateLayout();
                var h = Ui.Hwnd(this);
                if (h == IntPtr.Zero || !Win32.GetWindowRect(h, out var r)) return;
                double s = Ui.Scale(this);
                // liquid glass is clearer: less blur, the lens shader does the rest
                double clarity = App.Settings.GlassClarity;
                double blur = ThemeManager.IsLiquid ? 5 + (1 - clarity) * 22 : 26;
                var img = ScreenSnapshot.CaptureBlurred(r.Left, r.Top, r.Width, r.Height + (int)Math.Round(extraHeightDip * s), s, blur, _liveCapturing ? LiveBackdrop.Downscale : 2);
                Snapshot = img;
                if (frame != null && applyToFrame)
                    frame.Backdrop = img == null ? null : new ImageBrush(img) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
                SnapshotChanged?.Invoke();
                StartLive(r.Width * (double)r.Height);
            }
            catch (Exception e) { App.Log(e); }
        }

        /// <summary>Keeps the backdrop following whatever moves behind the panel while it is open.</summary>
        void StartLive(double areaPx)
        {
            if (!LiveBackdrop.Enabled) return;
            if (_live == null)
            {
                _live = new LiveBackdrop(this, () => Opacity > 0.5, () =>
                {
                    _liveCapturing = true;
                    try { RefreshBackdrop(_liveFrame, _liveExtra, _liveApply); } finally { _liveCapturing = false; }
                });
                IsVisibleChanged += (_, _) => { if (!IsVisible) _live.Stop(); };
            }
            _live.IntervalMs = Math.Clamp(areaPx / 4000, 90, 260);   // bigger windows refresh a little slower
            _live.Start();
        }

        DateTime _shownAt = DateTime.MinValue, _autoHiddenAt = DateTime.MinValue;

        /// <summary>
        /// Popovers close when another window takes focus — but not in the first moments after opening: a window
        /// grabbing focus right then (or the synthetic key used to win the foreground) used to close the panel
        /// the instant it appeared. Focus is taken back instead.
        /// </summary>
        protected void HideOnDeactivate(Action hide)
        {
            Deactivated += (_, _) =>
            {
                if (!IsVisible) return;
                if (!_open) return;
                if ((DateTime.Now - _shownAt).TotalMilliseconds < 450) return;   // settling: ignore the blip
                // only another app's window counts: focus moving to MacWin's own menu bar / Dock (the click that
                // toggles this panel) or to nothing at all must not close it
                var fg = Win32.GetForegroundWindow();
                Win32.GetWindowThreadProcessId(fg, out uint pid);
                if (pid == (uint)Environment.ProcessId) return;
                if (fg == IntPtr.Zero)
                {
                    // nobody has focus yet (e.g. a click on the desktop): decide once Windows has settled
                    var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
                    t.Tick += (_, _) =>
                    {
                        t.Stop();
                        if (!_open || !IsVisible) return;
                        var fg2 = Win32.GetForegroundWindow();
                        Win32.GetWindowThreadProcessId(fg2, out uint pid2);
                        if (fg2 != IntPtr.Zero && pid2 == (uint)Environment.ProcessId) return;
                        _autoHiddenAt = DateTime.Now;
                        hide();
                    };
                    t.Start();
                    return;
                }
                App.LogInfo($"{Title}: closed, focus went to {Win32.GetClass(fg)} pid {pid}");
                _autoHiddenAt = DateTime.Now;
                hide();
            };
        }

        /// <summary>Call when the panel starts opening.</summary>
        bool _open;

        /// <summary>Call when the panel starts opening.</summary>
        protected void MarkShown()
        {
            if (IsVisible && !_open) { App.LogInfo(Title + ": reopened while still fading out"); try { Hide(); } catch { } }
            _shownAt = DateTime.Now;
            _open = true;
            Ui.Stop(this, OpacityProperty);   // a hide animation still running must not finish (and hide) after reopening
        }

        /// <summary>Call when the panel starts closing; makes sure it really ends up hidden.</summary>
        protected void MarkHidden()
        {
            _open = false;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            t.Tick += (_, _) => { t.Stop(); if (!_open && IsVisible) { Ui.Stop(this, OpacityProperty); Opacity = 0; Hide(); } };
            t.Start();
        }

        /// <summary>True right after the panel closed itself because focus moved (e.g. onto its own menu bar icon):
        /// that same click must not open it again.</summary>
        public bool JustAutoHidden => (DateTime.Now - _autoHiddenAt).TotalMilliseconds < 350;

        /// <summary>Open (or opening) and not on its way out.</summary>
        public bool IsOpen => _open && IsVisible;

        /// <summary>What was behind the panel when it opened (blurred); glass tiles sample their own part of it.</summary>
        public ImageSource Snapshot { get; private set; }
        public event Action SnapshotChanged;
        /// <summary>Re-aligns glass tiles after an opening animation has moved them.</summary>
        public void RealignGlass() => SnapshotChanged?.Invoke();
    }

    /// <summary>Red / yellow / green window controls.</summary>
    public class TrafficLights : StackPanel
    {
        readonly Border[] _dots = new Border[3];
        readonly TextBlock[] _glyphs = new TextBlock[3];
        static readonly string[] Colors = { "#FF5F57", "#FEBC2E", "#28C840" };
        static readonly string[] Glyphs = { "", "", "" };
        public bool CanZoom { get; set; }
        public bool CanMinimize { get; set; } = true;

        public TrafficLights(Window w)
        {
            Orientation = Orientation.Horizontal;
            Background = Brushes.Transparent;
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var g = new TextBlock
                {
                    Text = Glyphs[i], FontFamily = Ui.IconFont, FontSize = 7, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0
                };
                var dot = new Border
                {
                    Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0),
                    BorderThickness = new Thickness(0.5), BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
                    Child = g, Cursor = Cursors.Arrow
                };
                dot.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (idx == 0) w.Close();
                    else if (idx == 1 && CanMinimize) w.WindowState = WindowState.Minimized;
                    else if (idx == 2 && CanZoom) w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                };
                dot.MouseLeftButtonDown += (_, e) => e.Handled = true;
                _dots[i] = dot;
                _glyphs[i] = g;
                Children.Add(dot);
            }
            MouseEnter += (_, _) => { foreach (var g in _glyphs) g.Opacity = 1; };
            MouseLeave += (_, _) => { foreach (var g in _glyphs) g.Opacity = 0; };
            w.Activated += (_, _) => Paint(true);
            w.Deactivated += (_, _) => Paint(false);
            Loaded += (_, _) => Paint(w.IsActive);
        }

        void Paint(bool active)
        {
            for (int i = 0; i < 3; i++)
            {
                bool enabled = i == 0 || (i == 1 && CanMinimize) || (i == 2 && CanZoom);
                string c = active && enabled ? Colors[i] : (ThemeManager.IsDark ? "#4D4D50" : "#D3D3D6");
                _dots[i].Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c));
            }
        }
    }

    /// <summary>
    /// Base for app-style windows (Settings, About, Force Quit, Setup): frosted, rounded, traffic lights, drag to move.
    /// </summary>
    public class MacWindow : PanelWindow
    {
        protected readonly Grid Root = new();
        protected readonly TrafficLights Lights;

        /// <summary>Window corner radius for the current style (larger for liquid glass).</summary>
        protected static double Radius => ThemeManager.PanelRadius(11);

        public MacWindow(double width, double height) : base(Radius, false, 0x50)
        {
            Width = width;
            Height = height;
            Topmost = false;
            ShowInTaskbar = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var frame = ContinuousBorder.Glass(Radius);
            frame.Child = Root;
            frame.ClipContent = true;
            Content = frame;
            Lights = new TrafficLights(this) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 14, 0, 0) };
            Panel.SetZIndex(Lights, 100);
            Root.Children.Add(Lights);
            MouseLeftButtonDown += (_, e) =>
            {
                // drag only from the title-bar strip, like a native window
                if (e.ButtonState == MouseButtonState.Pressed && e.GetPosition(this).Y < 44 && e.OriginalSource is not TextBox)
                {
                    var before = new Point(Left, Top);
                    try { DragMove(); } catch { }
                    if (Math.Abs(Left - before.X) + Math.Abs(Top - before.Y) > 4) RecaptureBackdrop(frame);
                }
            };
            KeyDown += (_, e) => { if (e.Key == Key.Escape && CloseOnEscape) Close(); };
            SourceInitialized += (_, _) =>
            {
                // show in Alt+Tab like a normal app window
                var h = Ui.Hwnd(this);
                long ex = Win32.GetExStyle(h) & ~Win32.WS_EX_TOOLWINDOW;
                Win32.SetWindowLongPtr(h, Win32.GWL_EXSTYLE, new IntPtr(ex | Win32.WS_EX_APPWINDOW));
            };
            Opacity = 0;
            Loaded += (_, _) =>
            {
                RefreshBackdrop(frame);
                var st = new ScaleTransform(0.96, 0.96, Width / 2, Height / 2);
                Root.RenderTransform = st;
                Ui.Animate(this, OpacityProperty, 1, 180);
                Ui.Animate(st, ScaleTransform.ScaleXProperty, 1, 220);
                Ui.Animate(st, ScaleTransform.ScaleYProperty, 1, 220);
            };
        }

        protected bool CloseOnEscape { get; set; } = true;

        /// <summary>After the window was dragged somewhere else, take a fresh snapshot of what's behind it.</summary>
        async void RecaptureBackdrop(ContinuousBorder frame)
        {
            if (!App.Settings.RealBlur) return;
            Ui.Stop(this, OpacityProperty);
            Opacity = 0;
            await System.Threading.Tasks.Task.Delay(45); // let the desktop repaint without us
            RefreshBackdrop(frame);
            Opacity = 1;
        }
    }
}
