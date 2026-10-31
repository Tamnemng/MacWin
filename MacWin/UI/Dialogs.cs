using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacWin.Native;
using MacWin.Services;
using Microsoft.Win32;

namespace MacWin.UI
{
    /// <summary>A small alert sheet with an icon, message and two buttons.</summary>
    public class MacDialog : PanelWindow
    {
        bool _result;

        MacDialog(string message, string okText, string detail) : base(ThemeManager.PanelRadius(14), false, 0x50)
        {
            Width = 280;
            SizeToContent = SizeToContent.Height;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var frame = ContinuousBorder.Glass(ThemeManager.PanelRadius(14));
            frame.Padding = new Thickness(18);
            var sp = new StackPanel();
            var icon = new Image { Width = 56, Height = 56, Margin = new Thickness(0, 0, 0, 10) };
            _ = Load(icon);
            sp.Children.Add(icon);
            var t = Ui.Text(message, 13, FontWeights.SemiBold);
            t.TextWrapping = TextWrapping.Wrap;
            t.TextAlignment = TextAlignment.Center;
            sp.Children.Add(t);
            if (detail != null)
            {
                var d = Ui.Text(detail, 11.5, null, "Text.Secondary");
                d.TextWrapping = TextWrapping.Wrap;
                d.TextAlignment = TextAlignment.Center;
                d.Margin = new Thickness(0, 6, 0, 0);
                sp.Children.Add(d);
            }
            var buttons = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            var cancel = new Button { Content = "Cancel", Style = Ui.Style("MacButton"), IsCancel = true };
            cancel.Click += (_, _) => Close();
            var ok = new Button { Content = okText, Style = Ui.Style("AccentButton"), IsDefault = true };
            ok.Click += (_, _) => { _result = true; Close(); };
            Grid.SetColumn(ok, 2);
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            sp.Children.Add(buttons);
            frame.Child = sp;
            Content = frame;
            Opacity = 0;
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += (_, _) =>
            {
                RefreshBackdrop(frame);
                Ui.Animate(this, OpacityProperty, 1, 120);
                Ui.ForceActivate(this);
            };
        }

        static async System.Threading.Tasks.Task Load(Image i)
        {
            try { i.Source = await IconProvider.ForKey("settings"); } catch { }
        }

        public static bool Confirm(string message, string okText = "OK", string detail = null)
        {
            var d = new MacDialog(message, okText, detail);
            d.ShowDialog();
            return d._result;
        }
    }

    /// <summary>"About This PC" in the style of a compact system information panel.</summary>
    public class AboutWindow : MacWindow
    {
        public AboutWindow() : base(300, 470)
        {
            Title = "About This PC";
            Lights.CanMinimize = false;
            var sp = new StackPanel { Margin = new Thickness(20, 44, 20, 20) };
            sp.Children.Add(DeviceArt());
            var name = Ui.Text(Environment.MachineName, 20, FontWeights.SemiBold);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Margin = new Thickness(0, 14, 0, 2);
            sp.Children.Add(name);
            var os = Ui.Text(SystemInfo.OsName(), 12, null, "Text.Secondary");
            os.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(os);
            var rows = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            rows.Children.Add(Info("Processor", SystemInfo.Cpu()));
            rows.Children.Add(Info("Memory", SystemInfo.Memory()));
            rows.Children.Add(Info("Graphics", SystemInfo.Gpu()));
            rows.Children.Add(Info("Display", $"{Win32.GetSystemMetrics(Win32.SM_CXSCREEN)} × {Win32.GetSystemMetrics(Win32.SM_CYSCREEN)}"));
            rows.Children.Add(Info("Windows", SystemInfo.OsVersion()));
            sp.Children.Add(rows);
            var more = new Button { Content = "More Info…", Style = Ui.Style("MacButton"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
            more.Click += (_, _) => Shell.Open("ms-settings:about");
            sp.Children.Add(more);
            Root.Children.Add(sp);
        }

        static FrameworkElement DeviceArt()
        {
            // original laptop illustration showing the current wallpaper palette
            var g = new Grid { Width = 150, Height = 96, HorizontalAlignment = HorizontalAlignment.Center };
            var lid = new Border { Width = 128, Height = 82, CornerRadius = new CornerRadius(7, 7, 2, 2), Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)), Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Top };
            var screen = new Border { CornerRadius = new CornerRadius(3), ClipToBounds = true };
            var pal = Wallpapers.All.FirstOrDefault(p => p.Name == App.Settings.Wallpaper) ?? Wallpapers.All[0];
            screen.Background = new ImageBrush(Wallpapers.Thumbnail(pal, ThemeManager.IsDark, 120, 74));
            lid.Child = screen;
            var bas = new Border { Width = 150, Height = 8, CornerRadius = new CornerRadius(0, 0, 5, 5), VerticalAlignment = VerticalAlignment.Bottom, Background = new LinearGradientBrush(Color.FromRgb(0xD8, 0xD8, 0xDC), Color.FromRgb(0x9A, 0x9A, 0xA0), 90) };
            g.Children.Add(lid);
            g.Children.Add(bas);
            return g;
        }

        static FrameworkElement Info(string k, string v)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var kt = Ui.Text(k, 11.5, FontWeights.SemiBold);
            kt.HorizontalAlignment = HorizontalAlignment.Right;
            kt.Margin = new Thickness(0, 0, 10, 0);
            var vt = Ui.Text(v, 11.5, null, "Text.Secondary");
            vt.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(vt, 1);
            g.Children.Add(kt);
            g.Children.Add(vt);
            return g;
        }
    }

    /// <summary>"About &lt;App&gt;" for the front application.</summary>
    public class AppInfoWindow : MacWindow
    {
        public AppInfoWindow(string appId) : base(320, 300)
        {
            Title = "About";
            Lights.CanMinimize = false;
            var sp = new StackPanel { Margin = new Thickness(20, 44, 20, 20), HorizontalAlignment = HorizontalAlignment.Center };
            var img = new Image { Width = 80, Height = 80 };
            _ = Load(img, appId);
            sp.Children.Add(img);
            var name = Ui.Text(AppCatalog.DisplayName(appId), 17, FontWeights.SemiBold);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Margin = new Thickness(0, 10, 0, 4);
            sp.Children.Add(name);
            string exe = AppCatalog.ById(appId)?.ExePath ?? (System.IO.File.Exists(appId ?? "") ? appId : null);
            if (exe == null)
            {
                var w = WindowTracker.WindowsOf(appId).FirstOrDefault();
                if (w != null) exe = Win32.GetProcessPath(w.Pid);
            }
            if (exe != null)
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(exe);
                    Add(sp, $"Version {vi.ProductVersion ?? vi.FileVersion}");
                    if (!string.IsNullOrWhiteSpace(vi.CompanyName)) Add(sp, vi.CompanyName);
                    if (!string.IsNullOrWhiteSpace(vi.LegalCopyright)) Add(sp, vi.LegalCopyright);
                }
                catch { }
                var reveal = new Button { Content = "Show in File Explorer", Style = Ui.Style("MacButton"), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
                reveal.Click += (_, _) => Shell.Reveal(exe);
                sp.Children.Add(reveal);
            }
            Root.Children.Add(sp);
        }

        static void Add(StackPanel sp, string text)
        {
            var t = Ui.Text(text, 11.5, null, "Text.Secondary");
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.TextWrapping = TextWrapping.Wrap;
            t.TextAlignment = TextAlignment.Center;
            sp.Children.Add(t);
        }

        static async System.Threading.Tasks.Task Load(Image i, string id)
        {
            try { i.Source = await IconProvider.ForApp(id); } catch { }
        }
    }

    /// <summary>Force Quit Applications.</summary>
    public class ForceQuitWindow : MacWindow
    {
        readonly StackPanel _list = new();
        Border _selected;
        uint _selectedPid;

        public ForceQuitWindow() : base(380, 430)
        {
            Title = "Force Quit Applications";
            Lights.CanMinimize = false;
            var sp = new DockPanel { Margin = new Thickness(18, 40, 18, 16), LastChildFill = true };
            var head = Ui.Text("If an app doesn't respond for a while, select its name and click Force Quit.", 12);
            head.TextWrapping = TextWrapping.Wrap;
            head.Margin = new Thickness(0, 0, 0, 10);
            DockPanel.SetDock(head, Dock.Top);
            sp.Children.Add(head);
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var tm = new Button { Content = "Task Manager", Style = Ui.Style("MacButton"), Margin = new Thickness(0, 0, 8, 0) };
            tm.Click += (_, _) => Shell.TaskManager();
            var fq = new Button { Content = "Force Quit", Style = Ui.Style("AccentButton") };
            fq.Click += (_, _) =>
            {
                if (_selectedPid == 0) return;
                WindowTracker.ForceQuit(_selectedPid);
                System.Threading.Tasks.Task.Delay(400).ContinueWith(_ => Dispatcher.Invoke(() => { WindowTracker.Refresh(); Fill(); }));
            };
            bottom.Children.Add(tm);
            bottom.Children.Add(fq);
            DockPanel.SetDock(bottom, Dock.Bottom);
            sp.Children.Add(bottom);
            var box = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0.5), Child = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Padding = new Thickness(4) };
            box.SetResourceReference(Border.BackgroundProperty, "Panel.Solid");
            box.SetResourceReference(Border.BorderBrushProperty, "Separator");
            sp.Children.Add(box);
            Root.Children.Add(sp);
            Fill();
        }

        void Fill()
        {
            _list.Children.Clear();
            _selectedPid = 0;
            foreach (var grp in WindowTracker.Windows.GroupBy(w => w.AppId ?? w.Pid.ToString()))
            {
                var w = grp.First();
                var row = new Grid { Height = 30 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var img = new Image { Width = 22, Height = 22 };
                _ = Load(img, w.AppId);
                row.Children.Add(img);
                bool hung = IsHung(w.Hwnd);
                var t = Ui.Text(AppCatalog.DisplayName(w.AppId) + (hung ? "  (not responding)" : ""), 13, null, hung ? null : "Text.Primary");
                if (hung) t.Foreground = Brushes.IndianRed;
                t.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(t, 1);
                row.Children.Add(t);
                var b = new Border { Child = row, CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 0, 6, 0), Background = Brushes.Transparent };
                uint pid = w.Pid;
                b.MouseLeftButtonDown += (_, _) =>
                {
                    if (_selected != null) _selected.Background = Brushes.Transparent;
                    _selected = b;
                    _selectedPid = pid;
                    b.SetResourceReference(Border.BackgroundProperty, "Selection");
                };
                _list.Children.Add(b);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr hwnd);
        static bool IsHung(IntPtr h) { try { return IsHungAppWindow(h); } catch { return false; } }

        static async System.Threading.Tasks.Task Load(Image i, string id)
        {
            try { i.Source = await IconProvider.ForApp(id); } catch { }
        }
    }

    public static class SystemInfo
    {
        static string Reg(string key, string name)
        {
            try { using var k = Registry.LocalMachine.OpenSubKey(key); return k?.GetValue(name)?.ToString(); } catch { return null; }
        }

        public static string OsName()
        {
            string name = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName") ?? "Windows";
            if (Environment.OSVersion.Version.Build >= 22000) name = name.Replace("Windows 10", "Windows 11");
            return name;
        }

        public static string OsVersion()
        {
            string disp = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion") ?? "";
            return $"{disp} (build {Environment.OSVersion.Version.Build})".Trim();
        }

        public static string Cpu() => Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim() ?? "Unknown";

        public static string Memory()
        {
            var m = new Win32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
            return Win32.GlobalMemoryStatusEx(ref m) ? $"{Math.Round(m.ullTotalPhys / 1024.0 / 1024 / 1024)} GB" : "Unknown";
        }

        public static string Gpu()
        {
            try
            {
                using var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                return string.Join(", ", s.Get().Cast<System.Management.ManagementObject>().Select(o => o["Name"]?.ToString()).Where(n => n != null));
            }
            catch { return "Unknown"; }
        }
    }
}
