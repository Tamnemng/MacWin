using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MacWin.Services;

namespace MacWin.UI
{
    /// <summary>Full-screen app grid with paging, search and zoom animation.</summary>
    public class LaunchpadWindow : PanelWindow
    {
        const int Cols = 7, Rows = 5;
        readonly Grid _root = new();
        readonly Grid _content = new();
        readonly Canvas _pagesHost = new() { ClipToBounds = true };
        readonly StackPanel _pages = new() { Orientation = Orientation.Horizontal };
        readonly TranslateTransform _pageShift = new();
        readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        readonly TextBox _search = new();
        readonly TextBlock _placeholder;
        readonly ScaleTransform _zoom = new(1, 1);
        readonly Dictionary<string, FrameworkElement> _tiles = new(StringComparer.OrdinalIgnoreCase);
        List<AppEntry> _shown = new();
        int _page, _pageCount = 1;
        bool _closing;
        Point? _dragStart;
        double _dragShift;
        DateTime _lastWheel;
        double _iconSize = 88;
        readonly System.Windows.Threading.DispatcherTimer _filterTimer = new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(110) };

        public LaunchpadWindow() : base(0, false, 0x10)
        {
            Title = "Launchpad";
            Topmost = true;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            var dim = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)) };
            _root.Children.Add(dim);
            _root.Children.Add(_content);
            Content = _root;

            _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(70) });
            _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(110) });
            _content.RenderTransform = _zoom;
            _content.RenderTransformOrigin = new Point(0.5, 0.5);

            // search field
            var searchBox = new Border
            {
                Width = 260, Height = 30, CornerRadius = new CornerRadius(ThemeManager.IsLiquid ? 15 : 8), VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.5)
            };
            if (ThemeManager.IsLiquid) searchBox.SetResourceReference(Border.BorderBrushProperty, "Glass.Frame");
            var sg = new Grid { Margin = new Thickness(8, 0, 8, 0) };
            var glyph = new TextBlock { Text = "", FontFamily = Ui.IconFont, FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
            _placeholder = new TextBlock { Text = "Search", Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 255, 255, 255)), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(22, 0, 0, 1), IsHitTestVisible = false };
            _search.Style = Ui.Style("PlainTextBox");
            _search.Foreground = Brushes.White;
            _search.CaretBrush = Brushes.White;
            _search.FontSize = 14;
            _search.VerticalAlignment = VerticalAlignment.Center;
            _search.Margin = new Thickness(20, 0, 0, 0);
            _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); Populate(); };
            _search.TextChanged += (_, _) =>
            {
                _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                _filterTimer.Stop();
                _filterTimer.Start(); // filter once typing pauses, not on every keystroke
            };
            sg.Children.Add(glyph);
            sg.Children.Add(_placeholder);
            sg.Children.Add(_search);
            searchBox.Child = sg;
            _content.Children.Add(searchBox);

            _pages.RenderTransform = _pageShift;
            _pagesHost.Children.Add(_pages);
            Grid.SetRow(_pagesHost, 1);
            _content.Children.Add(_pagesHost);
            _pagesHost.SizeChanged += (_, _) => Populate();

            Grid.SetRow(_dots, 2);
            _dots.VerticalAlignment = VerticalAlignment.Top;
            _dots.Margin = new Thickness(0, 14, 0, 0);
            _content.Children.Add(_dots);

            // close when clicking empty space
            _root.MouseLeftButtonDown += (_, e) =>
            {
                _dragStart = e.GetPosition(this);
                _dragShift = 0;
                _root.CaptureMouse();
            };
            _root.MouseMove += (_, e) =>
            {
                if (_dragStart == null || e.LeftButton != MouseButtonState.Pressed) return;
                _dragShift = e.GetPosition(this).X - _dragStart.Value.X;
                if (Math.Abs(_dragShift) > 8) { Ui.Stop(_pageShift, TranslateTransform.XProperty); _pageShift.X = -_page * PageWidth + _dragShift; }
            };
            _root.MouseLeftButtonUp += (_, e) =>
            {
                _root.ReleaseMouseCapture();
                var start = _dragStart;
                _dragStart = null;
                if (start == null) return;
                if (Math.Abs(_dragShift) > 60) GoTo(_page + (_dragShift < 0 ? 1 : -1));
                else if (Math.Abs(_dragShift) > 8) GoTo(_page);
                else if (e.OriginalSource is not TextBox && !IsOnTile(e.OriginalSource as DependencyObject)) CloseAnimated();
            };
            MouseWheel += (_, e) =>
            {
                if ((DateTime.Now - _lastWheel).TotalMilliseconds < 350) return;
                _lastWheel = DateTime.Now;
                GoTo(_page + (e.Delta < 0 ? 1 : -1));
            };
            PreviewKeyDown += OnKey;
            Deactivated += (_, _) => { if (IsVisible && !_closing) CloseAnimated(); };
            AppCatalog.Loaded += () => Dispatcher.BeginInvoke(() => { _tiles.Clear(); Populate(); });
        }

        static bool IsOnTile(DependencyObject d)
        {
            while (d != null)
            {
                if (d is Button) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        double PageWidth => Math.Max(1, _pagesHost.ActualWidth);

        void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    if (_search.Text.Length > 0) _search.Text = "";
                    else CloseAnimated();
                    e.Handled = true; break;
                case Key.Right when _search.Text.Length == 0: GoTo(_page + 1); e.Handled = true; break;
                case Key.Left when _search.Text.Length == 0: GoTo(_page - 1); e.Handled = true; break;
                case Key.Enter:
                    var first = _shown.FirstOrDefault();
                    if (first != null) Launch(first);
                    e.Handled = true; break;
            }
        }

        // ------------------------------------------------------------------ content

        void Populate()
        {
            if (_pagesHost.ActualWidth < 10) return;
            string q = _search.Text.Trim();
            var apps = AppCatalog.Apps;
            _shown = q.Length == 0 ? apps.ToList() : Search.RankApps(apps, q).ToList();

            double w = _pagesHost.ActualWidth, h = _pagesHost.ActualHeight;
            double cellW = Math.Min(170, (w - 160) / Cols), cellH = Math.Min(160, h / Rows);
            _iconSize = Math.Clamp(Math.Min(cellW, cellH) * 0.6, 56, 96);
            int perPage = Cols * Rows;
            _pageCount = Math.Max(1, (int)Math.Ceiling(_shown.Count / (double)perPage));
            _pages.Children.Clear();
            for (int p = 0; p < _pageCount; p++)
            {
                var grid = new UniformGrid { Columns = Cols, Rows = Rows, Width = cellW * Cols, Height = cellH * Rows };
                foreach (var a in _shown.Skip(p * perPage).Take(perPage))
                {
                    // reuse tiles between searches: creating hundreds of elements per keystroke was the lag
                    string key = $"{a.Id}|{cellW:0}|{cellH:0}";
                    if (!_tiles.TryGetValue(key, out var tile)) _tiles[key] = tile = Tile(a, cellW, cellH);
                    if (tile.Parent is Panel old) old.Children.Remove(tile);
                    grid.Children.Add(tile);
                }
                var page = new Grid { Width = w, Height = h };
                page.Children.Add(grid);
                grid.HorizontalAlignment = HorizontalAlignment.Center;
                grid.VerticalAlignment = VerticalAlignment.Center;
                _pages.Children.Add(page);
            }
            _page = Math.Min(_page, _pageCount - 1);
            if (q.Length > 0) _page = 0;
            _pageShift.X = -_page * w;
            BuildDots();
        }

        FrameworkElement Tile(AppEntry a, double cellW, double cellH)
        {
            var img = new Image { Width = _iconSize, Height = _iconSize };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            LoadIcon(img, a.Id);
            var label = new TextBlock
            {
                Text = a.Name, Foreground = Brushes.White, FontSize = 13, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 6, 4, 0), MaxWidth = cellW - 8
            };
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(img);
            sp.Children.Add(label);
            var b = new Button { Content = sp, Style = Ui.Style("GhostButton"), Width = cellW - 10, Height = cellH - 10, ToolTip = null, Background = Brushes.Transparent };
            b.Resources["Selection"] = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            var press = new ScaleTransform(1, 1);
            img.RenderTransform = press;
            img.RenderTransformOrigin = new Point(0.5, 0.5);
            b.PreviewMouseLeftButtonDown += (_, _) => { press.ScaleX = press.ScaleY = 0.92; img.Opacity = 0.75; };
            b.PreviewMouseLeftButtonUp += (_, _) => { press.ScaleX = press.ScaleY = 1; img.Opacity = 1; };
            b.MouseLeave += (_, _) => { press.ScaleX = press.ScaleY = 1; img.Opacity = 1; };
            b.Click += (_, _) => { if (Math.Abs(_dragShift) <= 8) Launch(a); };
            b.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                var s = App.Settings;
                string key = "app:" + a.Id;
                bool inDock = s.PinnedApps?.Contains(key) == true || s.Roles.Any(r => r.AppId == a.Id && s.PinnedApps?.Contains("role:" + r.Role) == true);
                var cm = Menus.Build(
                    Menus.Item("Open", () => Launch(a)),
                    Menus.Item(inDock ? "In Dock" : "Add to Dock", () => { s.PinnedApps ??= new List<string>(); s.PinnedApps.Add(key); s.Save(false); App.Dock?.Rebuild(); }, enabled: !inDock),
                    a.ExePath != null ? Menus.Item("Show in File Explorer", () => { CloseAnimated(); Shell.Reveal(a.ExePath); }) : null,
                    Menus.Sep(),
                    Menus.Item("Uninstall…", () => { CloseAnimated(); Shell.Open("ms-settings:appsfeatures"); }));
                cm.PlacementTarget = b;
                cm.IsOpen = true;
            };
            return b;
        }

        static async void LoadIcon(Image img, string id)
        {
            try { img.Source = await IconProvider.ForApp(id); } catch { }
        }

        void BuildDots()
        {
            _dots.Children.Clear();
            if (_pageCount <= 1) return;
            for (int i = 0; i < _pageCount; i++)
            {
                int idx = i;
                var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(5), Fill = Brushes.White, Opacity = i == _page ? 0.95 : 0.35, Cursor = Cursors.Hand };
                dot.MouseLeftButtonUp += (_, e) => { e.Handled = true; GoTo(idx); };
                dot.MouseLeftButtonDown += (_, e) => e.Handled = true;
                _dots.Children.Add(dot);
            }
        }

        void GoTo(int page)
        {
            _page = Math.Clamp(page, 0, _pageCount - 1);
            Ui.Animate(_pageShift, TranslateTransform.XProperty, -_page * PageWidth, 380, new CubicEase { EasingMode = EasingMode.EaseOut });
            for (int i = 0; i < _dots.Children.Count; i++) ((Ellipse)_dots.Children[i]).Opacity = i == _page ? 0.95 : 0.35;
        }

        void Launch(AppEntry a)
        {
            CloseAnimated();
            AppCatalog.Launch(a.Id);
            App.Dock?.BounceApp(a.Id);
        }

        // ------------------------------------------------------------------ show / hide

        public void ShowAnimated()
        {
            AppCatalog.RefreshIfStale(TimeSpan.FromSeconds(60));
            _closing = false;
            _search.Text = "";
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            _page = 0;
            _pageShift.X = 0;
            Opacity = 0;
            _zoom.ScaleX = _zoom.ScaleY = 1.1;
            Show();
            Ui.ForceActivate(this);
            _search.Focus();
            Ui.Animate(this, OpacityProperty, 1, 220);
            Ui.Animate(_zoom, ScaleTransform.ScaleXProperty, 1, 300);
            Ui.Animate(_zoom, ScaleTransform.ScaleYProperty, 1, 300);
            App.Dock?.Dispatcher.BeginInvoke(() => App.RaiseDock());
        }

        public void CloseAnimated()
        {
            if (_closing || !IsVisible) return;
            _closing = true;
            Ui.Animate(_zoom, ScaleTransform.ScaleXProperty, 1.08, 180);
            Ui.Animate(_zoom, ScaleTransform.ScaleYProperty, 1.08, 180);
            Ui.Animate(this, OpacityProperty, 0, 180, done: () => { Hide(); _closing = false; });
        }
    }

    /// <summary>Fuzzy ranking shared by Launchpad and Spotlight.</summary>
    public static class Search
    {
        public static IEnumerable<AppEntry> RankApps(IEnumerable<AppEntry> apps, string q)
        {
            q = q.Trim();
            if (q.Length == 0) return Enumerable.Empty<AppEntry>();
            // match the display name first, then the program file ("chrome.exe") and the app id ("Chrome", "MSEdge"…)
            return apps.Select(a => (a, score: Math.Max(Score(a.Name, q),
                                               Math.Max(a.ExePath != null ? Score(System.IO.Path.GetFileNameWithoutExtension(a.ExePath), q) - 5 : 0,
                                                        Score(a.Id, q) - 10))))
                .Concat(App.Settings.Roles.Where(r => r.Label != null && AppCatalog.ById(r.AppId) is AppEntry)
                    .Select(r => (a: AppCatalog.ById(r.AppId), score: Score(r.Label, q) - 1)))
                .Where(x => x.score > 0)
                .GroupBy(x => x.a.Id).Select(g => g.OrderByDescending(x => x.score).First())
                .OrderByDescending(x => x.score).ThenBy(x => x.a.Name.Length)
                .Select(x => x.a);
        }

        public static int Score(string name, string q)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            var n = name.ToLowerInvariant();
            var s = q.ToLowerInvariant();
            if (n == s) return 100;
            if (n.StartsWith(s)) return 80;
            var words = n.Split(new[] { ' ', '-', '.', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Any(w => w.StartsWith(s))) return 60;
            string initials = string.Concat(words.Select(w => w[0]));
            if (initials.StartsWith(s)) return 50;
            if (n.Contains(s)) return 40;
            // subsequence match
            int i = 0;
            foreach (var ch in n) if (i < s.Length && ch == s[i]) i++;
            return i == s.Length && s.Length >= 2 ? 15 : 0;
        }
    }
}
