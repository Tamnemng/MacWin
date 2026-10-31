using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MacWin.Services;

namespace MacWin.UI
{
    /// <summary>Shared pieces of a notification card (banner and Notification Center look the same).</summary>
    static class NoteCard
    {
        public const double Width = 356;

        public static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d < TimeSpan.FromMinutes(1)) return "now";
            if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}m ago";
            if (t.Date == DateTime.Today) return t.ToString(App.Settings.Clock24h ? "HH:mm" : "h:mm tt");
            if (t.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
            return t.ToString("d MMM");
        }

        /// <summary>Icon · title + time · body, as a floating piece of glass.</summary>
        public static GlassTile Build(MacNote n, bool expanded, out FrameworkElement closeButton)
        {
            var grid = new Grid { Margin = new Thickness(12, 11, 14, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            var icon = new Image { Width = 34, Height = 34, Source = n.Icon, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            if (n.Icon == null)
            {
                var ph = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top, Child = Ui.Glyph("", 16, "Text.Secondary") };
                ph.SetResourceReference(Border.BackgroundProperty, "Separator");
                ((TextBlock)ph.Child).HorizontalAlignment = HorizontalAlignment.Center;
                ((TextBlock)ph.Child).VerticalAlignment = VerticalAlignment.Center;
                grid.Children.Add(ph);
            }
            else grid.Children.Add(icon);

            var text = new StackPanel();
            Grid.SetColumn(text, 1);
            var head = new DockPanel { LastChildFill = true };
            var time = Ui.Text(Ago(n.Time), 11.5, null, "Text.Secondary");
            time.Margin = new Thickness(8, 1, 0, 0);
            DockPanel.SetDock(time, Dock.Right);
            head.Children.Add(time);
            var title = Ui.Text(string.IsNullOrWhiteSpace(n.Title) ? n.AppName : n.Title, 13, FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            head.Children.Add(title);
            text.Children.Add(head);
            if (App.Settings.NotificationPreviews && !string.IsNullOrWhiteSpace(n.Body))
            {
                var body = Ui.Text(n.Body, 13);
                body.TextWrapping = TextWrapping.Wrap;
                body.TextTrimming = TextTrimming.CharacterEllipsis;
                body.MaxHeight = expanded ? 17.5 * 8 : 17.5 * 2;
                body.Margin = new Thickness(0, 1, 0, 0);
                text.Children.Add(body);
            }
            else if (!App.Settings.NotificationPreviews)
                text.Children.Add(Ui.Text(n.AppName, 13, null, "Text.Secondary"));
            grid.Children.Add(text);

            var tile = new GlassTile(ThemeManager.PanelRadius(14)) { Inner = grid, Width = Width, Cursor = Cursors.Hand };
            tile.ToolTip = null;

            // the macOS "×" that appears on the card's top-left corner while hovering
            var x = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(0.5),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(-6, -6, 0, 0), Opacity = 0, Cursor = Cursors.Arrow,
                Child = new TextBlock { Text = "", FontFamily = Ui.IconFont, FontSize = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            x.SetResourceReference(Border.BackgroundProperty, "Menu.Background");
            x.SetResourceReference(Border.BorderBrushProperty, "Separator");
            ((TextBlock)x.Child).SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            closeButton = x;
            return tile;
        }

        /// <summary>Hover shows the close button; returns the host that holds card + button.</summary>
        public static Grid WithClose(GlassTile tile, FrameworkElement close)
        {
            var host = new Grid { Background = Brushes.Transparent };
            host.Children.Add(tile);
            host.Children.Add(close);
            host.MouseEnter += (_, _) => Ui.Animate(close, UIElement.OpacityProperty, 1, 120);
            host.MouseLeave += (_, _) => Ui.Animate(close, UIElement.OpacityProperty, 0, 160);
            return host;
        }
    }

    /// <summary>
    /// Mac-style banners: slide in from the right edge under the menu bar, stack newest on top, stay a few seconds
    /// (longer while the pointer is over them), then slide back out. Click opens the app; swipe right dismisses.
    /// </summary>
    public static class Banners
    {
        const double Gap = 8, Edge = 10;
        static readonly List<BannerWindow> _live = new();

        public static void Start()
        {
            Notifications.Arrived -= Show;
            Notifications.Arrived += Show;
        }

        public static void Show(MacNote n)
        {
            var s = App.Settings;
            if (!s.MacNotifications || !s.NotificationBanners || s.DoNotDisturb || App.FullscreenAppActive || App.NotificationCenterOpen) return;
            while (_live.Count >= 4) _live[^1].Dismiss();
            var b = new BannerWindow(n);
            _live.Insert(0, b);
            b.Closed += (_, _) => { _live.Remove(b); Layout(); };
            Layout(except: b);
            b.Present(TopFor(0));
        }

        static double TopFor(int index)
        {
            double y = MenuBarWindow.BarHeight + 6;
            for (int i = 0; i < index && i < _live.Count; i++) y += _live[i].SlotHeight + Gap;
            return y;
        }

        /// <summary>Called by a banner once it knows its real height.</summary>
        public static void Relayout() => Layout();

        /// <summary>Moves the other banners down (or up after one leaves) with a short glide.</summary>
        static void Layout(BannerWindow except = null)
        {
            for (int i = 0; i < _live.Count; i++)
            {
                var b = _live[i];
                if (b == except || b.Leaving) continue;
                b.MoveTo(TopFor(i));
            }
        }

        public static double RightEdge => SystemParameters.PrimaryScreenWidth - Edge;
    }

    public class BannerWindow : PanelWindow
    {
        const double Pad = 14;   // room for the outer shadow and the hover close button
        readonly MacNote _note;
        readonly Grid _content;
        readonly TranslateTransform _slide = new();
        readonly DispatcherTimer _life = new();
        bool _hover;
        Point? _down;
        public bool Leaving { get; private set; }
        public double SlotHeight => Math.Max(40, _content.ActualHeight > 0 ? _content.ActualHeight : 64);

        public BannerWindow(MacNote n) : base(ThemeManager.PanelRadius(14), noActivate: true)
        {
            _note = n;
            Title = "Notification";
            SizeToContent = SizeToContent.Height;
            Width = NoteCard.Width + 2 * Pad;
            var card = NoteCard.Build(n, false, out var close);
            _content = NoteCard.WithClose(card, close);
            _content.RenderTransform = _slide;
            Content = new Grid { Margin = new Thickness(Pad), Children = { _content }, ClipToBounds = false };
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Notifications.Dismiss(n.Id); Dismiss(); };
            close.MouseLeftButtonDown += (_, e) => e.Handled = true;

            _content.MouseEnter += (_, _) => _hover = true;
            _content.MouseLeave += (_, _) => { _hover = false; RestartLife(); };
            _content.MouseLeftButtonDown += (_, e) => { _down = e.GetPosition(this); _content.CaptureMouse(); };
            _content.MouseMove += (_, e) =>
            {
                if (_down == null) return;
                double dx = e.GetPosition(this).X - _down.Value.X;
                _slide.BeginAnimation(TranslateTransform.XProperty, null);
                _slide.X = Math.Max(0, dx);
            };
            _content.MouseLeftButtonUp += (_, e) =>
            {
                if (_down == null) return;
                double dx = e.GetPosition(this).X - _down.Value.X;
                _down = null;
                _content.ReleaseMouseCapture();
                if (dx > 70) { Dismiss(); return; }                      // swiped away: stays in Notification Center
                if (Math.Abs(dx) < 5) { Notifications.Open(n); Notifications.Dismiss(n.Id); Dismiss(); return; }
                Ui.Animate(_slide, TranslateTransform.XProperty, 0, 220, new CubicEase { EasingMode = EasingMode.EaseOut });
            };
            _life.Tick += (_, _) => { if (!_hover && _down == null) Dismiss(); };
        }

        void RestartLife()
        {
            _life.Stop();
            _life.Interval = TimeSpan.FromSeconds(Math.Clamp(App.Settings.BannerSeconds, 2, 30));
            _life.Start();
        }

        /// <summary>Captures what is behind the slot, then slides the card in from the screen edge.</summary>
        public async void Present(double top)
        {
            try { await PresentCore(top); }
            catch (Exception e) { App.Log(e); try { Close(); } catch { } }
        }

        async Task PresentCore(double top)
        {
            // the whole window slides (not its content), so the glass keeps sampling the right part of the backdrop
            double final = Banners.RightEdge - Width + Pad;
            Left = final;
            Top = top - Pad;
            Opacity = 0;
            Show();
            UpdateLayout();
            Banners.Relayout();                                      // the card's real height is known now
            await Task.Delay(App.Settings.ReduceMotion ? 0 : 240);   // let the banners below glide out of the way first
            if (Leaving) return;
            RefreshBackdrop(null, 0, applyToFrame: false);
            Left = SystemParameters.PrimaryScreenWidth;
            Opacity = 1;
            Ui.Animate(this, LeftProperty, final, 480, new BackEase { Amplitude = 0.16, EasingMode = EasingMode.EaseOut }, from: SystemParameters.PrimaryScreenWidth);
            RestartLife();
        }

        public void MoveTo(double top) => Ui.Animate(this, TopProperty, top - Pad, 240, new CubicEase { EasingMode = EasingMode.EaseOut });

        public void Dismiss()
        {
            if (Leaving) return;
            Leaving = true;
            _life.Stop();
            if (_slide.X > 0) Ui.Animate(_slide, TranslateTransform.XProperty, Width, 260, new CubicEase { EasingMode = EasingMode.EaseIn }, Close);
            else Ui.Animate(this, LeftProperty, SystemParameters.PrimaryScreenWidth, 340, new CubicEase { EasingMode = EasingMode.EaseIn }, Close);
            Ui.Animate(this, OpacityProperty, 0.4, 340);
        }
    }

    /// <summary>
    /// Notification Center: slides in from the right when the clock is clicked. Notifications are grouped per
    /// app as a stack (click to expand), each can be closed with the hover "×", plus Calendar widgets below.
    /// </summary>
    public class NotificationCenterWindow : PanelWindow
    {
        const double Pad = 12;
        readonly StackPanel _list = new();
        readonly ScrollViewer _scroll;
        readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

        public NotificationCenterWindow() : base(ThemeManager.PanelRadius(14), false)
        {
            Title = "Notification Center";
            Width = NoteCard.Width + 2 * Pad + 8;
            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _list, Padding = new Thickness(Pad, 4, Pad, Pad), Focusable = false
            };
            _scroll.ScrollChanged += (_, _) => RealignGlass();   // glass samples the backdrop at its on-screen position
            Content = _scroll;
            HideOnDeactivate(HideAnimated);
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HideAnimated(); };
            Action onChanged = () => { if (IsVisible) Rebuild(); };
            Notifications.Changed += onChanged;
            var tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
            tick.Tick += (_, _) => { if (IsOpen) Rebuild(); else tick.Stop(); };
            IsVisibleChanged += (_, _) => { if (IsVisible) tick.Start(); else tick.Stop(); };
            Closed += (_, _) => { Notifications.Changed -= onChanged; tick.Stop(); };
            // clicking the empty area closes it, like macOS
            MouseLeftButtonUp += (_, e) => { if (e.OriginalSource == _scroll || e.OriginalSource == _list) HideAnimated(); };
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        }

        void Rebuild()
        {
            _list.Children.Clear();
            var notes = Notifications.All;

            if (notes.Count > 0)
            {
                var header = new DockPanel { Margin = new Thickness(4, 6, 0, 8) };
                var clear = Pill("Clear All", Notifications.ClearAll);
                DockPanel.SetDock(clear, Dock.Right);
                header.Children.Add(clear);
                var h = Ui.Text("Notifications", 15, FontWeights.SemiBold);
                h.Effect = TextShadow();
                h.VerticalAlignment = VerticalAlignment.Center;
                header.Children.Add(h);
                _list.Children.Add(header);
            }

            foreach (var group in notes.GroupBy(n => n.AppId ?? n.AppName).OrderByDescending(g => g.Max(n => n.Time)))
            {
                var items = group.OrderByDescending(n => n.Time).ToList();
                bool open = items.Count == 1 || _expanded.Contains(group.Key);
                if (items.Count > 1 && open) _list.Children.Add(GroupHeader(group.Key, items[0].AppName));
                if (open)
                    foreach (var n in items) _list.Children.Add(Card(n, items.Count > 1));
                else
                    _list.Children.Add(Stack(group.Key, items));
            }

            if (notes.Count == 0)
            {
                var none = Ui.Text("No Notifications", 13, FontWeights.SemiBold, "Text.Secondary");
                none.HorizontalAlignment = HorizontalAlignment.Center;
                none.Margin = new Thickness(0, 18, 0, 18);
                none.Effect = TextShadow();
                _list.Children.Add(none);
            }

            _list.Children.Add(Widgets());
            Dispatcher.BeginInvoke(RealignGlass, DispatcherPriority.Loaded);
        }

        static System.Windows.Media.Effects.DropShadowEffect TextShadow() =>
            new() { BlurRadius = 6, ShadowDepth = 0, Opacity = ThemeManager.IsDark ? 0.5 : 0.25, Color = ThemeManager.IsDark ? Colors.Black : Colors.White };

        FrameworkElement Card(MacNote n, bool inGroup)
        {
            var tile = NoteCard.Build(n, true, out var close);
            var host = NoteCard.WithClose(tile, close);
            host.Margin = new Thickness(0, 0, 0, 8);
            close.MouseLeftButtonDown += (_, e) => e.Handled = true;
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Remove(host, () => Notifications.Dismiss(n.Id)); };
            tile.MouseLeftButtonUp += (_, _) => { Notifications.Open(n); Notifications.Dismiss(n.Id); HideAnimated(); };
            return host;
        }

        /// <summary>Several notifications from one app: the newest card with two "sheets" peeking out below it.</summary>
        FrameworkElement Stack(string key, List<MacNote> items)
        {
            var top = NoteCard.Build(items[0], false, out var close);
            var host = new Grid { Margin = new Thickness(0, 0, 0, 8 + (items.Count > 2 ? 12 : 6)), Cursor = Cursors.Hand };
            for (int i = Math.Min(items.Count - 1, 2); i >= 1; i--)
            {
                var sheet = new GlassTile(ThemeManager.PanelRadius(14))
                {
                    Margin = new Thickness(10 * i, 0, 10 * i, -6 * i), Opacity = 1 - 0.25 * i, IsHitTestVisible = false,
                    VerticalAlignment = VerticalAlignment.Bottom, Height = 30
                };
                host.Children.Add(sheet);
            }
            var front = NoteCard.WithClose(top, close);
            host.Children.Add(front);
            close.MouseLeftButtonDown += (_, e) => e.Handled = true;
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Remove(host, () => Notifications.DismissApp(items[0].AppId)); };
            top.MouseLeftButtonUp += (_, _) => { _expanded.Add(key); Rebuild(); };
            return host;
        }

        FrameworkElement GroupHeader(string key, string appName)
        {
            var row = new DockPanel { Margin = new Thickness(4, 2, 0, 6) };
            var x = Pill("", () => Notifications.DismissApp(key), glyph: true);
            DockPanel.SetDock(x, Dock.Right);
            row.Children.Add(x);
            var less = Pill("Show Less", () => { _expanded.Remove(key); Rebuild(); });
            less.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(less, Dock.Right);
            row.Children.Add(less);
            var t = Ui.Text(appName, 15, FontWeights.SemiBold);
            t.Effect = TextShadow();
            t.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(t);
            return row;
        }

        static GlassTile Pill(string text, Action click, bool glyph = false)
        {
            var label = glyph ? Ui.Glyph(text, 9) : Ui.Text(text, 12, FontWeights.Medium);
            label.Margin = glyph ? new Thickness(0) : new Thickness(12, 4, 12, 5);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            var p = new GlassTile(13) { Inner = label, Height = 26, MinWidth = 26, Cursor = Cursors.Hand };
            p.MouseLeftButtonDown += (_, e) => e.Handled = true;
            p.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            return p;
        }

        /// <summary>Card leaves to the right, then the list closes the gap.</summary>
        void Remove(FrameworkElement el, Action then)
        {
            var tt = new TranslateTransform();
            el.RenderTransform = tt;
            el.IsHitTestVisible = false;
            Ui.Animate(el, OpacityProperty, 0, 200);
            Ui.Animate(tt, TranslateTransform.XProperty, 60, 200, new CubicEase { EasingMode = EasingMode.EaseIn }, then);
        }

        // ---------------------------------------------------------------- widgets

        FrameworkElement Widgets()
        {
            var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            double side = (NoteCard.Width - 12) / 2;
            var r = ThemeManager.PanelRadius(14) * 1.3;

            // Calendar (day): weekday in red, big date, nothing scheduled
            var now = DateTime.Now;
            var day = new StackPanel { Margin = new Thickness(16, 14, 12, 12) };
            var wd = Ui.Text(now.ToString("dddd").ToUpperInvariant(), 11, FontWeights.Bold);
            wd.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
            day.Children.Add(wd);
            var big = Ui.Text(now.Day.ToString(), 44, FontWeights.Light);
            big.FontFamily = Ui.DisplayFont;
            big.Margin = new Thickness(-1, -4, 0, 0);
            day.Children.Add(big);
            var ev = Ui.Text("No events today", 12, null, "Text.Secondary");
            ev.Margin = new Thickness(0, 6, 0, 0);
            day.Children.Add(ev);
            var dayTile = new GlassTile(r) { Inner = day, Height = side, Cursor = Cursors.Hand };
            dayTile.MouseLeftButtonUp += (_, _) => { HideAnimated(); Shell.Open("outlookcal:"); };
            row.Children.Add(dayTile);

            // Calendar (month)
            var month = new Grid { Margin = new Thickness(12, 12, 12, 10) };
            month.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < 7; i++) month.RowDefinitions.Add(new RowDefinition());
            for (int i = 0; i < 7; i++) month.ColumnDefinitions.Add(new ColumnDefinition());
            var mt = Ui.Text(now.ToString("MMMM").ToUpperInvariant(), 10.5, FontWeights.Bold);
            mt.Foreground = wd.Foreground;
            mt.Margin = new Thickness(3, 0, 0, 3);
            Grid.SetColumnSpan(mt, 7);
            month.Children.Add(mt);
            var first = new DateTime(now.Year, now.Month, 1);
            int firstDow = (int)System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            string[] names = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames;
            for (int c = 0; c < 7; c++)
            {
                var dn = Ui.Text(names[(c + firstDow) % 7].Substring(0, 1).ToUpperInvariant(), 9, FontWeights.SemiBold, "Text.Secondary");
                dn.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetRow(dn, 1); Grid.SetColumn(dn, c);
                month.Children.Add(dn);
            }
            int offset = ((int)first.DayOfWeek - firstDow + 7) % 7;
            for (int d = 1; d <= DateTime.DaysInMonth(now.Year, now.Month); d++)
            {
                int cell = offset + d - 1;
                var t = Ui.Text(d.ToString(), 9.5, d == now.Day ? FontWeights.Bold : FontWeights.Medium);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.VerticalAlignment = VerticalAlignment.Center;
                FrameworkElement el = t;
                if (d == now.Day)
                {
                    t.Foreground = Brushes.White;
                    el = new Border { Width = 17, Height = 17, CornerRadius = new CornerRadius(8.5), Background = wd.Foreground, Child = t };
                }
                Grid.SetRow(el, 2 + cell / 7); Grid.SetColumn(el, cell % 7);
                month.Children.Add(el);
            }
            var monthTile = new GlassTile(r) { Inner = month, Height = side };
            Grid.SetColumn(monthTile, 2);
            row.Children.Add(monthTile);
            return row;
        }

        // ---------------------------------------------------------------- show / hide

        public void ShowAnimated()
        {
            MarkShown();
            Rebuild();
            var wa = SystemParameters.WorkArea;
            Left = SystemParameters.PrimaryScreenWidth - Width;
            Top = MenuBarWindow.BarHeight + 2;
            Height = Math.Max(300, wa.Bottom - Top - 8);
            Ui.Stop(this, LeftProperty);
            double final = SystemParameters.PrimaryScreenWidth - Width;
            Left = final;
            Opacity = 0;
            Show();
            RefreshBackdrop(null, 0, applyToFrame: false);
            Ui.ForceActivate(this);
            Ui.Animate(this, OpacityProperty, 1, 180);
            Ui.Animate(this, LeftProperty, final, 340, new CubicEase { EasingMode = EasingMode.EaseOut }, from: final + 48);
        }

        public void HideAnimated()
        {
            MarkHidden();
            Ui.Animate(this, LeftProperty, SystemParameters.PrimaryScreenWidth - Width + 48, 200, new CubicEase { EasingMode = EasingMode.EaseIn });
            Ui.Animate(this, OpacityProperty, 0, 200, done: () => { if (Opacity == 0) Hide(); });
        }
    }
}
