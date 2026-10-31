using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacWin.Services;

namespace MacWin.UI
{
    public class SpotResult
    {
        public string Group;
        public string Title;
        public string Subtitle;
        public Func<Task<ImageSource>> Icon;
        public string Glyph;
        public Action Open;
        public Action Reveal;
        public string Path;
        public DateTime? Modified;
        public string BigText;
    }

    /// <summary>Spotlight: one search field for apps, files, settings, commands, maths and the web.</summary>
    public class SpotlightWindow : PanelWindow
    {
        const double W = 700, FieldH = 54, ListH = 380;
        readonly TextBox _field = new();
        readonly TextBlock _hint;
        readonly Image _fieldIcon = new() { Width = 26, Height = 26, Visibility = Visibility.Collapsed };
        readonly Grid _results = new() { Height = ListH, Visibility = Visibility.Collapsed };
        readonly StackPanel _list = new();
        readonly ScrollViewer _scroll;
        readonly Grid _preview = new() { Margin = new Thickness(16) };
        readonly ContinuousBorder _frame;
        List<SpotResult> _items = new();
        readonly List<Border> _rows = new();
        int _sel = -1;
        int _version;
        CancellationTokenSource _cts;
        readonly System.Windows.Threading.DispatcherTimer _queryTimer = new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(60) };

        public SpotlightWindow() : base(ThemeManager.PanelRadius(14), false, 0x40)
        {
            Title = "Spotlight";
            Width = W;
            SizeToContent = SizeToContent.Height;

            _frame = ContinuousBorder.Glass(ThemeManager.PanelRadius(14));
            _frame.ClipContent = true;
            var root = new StackPanel();
            _frame.Child = root;
            Content = _frame;

            // ---- search field
            var field = new Grid { Height = FieldH, Margin = new Thickness(18, 0, 18, 0) };
            var glass = Ui.Glyph("", 20, "Text.Secondary");
            glass.HorizontalAlignment = HorizontalAlignment.Left;
            _fieldIcon.HorizontalAlignment = HorizontalAlignment.Right;
            _hint = Ui.Text("Spotlight Search", 22, FontWeights.Light, "Text.Tertiary");
            _hint.VerticalAlignment = VerticalAlignment.Center;
            _hint.Margin = new Thickness(36, 0, 0, 2);
            _hint.IsHitTestVisible = false;
            _field.Style = Ui.Style("PlainTextBox");
            _field.FontSize = 22;
            _field.FontFamily = Ui.DisplayFont;
            _field.FontWeight = FontWeights.Light;
            _field.VerticalAlignment = VerticalAlignment.Center;
            _field.Margin = new Thickness(34, 0, 36, 0);
            // search once typing pauses briefly instead of rebuilding the results on every keystroke
            _queryTimer.Tick += (_, _) => { _queryTimer.Stop(); Query(); };
            _field.TextChanged += (_, _) =>
            {
                _hint.Visibility = _field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                _queryTimer.Stop();
                if (_field.Text.Trim().Length == 0) Query(); else _queryTimer.Start();
            };
            field.Children.Add(glass);
            field.Children.Add(_hint);
            field.Children.Add(_field);
            field.Children.Add(_fieldIcon);
            root.Children.Add(field);

            // ---- results: list + preview
            var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top };
            line.SetResourceReference(Border.BackgroundProperty, "Separator");
            _results.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
            _results.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            _results.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8, 6, 6, 8), Focusable = false };
            _results.Children.Add(_scroll);
            var split = new Border();
            split.SetResourceReference(Border.BackgroundProperty, "Separator");
            Grid.SetColumn(split, 1);
            _results.Children.Add(split);
            Grid.SetColumn(_preview, 2);
            _results.Children.Add(_preview);
            _results.Children.Add(line);
            Grid.SetColumnSpan(line, 3);
            root.Children.Add(_results);

            PreviewKeyDown += OnKey;
            HideOnDeactivate(HideAnimated);
            // the app list may refresh while Spotlight is open (new install): re-run the search
            AppCatalog.Loaded += () => Dispatcher.BeginInvoke(() => { if (IsVisible && _field.Text.Trim().Length > 0) Query(); });
        }

        // ------------------------------------------------------------------ show / hide

        public void ShowAnimated()
        {
            MarkShown();
            AppCatalog.RefreshIfStale(TimeSpan.FromSeconds(60));
            Left = (SystemParameters.PrimaryScreenWidth - W) / 2;
            Top = SystemParameters.PrimaryScreenHeight * 0.2;
            _field.Text = "";
            Opacity = 0;
            var st = new ScaleTransform(0.97, 0.97, W / 2, FieldH / 2);
            _frame.RenderTransform = st;
            Show();
            RefreshBackdrop(_frame, ListH + 2);   // results grow downwards: capture that area too
            Ui.ForceActivate(this);
            _field.Focus();
            Keyboard.Focus(_field);
            Ui.Animate(this, OpacityProperty, 1, 130);
            Ui.Animate(st, ScaleTransform.ScaleXProperty, 1, 160);
            Ui.Animate(st, ScaleTransform.ScaleYProperty, 1, 160);
        }

        public void HideAnimated()
        {
            MarkHidden();
            _cts?.Cancel();
            Ui.Animate(this, OpacityProperty, 0, 110, done: () => { if (Opacity == 0) Hide(); });
        }

        // ------------------------------------------------------------------ keyboard

        void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    if (_field.Text.Length > 0) _field.Text = ""; else HideAnimated();
                    e.Handled = true; break;
                case Key.Down: Select(Math.Min(_items.Count - 1, _sel + 1)); e.Handled = true; break;
                case Key.Up: Select(Math.Max(0, _sel - 1)); e.Handled = true; break;
                case Key.Enter:
                    if (_sel >= 0 && _sel < _items.Count)
                    {
                        var r = _items[_sel];
                        HideAnimated();
                        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && r.Reveal != null) r.Reveal();
                        else r.Open?.Invoke();
                    }
                    e.Handled = true; break;
            }
        }

        // ------------------------------------------------------------------ querying

        void Query()
        {
            string q = _field.Text.Trim();
            int ver = ++_version;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            if (q.Length == 0) { Render(new List<SpotResult>()); return; }

            var list = new List<SpotResult>();
            if (Calculate(q) is SpotResult calc) list.Add(calc);
            list.AddRange(Search.RankApps(AppCatalog.Apps, q).Take(8).Select(AppResult));
            list.AddRange(RunningNotInCatalog(q));
            list.AddRange(Commands().Where(c => Search.Score(c.Title, q) > 0 || (c.Subtitle?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).Take(4));
            list.AddRange(SettingsPages().Where(c => Search.Score(c.Title, q) > 0 || (c.Subtitle?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).Take(4));
            list.Add(WebResult(q));
            Render(list);

            // files from the Windows index arrive asynchronously
            var token = _cts.Token;
            Task.Run(async () =>
            {
                await Task.Delay(140, token);
                return FindFiles(q, token);
            }, token).ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion || ver != _version || t.Result.Count == 0) return;
                var merged = list.Where(r => r.Group != "Web").Concat(t.Result).Concat(list.Where(r => r.Group == "Web")).ToList();
                var selectedTitle = _sel >= 0 && _sel < _items.Count ? _items[_sel] : null;
                Render(merged, selectedTitle);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static SpotResult AppResult(AppEntry a)
        {
            var role = Roles.ForApp(a.Id);
            return new SpotResult
            {
                Group = "Applications",
                Title = a.Name,
                Subtitle = role != null ? $"{a.Name} — {role.Label}" : "Application",
                Icon = () => IconProvider.ForApp(a.Id),
                Open = () => { AppCatalog.Launch(a.Id); App.Dock?.BounceApp(a.Id); },
                Reveal = a.ExePath != null ? () => Shell.Reveal(a.ExePath) : null,
                Path = a.ExePath
            };
        }

        /// <summary>Running apps that aren't in the Start menu (portable apps etc.) — opening one brings its window forward.</summary>
        static IEnumerable<SpotResult> RunningNotInCatalog(string q)
        {
            foreach (var g in WindowTracker.Windows.Where(w => w.AppId != null && AppCatalog.ById(w.AppId) == null).GroupBy(w => w.AppId).Take(20))
            {
                string id = g.Key;
                string name = AppCatalog.DisplayName(id);
                string file = System.IO.Path.GetFileNameWithoutExtension(id);
                if (Search.Score(name, q) <= 0 && Search.Score(file, q) <= 0) continue;
                var win = g.First();
                yield return new SpotResult
                {
                    Group = "Applications",
                    Title = name,
                    Subtitle = "Running",
                    Icon = () => IconProvider.ForApp(id),
                    Open = () => WindowTracker.Activate(win),
                    Reveal = File.Exists(id) ? () => Shell.Reveal(id) : null,
                    Path = File.Exists(id) ? id : null
                };
            }
        }

        static SpotResult WebResult(string q)
        {
            string engine = App.Settings.SearchEngine;
            string url = engine switch
            {
                "Bing" => "https://www.bing.com/search?q=",
                "DuckDuckGo" => "https://duckduckgo.com/?q=",
                _ => "https://www.google.com/search?q="
            } + Uri.EscapeDataString(q);
            return new SpotResult { Group = "Web", Title = $"Search {engine} for “{q}”", Subtitle = url, Glyph = "", Open = () => Shell.Open(url) };
        }

        static readonly Regex MathExpr = new(@"^[\d\s\.\,\+\-\*/x×÷\(\)%]+$", RegexOptions.Compiled);

        static SpotResult Calculate(string q)
        {
            if (!MathExpr.IsMatch(q) || !Regex.IsMatch(q, @"\d\s*[\+\-\*/x×÷%]\s*[\d\(]")) return null;
            try
            {
                string expr = q.Replace('x', '*').Replace('×', '*').Replace('÷', '/');
                if (!expr.Contains('.')) expr = expr.Replace(',', '.');
                expr = Regex.Replace(expr, @"(\d+(\.\d+)?)%", "($1/100)");
                var v = Convert.ToDouble(new DataTable().Compute(expr, ""), CultureInfo.InvariantCulture);
                if (double.IsInfinity(v) || double.IsNaN(v)) return null;
                string s = Math.Abs(v) >= 1e15 ? v.ToString("G10", CultureInfo.CurrentCulture) : v.ToString("#,0.##########", CultureInfo.CurrentCulture);
                return new SpotResult
                {
                    Group = "Calculator", Title = s, Subtitle = q + " =", Glyph = "", BigText = s,
                    Open = () => { try { Clipboard.SetText(s); } catch { } }
                };
            }
            catch { return null; }
        }

        static List<SpotResult> Commands()
        {
            SpotResult C(string t, string kw, string glyph, Action a) => new() { Group = "Commands", Title = t, Subtitle = kw, Glyph = glyph, Open = a };
            return new List<SpotResult>
            {
                C("Lock Screen", "lock khóa màn hình", "", Shell.Lock),
                C("Sleep", "sleep ngủ", "", Shell.Sleep),
                C("Restart", "restart reboot khởi động lại", "", () => { if (MacDialog.Confirm("Restart your computer now?", "Restart")) Shell.Restart(); }),
                C("Shut Down", "shutdown power off tắt máy", "", () => { if (MacDialog.Confirm("Shut down your computer now?", "Shut Down")) Shell.ShutDown(); }),
                C("Empty Trash", "trash recycle bin thùng rác", "", Shell.EmptyTrash),
                C("Toggle Dark Mode", "dark light theme giao diện tối sáng", "", App.ToggleDarkMode),
                C("Show Desktop", "desktop màn hình nền", "", Shell.ShowDesktop),
                C("Mission Control", "windows overview task view", "", Shell.MissionControl),
                C("Launchpad", "apps ứng dụng", "", App.ToggleLaunchpad),
                C("Take Screenshot", "screenshot chụp màn hình", "", Shell.Screenshot),
                C("Task Manager", "processes force quit", "", Shell.TaskManager),
                C("Control Panel", "legacy settings", "", () => Shell.Open("control.exe")),
                C("MacWin Settings", "preferences dock menu bar cài đặt", "", () => App.ShowSettings()),
            };
        }

        static List<SpotResult> SettingsPages()
        {
            SpotResult S(string t, string kw, string uri) => new() { Group = "System Settings", Title = t, Subtitle = kw, Glyph = "", Open = () => Shell.Open(uri) };
            return new List<SpotResult>
            {
                S("Wi-Fi", "wifi wireless mạng", "ms-settings:network-wifi"),
                S("Bluetooth", "bluetooth devices thiết bị", "ms-settings:bluetooth"),
                S("Display", "screen resolution brightness màn hình độ sáng", "ms-settings:display"),
                S("Sound", "audio volume âm thanh", "ms-settings:sound"),
                S("Notifications", "notifications thông báo", "ms-settings:notifications"),
                S("Focus", "do not disturb focus", "ms-settings:quiethours"),
                S("Battery & Power", "battery power sleep pin", "ms-settings:powersleep"),
                S("Storage", "disk storage ổ đĩa", "ms-settings:storagesense"),
                S("Apps", "installed apps uninstall gỡ ứng dụng", "ms-settings:appsfeatures"),
                S("Default Apps", "default browser trình duyệt mặc định", "ms-settings:defaultapps"),
                S("Startup Apps", "startup khởi động", "ms-settings:startupapps"),
                S("Wallpaper", "background hình nền", "ms-settings:personalization-background"),
                S("Colors", "accent color màu", "ms-settings:colors"),
                S("Night Light", "night light blue light", "ms-settings:nightlight"),
                S("Windows Update", "update cập nhật", "ms-settings:windowsupdate"),
                S("Privacy & Security", "privacy security bảo mật", "ms-settings:privacy"),
                S("Accounts", "account user tài khoản", "ms-settings:yourinfo"),
                S("Date & Time", "date time clock ngày giờ", "ms-settings:dateandtime"),
                S("Keyboard", "keyboard language bàn phím", "ms-settings:keyboard"),
                S("Mouse & Touchpad", "mouse trackpad chuột", "ms-settings:mousetouchpad"),
                S("Printers & Scanners", "printer máy in", "ms-settings:printers"),
                S("VPN", "vpn network", "ms-settings:network-vpn"),
                S("About", "about pc specs thông tin máy", "ms-settings:about"),
            };
        }

        static List<SpotResult> FindFiles(string q, CancellationToken token)
        {
            var list = new List<SpotResult>();
            if (q.Length < 2) return list;
            try
            {
                string safe = q.Replace("'", "''").Replace("%", "[%]").Replace("_", "[_]");
                string sql = "SELECT TOP 12 System.ItemPathDisplay, System.ItemNameDisplay, System.DateModified, System.ItemTypeText FROM SystemIndex " +
                             $"WHERE SCOPE='file:' AND System.ItemNameDisplay LIKE '%{safe}%' ORDER BY System.DateModified DESC";
                using var conn = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
                conn.Open();
                using var cmd = new OleDbCommand(sql, conn);
                using var r = cmd.ExecuteReader();
                while (r != null && r.Read() && !token.IsCancellationRequested)
                {
                    string path = r[0] as string;
                    if (string.IsNullOrEmpty(path)) continue;
                    string name = r[1] as string ?? System.IO.Path.GetFileName(path);
                    DateTime? mod = r[2] is DateTime d ? d : null;
                    string type = r[3] as string;
                    list.Add(new SpotResult
                    {
                        Group = Directory.Exists(path) ? "Folders" : "Documents",
                        Title = name,
                        Subtitle = type ?? System.IO.Path.GetDirectoryName(path),
                        Icon = () => IconProvider.ForFile(path, 96),
                        Open = () => Shell.Open(path),
                        Reveal = () => Shell.Reveal(path),
                        Path = path,
                        Modified = mod
                    });
                }
            }
            catch { }
            return list;
        }

        // ------------------------------------------------------------------ rendering

        void Render(List<SpotResult> items, SpotResult keepSelected = null)
        {
            _items = items;
            _list.Children.Clear();
            _rows.Clear();
            _results.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            _fieldIcon.Visibility = Visibility.Collapsed;
            string group = null;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.Group != group)
                {
                    group = it.Group;
                    var h = Ui.Text(group, 11, FontWeights.SemiBold, "Text.Secondary");
                    h.Margin = new Thickness(8, _list.Children.Count == 0 ? 2 : 10, 0, 4);
                    _list.Children.Add(h);
                }
                var row = Row(it, i);
                _rows.Add(row);
                _list.Children.Add(row);
            }
            int idx = keepSelected != null ? items.IndexOf(keepSelected) : -1;
            _sel = -1;
            Select(idx >= 0 ? idx : items.Count > 0 ? 0 : -1);
        }

        Border Row(SpotResult it, int index)
        {
            var g = new Grid { Height = 26 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FrameworkElement icon;
            if (it.Icon != null)
            {
                var img = new Image { Width = 20, Height = 20 };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                _ = SetImage(img, it.Icon);
                icon = img;
            }
            else icon = Ui.Glyph(it.Glyph ?? "", 14, null);
            var title = new TextBlock { Text = it.Title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(title, 1);
            g.Children.Add(icon);
            g.Children.Add(title);
            var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 0, 6, 0), Child = g, Background = Brushes.Transparent, Cursor = Cursors.Arrow };
            b.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount >= 2 || _sel == index) { HideAnimated(); it.Open?.Invoke(); }
                else Select(index);
            };
            return b;
        }

        static async Task SetImage(Image img, Func<Task<ImageSource>> f)
        {
            try { img.Source = await f(); } catch { }
        }

        void Select(int i)
        {
            if (_sel >= 0 && _sel < _rows.Count)
            {
                _rows[_sel].Background = Brushes.Transparent;
                SetRowForeground(_rows[_sel], false);
            }
            _sel = i;
            if (i < 0 || i >= _rows.Count) { _preview.Children.Clear(); return; }
            _rows[i].SetResourceReference(Border.BackgroundProperty, "Accent");
            SetRowForeground(_rows[i], true);
            _rows[i].BringIntoView();
            ShowPreview(_items[i]);
        }

        static void SetRowForeground(Border row, bool selected)
        {
            foreach (var tb in ((Grid)row.Child).Children.OfType<TextBlock>())
            {
                if (selected) tb.Foreground = Brushes.White;
                else tb.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            }
        }

        void ShowPreview(SpotResult r)
        {
            _preview.Children.Clear();
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            if (r.BigText != null)
            {
                var big = Ui.Text(r.BigText, 40, FontWeights.Light);
                big.FontFamily = Ui.DisplayFont;
                big.HorizontalAlignment = HorizontalAlignment.Center;
                var sub = Ui.Text(r.Subtitle, 14, null, "Text.Secondary");
                sub.HorizontalAlignment = HorizontalAlignment.Center;
                sp.Children.Add(sub);
                sp.Children.Add(big);
                var hint = Ui.Text("Press Enter to copy", 11, null, "Text.Tertiary");
                hint.HorizontalAlignment = HorizontalAlignment.Center;
                hint.Margin = new Thickness(0, 14, 0, 0);
                sp.Children.Add(hint);
                _preview.Children.Add(sp);
                return;
            }
            FrameworkElement icon;
            if (r.Icon != null)
            {
                var img = new Image { Width = 112, Height = 112, Margin = new Thickness(0, 0, 0, 14) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                _ = SetImage(img, r.Icon);
                icon = img;
            }
            else
            {
                var g = Ui.Glyph(r.Glyph ?? "", 64, "Text.Secondary");
                g.Margin = new Thickness(0, 0, 0, 18);
                icon = g;
            }
            sp.Children.Add(icon);
            var t = Ui.Text(r.Title, 17, FontWeights.SemiBold);
            t.TextAlignment = TextAlignment.Center;
            t.TextWrapping = TextWrapping.Wrap;
            t.MaxWidth = 340;
            sp.Children.Add(t);
            if (!string.IsNullOrEmpty(r.Subtitle) && r.Group is not ("Commands" or "System Settings"))
            {
                var s = Ui.Text(r.Subtitle, 12, null, "Text.Secondary");
                s.HorizontalAlignment = HorizontalAlignment.Center;
                s.Margin = new Thickness(0, 4, 0, 0);
                s.MaxWidth = 340;
                sp.Children.Add(s);
            }
            if (r.Path != null || r.Modified != null)
            {
                var sep = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 10), Width = 300 };
                sep.SetResourceReference(Border.BackgroundProperty, "Separator");
                sp.Children.Add(sep);
                if (r.Modified is DateTime m) sp.Children.Add(Detail("Modified", m.ToString("g", CultureInfo.CurrentCulture)));
                if (r.Path != null)
                {
                    try { if (File.Exists(r.Path)) sp.Children.Add(Detail("Size", FormatSize(new FileInfo(r.Path).Length))); } catch { }
                    sp.Children.Add(Detail("Where", System.IO.Path.GetDirectoryName(r.Path)));
                }
            }
            _preview.Children.Add(sp);
        }

        static FrameworkElement Detail(string k, string v)
        {
            var g = new Grid { Width = 300, Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var kt = Ui.Text(k, 11, null, "Text.Secondary");
            kt.HorizontalAlignment = HorizontalAlignment.Right;
            kt.Margin = new Thickness(0, 0, 8, 0);
            var vt = Ui.Text(v, 11);
            vt.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(vt, 1);
            g.Children.Add(kt);
            g.Children.Add(vt);
            return g;
        }

        public static string FormatSize(long b)
        {
            string[] u = { "bytes", "KB", "MB", "GB", "TB" };
            double v = b; int i = 0;
            while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
            return i == 0 ? $"{b} bytes" : $"{v:0.#} {u[i]}";
        }
    }
}
