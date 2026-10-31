using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacWin.Native;
using MacWin.Services;

namespace MacWin.UI
{
    /// <summary>The translucent bar across the top of the screen (registered as an app bar so windows maximise below it).</summary>
    public class MenuBarWindow : PanelWindow
    {
        public const double BarHeight = 24;   // macOS menu bar height on displays without a notch
        readonly AppBar _appBar;
        readonly StackPanel _left = new() { Orientation = Orientation.Horizontal };
        readonly StackPanel _right = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        readonly ToggleButton _appName;
        readonly TextBlock _clock = new() { VerticalAlignment = VerticalAlignment.Center };
        readonly ToggleButton _clockButton, _wifiButton, _batteryButton;
        readonly TextBlock _wifiGlyph;
        readonly TextBlock _batteryText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0), FontSize = 12 };
        readonly Border _batteryFill = new() { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(1.5) };
        readonly DispatcherTimer _clockTimer = new(DispatcherPriority.Background);
        readonly DispatcherTimer _menuHover = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        readonly List<ToggleButton> _titles = new();
        readonly StackPanel _trayPanel = new() { Orientation = Orientation.Horizontal };
        readonly TextBlock _statsText = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        readonly ToggleButton _statsButton;
        readonly ToggleButton _spotlightButton, _ccButton, _volumeButton, _diskButton;
        readonly TextBlock _volumeGlyph;
        readonly TextBlock _diskText = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        readonly StackPanel _center = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        readonly RotateTransform _chevronRotate = new();
        readonly DispatcherTimer _trayCollapse = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(12) };
        bool _trayExpanded;
        int _diskTick;
        readonly ToggleButton _inputButton;
        readonly TextBlock _inputText = new() { FontSize = 10, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 1) };
        readonly DispatcherTimer _inputTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(800) };
        IntPtr _lastHkl;
        ToggleButton _openTitle;
        ContextMenu _openMenu;

        public MenuBarWindow() : base(0, noActivate: true, tintAlpha: 0x40)
        {
            Title = "MacWin Menu Bar";
            Height = BarHeight;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            _appBar = new AppBar(this);
            _appBar.FullscreenAppChanged += _ => App.CheckFullscreen();

            var bg = new Border();
            bg.SetResourceReference(Border.BackgroundProperty, "Bar.Background");
            var grid = new Grid { Margin = new Thickness(8, 0, 8, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_right, 1);
            grid.Children.Add(_left);
            grid.Children.Add(_right);
            Grid.SetColumnSpan(_center, 2);   // centred clock (optional)
            grid.Children.Add(_center);
            bg.Child = grid;
            Content = bg;

            // ---- left: logo, app name, standard menus
            var logo = Title_(LogoGlyph(), LogoMenu);
            logo.Padding = new Thickness(10, 0, 10, 0);
            _appName = Title_(new TextBlock { FontWeight = FontWeights.Bold, Text = "Files" }, AppMenu);
            Title_("File", FileMenu);
            Title_("Edit", EditMenu);
            Title_("View", ViewMenu);
            Title_("Window", WindowMenu);
            Title_("Help", HelpMenu);

            // ---- right: status items
            _right.Children.Add(_trayPanel);                       // apps' tray icons (Unikey, Zalo, OneDrive…)
            _statsText.SetResourceReference(TextBlock.ForegroundProperty, "Bar.Foreground");
            _statsButton = StatusItem(_statsText, () => App.ToggleActivity(_statsButton)); // CPU / RAM
            SystemStats.Updated += UpdateStats;
            _inputButton = StatusItem(InputVisual(), ShowInputMenu); // keyboard language
            _diskButton = StatusItem(_diskText, () => App.ToggleActivity(_diskButton));
            _batteryButton = StatusItem(BatteryVisual(), () => Shell.Open("ms-settings:batterysaver"));
            _volumeGlyph = Ui.Glyph("\uE767", 14, "Bar.Foreground");
            _volumeButton = StatusItem(_volumeGlyph, () => App.ToggleControlCenter("sound"));
            _wifiGlyph = Ui.Glyph("", 14, "Bar.Foreground");
            _wifiButton = StatusItem(_wifiGlyph, () => App.ToggleControlCenter("wifi"));
            _spotlightButton = StatusItem(Ui.Glyph("", 13, "Bar.Foreground"), App.ToggleSpotlight);
            _ccButton = StatusItem(ControlCenterGlyph(), () => App.ToggleControlCenter());
            _clock.SetResourceReference(TextBlock.ForegroundProperty, "Bar.Foreground");
            _clockButton = StatusItem(_clock, Shell.NotificationCenter);
            _clockButton.Padding = new Thickness(8, 0, 4, 0);

            _clockTimer.Tick += (_, _) => UpdateClock();
            _inputTimer.Tick += (_, _) => UpdateInput();   // layout switches (Win+Space, Alt+Shift) raise no event for us
            _inputTimer.Start();
            TrayHost.Changed += RebuildTray;
            WindowTracker.ForegroundChanged += UpdateInput;
            _menuHover.Tick += MenuHoverTick;

            WindowTracker.ForegroundChanged += UpdateAppName;
            NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.BeginInvoke(UpdateNetwork);
            NetworkChange.NetworkAvailabilityChanged += (_, _) => Dispatcher.BeginInvoke(UpdateNetwork);

            Loaded += (_, _) =>
            {
                _appBar.Register(AppBarEdge.Top, BarHeight);
                PlaceAtTop();
                UpdateClock();
                UpdateNetwork();
                UpdateBattery();
                UpdateAppName();
                ApplySettings();
            };
            SizeChanged += (_, _) => { };
            Closed += (_, _) => _appBar.Unregister();
            SystemParameters.StaticPropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SystemParameters.PrimaryScreenWidth)) PlaceAtTop();
            };
        }

        void PlaceAtTop()
        {
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            _appBar.Update(AppBarEdge.Top, BarHeight);
        }

        public void ApplySettings()
        {
            var s = App.Settings;
            Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
            _spotlightButton.Visibility = V(s.ShowSpotlightIcon);
            _ccButton.Visibility = V(s.ShowControlCenterIcon);
            _wifiButton.Visibility = V(s.ShowWifiIcon);
            _volumeButton.Visibility = V(s.ShowVolumeIcon);
            _diskButton.Visibility = V(s.ShowDiskIcon);
            // clock in the middle of the bar or at the right end
            var host = s.ClockCentered ? _center : _right;
            if (_clockButton.Parent != host)
            {
                (_clockButton.Parent as Panel)?.Children.Remove(_clockButton);
                host.Children.Add(_clockButton);
            }
            UpdateClock();
            UpdateBattery();
            UpdateStats();
            UpdateDisk();
            UpdateInput();
            RebuildTray();
        }

        void UpdateDisk()
        {
            if (!App.Settings.ShowDiskIcon) return;
            try
            {
                var d = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
                _diskText.Text = $"{d.Name.TrimEnd('\\')} {SystemStats.Bytes(d.TotalFreeSpace)} free";
                _diskButton.ToolTip = $"{SystemStats.Bytes(d.TotalSize - d.TotalFreeSpace)} of {SystemStats.Bytes(d.TotalSize)} used";
            }
            catch { }
        }

        public void Unregister() => _appBar.Unregister();

        // ------------------------------------------------------------------ builders

        static FrameworkElement LogoGlyph()
        {
            // a logo the user placed on their own PC: %AppData%\MacWin\Icons\menubar-logo.png (or .ico)
            foreach (var ext in new[] { ".png", ".ico" })
            {
                var path = System.IO.Path.Combine(IconProvider.UserIconFolder, "menubar-logo" + ext);
                if (!System.IO.File.Exists(path)) continue;
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.DecodePixelHeight = 48;
                    bmp.UriSource = new Uri(path);
                    bmp.EndInit();
                    bmp.Freeze();
                    var img = new Image { Source = bmp, Height = 15, Stretch = Stretch.Uniform };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    return img;
                }
                catch { }
            }
            // An original mark: a rounded tile split into four panes.
            var g = new Grid { Width = 14, Height = 14 };
            var outer = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.6) };
            outer.SetResourceReference(Border.BorderBrushProperty, "Bar.Foreground");
            g.Children.Add(outer);
            var h = new Rectangle { Height = 1.6, VerticalAlignment = VerticalAlignment.Center };
            h.SetResourceReference(Shape.FillProperty, "Bar.Foreground");
            var v = new Rectangle { Width = 1.6, HorizontalAlignment = HorizontalAlignment.Center };
            v.SetResourceReference(Shape.FillProperty, "Bar.Foreground");
            g.Children.Add(h);
            g.Children.Add(v);
            return g;
        }

        static FrameworkElement ControlCenterGlyph()
        {
            var sp = new StackPanel { Width = 16, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 2; i++)
            {
                var pill = new Grid { Height = 6.5, Margin = new Thickness(0, i == 0 ? 0 : 2, 0, 0) };
                var track = new Border { CornerRadius = new CornerRadius(3.25), BorderThickness = new Thickness(1.2) };
                track.SetResourceReference(Border.BorderBrushProperty, "Bar.Foreground");
                var knob = new Ellipse { Width = 6.5, Height = 6.5, HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right };
                knob.SetResourceReference(Shape.FillProperty, "Bar.Foreground");
                pill.Children.Add(track);
                pill.Children.Add(knob);
                sp.Children.Add(pill);
            }
            return sp;
        }

        FrameworkElement BatteryVisual()
        {
            _batteryText.SetResourceReference(TextBlock.ForegroundProperty, "Bar.Foreground");
            var body = new Border { Width = 23, Height = 11.5, CornerRadius = new CornerRadius(3.2), BorderThickness = new Thickness(1), Child = _batteryFill };
            body.SetResourceReference(Border.BorderBrushProperty, "Text.Secondary");
            var nub = new Border { Width = 1.6, Height = 4, CornerRadius = new CornerRadius(0, 1, 1, 0), Margin = new Thickness(1, 0, 0, 0) };
            nub.SetResourceReference(Border.BackgroundProperty, "Text.Secondary");
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(_batteryText);
            sp.Children.Add(body);
            sp.Children.Add(nub);
            return sp;
        }

        ToggleButton Title_(object content, Func<ContextMenu> menu)
        {
            if (content is string s) content = new TextBlock { Text = s };
            var b = new ToggleButton { Content = content, Style = Ui.Style("BarButton"), Tag = menu, IsThreeState = false };
            b.Click += (_, _) =>
            {
                if (_openTitle == b) { CloseMenu(); return; }
                OpenMenu(b);
            };
            _titles.Add(b);
            _left.Children.Add(b);
            return b;
        }

        ToggleButton StatusItem(FrameworkElement content, Action click)
        {
            var b = new ToggleButton { Content = content, Style = Ui.Style("BarButton"), Padding = new Thickness(7, 0, 7, 0) };
            b.Click += (_, _) => { b.IsChecked = false; click(); };
            _right.Children.Add(b);
            return b;
        }

        // ------------------------------------------------------------------ menu handling

        void OpenMenu(ToggleButton title)
        {
            CloseMenu();
            if (title.Tag is not Func<ContextMenu> factory) return;
            var cm = factory();
            cm.PlacementTarget = title;
            cm.Placement = PlacementMode.Bottom;
            cm.HorizontalOffset = -12;
            cm.VerticalOffset = -2;
            cm.Closed += (_, _) =>
            {
                title.IsChecked = false;
                if (_openMenu == cm) { _openMenu = null; _openTitle = null; _menuHover.Stop(); }
            };
            _openMenu = cm;
            _openTitle = title;
            title.IsChecked = true;
            cm.IsOpen = true;
            _menuHover.Start();
        }

        void CloseMenu()
        {
            if (_openMenu != null) _openMenu.IsOpen = false;
            if (_openTitle != null) _openTitle.IsChecked = false;
            _openMenu = null;
            _openTitle = null;
            _menuHover.Stop();
        }

        /// <summary>While a menu is open, sliding the pointer over another title switches menus (as on macOS).</summary>
        void MenuHoverTick(object sender, EventArgs e)
        {
            if (_openMenu == null || !Win32.GetCursorPos(out var p)) return;
            foreach (var t in _titles)
            {
                if (t == _openTitle || !t.IsVisible) continue;
                var tl = t.PointToScreen(new Point(0, 0));
                var br = t.PointToScreen(new Point(t.ActualWidth, t.ActualHeight));
                if (p.X >= tl.X && p.X < br.X && p.Y >= tl.Y && p.Y < br.Y)
                {
                    OpenMenu(t);
                    return;
                }
            }
        }

        // ------------------------------------------------------------------ state

        string CurrentAppId => WindowTracker.ForegroundAppId;

        static string AppDisplayName(string id)
        {
            if (id == null || id == "desktop") return Roles.Binding("files")?.Label ?? "Files";
            var role = Roles.ForApp(id);
            if (role != null && !string.IsNullOrWhiteSpace(role.Label)) return role.Label;
            return AppCatalog.DisplayName(id);
        }

        void UpdateAppName()
        {
            if (_appName.Content is TextBlock tb) tb.Text = AppDisplayName(CurrentAppId);
        }

        void UpdateClock()
        {
            var s = App.Settings;
            var now = DateTime.Now;
            string time = now.ToString(s.Clock24h ? (s.ShowSeconds ? "HH:mm:ss" : "HH:mm") : (s.ShowSeconds ? "h:mm:ss tt" : "h:mm tt"), CultureInfo.CurrentCulture);
            _clock.Text = s.ShowDate ? $"{now.ToString("ddd d MMM", CultureInfo.CurrentCulture)}   {time}" : time;
            if (!string.IsNullOrWhiteSpace(s.ClockFormat))
            {
                // user's own pattern, e.g. "dddd dd/MM/yyyy  HH:mm"
                try { _clock.Text = now.ToString(s.ClockFormat, CultureInfo.CurrentCulture); } catch { }
            }
            // tick exactly on the next second/minute boundary: no wasted wake-ups
            var next = s.ShowSeconds ? 1000 - now.Millisecond : (60 - now.Second) * 1000 - now.Millisecond;
            _clockTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, next + 5));
            _clockTimer.Start();
            if (!s.ShowSeconds && now.Second == 0) UpdateBattery();
        }

        string _lastSsid;

        void UpdateNetwork()
        {
            try
            {
                var up = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .ToList();
                bool wifi = up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
                bool wired = up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet || n.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet);
                _wifiGlyph.Text = wifi ? "" : wired ? "" : "";
                _wifiGlyph.Opacity = wifi || wired ? 1 : 0.5;
            }
            catch { }
        }

        void UpdateBattery()
        {
            if (!Win32.GetSystemPowerStatus(out var ps) || ps.BatteryFlag == 128 || ps.BatteryFlag == 255)
            {
                _batteryButton.Visibility = Visibility.Collapsed;
                return;
            }
            _batteryButton.Visibility = Visibility.Visible;
            int pct = Math.Clamp((int)ps.BatteryLifePercent, 0, 100);
            _batteryText.Text = App.Settings.ShowBatteryPercent ? pct + "%" : "";
            _batteryFill.Width = Math.Max(1.5, 18.0 * pct / 100);
            bool charging = ps.ACLineStatus == 1;
            _batteryFill.Background = new SolidColorBrush(charging ? Color.FromRgb(0x34, 0xC7, 0x59) : pct <= 20 ? Color.FromRgb(0xFF, 0x3B, 0x30) : (ThemeManager.IsDark ? Colors.White : Color.FromRgb(0x1D, 0x1D, 0x1F)));
            _batteryButton.ToolTip = $"{pct}% {(charging ? "— charging" : "")}";
        }

        // ------------------------------------------------------------------ CPU / RAM

        void UpdateStats()
        {
            bool show = App.Settings.ShowStatsInMenuBar;
            _statsButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            _statsText.Text = $"CPU {SystemStats.Cpu,2:0}%   RAM {SystemStats.MemoryPercent:0}%";
            if (++_diskTick % 15 == 0) UpdateDisk(); // free space barely changes: every ~30 s
            _statsButton.ToolTip = $"Memory {SystemStats.Bytes(SystemStats.MemoryUsed)} of {SystemStats.Bytes(SystemStats.MemoryTotal)} — click for Activity";
        }

        // ------------------------------------------------------------------ tray icons

        /// <summary>
        /// Background-app icons. With "collapse" on they sit behind an arrow: click it, the arrow turns and the
        /// icons slide out to its left (they tuck away again after a few seconds). Icons marked "Always show"
        /// stay visible.
        /// </summary>
        void RebuildTray()
        {
            _trayPanel.Children.Clear();
            var s = App.Settings;
            if (!s.ShowTrayIcons) return;
            var icons = TrayHost.Icons.Where(i => !i.Hidden && i.Icon != null).ToList();
            var always = new HashSet<string>(s.AlwaysShowTray ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var shown = icons.Where(i => !s.CollapseTrayIcons || always.Contains(i.Owner ?? "")).ToList();
            var tucked = icons.Except(shown).ToList();

            if (tucked.Count > 0)
            {
                var host = new StackPanel { Orientation = Orientation.Horizontal, Visibility = _trayExpanded ? Visibility.Visible : Visibility.Collapsed };
                foreach (var i in tucked) host.Children.Add(TrayButton(i));
                _trayPanel.Children.Add(host);
                var arrow = new TextBlock { Text = "", FontFamily = Ui.IconFont, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, RenderTransform = _chevronRotate, RenderTransformOrigin = new Point(0.5, 0.5) };
                arrow.SetResourceReference(TextBlock.ForegroundProperty, "Bar.Foreground");
                var b = new Button { Content = arrow, Style = Ui.Style("BarButton"), Padding = new Thickness(7, 0, 7, 0), ToolTip = $"{tucked.Count} background apps" };
                b.Click += (_, _) => ToggleTray(host);
                _trayPanel.Children.Add(b);
                if (_trayCollapse.Tag == null)
                {
                    _trayCollapse.Tag = true;
                    _trayCollapse.Tick += (_, _) => { _trayCollapse.Stop(); if (_trayExpanded && _trayPanel.Children.Count > 0 && _trayPanel.Children[0] is StackPanel h) ToggleTray(h); };
                }
            }
            foreach (var i in shown) _trayPanel.Children.Add(TrayButton(i));
        }

        void ToggleTray(StackPanel host)
        {
            _trayExpanded = !_trayExpanded;
            Ui.Animate(_chevronRotate, RotateTransform.AngleProperty, _trayExpanded ? 180 : 0, 280, new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut });
            if (_trayExpanded)
            {
                host.Visibility = Visibility.Visible;
                host.Opacity = 0;
                var tt = new TranslateTransform(16, 0);
                host.RenderTransform = tt;
                Ui.Animate(host, OpacityProperty, 1, 200);
                Ui.Animate(tt, TranslateTransform.XProperty, 0, 280);
                _trayCollapse.Stop();
                _trayCollapse.Start();
            }
            else
            {
                _trayCollapse.Stop();
                Ui.Animate(host, OpacityProperty, 0, 150, done: () => { if (!_trayExpanded) host.Visibility = Visibility.Collapsed; });
            }
        }

        Button TrayButton(TrayIcon icon)
        {
            var img = new Image { Source = icon.Icon, Width = 16, Height = 16 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var b = new Button { Content = img, Style = Ui.Style("BarButton"), Padding = new Thickness(5, 0, 5, 0) };
            if (!string.IsNullOrWhiteSpace(icon.Tip)) b.ToolTip = icon.Tip;
            b.Click += (_, _) => { TrayHost.Click(icon, false, false); _trayCollapse.Stop(); };
            b.MouseDoubleClick += (_, _) => TrayHost.Click(icon, false, true);
            b.MouseRightButtonUp += (_, e) => { e.Handled = true; TrayHost.Click(icon, true, false); _trayCollapse.Stop(); };
            b.MouseEnter += (_, _) => { TrayHost.Hover(icon); if (_trayExpanded) { _trayCollapse.Stop(); _trayCollapse.Start(); } };
            return b;
        }

        // ------------------------------------------------------------------ keyboard input source

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int n, IntPtr[] list);
        const int WM_INPUTLANGCHANGEREQUEST = 0x0050;

        FrameworkElement InputVisual()
        {
            var box = new Border { CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1.1), Child = _inputText, VerticalAlignment = VerticalAlignment.Center };
            box.SetResourceReference(Border.BorderBrushProperty, "Bar.Foreground");
            _inputText.SetResourceReference(TextBlock.ForegroundProperty, "Bar.Foreground");
            return box;
        }

        static string LangCode(IntPtr hkl)
        {
            int lang = (int)((long)hkl & 0xFFFF);
            try { return new CultureInfo(lang).TwoLetterISOLanguageName.ToUpperInvariant(); } catch { return "?"; }
        }

        static string LangName(IntPtr hkl)
        {
            int lang = (int)((long)hkl & 0xFFFF);
            try { return new CultureInfo(lang).DisplayName; } catch { return "Keyboard " + lang.ToString("X4"); }
        }

        void UpdateInput()
        {
            if (!App.Settings.ShowInputSource) { _inputButton.Visibility = Visibility.Collapsed; return; }
            int count = GetKeyboardLayoutList(0, null);
            _inputButton.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (count <= 1) return;
            var fg = WindowTracker.LastExternalForeground;
            uint tid = Win32.GetWindowThreadProcessId(fg, out _);
            var hkl = GetKeyboardLayout(tid);
            if (hkl == _lastHkl) return;
            _lastHkl = hkl;
            _inputText.Text = LangCode(hkl);
            _inputButton.ToolTip = LangName(hkl);
        }

        void ShowInputMenu()
        {
            int count = GetKeyboardLayoutList(0, null);
            var list = new IntPtr[Math.Max(1, count)];
            GetKeyboardLayoutList(list.Length, list);
            var cm = new ContextMenu();
            var target = WindowTracker.LastExternalForeground;
            foreach (var hkl in list.Where(h => h != IntPtr.Zero))
            {
                var h = hkl;
                cm.Items.Add(Menus.Item($"{LangName(h)}  ({LangCode(h)})", () =>
                {
                    // ask the app the user is typing in to switch its keyboard
                    Win32.PostMessage(target, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, h);
                    _lastHkl = IntPtr.Zero;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateInput);
                }, isChecked: h == _lastHkl));
            }
            cm.Items.Add(Menus.Sep());
            cm.Items.Add(Menus.Item("Show Emoji & Symbols", Shell.EmojiPanel, "Win+."));
            cm.Items.Add(Menus.Item("Keyboard Settings…", () => Shell.Open("ms-settings:keyboard")));
            cm.PlacementTarget = _inputButton;
            cm.Placement = PlacementMode.Bottom;
            cm.HorizontalOffset = -12;
            cm.IsOpen = true;
        }

        void ShowWifiMenu()
        {
            string ssid = _lastSsid;
            System.Threading.Tasks.Task.Run(NetworkInfo.CurrentSsid).ContinueWith(t => _lastSsid = t.Result);
            var cm = Menus.Build(
                Menus.Header("Wi-Fi"),
                Menus.Item(ssid ?? "Not connected", null, enabled: ssid != null, isChecked: ssid != null),
                Menus.Sep(),
                Menus.Item("Network Settings…", () => Shell.Open("ms-settings:network-wifi")),
                Menus.Item("Show Available Networks", () => Shell.Open("ms-availablenetworks:")));
            cm.PlacementTarget = _wifiButton;
            cm.Placement = PlacementMode.Bottom;
            cm.HorizontalOffset = -12;
            cm.IsOpen = true;
        }

        // ------------------------------------------------------------------ menus

        ContextMenu LogoMenu() => Menus.Build(
            Menus.Item("About This PC", () => App.ShowAbout()),
            Menus.Sep(),
            Menus.Item("System Settings…", () => Shell.Open("ms-settings:")),
            Menus.Item("MacWin Settings…", () => App.ShowSettings(), "Ctrl+,"),
            Menus.Item("App Store…", () => Shell.Open("ms-windows-store:")),
            Menus.Sep(),
            Menus.Item("Force Quit…", () => App.ShowForceQuit(), "Ctrl+Shift+Esc"),
            Menus.Sep(),
            Menus.Item("Sleep", Shell.Sleep),
            Menus.Item("Restart…", () => { if (MacDialog.Confirm("Are you sure you want to restart your computer now?", "Restart")) Shell.Restart(); }),
            Menus.Item("Shut Down…", () => { if (MacDialog.Confirm("Are you sure you want to shut down your computer now?", "Shut Down")) Shell.ShutDown(); }),
            Menus.Sep(),
            Menus.Item("Lock Screen", Shell.Lock, "Win+L"),
            Menus.Item($"Log Out {Environment.UserName}…", () => { if (MacDialog.Confirm("Are you sure you want to quit all applications and log out now?", "Log Out")) Shell.LogOut(); }),
            Menus.Sep(),
            Menus.Item("Quit MacWin", () => App.Quit()));

        ContextMenu AppMenu()
        {
            string id = CurrentAppId;
            string name = AppDisplayName(id);
            bool real = id != null && id != "desktop";
            return Menus.Build(
                Menus.Item($"About {name}", () => App.ShowAppInfo(id), enabled: real),
                Menus.Sep(),
                Menus.Item("Settings…", () => Shell.SendToApp(Win32.VK_CONTROL, 0xBC /* , */), "Ctrl+,", real),
                Menus.Sep(),
                Menus.Item($"Hide {name}", () => WindowTracker.HideApp(id), enabled: real),
                Menus.Item("Hide Others", () => WindowTracker.HideOthers(id)),
                Menus.Item("Show All", ShowAll),
                Menus.Sep(),
                Menus.Item($"Quit {name}", () => WindowTracker.QuitApp(id), "Alt+Q", real));
        }

        static void ShowAll()
        {
            foreach (var w in WindowTracker.Windows.Where(w => w.Minimized)) Win32.ShowWindowAsync(w.Hwnd, Win32.SW_SHOWNOACTIVATE);
        }

        static ContextMenu FileMenu() => Menus.Build(
            Menus.Item("New Window", () => Shell.SendToApp(Win32.VK_CONTROL, 0x4E), "Ctrl+N"),
            Menus.Item("New Tab", () => Shell.SendToApp(Win32.VK_CONTROL, 0x54), "Ctrl+T"),
            Menus.Item("Open…", () => Shell.SendToApp(Win32.VK_CONTROL, 0x4F), "Ctrl+O"),
            Menus.Sep(),
            Menus.Item("Close Window", () => Shell.SendToApp(Win32.VK_CONTROL, 0x57), "Ctrl+W"),
            Menus.Item("Save", () => Shell.SendToApp(Win32.VK_CONTROL, 0x53), "Ctrl+S"),
            Menus.Item("Save As…", () => Shell.SendToApp(Win32.VK_CONTROL, Win32.VK_SHIFT, 0x53), "Ctrl+Shift+S"),
            Menus.Sep(),
            Menus.Item("Print…", () => Shell.SendToApp(Win32.VK_CONTROL, 0x50), "Ctrl+P"));

        static ContextMenu EditMenu() => Menus.Build(
            Menus.Item("Undo", () => Shell.SendToApp(Win32.VK_CONTROL, 0x5A), "Ctrl+Z"),
            Menus.Item("Redo", () => Shell.SendToApp(Win32.VK_CONTROL, 0x59), "Ctrl+Y"),
            Menus.Sep(),
            Menus.Item("Cut", () => Shell.SendToApp(Win32.VK_CONTROL, 0x58), "Ctrl+X"),
            Menus.Item("Copy", () => Shell.SendToApp(Win32.VK_CONTROL, 0x43), "Ctrl+C"),
            Menus.Item("Paste", () => Shell.SendToApp(Win32.VK_CONTROL, 0x56), "Ctrl+V"),
            Menus.Item("Select All", () => Shell.SendToApp(Win32.VK_CONTROL, 0x41), "Ctrl+A"),
            Menus.Sep(),
            Menus.Item("Find…", () => Shell.SendToApp(Win32.VK_CONTROL, 0x46), "Ctrl+F"),
            Menus.Sep(),
            Menus.Item("Clipboard History", () => Input.Chord(Win32.VK_LWIN, 0x56), "Win+V"),
            Menus.Item("Emoji & Symbols", Shell.EmojiPanel, "Win+."));

        static ContextMenu ViewMenu() => Menus.Build(
            Menus.Item("Enter Full Screen", () => Shell.SendToApp(Win32.VK_F11), "F11"),
            Menus.Sep(),
            Menus.Item("Zoom In", () => Shell.SendToApp(Win32.VK_CONTROL, Win32.VK_OEM_PLUS), "Ctrl++"),
            Menus.Item("Zoom Out", () => Shell.SendToApp(Win32.VK_CONTROL, Win32.VK_OEM_MINUS), "Ctrl+-"),
            Menus.Item("Actual Size", () => Shell.SendToApp(Win32.VK_CONTROL, 0x30), "Ctrl+0"),
            Menus.Sep(),
            Menus.Item("Reload", () => Shell.SendToApp(0x74), "F5"),
            Menus.Sep(),
            Menus.Item("Mission Control", Shell.MissionControl, "Win+Tab"),
            Menus.Item("Show Desktop", Shell.ShowDesktop, "Win+D"));

        ContextMenu WindowMenu()
        {
            var target = WindowTracker.LastExternalForeground;
            var cm = Menus.Build(
                Menus.Item("Minimize", () => WindowTracker.Minimize(target), "Win+↓"),
                Menus.Item("Zoom", () => WindowTracker.ToggleZoom(target)),
                Menus.Sep(),
                Menus.Item("Move Window to Left Side of Screen", () => { Input.Activate(target); Input.Chord(Win32.VK_LWIN, Win32.VK_LEFT); }),
                Menus.Item("Move Window to Right Side of Screen", () => { Input.Activate(target); Input.Chord(Win32.VK_LWIN, Win32.VK_RIGHT); }),
                Menus.Item("Snap Layouts…", () => { Input.Activate(target); Input.Chord(Win32.VK_LWIN, 0x5A); }, "Win+Z"),
                Menus.Sep(),
                Menus.Item("Bring All to Front", () =>
                {
                    foreach (var w in WindowTracker.WindowsOf(CurrentAppId).AsEnumerable().Reverse()) WindowTracker.Activate(w);
                }));
            var wins = WindowTracker.WindowsOf(CurrentAppId);
            if (wins.Count > 0)
            {
                cm.Items.Add(Menus.Sep());
                foreach (var w in wins.Take(12))
                {
                    var win = w;
                    cm.Items.Add(Menus.Item(Trim(w.Title), () => WindowTracker.Activate(win), isChecked: w.Hwnd == target));
                }
            }
            return cm;
        }

        ContextMenu HelpMenu()
        {
            string name = AppDisplayName(CurrentAppId);
            return Menus.Build(
                Menus.Item("Search…", App.ToggleSpotlight, "Alt+Space"),
                Menus.Sep(),
                Menus.Item($"{name} Help", () => Shell.SendToApp(0x70), "F1"),
                Menus.Item("Keyboard Shortcuts", () => App.ShowSettings("Keyboard")),
                Menus.Item("Get Help with Windows", () => Shell.Open("ms-contact-support:")));
        }

        static string Trim(string s) => string.IsNullOrEmpty(s) ? "Untitled" : s.Length > 60 ? s[..57] + "…" : s;
    }

    public static class NetworkInfo
    {
        /// <summary>Current Wi-Fi network name (runs netsh only on demand).</summary>
        public static string CurrentSsid()
        {
            // the Wi-Fi API first; netsh is a fallback for systems where it isn't available
            var s = WifiNetworks.CurrentSsid();
            if (s != null) return s;
            try
            {
                var psi = new ProcessStartInfo("netsh", "wlan show interfaces") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                foreach (var line in output.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                    {
                        int i = t.IndexOf(':');
                        if (i > 0) { var v = t[(i + 1)..].Trim(); return v.Length > 0 ? v : null; }
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
