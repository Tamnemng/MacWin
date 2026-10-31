using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacWin.Native;
using MacWin.Services;
using Windows.Devices.Radios;
using Windows.Media.Control;

namespace MacWin.UI
{
    /// <summary>
    /// Control Center: every control is its own floating piece of glass (no shared panel), laid out on a
    /// 4-column grid. Wi-Fi and Sound open detail pages (networks / output devices); "Edit Controls" chooses
    /// which buttons appear.
    /// </summary>
    public class ControlCenterWindow : PanelWindow
    {
        const double T = 64, G = 12, M = 10;           // tile size, gap, outer margin
        const double W = 4 * T + 3 * G + 2 * M;

        readonly Grid _root = new() { Margin = new Thickness(M) };
        readonly Grid _main = new();
        readonly ContentControl _detail = new() { Visibility = Visibility.Collapsed };
        GlassTile _wifiCircle, _btCircle, _focusCircle;
        TextBlock _wifiState;
        readonly List<(GlassTile tile, Func<bool> isOn)> _toggles = new();
        Slider _brightness, _volume;
        TextBlock _volGlyph;
        GlassTile _nowPlaying;
        Image _art;
        TextBlock _songTitle, _songArtist, _playGlyph;
        bool _updating;
        GlobalSystemMediaTransportControlsSessionManager _media;
        readonly DispatcherTimer _brightnessDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };

        public ControlCenterWindow() : base(ThemeManager.PanelRadius(16), false, 0x40)
        {
            Title = "Control Center";
            Width = W;
            SizeToContent = SizeToContent.Height;
            Content = _root;
            _root.Children.Add(_main);
            _root.Children.Add(_detail);
            Build();
            HideOnDeactivate(HideAnimated);
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                if (_detail.Visibility == Visibility.Visible) ShowMain(); else HideAnimated();
            };
            _brightnessDebounce.Tick += (_, _) => { _brightnessDebounce.Stop(); var v = (byte)_brightness.Value; Task.Run(() => Brightness.Set(v)); };
        }

        // ================================================================== building blocks

        static GlassTile Tile(double radius, UIElement inner, Action click = null)
        {
            var t = new GlassTile(radius) { Inner = inner };
            if (click != null)
            {
                t.Cursor = Cursors.Hand;
                AddPress(t, click);
            }
            return t;
        }

        /// <summary>Press feedback: a quick spring-like squeeze, like pressing real glass.</summary>
        static void AddPress(FrameworkElement el, Action click)
        {
            var st = new ScaleTransform(1, 1);
            el.RenderTransform = st;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.MouseLeftButtonDown += (_, e) =>
            {
                Ui.Animate(st, ScaleTransform.ScaleXProperty, 0.94, 90);
                Ui.Animate(st, ScaleTransform.ScaleYProperty, 0.94, 90);
                e.Handled = true;
            };
            el.MouseLeftButtonUp += (_, e) =>
            {
                var spring = new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut };
                Ui.Animate(st, ScaleTransform.ScaleXProperty, 1, 380, spring);
                Ui.Animate(st, ScaleTransform.ScaleYProperty, 1, 380, spring);
                e.Handled = true;
                click();
            };
            el.MouseLeave += (_, _) => { Ui.Animate(st, ScaleTransform.ScaleXProperty, 1, 150); Ui.Animate(st, ScaleTransform.ScaleYProperty, 1, 150); };
        }

        static TextBlock Glyph(string g, double size = 20)
        {
            var t = Ui.Glyph(g, size, "Text.Primary");
            return t;
        }

        static void SetOn(GlassTile tile, bool on)
        {
            if (on)
            {
                var c = ThemeManager.Accent;
                tile.SetTint(new SolidColorBrush(Color.FromArgb(0xD8, c.R, c.G, c.B)));
            }
            else tile.SetTint(null);
            if (tile.Inner is TextBlock tb)
            {
                if (on) tb.Foreground = Brushes.White;
                else tb.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            }
        }

        static GlassTile Circle(string glyph, Action click, double size = T)
        {
            var t = Tile(size / 2, Glyph(glyph, size * 0.32), click);
            t.Width = t.Height = size;
            return t;
        }

        /// <summary>Wide capsule: icon circle + title / subtitle (Wi-Fi, Focus).</summary>
        GlassTile Pill(string glyph, string title, out GlassTile circle, out TextBlock subtitle, Action onCircle, Action onBody)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var c = Circle(glyph, onCircle, 40);
            c.Margin = new Thickness(10, 0, 0, 0);
            c.HorizontalAlignment = HorizontalAlignment.Left;
            circle = c;
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) };
            sp.Children.Add(Ui.Text(title, 13, FontWeights.SemiBold));
            subtitle = Ui.Text("", 11.5, null, "Text.Secondary");
            sp.Children.Add(subtitle);
            Grid.SetColumn(sp, 1);
            g.Children.Add(c);
            g.Children.Add(sp);
            var pill = Tile(T / 2, g, onBody);
            pill.Height = T;
            return pill;
        }

        static void Place(Grid grid, UIElement e, int row, int col, int rowSpan = 1, int colSpan = 1)
        {
            Grid.SetRow(e, row); Grid.SetColumn(e, col);
            Grid.SetRowSpan(e, rowSpan); Grid.SetColumnSpan(e, colSpan);
            grid.Children.Add(e);
        }

        // ================================================================== main page

        void Build()
        {
            var s = App.Settings;
            _main.Children.Clear();
            _main.RowDefinitions.Clear();
            _main.ColumnDefinitions.Clear();
            _toggles.Clear();
            for (int i = 0; i < 7; i++) _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i % 2 == 0 ? T : G) });
            void Row(double h) => _main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });
            void RowAuto() => _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Row(T); Row(G); Row(T); Row(G); Row(T); Row(G);     // 0..5: connectivity, now playing, focus
            RowAuto(); RowAuto(); RowAuto(); RowAuto();          // 6..9: display, sound, buttons, edit

            // ---- Wi-Fi capsule (circle toggles the radio, the rest opens the network list)
            var wifi = Pill("", "Wi-Fi", out _wifiCircle, out _wifiState, () => ToggleRadio(RadioKind.WiFi), ShowWifi);
            Place(_main, wifi, 0, 0, 1, 3);

            // ---- Bluetooth / Nearby Sharing circles
            _btCircle = Circle("", ShowBluetooth);          // devices page (the switch is inside); right-click toggles
            _btCircle.MouseRightButtonUp += (_, e) => { e.Handled = true; ToggleRadio(RadioKind.Bluetooth); };
            _btCircle.ToolTip = "Bluetooth";
            Place(_main, _btCircle, 2, 0);
            var share = Circle("", () => { HideAnimated(); Shell.Open("ms-settings:crossdevice"); });
            share.ToolTip = "Nearby Sharing";
            Place(_main, share, 2, 2);

            // ---- Now Playing (2×2)
            if (s.ControlCenterNowPlaying)
            {
                _nowPlaying = NowPlayingTile();
                Place(_main, _nowPlaying, 0, 4, 3, 3);
            }

            // ---- Focus capsule + Mission Control + Screen Mirroring
            // Focus: the circle toggles Do Not Disturb (no banners), the body opens notification settings
            TextBlock focusSub = null;
            var focus = Pill("", "Focus", out _focusCircle, out focusSub, () =>
            {
                s.DoNotDisturb = !s.DoNotDisturb;
                s.Save(false);
                SetOn(_focusCircle, s.DoNotDisturb);
                focusSub.Text = s.DoNotDisturb ? "Do Not Disturb" : "Off";
            }, () => { HideAnimated(); App.ShowSettings("Notifications"); });
            focusSub.Text = s.DoNotDisturb ? "Do Not Disturb" : "Off";
            _toggles.Add((_focusCircle, () => App.Settings.DoNotDisturb));
            Place(_main, focus, 4, 0, 1, 3);
            var mc = Circle("", () => { HideAnimated(); Shell.MissionControl(); });
            mc.ToolTip = "Mission Control";
            Place(_main, mc, 4, 4);
            var cast = Circle("", () => { HideAnimated(); Shell.Cast(); });
            cast.ToolTip = "Screen Mirroring";
            Place(_main, cast, 4, 6);

            // ---- Display
            if (s.ControlCenterDisplay)
            {
                _brightness = NewSlider();
                _brightness.ValueChanged += (_, _) => { if (!_updating) { _brightnessDebounce.Stop(); _brightnessDebounce.Start(); } };
                var disp = SliderModule("Display", "", _brightness, null, null);
                disp.Margin = new Thickness(0, 0, 0, G);
                Place(_main, disp, 6, 0, 1, 7);
            }

            // ---- Sound (with output-device button)
            if (s.ControlCenterSound)
            {
                _volume = NewSlider();
                _volume.ValueChanged += (_, _) =>
                {
                    if (_updating) return;
                    AudioVolume.Set((float)(_volume.Value / 100));
                    if (AudioVolume.GetMute()) AudioVolume.SetMute(false);
                    UpdateVolumeGlyph();
                };
                var snd = SliderModule("Sound", "", _volume, out _volGlyph, ShowSound);
                snd.Margin = new Thickness(0, 0, 0, G);
                Place(_main, snd, 7, 0, 1, 7);
            }

            // ---- customisable buttons, four per row
            var buttons = new Grid();
            for (int i = 0; i < 7; i++) buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i % 2 == 0 ? T : G) });
            var keys = (s.ControlCenterButtons ?? new List<string>()).Where(Buttons.ContainsKey).Take(8).ToList();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i % 4 == 0)
                {
                    if (i > 0) buttons.RowDefinitions.Add(new RowDefinition { Height = new GridLength(G) });
                    buttons.RowDefinitions.Add(new RowDefinition { Height = new GridLength(T) });
                }
                var def = Buttons[keys[i]];
                var b = Circle(def.Glyph, () => { if (def.Toggle == null) HideAnimated(); def.Run(); RefreshToggles(); });
                b.ToolTip = def.Label;
                if (def.Toggle != null) _toggles.Add((b, def.Toggle));
                Place(buttons, b, (i / 4) * 2, (i % 4) * 2);
            }
            buttons.Margin = new Thickness(0, 0, 0, G);
            Place(_main, buttons, 8, 0, 1, 7);

            // ---- Edit Controls
            var edit = Tile(13, new TextBlock { Text = "Edit Controls", FontSize = 12.5, Margin = new Thickness(14, 5, 14, 6), Foreground = ThemeManager.IsDark ? Brushes.White : new SolidColorBrush(ThemeManager.Accent) },
                () => { HideAnimated(); App.ShowSettings("Control Center"); });
            edit.HorizontalAlignment = HorizontalAlignment.Center;
            Place(_main, edit, 9, 0, 1, 7);
        }

        record ButtonDef(string Glyph, string Label, Action Run, Func<bool> Toggle = null);

        /// <summary>Buttons that "Edit Controls" can add to Control Center.</summary>
        public static readonly Dictionary<string, (string Glyph, string Label, Action Run, Func<bool> Toggle)> Buttons = new()
        {
            ["darkmode"] = ("", "Dark Mode", App.ToggleDarkMode, () => ThemeManager.IsDark),
            ["calculator"] = ("", "Calculator", () => Shell.Open("calc.exe"), null),
            ["clock"] = ("", "Clock & Timer", () => Shell.Open("ms-clock:"), null),
            ["screenshot"] = ("", "Screenshot", Shell.Screenshot, null),
            ["nightlight"] = ("", "Night Light", () => Shell.Open("ms-settings:nightlight"), null),
            ["accessibility"] = ("", "Accessibility", () => Shell.Open("ms-settings:easeofaccess"), null),
            ["hotspot"] = ("", "Mobile Hotspot", () => Shell.Open("ms-settings:network-mobilehotspot"), null),
            ["lock"] = ("", "Lock Screen", Shell.Lock, null),
            ["notifications"] = ("", "Notifications", Shell.NotificationCenter, null),
            ["emoji"] = ("", "Emoji & Symbols", Shell.EmojiPanel, null),
            ["terminal"] = ("", "Terminal", () => Shell.Open("wt.exe"), null),
            ["settings"] = ("", "Settings", () => Shell.Open("ms-settings:"), null),
            ["activity"] = ("", "Activity", () => App.ToggleActivity(), null),
            ["battery"] = ("", "Battery", () => Shell.Open("ms-settings:batterysaver"), null),
        };

        static Slider NewSlider() => new() { Minimum = 0, Maximum = 100, Style = Ui.Style("PillSlider"), VerticalAlignment = VerticalAlignment.Center };

        GlassTile SliderModule(string title, string glyph, Slider slider, out TextBlock glyphBlock, Action more)
        {
            var g = new Grid { Margin = new Thickness(14, 10, 12, 12) };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Ui.Text(title, 13, FontWeights.SemiBold));
            var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(slider);
            glyphBlock = new TextBlock { Text = glyph, FontFamily = Ui.IconFont, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0x40, 0x40, 0x40)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), IsHitTestVisible = false };
            row.Children.Add(glyphBlock);
            Grid.SetRow(row, 1);
            g.Children.Add(row);
            if (more != null)
            {
                var b = Circle("", more, 30);
                b.ToolTip = "Output";
                b.Margin = new Thickness(10, 8, 0, 0);
                Grid.SetRow(b, 1);
                Grid.SetColumn(b, 1);
                g.Children.Add(b);
            }
            var tile = Tile(22, g);
            return tile;
        }

        GlassTile SliderModule(string title, string glyph, Slider slider, object unused, Action more) => SliderModule(title, glyph, slider, out _, more);

        GlassTile NowPlayingTile()
        {
            var g = new Grid { Margin = new Thickness(12) };
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _art = new Image { Stretch = Stretch.UniformToFill };
            var artFrame = new ContinuousBorder { Width = 50, Height = 50, CornerRadius = 12, Child = _art, ClipContent = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            artFrame.SetResourceReference(ContinuousBorder.BackgroundProperty, "Tile.Off");
            g.Children.Add(artFrame);
            var meta = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
            _songTitle = Ui.Text("Now Playing", 12.5, FontWeights.SemiBold);
            _songArtist = Ui.Text("", 11, null, "Text.Secondary");
            meta.Children.Add(_songTitle);
            meta.Children.Add(_songArtist);
            Grid.SetRow(meta, 1);
            g.Children.Add(meta);
            var controls = new UniformGrid3();
            controls.Add(MediaButton("", async x => await x.TrySkipPreviousAsync()));
            var play = MediaButton("", async x => await x.TryTogglePlayPauseAsync());
            _playGlyph = (TextBlock)((Button)play).Content;
            controls.Add(play);
            controls.Add(MediaButton("", async x => await x.TrySkipNextAsync()));
            Grid.SetRow(controls.Panel, 2);
            g.Children.Add(controls.Panel);
            return Tile(22, g);
        }

        FrameworkElement MediaButton(string glyph, Func<GlobalSystemMediaTransportControlsSession, Task> act)
        {
            var b = new Button { Content = Ui.Glyph(glyph, 15), Style = Ui.Style("GhostButton"), Height = 28 };
            b.Click += async (_, _) =>
            {
                var session = _media?.GetCurrentSession();
                if (session != null) { try { await act(session); } catch { } }
                await Task.Delay(250);
                await UpdateMedia();
            };
            return b;
        }

        class UniformGrid3
        {
            public readonly Grid Panel = new();
            int _n;
            public UniformGrid3() { for (int i = 0; i < 3; i++) Panel.ColumnDefinitions.Add(new ColumnDefinition()); }
            public void Add(FrameworkElement e) { Grid.SetColumn(e, _n++); Panel.Children.Add(e); }
        }

        // ================================================================== detail pages

        GlassTile DetailFrame(string title, UIElement headerRight, out StackPanel body)
        {
            var sp = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var back = new Button { Content = Ui.Glyph("", 12), Style = Ui.Style("GhostButton"), Width = 26, Height = 26, Margin = new Thickness(-6, 0, 4, 0), ToolTip = "Back" };
            back.Click += (_, _) => ShowMain();
            DockPanel.SetDock(back, Dock.Left);
            head.Children.Add(back);
            if (headerRight != null) { DockPanel.SetDock(headerRight, Dock.Right); head.Children.Add(headerRight); }
            var t = Ui.Text(title, 15, FontWeights.SemiBold);
            t.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(t);
            sp.Children.Add(head);
            body = new StackPanel();
            sp.Children.Add(new ScrollViewer { Content = body, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            return Tile(22, sp);
        }

        static Border ListRow(UIElement left, string text, UIElement right, Action click, bool bold = false)
        {
            var g = new Grid { Height = 30 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (left != null) g.Children.Add(left);
            var tb = Ui.Text(text, 13, bold ? FontWeights.SemiBold : FontWeights.Normal);
            tb.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(tb, 1);
            g.Children.Add(tb);
            if (right != null) { Grid.SetColumn(right, 2); g.Children.Add(right); }
            var b = new Border { Child = g, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Selection");
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (_, _) => click?.Invoke();
            return b;
        }

        static TextBlock Caption(string s)
        {
            var t = Ui.Text(s, 11, FontWeights.SemiBold, "Text.Secondary");
            t.Margin = new Thickness(6, 8, 0, 4);
            return t;
        }

        void OpenDetail(GlassTile frame)
        {
            _detail.Content = frame;
            _detail.Visibility = Visibility.Visible;
            _detail.Opacity = 0;
            var st = new ScaleTransform(0.96, 0.96);
            _detail.RenderTransform = st;
            _detail.RenderTransformOrigin = new Point(0.5, 0);
            Ui.Animate(_main, OpacityProperty, 0, 140, done: () => _main.Visibility = Visibility.Hidden);
            Ui.Animate(_detail, OpacityProperty, 1, 200);
            Ui.Animate(st, ScaleTransform.ScaleXProperty, 1, 260);
            Ui.Animate(st, ScaleTransform.ScaleYProperty, 1, 260, done: RealignGlass);
        }

        void ShowMain()
        {
            Ui.Stop(_main, OpacityProperty);
            _main.Visibility = Visibility.Visible;
            Ui.Animate(_detail, OpacityProperty, 0, 120, done: () => { _detail.Visibility = Visibility.Collapsed; _detail.Content = null; });
            Ui.Animate(_main, OpacityProperty, 1, 200, done: RealignGlass);
        }

        // ---- Wi-Fi
        async void ShowWifi()
        {
            var sw = new CheckBox { Style = Ui.Style("Switch") };
            var frame = DetailFrame("Wi-Fi", sw, out var body);
            body.Children.Add(Ui.Text("Searching for networks…", 12, null, "Text.Secondary"));
            OpenDetail(frame);

            bool? on = await GetRadioState(RadioKind.WiFi);
            sw.IsChecked = on ?? true;
            sw.IsEnabled = on != null;
            sw.Click += async (_, _) => { await SetRadio(RadioKind.WiFi, sw.IsChecked == true); await Task.Delay(1500); ShowWifi(); };
            if (on == false) { body.Children.Clear(); body.Children.Add(Ui.Text("Wi-Fi is off", 12, null, "Text.Secondary")); AddWifiFooter(body); return; }

            var nets = await WifiNetworks.ScanAsync();
            RenderNetworks(body, nets);
            if (WifiNetworks.ReportStale)
            {
                // show what Windows already knows right away; refresh the list once a fresh scan is in
                _ = WifiNetworks.RescanAsync().ContinueWith(async _ =>
                {
                    if (_detail.Content != frame || !IsVisible) return;
                    var fresh = await WifiNetworks.ScanAsync();
                    if (_detail.Content == frame) RenderNetworks(body, fresh);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        void RenderNetworks(StackPanel body, List<WifiNetwork> nets)
        {
            body.Children.Clear();
            var known = nets.Where(n => n.Known || n.Connected).ToList();
            var others = nets.Except(known).ToList();
            if (known.Count > 0) body.Children.Add(Caption("Known Networks"));
            if (nets.Count > 0) { var hint = Ui.Text("Right-click a network for Disconnect, Forget and Auto-Join.", 10.5, null, "Text.Tertiary"); hint.Margin = new Thickness(6, -2, 0, 4); body.Children.Add(hint); }
            foreach (var n in known) body.Children.Add(NetworkRow(n));
            if (others.Count > 0) body.Children.Add(Caption("Other Networks"));
            foreach (var n in others.Take(12)) body.Children.Add(NetworkRow(n));
            if (nets.Count == 0) body.Children.Add(Ui.Text("No networks found", 12, null, "Text.Secondary"));
            AddWifiFooter(body);
        }

        // ---- Bluetooth
        BluetoothDevices.Discovery _btScan;

        async void ShowBluetooth()
        {
            var sw = new CheckBox { Style = Ui.Style("Switch") };
            var frame = DetailFrame("Bluetooth", sw, out var body);
            body.Children.Add(Ui.Text("Looking for devices…", 12, null, "Text.Secondary"));
            OpenDetail(frame);
            _btScan?.Dispose(); _btScan = null;

            bool? on = await GetRadioState(RadioKind.Bluetooth);
            sw.IsChecked = on ?? false;
            sw.IsEnabled = on != null;
            sw.Click += async (_, _) => { await SetRadio(RadioKind.Bluetooth, sw.IsChecked == true); await Task.Delay(1200); ShowBluetooth(); };
            if (on != true) { body.Children.Clear(); body.Children.Add(Ui.Text(on == null ? "Bluetooth is unavailable" : "Bluetooth is off", 12, null, "Text.Secondary")); AddBtFooter(body); return; }

            var paired = await BluetoothDevices.PairedAsync();
            var scan = new BluetoothDevices.Discovery();
            _btScan = scan;
            void Render()
            {
                if (_detail.Content != frame) return;
                body.Children.Clear();
                if (paired.Count > 0) body.Children.Add(Caption("My Devices"));
                foreach (var d in paired) body.Children.Add(BtRow(d, frame));
                var others = scan.Devices;
                body.Children.Add(Caption(others.Count > 0 ? "Other Devices" : "Looking for other devices…"));
                foreach (var d in others.Take(10)) body.Children.Add(BtRow(d, frame));
                AddBtFooter(body);
            }
            Render();
            scan.Changed += () => Dispatcher.BeginInvoke(Render);
            // stop discovering once the page is left
            void Stop() { if (_btScan == scan) { scan.Dispose(); _btScan = null; } }
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(1000);
                    bool still = await Dispatcher.InvokeAsync(() => _detail.Content == frame && IsVisible);
                    if (!still) break;
                }
                await Dispatcher.InvokeAsync(Stop);
            });
        }

        void AddBtFooter(StackPanel body)
        {
            var sep = new Border { Height = 1, Margin = new Thickness(4, 8, 4, 4) };
            sep.SetResourceReference(Border.BackgroundProperty, "Separator");
            body.Children.Add(sep);
            body.Children.Add(ListRow(null, "Bluetooth Settings…", null, () => { HideAnimated(); Shell.Open("ms-settings:bluetooth"); }));
        }

        static string BtGlyph(string kind) => kind switch
        {
            "headphones" => "\ue7f6", "speaker" => "\ue7f5", "phone" => "\ue8ea", "computer" => "\ue7f8",
            "keyboard" => "\ue765", "mouse" => "\ue962", "controller" => "\ue7fc", "watch" => "\ue916", _ => "\ue702",
        };

        Border BtRow(BtDevice d, GlassTile frame)
        {
            var circle = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Left };
            var g = Ui.Glyph(BtGlyph(d.Kind), 11, null);
            if (d.Connected) { circle.SetResourceReference(Border.BackgroundProperty, "Accent"); g.Foreground = Brushes.White; }
            else { circle.SetResourceReference(Border.BackgroundProperty, "Tile.Off"); g.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary"); }
            circle.Child = g;
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var state = Ui.Text(d.Connected ? "Connected" : d.Paired ? "Not Connected" : "", 10.5, null, "Text.Tertiary");
            state.Margin = new Thickness(0, 0, 4, 0);
            right.Children.Add(state);
            var more = new Button { Content = Ui.Glyph("\ue712", 11), Style = Ui.Style("GhostButton"), Width = 22, Height = 22, Margin = new Thickness(4, 0, 0, 0), ToolTip = "More" };
            right.Children.Add(more);
            var row = ListRow(circle, d.Name, right, () => BtAction(d, frame, d.Paired ? (d.Connected ? null : "connect") : "pair"), d.Connected);
            more.Click += (_, e) => { e.Handled = true; BtMenu(d, frame, more); };
            row.MouseRightButtonUp += (_, e) => { e.Handled = true; BtMenu(d, frame, row); };
            return row;
        }

        void BtMenu(BtDevice d, GlassTile frame, FrameworkElement anchor)
        {
            var items = new List<object> { Menus.Header(d.Name) };
            if (!d.Paired) items.Add(Menus.Item("Connect", () => BtAction(d, frame, "pair")));
            else
            {
                if (!d.Connected) items.Add(Menus.Item("Connect", () => BtAction(d, frame, "connect")));
                else items.Add(Menus.Item("Disconnect…", () => { HideAnimated(); Shell.Open("ms-settings:bluetooth"); }));   // no public API for this
                items.Add(Menus.Item("Forget This Device…", () => BtAction(d, frame, "forget")));
            }
            items.Add(Menus.Sep());
            items.Add(Menus.Item("Bluetooth Settings…", () => { HideAnimated(); Shell.Open("ms-settings:bluetooth"); }));
            var cm = Menus.Build(items.ToArray());
            cm.PlacementTarget = anchor;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        /// <summary>Pair / connect / forget with a progress page, then back to the device list.</summary>
        async void BtAction(BtDevice d, GlassTile frame, string action)
        {
            if (action == null) return;
            if (action == "forget" && !MacDialog.Confirm($"Forget “{d.Name}”?", "Forget", "Windows will no longer connect to this device until you pair it again.")) return;
            var progress = DetailFrame("Bluetooth", null, out var body);
            body.Children.Add(Ui.Text(action switch { "pair" => $"Pairing with {d.Name}…", "connect" => $"Connecting to {d.Name}…", _ => $"Forgetting {d.Name}…" }, 12, null, "Text.Secondary"));
            OpenDetail(progress);
            string err = action switch
            {
                "pair" => await BluetoothDevices.PairAsync(d) ?? await BluetoothDevices.ConnectAsync(d with { Paired = true }),
                "connect" => await BluetoothDevices.ConnectAsync(d),
                _ => await BluetoothDevices.UnpairAsync(d) ? null : "Couldn't forget the device",
            };
            if (err != null)
            {
                body.Children.Clear();
                var t = Ui.Text(err, 12);
                t.TextWrapping = TextWrapping.Wrap;
                t.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
                body.Children.Add(t);
                var back = new Button { Content = "OK", Style = Ui.Style("MacButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 4, 0) };
                back.Click += (_, _) => ShowBluetooth();
                body.Children.Add(back);
                return;
            }
            await Task.Delay(400);
            _ = UpdateRadios();
            ShowBluetooth();
        }

        void AddWifiFooter(StackPanel body)
        {
            var sep = new Border { Height = 1, Margin = new Thickness(4, 8, 4, 4) };
            sep.SetResourceReference(Border.BackgroundProperty, "Separator");
            body.Children.Add(sep);
            body.Children.Add(ListRow(null, "Wi-Fi Settings…", null, () => { HideAnimated(); Shell.Open("ms-settings:network-wifi"); }));
        }

        Border NetworkRow(WifiNetwork n)
        {
            var circle = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Left };
            var g = Ui.Glyph("", 11, null);
            g.Opacity = n.Signal >= 66 ? 1 : n.Signal >= 33 ? 0.75 : 0.5;
            if (n.Connected) { circle.SetResourceReference(Border.BackgroundProperty, "Accent"); g.Foreground = Brushes.White; }
            else { circle.SetResourceReference(Border.BackgroundProperty, "Tile.Off"); g.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary"); }
            circle.Child = g;
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var sig = Ui.Text(n.Signal + "%", 10.5, null, "Text.Tertiary");
            sig.Margin = new Thickness(0, 0, 8, 0);
            right.Children.Add(sig);
            if (n.Secured) right.Children.Add(Ui.Glyph("", 10, "Text.Secondary"));
            var more = new Button { Content = Ui.Glyph("", 11), Style = Ui.Style("GhostButton"), Width = 22, Height = 22, Margin = new Thickness(6, 0, 0, 0), ToolTip = "More" };
            right.Children.Add(more);
            var row = ListRow(circle, n.Ssid, right, async () =>
            {
                if (n.Connected) return;
                if (n.Secured && !n.Known) { ShowWifiPassword(n); return; }
                await JoinWifi(n, null);
            }, n.Connected);
            more.Click += (_, e) => { e.Handled = true; NetworkMenu(n, more); };
            row.MouseRightButtonUp += (_, e) => { e.Handled = true; NetworkMenu(n, row); };
            return row;
        }

        /// <summary>Per-network actions, like Windows' own list: connect / disconnect, forget, auto-join, details.</summary>
        async void NetworkMenu(WifiNetwork n, FrameworkElement anchor)
        {
            bool? auto = n.Known ? await WifiNetworks.AutoJoinAsync(n.Ssid) : null;
            var items = new List<object> { Menus.Header(n.Ssid) };
            if (n.Connected) items.Add(Menus.Item("Disconnect", async () => { await WifiNetworks.DisconnectAsync(); await Task.Delay(800); _ = UpdateRadios(); ShowWifi(); }));
            else items.Add(Menus.Item("Connect", async () => { if (n.Secured && !n.Known) ShowWifiPassword(n); else await JoinWifi(n, null); }));
            if (n.Known)
            {
                items.Add(Menus.Item("Auto-Join", async () => { await WifiNetworks.SetAutoJoinAsync(n.Ssid, auto != true); }, isChecked: auto == true, enabled: auto != null));
                items.Add(Menus.Item("Forget This Network…", async () =>
                {
                    if (!MacDialog.Confirm($"Forget “{n.Ssid}”?", "Forget", "Windows will no longer join this network automatically, and its saved password is removed.")) return;
                    await WifiNetworks.ForgetAsync(n.Ssid);
                    if (n.Connected) await WifiNetworks.DisconnectAsync();
                    await Task.Delay(500);
                    ShowWifi();
                }));
            }
            items.Add(Menus.Sep());
            items.Add(Menus.Item($"Signal {n.Signal}%  ·  {(n.Secured ? "Secured" : "Open")}", null, enabled: false));
            items.Add(Menus.Item("Network Details…", () => { HideAnimated(); Shell.Open("ms-settings:network-wifi"); }));
            var cm = Menus.Build(items.ToArray());
            cm.PlacementTarget = anchor;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        /// <summary>Connects and shows the result; a secured network without a saved password asks for one.</summary>
        async Task JoinWifi(WifiNetwork n, string password)
        {
            var frame = DetailFrame("Wi-Fi", null, out var body);
            body.Children.Add(Ui.Text($"Connecting to {n.Ssid}…", 12, null, "Text.Secondary"));
            OpenDetail(frame);
            string err = await WifiNetworks.ConnectAsync(n, password);
            if (err == null) { await Task.Delay(1200); _ = UpdateRadios(); ShowWifi(); return; }
            if (err == "Needs a password") { ShowWifiPassword(n); return; }
            ShowWifiPassword(n, err);
        }

        /// <summary>macOS-style "enter the password for …" page, inside the panel.</summary>
        void ShowWifiPassword(WifiNetwork n, string error = null)
        {
            var frame = DetailFrame("Wi-Fi", null, out var body);
            var t = Ui.Text($"The Wi-Fi network “{n.Ssid}” requires a password.", 12);
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(4, 2, 4, 8);
            body.Children.Add(t);
            var pw = new PasswordBox { FontSize = 13, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(4, 0, 4, 6), BorderThickness = new Thickness(1) };
            pw.SetResourceReference(Control.BackgroundProperty, "Panel.Background");
            pw.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
            pw.SetResourceReference(Control.BorderBrushProperty, "Separator");
            body.Children.Add(pw);
            if (error != null)
            {
                var e = Ui.Text(error, 11, null, "Text.Secondary");
                e.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
                e.Margin = new Thickness(6, 0, 0, 6);
                body.Children.Add(e);
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 4, 0) };
            var cancel = new Button { Content = "Cancel", Style = Ui.Style("MacButton"), Margin = new Thickness(0, 0, 8, 0) };
            cancel.Click += (_, _) => ShowWifi();
            var join = new Button { Content = "Join", Style = Ui.Style("AccentButton") };
            join.Click += async (_, _) => { if (pw.Password.Length > 0) await JoinWifi(n, pw.Password); };
            pw.KeyDown += async (_, e) => { if (e.Key == Key.Enter && pw.Password.Length > 0) await JoinWifi(n, pw.Password); };
            row.Children.Add(cancel);
            row.Children.Add(join);
            body.Children.Add(row);
            OpenDetail(frame);
            Dispatcher.BeginInvoke(() => pw.Focus(), DispatcherPriority.Input);
        }

        // ---- Sound outputs
        void ShowSound()
        {
            var frame = DetailFrame("Sound", null, out var body);
            var vol = NewSlider();
            vol.Value = AudioVolume.Get() * 100;
            vol.ValueChanged += (_, _) => { AudioVolume.Set((float)(vol.Value / 100)); if (_volume != null) { _updating = true; _volume.Value = vol.Value; _updating = false; } };
            vol.Margin = new Thickness(0, 0, 0, 6);
            body.Children.Add(vol);
            body.Children.Add(Caption("Output"));
            foreach (var d in AudioDevices.Outputs())
            {
                var dev = d;
                var icon = Ui.Glyph(d.Name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0 || d.Name.IndexOf("tai nghe", StringComparison.OrdinalIgnoreCase) >= 0 ? "" : "", 13, null);
                icon.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
                icon.HorizontalAlignment = HorizontalAlignment.Left;
                var check = d.IsDefault ? Ui.Glyph("", 11, "Accent") : null;
                body.Children.Add(ListRow(icon, d.Name, check, () => { AudioDevices.SetDefault(dev.Id); ShowSound(); Refresh(); }, d.IsDefault));
            }
            var sep = new Border { Height = 1, Margin = new Thickness(4, 8, 4, 4) };
            sep.SetResourceReference(Border.BackgroundProperty, "Separator");
            body.Children.Add(sep);
            body.Children.Add(ListRow(null, "Sound Settings…", null, () => { HideAnimated(); Shell.Open("ms-settings:sound"); }));
            if (_detail.Visibility == Visibility.Visible) _detail.Content = frame;
            else OpenDetail(frame);
        }

        // ================================================================== state

        enum RadioKind { WiFi, Bluetooth }

        static Windows.Devices.Radios.RadioKind Map(RadioKind k) => k == RadioKind.WiFi ? Windows.Devices.Radios.RadioKind.WiFi : Windows.Devices.Radios.RadioKind.Bluetooth;

        static async Task<bool?> GetRadioState(RadioKind kind)
        {
            try
            {
                var r = (await Radio.GetRadiosAsync()).FirstOrDefault(x => x.Kind == Map(kind));
                return r == null ? null : r.State == RadioState.On;
            }
            catch { return null; }
        }

        static async Task<bool> SetRadio(RadioKind kind, bool on)
        {
            try
            {
                if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return false;
                var r = (await Radio.GetRadiosAsync()).FirstOrDefault(x => x.Kind == Map(kind));
                if (r == null) return false;
                return await r.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
            }
            catch { return false; }
        }

        async void ToggleRadio(RadioKind kind)
        {
            var state = await GetRadioState(kind);
            bool ok = state != null && await SetRadio(kind, !(state.Value));
            if (!ok)
            {
                // Windows refused programmatic switching (policy / driver): open the matching settings page
                HideAnimated();
                Shell.Open(kind == RadioKind.WiFi ? "ms-settings:network-wifi" : "ms-settings:bluetooth");
                return;
            }
            await Task.Delay(400);
            await UpdateRadios();
        }

        async Task UpdateRadios()
        {
            var wifi = await GetRadioState(RadioKind.WiFi);
            var bt = await GetRadioState(RadioKind.Bluetooth);
            SetOn(_wifiCircle, wifi == true);
            SetOn(_btCircle, bt == true);
            _btCircle.ToolTip = bt == null ? "Bluetooth unavailable" : bt.Value ? "Bluetooth: On" : "Bluetooth: Off";
            string ssid = wifi == true ? await Task.Run(NetworkInfo.CurrentSsid) : null;
            _wifiState.Text = wifi == null ? "Unavailable" : !wifi.Value ? "Off" : ssid ?? "Not Connected";
        }

        void RefreshToggles()
        {
            foreach (var (tile, isOn) in _toggles) SetOn(tile, isOn());
        }

        void UpdateVolumeGlyph()
        {
            if (_volGlyph == null || _volume == null) return;
            bool mute = AudioVolume.GetMute();
            double v = _volume.Value;
            _volGlyph.Text = mute || v == 0 ? "" : v < 33 ? "" : v < 66 ? "" : "";
        }

        async Task UpdateMedia()
        {
            if (_nowPlaying == null) return;
            try
            {
                _media ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var session = _media.GetCurrentSession();
                if (session == null) { _songTitle.Text = "Not Playing"; _songArtist.Text = ""; _art.Source = null; _playGlyph.Text = ""; return; }
                var props = await session.TryGetMediaPropertiesAsync();
                var info = session.GetPlaybackInfo();
                _songTitle.Text = string.IsNullOrWhiteSpace(props.Title) ? "Now Playing" : props.Title;
                _songArtist.Text = props.Artist ?? "";
                _playGlyph.Text = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "" : "";
                _art.Source = null;
                if (props.Thumbnail != null)
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    using var net = stream.AsStreamForRead();
                    var ms = new MemoryStream();
                    await net.CopyToAsync(ms);
                    ms.Position = 0;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 120;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    _art.Source = bmp;
                }
            }
            catch { }
        }

        void Refresh()
        {
            _updating = true;
            if (_volume != null) { _volume.Value = AudioVolume.Get() * 100; UpdateVolumeGlyph(); }
            _updating = false;
            RefreshToggles();
            _ = UpdateRadios();
            _ = UpdateMedia();
            if (_brightness != null)
                Task.Run(Brightness.Get).ContinueWith(t =>
                {
                    _updating = true;
                    if (t.Result is int b) { _brightness.IsEnabled = true; _brightness.Value = b; _brightness.ToolTip = null; }
                    else { _brightness.IsEnabled = false; _brightness.Value = 100; _brightness.ToolTip = "Brightness control isn't available for this display"; }
                    _updating = false;
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ================================================================== show / hide

        /// <summary>Opens Control Center, optionally straight on a detail page ("sound", "wifi").</summary>
        public void ShowAnimated(string page = null)
        {
            MarkShown();
            try { Build(); }                   // picks up "Edit Controls" changes
            catch (Exception e) { App.Log(e); }
            // a detail page (Wi-Fi / Sound) fades the main grid out with an animation; that animation still owns
            // the Opacity value until it is removed, so plain assignments below would be ignored and the panel
            // would open empty
            Ui.Stop(_main, OpacityProperty);
            Ui.Stop(_detail, OpacityProperty);
            _detail.Visibility = Visibility.Collapsed;
            _detail.Content = null;
            _main.Visibility = Visibility.Visible;
            _main.Opacity = 1;
            try { Refresh(); } catch (Exception e) { App.Log(e); }
            Left = SystemParameters.PrimaryScreenWidth - W - 4;
            Top = MenuBarWindow.BarHeight + 2;
            Opacity = 0;
            var tt = new TranslateTransform(0, -8);
            _root.RenderTransform = tt;
            Show();
            RefreshBackdrop(null, 260, applyToFrame: false); // tiles float on their own: no shared panel
            Ui.ForceActivate(this);
            Ui.Animate(this, OpacityProperty, 1, 160);
            Ui.Animate(tt, TranslateTransform.YProperty, 0, 260, new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }, RealignGlass);
            if (page == "sound") ShowSound();
            else if (page == "wifi") ShowWifi();
            else if (page == "bluetooth") ShowBluetooth();
            // diagnostics: where the window really is and whether Windows considers it visible
            Dispatcher.BeginInvoke(() =>
            {
                var h = Ui.Hwnd(this);
                Win32.GetWindowRect(h, out var r);
                App.LogInfo($"Control Center shown: rect={r.Left},{r.Top} {r.Width}x{r.Height} winVisible={Win32.IsWindowVisible(h)} opacity={Opacity:0.00} live={LiveBackdrop.Enabled} smooth={LiveBackdrop.Smooth} tiles={_main.Children.Count}");
            }, DispatcherPriority.ApplicationIdle);
        }

        public void HideAnimated()
        {
            MarkHidden();
            Ui.Animate(this, OpacityProperty, 0, 120, done: () => { if (Opacity == 0) Hide(); });
        }
    }

    /// <summary>Built-in display brightness through WMI (laptops / supported panels).</summary>
    public static class Brightness
    {
        public static int? Get()
        {
            try
            {
                using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                foreach (ManagementObject o in s.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
            }
            catch { }
            return null;
        }

        public static void Set(byte value)
        {
            try
            {
                using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject o in s.Get()) o.InvokeMethod("WmiSetBrightness", new object[] { 1u, value });
            }
            catch { }
        }
    }
}
