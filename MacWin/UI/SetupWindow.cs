using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacWin.Services;

namespace MacWin.UI
{
    public enum SetupMode { Install, Uninstall }

    /// <summary>Installer / uninstaller wizard (the same EXE acts as setup, app and uninstaller).</summary>
    public class SetupWindow : MacWindow
    {
        readonly SetupMode _mode;
        readonly StackPanel _steps = new() { Margin = new Thickness(24, 70, 10, 10) };
        readonly ContentControl _page = new();
        readonly Button _back = new() { Content = "Go Back", Margin = new Thickness(0, 0, 8, 0) };
        readonly Button _next = new() { Content = "Continue", MinWidth = 96 };
        readonly string[] _stepNames = { "Introduction", "Options", "Apps", "Installation", "Summary" };
        int _step;
        bool _desktopShortcut = true, _startMenu = true, _busy;
        bool _removeSettings;

        public SetupWindow(SetupMode mode) : base(680, 470)
        {
            _mode = mode;
            Title = mode == SetupMode.Install ? "Install MacWin" : "Uninstall MacWin";
            Lights.CanMinimize = true;
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            Root.ColumnDefinitions.Add(new ColumnDefinition());

            var side = new Border { CornerRadius = new CornerRadius(12, 0, 0, 12) };
            side.SetResourceReference(Border.BackgroundProperty, "Panel.Sidebar");
            side.Child = _steps;
            Root.Children.Insert(0, side);

            var right = new Grid { Margin = new Thickness(26, 46, 26, 20) };
            Grid.SetColumn(right, 1);
            right.RowDefinitions.Add(new RowDefinition());
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(0.5), Padding = new Thickness(22), Child = _page };
            card.SetResourceReference(Border.BackgroundProperty, "Panel.Solid");
            card.SetResourceReference(Border.BorderBrushProperty, "Separator");
            right.Children.Add(card);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            _back.Style = Ui.Style("MacButton");
            _next.Style = Ui.Style("AccentButton");
            _back.Click += (_, _) => Go(_step - 1);
            _next.Click += (_, _) => Next();
            buttons.Children.Add(_back);
            buttons.Children.Add(_next);
            Grid.SetRow(buttons, 1);
            right.Children.Add(buttons);
            Root.Children.Add(right);

            Closing += (_, e) => { if (_busy) e.Cancel = true; };
            Closed += (_, _) => Application.Current.Shutdown();

            if (mode == SetupMode.Install)
            {
                _ = AppCatalog.LoadAsync().ContinueWith(_ => Dispatcher.Invoke(() => { Roles.AutoBind(); if (_step == 2) Go(2); }));
                Go(0);
            }
            else UninstallPage();
        }

        void RenderSteps()
        {
            _steps.Children.Clear();
            var names = _mode == SetupMode.Install ? _stepNames : new[] { "Uninstall" };
            for (int i = 0; i < names.Length; i++)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                if (i <= _step) dot.SetResourceReference(Border.BackgroundProperty, "Accent");
                else dot.SetResourceReference(Border.BackgroundProperty, "Tile.Off");
                sp.Children.Add(dot);
                sp.Children.Add(Ui.Text(names[i], 13, i == _step ? FontWeights.SemiBold : FontWeights.Normal, i == _step ? "Text.Primary" : "Text.Secondary"));
                _steps.Children.Add(sp);
            }
        }

        static TextBlock Heading(string s)
        {
            var t = Ui.Text(s, 17, FontWeights.SemiBold);
            t.Margin = new Thickness(0, 0, 0, 10);
            return t;
        }

        static TextBlock Para(string s)
        {
            var t = Ui.Text(s, 12.5, null, "Text.Primary");
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.None;
            t.Margin = new Thickness(0, 0, 0, 8);
            return t;
        }

        static FrameworkElement Option(string label, bool value, Action<bool> set)
        {
            var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Ui.Text(label, 13));
            var c = new CheckBox { Style = Ui.Style("Switch"), IsChecked = value };
            c.Click += (_, _) => set(c.IsChecked == true);
            Grid.SetColumn(c, 1);
            g.Children.Add(c);
            return g;
        }

        void Go(int step)
        {
            if (step < 0 || step >= _stepNames.Length) return;
            _step = step;
            RenderSteps();
            _back.Visibility = step is > 0 and < 3 ? Visibility.Visible : Visibility.Collapsed;
            _next.Content = step switch { 2 => "Install", 3 => "Installing…", 4 => "Open MacWin", _ => "Continue" };
            _next.IsEnabled = step != 3;
            var s = App.Settings;
            var sp = new StackPanel();
            switch (step)
            {
                case 0:
                    sp.Children.Add(Heading("Welcome to MacWin"));
                    sp.Children.Add(Para("MacWin gives Windows a macOS-style desktop:"));
                    foreach (var f in new[]
                    {
                        "•  Translucent menu bar with app menus, Control Center and clock",
                        "•  Dock with magnification, bouncing launches and running indicators",
                        "•  Launchpad, Spotlight search and hot corners",
                        "•  Mac-style app slots — pick Chrome, Edge, Cốc Cốc… for the Browser, and more",
                        "•  Light / dark appearance and dynamic wallpapers",
                    }) sp.Children.Add(Para(f));
                    sp.Children.Add(Para("It runs in the background at sign-in, uses almost no CPU while idle, and everything can be undone by quitting or uninstalling."));
                    break;
                case 1:
                    sp.Children.Add(Heading("Options"));
                    sp.Children.Add(Option("Open MacWin automatically when you sign in", s.StartAtLogin, v => s.StartAtLogin = v));
                    sp.Children.Add(Option("Hide the Windows taskbar", s.HideTaskbar, v => s.HideTaskbar = v));
                    sp.Children.Add(Option("Hide the Dock when a window is maximised", s.DockSmartHide, v => s.DockSmartHide = v));
                    sp.Children.Add(Option("Hide desktop icons", s.HideDesktopIcons, v => s.HideDesktopIcons = v));
                    sp.Children.Add(Option("Tap the Windows key to open Launchpad", s.WinKeyLaunchpad, v => s.WinKeyLaunchpad = v));
                    sp.Children.Add(Option("Apply MacWin wallpaper", s.Wallpaper != "None", v => s.Wallpaper = v ? "Coast" : "None"));
                    sp.Children.Add(Option("Dark appearance", s.Appearance == AppearanceMode.Dark, v => s.Appearance = v ? AppearanceMode.Dark : AppearanceMode.Light));
                    sp.Children.Add(Option("Real blur effects (turn off on slow PCs)", s.RealBlur, v => s.RealBlur = v));
                    sp.Children.Add(Option("Liquid Glass style", s.LiquidGlass, v => s.LiquidGlass = v));
                    sp.Children.Add(Option("Red / yellow / green buttons on app windows", s.WindowButtons != WindowButtonsMode.Off,
                        v => s.WindowButtons = v ? WindowButtonsMode.Right : WindowButtonsMode.Off));
                    sp.Children.Add(Option("Desktop shortcut", _desktopShortcut, v => _desktopShortcut = v));
                    sp.Children.Add(Option("Start menu shortcut", _startMenu, v => _startMenu = v));
                    break;
                case 2:
                    sp.Children.Add(Heading("Choose your apps"));
                    sp.Children.Add(Para("Each Dock slot opens the app you pick. You can change these later in MacWin Settings ▸ Apps."));
                    if (AppCatalog.Apps.Count == 0) { sp.Children.Add(Para("Looking for installed apps…")); break; }
                    foreach (var key in new[] { "browser", "messages", "mail", "music", "notes", "code" })
                        sp.Children.Add(RolePicker(key));
                    break;
                case 3:
                    sp.Children.Add(Heading("Installing…"));
                    var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 20, 0, 8) };
                    bar.SetResourceReference(ForegroundProperty, "Accent");
                    var status = Para("");
                    sp.Children.Add(bar);
                    sp.Children.Add(status);
                    _page.Content = sp;
                    RunInstall(bar, status);
                    return;
                case 4:
                    sp.Children.Add(Heading("The installation was successful."));
                    sp.Children.Add(Para($"MacWin was installed to {Installer.InstallDir}."));
                    sp.Children.Add(Para("Click Open MacWin to start it now. It will also start automatically when you sign in" + (s.StartAtLogin ? "." : " if you enable that option in Settings.")));
                    sp.Children.Add(Para("To remove it: Windows Settings ▸ Apps ▸ Installed apps ▸ MacWin ▸ Uninstall."));
                    break;
            }
            _page.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        FrameworkElement RolePicker(string key)
        {
            var def = Roles.Def(key);
            var b = Roles.Binding(key);
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var icon = new Image { Width = 30, Height = 30 };
            _ = SetIcon(icon, key);
            g.Children.Add(icon);
            var t = Ui.Text(def.Label, 13);
            t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            var picker = new AppPicker(b.AppId, () => AppPicker.ForRole(key), 260);
            picker.Picked += id => b.AppId = id;
            Grid.SetColumn(picker, 2);
            g.Children.Add(picker);
            return g;
        }

        static async Task SetIcon(Image i, string key)
        {
            try { i.Source = await IconProvider.ForKey(key); } catch { }
        }

        void Next()
        {
            if (_mode == SetupMode.Uninstall) { RunUninstall(); return; }
            if (_step == 4)
            {
                try { Process.Start(new ProcessStartInfo(Installer.InstallPath) { UseShellExecute = true }); } catch { }
                Close();
                return;
            }
            Go(_step + 1);
        }

        async void RunInstall(ProgressBar bar, TextBlock status)
        {
            _busy = true;
            var s = App.Settings;
            s.PinnedApps ??= AppCatalog.Apps.Count > 0 ? Roles.DefaultDock() : null;
            s.FirstRunDone = true;
            s.Save(false);
            try
            {
                await Task.Run(() => Installer.Install(_desktopShortcut, _startMenu, s.StartAtLogin,
                    (p, msg) => Dispatcher.Invoke(() => { Ui.Animate(bar, System.Windows.Controls.Primitives.RangeBase.ValueProperty, p, 300); status.Text = msg; })));
                await Task.Delay(500);
                _busy = false;
                Go(4);
            }
            catch (Exception ex)
            {
                _busy = false;
                status.Text = "Installation failed: " + ex.Message;
                _next.Content = "Try Again";
                _next.IsEnabled = true;
                _step = 2;
            }
        }

        // ------------------------------------------------------------------ uninstall

        void UninstallPage()
        {
            RenderSteps();
            _back.Visibility = Visibility.Collapsed;
            _next.Content = "Uninstall";
            var sp = new StackPanel();
            sp.Children.Add(Heading("Uninstall MacWin?"));
            sp.Children.Add(Para("MacWin will quit, the Windows taskbar will come back, and the app and its shortcuts will be removed."));
            sp.Children.Add(Option("Also restore my original wallpaper and theme", true, v => _restoreLook = v));
            sp.Children.Add(Option("Also delete MacWin settings and custom icons", false, v => _removeSettings = v));
            _page.Content = sp;
        }

        bool _restoreLook = true;

        async void RunUninstall()
        {
            _busy = true;
            _next.IsEnabled = false;
            _next.Content = "Removing…";
            await Task.Run(() =>
            {
                Installer.StopRunningInstances();
                SystemTweaks.RestoreAll(_restoreLook);
                Installer.Uninstall(_removeSettings);
            });
            _busy = false;
            var sp = new StackPanel();
            sp.Children.Add(Heading("MacWin has been removed."));
            sp.Children.Add(Para("Thanks for trying it."));
            _page.Content = sp;
            _next.Content = "Close";
            _next.IsEnabled = true;
            _next.Click += (_, _) => Close();
        }
    }

    /// <summary>Optional start-up animation: logo and progress bar on black.</summary>
    public class SplashWindow : Window
    {
        public SplashWindow(Action done)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            Background = Brushes.Black;
            Left = 0; Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            // original mark: rounded tile with four panes
            var logo = new Grid { Width = 72, Height = 72, Margin = new Thickness(0, 0, 0, 56) };
            logo.Children.Add(new Border { CornerRadius = new CornerRadius(20), BorderBrush = Brushes.White, BorderThickness = new Thickness(7) });
            logo.Children.Add(new Border { Height = 7, Background = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            logo.Children.Add(new Border { Width = 7, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(logo);
            var track = new Border { Width = 190, Height = 5, CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)) };
            var fill = new Border { Width = 0, CornerRadius = new CornerRadius(2.5), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
            track.Child = fill;
            sp.Children.Add(track);
            Content = sp;
            Loaded += (_, _) =>
            {
                Ui.Animate(fill, WidthProperty, 190, 1500, new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
                    () => Ui.Animate(this, OpacityProperty, 0, 400, done: () => { Close(); done(); }));
            };
        }
    }
}
