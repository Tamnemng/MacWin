using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacWin.Services;
using Microsoft.Win32;

namespace MacWin.UI
{
    /// <summary>MacWin preferences in a System Settings–style window.</summary>
    public class SettingsWindow : MacWindow
    {
        readonly StackPanel _nav = new() { Margin = new Thickness(10, 50, 10, 10) };
        readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 0, 18, 20) };
        readonly TextBlock _pageTitle = Ui.Text("", 15, FontWeights.Bold);
        readonly Dictionary<string, Border> _navItems = new();
        string _page;

        static readonly (string key, string glyph, string color)[] Pages =
        {
            ("General", "", "#8E8E93"),
            ("Appearance", "", "#1C1C1E"),
            ("Apps", "", "#0A84FF"),
            ("Dock", "", "#5E5CE6"),
            ("Menu Bar", "", "#34C759"),
            ("Control Center", "\uE70F", "#0A84FF"),
            ("Notifications", "\uEA8F", "#FF3B30"),
            ("Hot Corners", "", "#FF9F0A"),
            ("Wallpaper", "", "#32ADE6"),
            ("Keyboard", "", "#8E8E93"),
            ("About", "", "#636366"),
        };

        public SettingsWindow() : base(860, 600)
        {
            Title = "MacWin Settings";
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // sidebar (translucent) and content (solid), like System Settings.
            // Liquid glass: the sidebar becomes a floating glass pane inset from the window edge.
            bool liquid = ThemeManager.IsLiquid;
            var sidebar = new Border
            {
                CornerRadius = liquid ? new CornerRadius(Radius - 5) : new CornerRadius(Radius, 0, 0, Radius),
                Margin = liquid ? new Thickness(7) : new Thickness(0),
                Child = _nav
            };
            sidebar.SetResourceReference(Border.BackgroundProperty, "Panel.Sidebar");
            if (liquid)
            {
                Ui.Glassify(sidebar);
                sidebar.SetResourceReference(Border.BorderBrushProperty, "Glass.Rim");
                _nav.Margin = new Thickness(4, 40, 4, 6);
            }
            Root.Children.Insert(0, sidebar);

            var content = new Border
            {
                CornerRadius = liquid ? new CornerRadius(0, Radius, Radius, 0) : new CornerRadius(0, Radius, Radius, 0),
                BorderThickness = liquid ? new Thickness(0) : new Thickness(0.5, 0, 0, 0),
                Background = liquid ? Brushes.Transparent : null
            };
            if (!liquid) content.SetResourceReference(Border.BackgroundProperty, "Panel.Solid");
            content.SetResourceReference(Border.BorderBrushProperty, "Separator");
            Grid.SetColumn(content, 1);
            var contentGrid = new Grid();
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            contentGrid.RowDefinitions.Add(new RowDefinition());
            _pageTitle.VerticalAlignment = VerticalAlignment.Center;
            _pageTitle.Margin = new Thickness(24, 0, 0, 0);
            contentGrid.Children.Add(_pageTitle);
            Grid.SetRow(_scroll, 1);
            contentGrid.Children.Add(_scroll);
            content.Child = contentGrid;
            Root.Children.Insert(1, content);

            foreach (var p in Pages) _nav.Children.Add(NavItem(p.key, p.glyph, p.color));
            Show("General");
            KeyDown += (_, e) => { if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control) Close(); };
        }

        Border NavItem(string key, string glyph, string color)
        {
            var icon = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 9, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                Child = new TextBlock { Text = glyph, FontFamily = Ui.IconFont, FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(icon);
            var t = Ui.Text(key, 13);
            t.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 1, 0, 1), Child = sp, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            b.MouseLeftButtonUp += (_, _) => Show(key);
            _navItems[key] = b;
            return b;
        }

        public void Show(string page)
        {
            if (page == null || !_navItems.ContainsKey(page)) page = "General";
            _page = page;
            foreach (var (k, b) in _navItems)
            {
                var text = ((StackPanel)b.Child).Children.OfType<TextBlock>().First();
                if (k == page) { b.SetResourceReference(Border.BackgroundProperty, "Accent"); text.Foreground = Brushes.White; }
                else { b.Background = Brushes.Transparent; text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary"); }
            }
            _pageTitle.Text = page;
            var body = new StackPanel();
            switch (page)
            {
                case "General": General(body); break;
                case "Appearance": Appearance(body); break;
                case "Apps": AppsPage(body); break;
                case "Dock": DockPage(body); break;
                case "Menu Bar": MenuBarPage(body); break;
                case "Control Center": ControlCenterPage(body); break;
                case "Notifications": NotificationsPage(body); break;
                case "Hot Corners": HotCornersPage(body); break;
                case "Wallpaper": WallpaperPage(body); break;
                case "Keyboard": KeyboardPage(body); break;
                case "About": AboutPage(body); break;
            }
            _scroll.Content = body;
            _scroll.ScrollToTop();
        }

        // ------------------------------------------------------------------ building blocks

        static AppSettings S => App.Settings;
        static void Save() => S.Save();

        static StackPanel Group(StackPanel parent, string title = null, string footer = null)
        {
            if (title != null)
            {
                var t = Ui.Text(title, 13, FontWeights.SemiBold);
                t.Margin = new Thickness(4, 14, 0, 6);
                parent.Children.Add(t);
            }
            var box = new StackPanel();
            var card = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(0.5), Padding = new Thickness(12, 2, 12, 2), Child = box, Margin = new Thickness(0, title == null ? 10 : 0, 0, 0) };
            card.SetResourceReference(Border.BackgroundProperty, "Input.Background");
            card.SetResourceReference(Border.BorderBrushProperty, "Separator");
            parent.Children.Add(card);
            if (footer != null)
            {
                var f = Ui.Text(footer, 11.5, null, "Text.Secondary");
                f.TextWrapping = TextWrapping.Wrap;
                f.Margin = new Thickness(4, 6, 4, 0);
                parent.Children.Add(f);
            }
            return box;
        }

        static void Row(StackPanel group, string label, FrameworkElement control, string hint = null)
        {
            if (group.Children.Count > 0)
            {
                var sep = new Border { Height = 1 };
                sep.SetResourceReference(Border.BackgroundProperty, "Separator");
                group.Children.Add(sep);
            }
            var g = new Grid { MinHeight = 40 };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 12, 6) };
            sp.Children.Add(Ui.Text(label, 13));
            if (hint != null)
            {
                var h = Ui.Text(hint, 11, null, "Text.Secondary");
                h.TextWrapping = TextWrapping.Wrap;
                sp.Children.Add(h);
            }
            g.Children.Add(sp);
            if (control != null)
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 1);
                g.Children.Add(control);
            }
            group.Children.Add(g);
        }

        static CheckBox Switch(bool value, Action<bool> set)
        {
            var c = new CheckBox { IsChecked = value, Style = Ui.Style("Switch") };
            c.Click += (_, _) => { set(c.IsChecked == true); Save(); };
            return c;
        }

        static Slider SliderFor(double min, double max, double value, Action<double> set, double width = 220)
        {
            var s = new Slider { Minimum = min, Maximum = max, Value = value, Width = width, Style = Ui.Style("MacSlider") };
            s.ValueChanged += (_, _) => set(s.Value);
            s.PreviewMouseLeftButtonUp += (_, _) => Save();
            s.LostMouseCapture += (_, _) => Save();
            return s;
        }

        static ComboBox Combo<T>(IEnumerable<T> values, T current, Func<T, string> text, Action<T> set, double width = 200)
        {
            var c = new ComboBox { Width = width };
            foreach (var v in values) c.Items.Add(new ComboBoxItem { Content = text(v), Tag = v, IsSelected = Equals(v, current) });
            c.SelectionChanged += (_, _) => { if (c.SelectedItem is ComboBoxItem i && i.Tag is T v) { set(v); Save(); } };
            return c;
        }

        /// <summary>Live preview while dragging the transparency slider (saved on release).</summary>
        static void ApplyThemeLive() => ThemeManager.Apply(ThemeManager.IsDark, S.Accent);

        static Button Btn(string text, Action click, bool accent = false)
        {
            var b = new Button { Content = text, Style = Ui.Style(accent ? "AccentButton" : "MacButton"), Margin = new Thickness(6, 0, 0, 0) };
            b.Click += (_, _) => click();
            return b;
        }

        // ------------------------------------------------------------------ pages

        void General(StackPanel body)
        {
            var g = Group(body, null, "MacWin hides the Windows taskbar while running and restores it when you quit.");
            Row(g, "Open MacWin when you log in", Switch(S.StartAtLogin, v => S.StartAtLogin = v), "Starts right after sign-in (Task Scheduler ▸ MacWin)");
            Row(g, "Hide the Windows taskbar", Switch(S.HideTaskbar, v => S.HideTaskbar = v));
            Row(g, "Hide desktop icons", Switch(S.HideDesktopIcons, v => S.HideDesktopIcons = v));
            var g2 = Group(body, "Startup");
            Row(g2, "Play start-up chime", Switch(S.StartupChime, v => S.StartupChime = v));
            Row(g2, "Show boot animation", Switch(S.BootAnimation, v => S.BootAnimation = v));
            var g3 = Group(body, "Performance", "Turn off real blur and animations on slower PCs. Idle CPU use is near zero either way.");
            Row(g3, "Real blur (frosted glass)", Switch(S.RealBlur, v => S.RealBlur = v));
            Row(g3, "Reduce motion", Switch(S.ReduceMotion, v => S.ReduceMotion = v));
            var g4 = Group(body, "Spotlight");
            Row(g4, "Web search engine", Combo(new[] { "Google", "Bing", "DuckDuckGo" }, S.SearchEngine, x => x, v => S.SearchEngine = v, 160));
        }

        void Appearance(StackPanel body)
        {
            var g = Group(body);
            var modes = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var m in new[] { AppearanceMode.Light, AppearanceMode.Dark, AppearanceMode.Auto })
                modes.Children.Add(AppearanceChoice(m));
            Row(g, "Appearance", modes);
            Row(g, "Also change Windows apps", Switch(S.ControlWindowsTheme, v => S.ControlWindowsTheme = v), "Sets Windows' own light/dark mode to match");

            var accents = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (name, color) in ThemeManager.Accents)
            {
                var dot = new Border
                {
                    Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(4, 0, 4, 0), Cursor = Cursors.Hand, ToolTip = name,
                    Background = new SolidColorBrush(color), BorderThickness = new Thickness(name == S.Accent ? 3 : 0), BorderBrush = Brushes.White
                };
                if (name == S.Accent) dot.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.5 };
                dot.MouseLeftButtonUp += (_, _) => { S.Accent = name; Save(); Show("Appearance"); };
                accents.Children.Add(dot);
            }
            Row(g, "Accent colour", accents);

            var glass = Group(body, "Material", "Liquid Glass: clearer surfaces, light-catching edges, a floating Dock with a glint that follows the pointer. Needs “Real blur” (General) for the frosted look.");
            Row(glass, "Liquid Glass", Switch(S.LiquidGlass, v => S.LiquidGlass = v));
            Row(glass, "Live glass", Switch(S.LiveGlass, v => S.LiveGlass = v), "Glass keeps showing what is really behind it (windows, video). Off = a snapshot taken when a panel opens; a little lighter on the CPU.");
            Row(glass, "Live glass rate", Combo(new[] { true, false }, S.LiveGlassSmooth, v => v ? "Every frame" : "10 per second", v => S.LiveGlassSmooth = v, 170),
                "Every frame: no visible lag, but the Dock and panels are left out of screenshots and screen sharing while on. 10 per second: screenshots include them.");
            var clarity = SliderFor(0, 100, S.GlassClarity * 100, v => { S.GlassClarity = v / 100; ApplyThemeLive(); }, 200);
            clarity.IsEnabled = S.LiquidGlass;
            var clarityRow = new StackPanel { Orientation = Orientation.Horizontal };
            var l1 = Ui.Text("Frosted", 11, null, "Text.Secondary"); l1.VerticalAlignment = VerticalAlignment.Center; l1.Margin = new Thickness(0, 0, 8, 0);
            var l2 = Ui.Text("Clear", 11, null, "Text.Secondary"); l2.VerticalAlignment = VerticalAlignment.Center; l2.Margin = new Thickness(8, 0, 0, 0);
            clarityRow.Children.Add(l1);
            clarityRow.Children.Add(clarity);
            clarityRow.Children.Add(l2);
            Row(glass, "Transparency", clarityRow);

            var wb = Group(body, "Window buttons", "Adds red / yellow / green buttons to the title bar of the window you're using. “Right” covers the standard ✕ □ – buttons; “Left” is the classic Mac position but may cover the first browser tab.");
            Row(wb, "Mac-style window buttons", Combo(new[] { WindowButtonsMode.Off, WindowButtonsMode.Right, WindowButtonsMode.Left }, S.WindowButtons,
                m => m switch { WindowButtonsMode.Off => "Off", WindowButtonsMode.Right => "On — right side", _ => "On — left side" }, v => S.WindowButtons = v, 170));

            var g2 = Group(body, "Icons");
            Row(g2, "Uniform rounded icons", Switch(S.SquircleIcons, v => { S.SquircleIcons = v; IconProvider.Invalidate(); }), "Places every app icon on the same rounded-square shape");
            Row(g2, "Custom icon folder", Btn("Open Folder", () =>
            {
                System.IO.Directory.CreateDirectory(IconProvider.UserIconFolder);
                Shell.Open(IconProvider.UserIconFolder);
            }), "Drop PNG/ICO files named after a slot (e.g. browser.png) or an app name to use your own icons");
        }

        FrameworkElement AppearanceChoice(AppearanceMode m)
        {
            bool sel = S.Appearance == m;
            var preview = new Border { Width = 66, Height = 44, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(sel ? 2.5 : 0.5), Margin = new Thickness(0, 0, 0, 4) };
            if (sel) preview.SetResourceReference(Border.BorderBrushProperty, "Accent");
            else preview.SetResourceReference(Border.BorderBrushProperty, "Separator");
            var light = new LinearGradientBrush(Color.FromRgb(0xEE, 0xF1, 0xF6), Color.FromRgb(0xBF, 0xD4, 0xEE), 90);
            var dark = new LinearGradientBrush(Color.FromRgb(0x2C, 0x2C, 0x2E), Color.FromRgb(0x10, 0x18, 0x2A), 90);
            preview.Background = m == AppearanceMode.Light ? light : m == AppearanceMode.Dark ? dark
                : new LinearGradientBrush(new GradientStopCollection { new(Color.FromRgb(0xEE, 0xF1, 0xF6), 0.5), new(Color.FromRgb(0x2C, 0x2C, 0x2E), 0.5) }, 0);
            var t = Ui.Text(m.ToString(), 11, sel ? FontWeights.SemiBold : FontWeights.Normal);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            var sp = new StackPanel { Margin = new Thickness(6, 4, 6, 4), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            sp.Children.Add(preview);
            sp.Children.Add(t);
            sp.MouseLeftButtonUp += (_, _) => { S.Appearance = m; Save(); Show("Appearance"); };
            return sp;
        }

        void AppsPage(StackPanel body)
        {
            var intro = Ui.Text("Each Dock slot has a Mac-style icon and opens the Windows app you choose. Rename slots freely — for example call the Browser slot whatever you like.", 12, null, "Text.Secondary");
            intro.TextWrapping = TextWrapping.Wrap;
            intro.Margin = new Thickness(4, 6, 4, 4);
            body.Children.Add(intro);
            var g = Group(body);
            foreach (var def in Roles.All)
            {
                var b = Roles.Binding(def.Key);
                if (g.Children.Count > 0)
                {
                    var sep = new Border { Height = 1 };
                    sep.SetResourceReference(Border.BackgroundProperty, "Separator");
                    g.Children.Add(sep);
                }
                g.Children.Add(RoleRow(def, b));
            }
            S.Save(false);
        }

        FrameworkElement RoleRow(RoleDef def, RoleBinding b)
        {
            var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new Image { Width = 36, Height = 36, Cursor = Cursors.Hand, ToolTip = "Change icon…" };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            async void LoadIcon() { try { icon.Source = await IconProvider.ForRole(def.Key); } catch { } }
            LoadIcon();
            icon.MouseLeftButtonUp += (_, _) => { if (App.ChooseIcon(def.Key)) LoadIcon(); };
            grid.Children.Add(icon);

            var name = new TextBox { Text = b.Label, Style = Ui.Style("FieldTextBox"), Width = 126, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = def.Description };
            name.LostFocus += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(name.Text)) name.Text = def.Label;
                if (b.Label == name.Text) return;
                b.Label = name.Text.Trim();
                S.Save(false);
                App.RefreshRoleUi();
            };
            name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            // searchable: type "chrome", "word"… to find any installed app
            var picker = new AppPicker(b.AppId, () => AppPicker.ForRole(def.Key), 230) { Margin = new Thickness(4, 0, 0, 0) };
            picker.Picked += id =>
            {
                b.AppId = id;
                S.Save(false);
                IconProvider.Invalidate();
                LoadIcon();
                App.RefreshRoleUi();
            };
            Grid.SetColumn(picker, 2);
            grid.Children.Add(picker);

            string key = "role:" + def.Key;
            var inDock = new CheckBox { Style = Ui.Style("Switch"), IsChecked = S.PinnedApps?.Contains(key) == true, ToolTip = "Show in Dock" };
            inDock.Click += (_, _) =>
            {
                S.PinnedApps ??= new List<string>();
                if (inDock.IsChecked == true) { if (!S.PinnedApps.Contains(key)) S.PinnedApps.Add(key); }
                else S.PinnedApps.Remove(key);
                S.Save(false);
                App.Dock?.Rebuild();
            };
            var macIcon = new CheckBox { Content = null, Style = Ui.Style("Switch"), IsChecked = b.MacIcon, ToolTip = "Mac-style icon", Margin = new Thickness(0, 0, 10, 0) };
            macIcon.Click += (_, _) => { b.MacIcon = macIcon.IsChecked == true; S.Save(false); IconProvider.Invalidate(); LoadIcon(); App.Dock?.ReloadIcons(); };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var l1 = Ui.Text("Mac icon", 11, null, "Text.Secondary"); l1.VerticalAlignment = VerticalAlignment.Center; l1.Margin = new Thickness(0, 0, 6, 0);
            var l2 = Ui.Text("Dock", 11, null, "Text.Secondary"); l2.VerticalAlignment = VerticalAlignment.Center; l2.Margin = new Thickness(0, 0, 6, 0);
            right.Children.Add(l1);
            right.Children.Add(macIcon);
            right.Children.Add(l2);
            right.Children.Add(inDock);
            Grid.SetColumn(right, 3);
            grid.Children.Add(right);
            return grid;
        }

        void DockPage(StackPanel body)
        {
            var g = Group(body);
            Row(g, "Size", SliderFor(32, 96, S.DockSize, v => { S.DockSize = Math.Round(v); App.Dock?.ApplySettings(); }));
            Row(g, "Magnification", Switch(S.Magnification, v => S.Magnification = v));
            Row(g, "Magnified size", SliderFor(40, 140, S.MagnifiedSize, v => { S.MagnifiedSize = Math.Round(v); }));
            Row(g, "Position on screen", Combo(new[] { DockPosition.Left, DockPosition.Bottom, DockPosition.Right }, S.DockPosition, x => x.ToString(), v => S.DockPosition = v, 140));
            var g2 = Group(body, "Behaviour");
            Row(g2, "Hide the Dock when a window is maximised", Switch(S.DockSmartHide, v => S.DockSmartHide = v),
                "Also hides when a window overlaps it. Push the pointer to the screen edge to bring it back.");
            Row(g2, "Always hide and show the Dock automatically", Switch(S.DockAutoHide, v => S.DockAutoHide = v));
            Row(g2, "Animate opening applications", Switch(S.BounceOnLaunch, v => S.BounceOnLaunch = v));
            Row(g2, "Show indicators for open applications", Switch(S.ShowIndicators, v => S.ShowIndicators = v));
            Row(g2, "Show window previews on hover", Switch(S.DockWindowPreviews, v => S.DockWindowPreviews = v),
                "When an app has two or more windows, resting the pointer on its icon shows them to choose from.");
            Row(g2, "Show running apps that aren't in the Dock", Switch(S.ShowRunningApps, v => S.ShowRunningApps = v));
            Row(g2, "Click the active app's icon to minimise it", Switch(S.ClickToMinimize, v => S.ClickToMinimize = v));
            DockItemsGroup(body);
            var g3 = Group(body, "Items", "You can also drag an app, file or folder onto the Dock, right-click an app in Launchpad ▸ Add to Dock, or right-click a running app ▸ Options ▸ Keep in Dock.");
            Row(g3, "Reset Dock to default apps", Btn("Reset", () => { S.PinnedApps = Roles.DefaultDock(); Save(); App.Dock?.Rebuild(); Show("Dock"); }));
        }

        /// <summary>"Apps in Dock": add (searchable), remove and reorder the pinned items.</summary>
        void DockItemsGroup(StackPanel body)
        {
            var g = Group(body, "Apps in Dock");
            var add = new AppPicker(null, AppPicker.AllApps, 260, "+ Add App to Dock…");
            add.Picked += id => { App.Dock?.PinApp(id); Show("Dock"); };
            Row(g, "Add an app", add, "Type to search, e.g. “chrome”, “word”");

            var pinned = S.PinnedApps ?? new List<string>();
            for (int i = 0; i < pinned.Count; i++)
            {
                int index = i;
                string key = pinned[i];
                string name = key switch
                {
                    "sys:launchpad" => "Launchpad",
                    _ when key.StartsWith("role:") => (Roles.Binding(key[5..])?.Label ?? key[5..]) + "  —  " + AppCatalog.DisplayName(Roles.Binding(key[5..])?.AppId),
                    _ when key.StartsWith("app:") => AppCatalog.DisplayName(key[4..]),
                    _ => key
                };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                Button Small(string glyph, string tip, Action a, bool enabled = true)
                {
                    var b = new Button { Content = Ui.Glyph(glyph, 10), Style = Ui.Style("MacButton"), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4, 0, 0, 0), ToolTip = tip, IsEnabled = enabled };
                    b.Click += (_, _) => a();
                    return b;
                }
                buttons.Children.Add(Small("", "Move left", () => Move(index, -1), index > 0));
                buttons.Children.Add(Small("", "Move right", () => Move(index, 1), index < pinned.Count - 1));
                bool removable = key != "role:files";
                buttons.Children.Add(Small("", "Remove from Dock", () => { S.PinnedApps.RemoveAt(index); S.Save(false); App.Dock?.Rebuild(); Show("Dock"); }, removable));
                Row(g, name, buttons);
            }

            void Move(int i, int delta)
            {
                var list = S.PinnedApps;
                int j = i + delta;
                if (list == null || j < 0 || j >= list.Count) return;
                (list[i], list[j]) = (list[j], list[i]);
                S.Save(false);
                App.Dock?.Rebuild();
                Show("Dock");
            }
        }

        /// <summary>"Edit Controls": which modules and buttons Control Center shows (buttons appear in the order turned on).</summary>
        void ControlCenterPage(StackPanel body)
        {
            var mods = Group(body, "Modules");
            Row(mods, "Now Playing", Switch(S.ControlCenterNowPlaying, v => S.ControlCenterNowPlaying = v));
            Row(mods, "Display brightness", Switch(S.ControlCenterDisplay, v => S.ControlCenterDisplay = v));
            Row(mods, "Sound", Switch(S.ControlCenterSound, v => S.ControlCenterSound = v));
            var btns = Group(body, "Buttons", "Up to 8 buttons, shown in the order you turn them on.");
            foreach (var (key, def) in ControlCenterWindow.Buttons)
            {
                string k = key;
                var icon = Ui.Glyph(def.Glyph, 14);
                icon.Margin = new Thickness(0, 0, 10, 0);
                var label = new StackPanel { Orientation = Orientation.Horizontal };
                label.Children.Add(icon);
                Row(btns, def.Label, Switch(S.ControlCenterButtons?.Contains(k) == true, v =>
                {
                    S.ControlCenterButtons ??= new List<string>();
                    S.ControlCenterButtons.Remove(k);
                    if (v) S.ControlCenterButtons.Add(k);
                }));
            }
        }
        void NotificationsPage(StackPanel body)
        {
            string status = !S.MacNotifications ? "Off — Windows shows its own notifications."
                : Notifications.Running ? $"Reading notifications · {Notifications.All.Count} in Notification Center"
                : Notifications.Access == Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Denied
                    ? "Windows denied access — allow it in Settings ▸ Privacy & security ▸ Notifications."
                    : "Not available on this version of Windows — Windows keeps its own notifications.";
            var g = Group(body, null, "Banners slide in at the top right and Notification Center opens from the clock. Click a banner to open its app, swipe it right to dismiss.");
            Row(g, "Mac-style notifications", Switch(S.MacNotifications, v => S.MacNotifications = v), status);
            Row(g, "Show banners", Switch(S.NotificationBanners, v => S.NotificationBanners = v));
            Row(g, "Hide Windows' own banners", Switch(S.HideWindowsBanners, v => S.HideWindowsBanners = v),
                "So each notification appears once. Notifications with buttons (calls, alarms) keep the Windows banner. Undone when MacWin quits.");
            Row(g, "Show previews", Switch(S.NotificationPreviews, v => S.NotificationPreviews = v));
            Row(g, "Banner time", SliderFor(3, 15, S.BannerSeconds, v => S.BannerSeconds = Math.Round(v), 160), "Seconds a banner stays on screen");
            var g2 = Group(body, "Focus");
            Row(g2, "Do Not Disturb", Switch(S.DoNotDisturb, v => S.DoNotDisturb = v), "No banners; notifications still collect in Notification Center");
            Row(g2, "Windows notification settings", Btn("Open…", () => Shell.Open("ms-settings:notifications")));
            Row(g2, "Test banner", Btn("Show", () => Banners.Show(new MacNote
            {
                Id = 0, AppName = "MacWin", Title = "MacWin", Body = "This is how notifications look.", Time = DateTime.Now
            })));
        }

        void MenuBarPage(StackPanel body)
        {
            var items = Group(body, "Show in menu bar");
            Row(items, "Spotlight", Switch(S.ShowSpotlightIcon, v => S.ShowSpotlightIcon = v));
            Row(items, "Control Center", Switch(S.ShowControlCenterIcon, v => S.ShowControlCenterIcon = v));
            Row(items, "Wi-Fi", Switch(S.ShowWifiIcon, v => S.ShowWifiIcon = v), "Click it for the network list");
            Row(items, "Sound", Switch(S.ShowVolumeIcon, v => S.ShowVolumeIcon = v), "Click it for volume and output devices");
            Row(items, "Disk free space", Switch(S.ShowDiskIcon, v => S.ShowDiskIcon = v));

            var clock = Group(body, "Clock position & format", "Custom format uses .NET date patterns, e.g.  ddd d MMM  HH:mm   ·   dd/MM/yyyy HH:mm   ·   dddd, d MMMM   ·   HH:mm:ss. Leave empty for the standard style.");
            Row(clock, "Position", Combo(new[] { false, true }, S.ClockCentered, c => c ? "Centre of the menu bar" : "Right side", v => S.ClockCentered = v, 190));
            var fmt = new TextBox { Text = S.ClockFormat ?? "", Style = Ui.Style("FieldTextBox"), Width = 190 };
            void ApplyFmt() { if ((S.ClockFormat ?? "") != fmt.Text.Trim()) { S.ClockFormat = fmt.Text.Trim(); Save(); } }
            fmt.LostFocus += (_, _) => ApplyFmt();
            fmt.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyFmt(); };
            var presets = Combo(new[] { "", "ddd d MMM   HH:mm", "dddd dd/MM/yyyy  HH:mm", "dd/MM/yyyy  HH:mm:ss", "HH:mm", "h:mm tt" }, S.ClockFormat ?? "",
                f => f.Length == 0 ? "Standard" : DateTime.Now.ToString(f), v => { S.ClockFormat = v; fmt.Text = v; }, 190);
            Row(clock, "Preset", presets);
            Row(clock, "Custom format", fmt);

            var tray = Group(body, "Background apps", "Hidden icons sit behind an arrow ‹ in the menu bar — click it to show them. Turn on an app below to keep its icon always visible.");
            Row(tray, "Collapse behind an arrow", Switch(S.CollapseTrayIcons, v => S.CollapseTrayIcons = v));
            foreach (var owner in TrayHost.Icons.Select(i => i.Owner).Where(o => !string.IsNullOrEmpty(o)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(o => o))
            {
                string o = owner;
                Row(tray, "Always show " + System.IO.Path.GetFileNameWithoutExtension(o), Switch(S.AlwaysShowTray?.Contains(o) == true, v =>
                {
                    S.AlwaysShowTray ??= new List<string>();
                    S.AlwaysShowTray.RemoveAll(x => string.Equals(x, o, StringComparison.OrdinalIgnoreCase));
                    if (v) S.AlwaysShowTray.Add(o);
                }));
            }

            var g = Group(body, "Clock");
            Row(g, "Show date", Switch(S.ShowDate, v => S.ShowDate = v));
            Row(g, "Use 24-hour clock", Switch(S.Clock24h, v => S.Clock24h = v));
            Row(g, "Show seconds", Switch(S.ShowSeconds, v => S.ShowSeconds = v));
            var g2 = Group(body, "Battery");
            Row(g2, "Show percentage", Switch(S.ShowBatteryPercent, v => S.ShowBatteryPercent = v));
            var g3 = Group(body, "Status items", "Background apps (Unikey, Zalo, OneDrive, Discord…) show their icons in the menu bar while the Windows taskbar is hidden. Left-click, right-click and double-click work as on the taskbar.");
            Row(g3, "Show background app icons", Switch(S.ShowTrayIcons, v => S.ShowTrayIcons = v));
            Row(g3, "Show keyboard input source", Switch(S.ShowInputSource, v => S.ShowInputSource = v), "EN / VI … — click to switch, or press Win+Space");
            Row(g3, "Show CPU and memory usage", Switch(S.ShowStatsInMenuBar, v => S.ShowStatsInMenuBar = v), "Click it for Activity: CPU, memory, GPU, VRAM, disk, network and top apps");
        }

        void HotCornersPage(StackPanel body)
        {
            var actions = Enum.GetValues<CornerAction>();
            ComboBox C(CornerAction cur, Action<CornerAction> set) => Combo(actions, cur, HotCorners.Describe, set, 170);
            var grid = new Grid { Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < 2; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var screen = new Border
            {
                Width = 240, Height = 150, CornerRadius = new CornerRadius(8), Margin = new Thickness(14), BorderThickness = new Thickness(4),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)), Background = new ImageBrush(Wallpapers.Thumbnail(Wallpapers.All[0], ThemeManager.IsDark, 240, 150))
            };
            Grid.SetColumn(screen, 1);
            Grid.SetRowSpan(screen, 2);
            grid.Children.Add(screen);
            void Place(FrameworkElement e, int col, int row, VerticalAlignment va) { Grid.SetColumn(e, col); Grid.SetRow(e, row); e.VerticalAlignment = va; grid.Children.Add(e); }
            Place(C(S.CornerTopLeft, v => S.CornerTopLeft = v), 0, 0, VerticalAlignment.Top);
            Place(C(S.CornerTopRight, v => S.CornerTopRight = v), 2, 0, VerticalAlignment.Top);
            Place(C(S.CornerBottomLeft, v => S.CornerBottomLeft = v), 0, 1, VerticalAlignment.Bottom);
            Place(C(S.CornerBottomRight, v => S.CornerBottomRight = v), 2, 1, VerticalAlignment.Bottom);
            body.Children.Add(grid);
            var hint = Ui.Text("Move the pointer into a corner of the screen to start the action.", 12, null, "Text.Secondary");
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.Margin = new Thickness(0, 16, 0, 0);
            body.Children.Add(hint);
        }

        void WallpaperPage(StackPanel body)
        {
            var g = Group(body);
            Row(g, "Dynamic (match light / dark)", Switch(S.DynamicWallpaper, v => S.DynamicWallpaper = v));
            Row(g, "Your own picture", Btn("Choose…", () =>
            {
                var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All files|*.*" };
                if (dlg.ShowDialog(this) == true) { S.CustomWallpaper = dlg.FileName; Save(); Show("Wallpaper"); }
            }));
            Row(g, "Original wallpaper", Btn("Restore", () => { S.Wallpaper = "None"; S.CustomWallpaper = null; Save(); SystemTweaks.RestoreWallpaper(); Show("Wallpaper"); }));

            var wrap = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            foreach (var p in Wallpapers.All)
            {
                bool sel = S.Wallpaper == p.Name && string.IsNullOrEmpty(S.CustomWallpaper);
                var img = new Image { Source = Wallpapers.Thumbnail(p, ThemeManager.IsDark, 168, 105), Width = 168, Height = 105 };
                var frame = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = img, BorderThickness = new Thickness(sel ? 3 : 0) };
                frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
                var t = Ui.Text(p.Name, 12, sel ? FontWeights.SemiBold : FontWeights.Normal);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.Margin = new Thickness(0, 4, 0, 0);
                var sp = new StackPanel { Margin = new Thickness(6), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                sp.Children.Add(frame);
                sp.Children.Add(t);
                sp.MouseLeftButtonUp += (_, _) => { S.Wallpaper = p.Name; S.CustomWallpaper = null; Save(); Show("Wallpaper"); };
                wrap.Children.Add(sp);
            }
            body.Children.Add(wrap);
        }

        void KeyboardPage(StackPanel body)
        {
            var g = Group(body, null, "Tapping the Windows key alone opens Launchpad; Windows key shortcuts (Win+E, Win+L…) keep working.");
            Row(g, "Tap Windows key to open Launchpad", Switch(S.WinKeyLaunchpad, v => S.WinKeyLaunchpad = v));
            Row(g, "Mac-style shortcuts", Switch(S.MacShortcuts, v => S.MacShortcuts = v), "Alt+Space Spotlight · Alt+Q quit app · Ctrl+Alt+Space Control Center");
            var g2 = Group(body, "Shortcuts");
            foreach (var (k, d) in new[]
            {
                ("Alt + Space", "Spotlight"), ("Win (tap)", "Launchpad"), ("Ctrl + Alt + Space", "Control Center"), ("Alt + Q", "Quit the front app"),
                ("Win + Tab", "Mission Control"), ("Win + D", "Show Desktop"), ("Ctrl + Shift + Esc", "Task Manager / Force Quit"), ("Win + Shift + S", "Screenshot"),
            })
            {
                var t = Ui.Text(k, 12, FontWeights.SemiBold, "Text.Secondary");
                Row(g2, d, t);
            }
        }

        void AboutPage(StackPanel body)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
            var icon = new Image { Width = 96, Height = 96 };
            _ = SetIcon(icon);
            sp.Children.Add(icon);
            var name = Ui.Text("MacWin", 26, FontWeights.SemiBold);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(name);
            var ver = Ui.Text("Version " + Installer.Version, 12, null, "Text.Secondary");
            ver.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(ver);
            var desc = Ui.Text("A macOS-style desktop for Windows: menu bar, Dock, Launchpad, Spotlight, Control Center and hot corners. Not affiliated with Apple Inc.", 12, null, "Text.Secondary");
            desc.TextWrapping = TextWrapping.Wrap;
            desc.TextAlignment = TextAlignment.Center;
            desc.MaxWidth = 420;
            desc.Margin = new Thickness(0, 12, 0, 18);
            sp.Children.Add(desc);
            body.Children.Add(sp);

            var g = Group(body);
            Row(g, "Settings & custom icons folder", Btn("Open", () => Shell.Open(AppSettings.Folder)));
            Row(g, "Restore Windows (taskbar, wallpaper, theme) and quit", Btn("Restore & Quit", () => App.Quit(restoreAppearance: true)));
            Row(g, "Uninstall MacWin", Btn("Uninstall…", () => App.StartUninstall()));
            Row(g, "Open-source licences", Btn("View", ShowLicences), "Liquid Glass shader based on liquid-glass by Archis Vaze (MIT)");
        }

        const string ThirdPartyNotices = @"MacWin uses the following open-source software.

liquid-glass — https://github.com/archisvaze/liquid-glass
(the Liquid Glass refraction shader was ported from its WebGL shader)

MIT License

Copyright (c) 2026 Archis Vaze

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
";

        static void ShowLicences()
        {
            try
            {
                var path = System.IO.Path.Combine(AppSettings.Folder, "ThirdPartyNotices.txt");
                System.IO.Directory.CreateDirectory(AppSettings.Folder);
                System.IO.File.WriteAllText(path, ThirdPartyNotices);
                Shell.Open(path);
            }
            catch (Exception e) { App.Log(e); }
        }

        static async System.Threading.Tasks.Task SetIcon(Image img)
        {
            try { img.Source = await IconProvider.ForKey("launchpad"); } catch { }
        }
    }
}
