using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MacWin.Services;

namespace MacWin.UI
{
    /// <summary>
    /// Searchable app chooser: a button showing the current app; clicking opens a glass popover with a
    /// search field ("chrome", "word"…) over suggested apps, websites and every installed app.
    /// </summary>
    public class AppPicker : Grid
    {
        public record Option(string Id, string Name, bool Suggested, string Note = null);

        readonly Func<List<Option>> _options;
        readonly Button _button = new();
        readonly TextBlock _label = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        readonly Image _icon = new() { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) };
        readonly Popup _popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.Fade };
        readonly TextBox _search = new();
        readonly StackPanel _list = new();
        readonly ScrollViewer _scroll;
        readonly List<(Border row, Option opt)> _rows = new();
        int _sel = -1;
        string _current;

        public event Action<string> Picked;

        readonly string _placeholder;

        public AppPicker(string currentId, Func<List<Option>> options, double width = 240, string placeholder = "Choose…")
        {
            _placeholder = placeholder;
            _options = options;
            _current = currentId;
            Width = width;
            HorizontalAlignment = HorizontalAlignment.Left;

            var content = new DockPanel { LastChildFill = true };
            var chevron = Ui.Glyph("", 9, "Text.Secondary");
            DockPanel.SetDock(chevron, Dock.Right);
            content.Children.Add(chevron);
            content.Children.Add(_icon);
            _label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            content.Children.Add(_label);
            _button.Style = Ui.Style("MacButton");
            _button.Padding = new Thickness(8, 3, 8, 3);
            _button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            _button.Content = content;
            _button.Click += (_, _) => Open();
            Children.Add(_button);

            // popover
            _search.Style = Ui.Style("FieldTextBox");
            _search.Margin = new Thickness(0, 0, 0, 6);
            _search.TextChanged += (_, _) => Fill();
            _search.PreviewKeyDown += OnKey;
            var hint = Ui.Text("Search apps", 12, null, "Text.Tertiary");
            hint.Margin = new Thickness(10, 4, 0, 0);
            hint.IsHitTestVisible = false;
            _search.TextChanged += (_, _) => hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var searchHost = new Grid();
            searchHost.Children.Add(_search);
            searchHost.Children.Add(hint);
            _scroll = new ScrollViewer { Content = _list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new DockPanel();
            DockPanel.SetDock(searchHost, Dock.Top);
            stack.Children.Add(searchHost);
            stack.Children.Add(_scroll);
            var frame = ContinuousBorder.Glass(ThemeManager.PanelRadius(10), "Menu.Background");
            frame.Padding = new Thickness(6);
            frame.Child = stack;
            frame.Width = Math.Max(width, 280);
            frame.Margin = new Thickness(0, 4, 12, 16);
            frame.Effect = (System.Windows.Media.Effects.Effect)Application.Current.Resources["MenuShadow"];
            _popup.Child = frame;
            _popup.PlacementTarget = _button;
            _popup.Opened += (_, _) => { _search.Focus(); Keyboard.Focus(_search); };
            UpdateButton();
        }

        void UpdateButton()
        {
            var opt = _options().FirstOrDefault(o => string.Equals(o.Id, _current, StringComparison.OrdinalIgnoreCase));
            _label.Text = opt?.Name ?? (_current == null ? _placeholder : AppCatalog.DisplayName(_current));
            LoadIcon(_icon, _current);
        }

        static async void LoadIcon(Image img, string id)
        {
            try { img.Source = id == null ? null : await IconProvider.ForApp(id); } catch { }
        }

        void Open()
        {
            _search.Text = "";
            Fill();
            _popup.IsOpen = true;
        }

        void Fill()
        {
            string q = _search.Text.Trim();
            var all = _options();
            IEnumerable<Option> shown;
            if (q.Length == 0) shown = all;
            else
            {
                shown = all.Select(o => (o, s: Math.Max(Search.Score(o.Name, q),
                                                 Math.Max(Search.Score(System.IO.Path.GetFileNameWithoutExtension(AppCatalog.ById(o.Id)?.ExePath ?? ""), q) - 5,
                                                          Search.Score(o.Id, q) - 10))))
                           .Where(x => x.s > 0).OrderByDescending(x => x.s).ThenBy(x => x.o.Name.Length).Select(x => x.o);
            }
            _list.Children.Clear();
            _rows.Clear();
            bool? lastSuggested = null;
            foreach (var o in shown.Take(200))
            {
                if (q.Length == 0 && lastSuggested != o.Suggested)
                {
                    lastSuggested = o.Suggested;
                    var h = Ui.Text(o.Suggested ? "Suggested" : "All apps", 11, FontWeights.SemiBold, "Text.Secondary");
                    h.Margin = new Thickness(6, _list.Children.Count == 0 ? 0 : 8, 0, 3);
                    _list.Children.Add(h);
                }
                var row = Row(o);
                _rows.Add((row, o));
                _list.Children.Add(row);
            }
            if (_rows.Count == 0)
            {
                var none = Ui.Text(AppCatalog.Apps.Count == 0 ? "Loading apps…" : "No matching apps", 12, null, "Text.Secondary");
                none.Margin = new Thickness(6, 4, 0, 4);
                _list.Children.Add(none);
                if (AppCatalog.Apps.Count == 0) AppCatalog.RefreshIfStale(TimeSpan.Zero);
            }
            Select(_rows.Count > 0 ? 0 : -1);
        }

        Border Row(Option o)
        {
            var g = new Grid { Height = 24 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var img = new Image { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            if (!(o.Id?.StartsWith("http", StringComparison.OrdinalIgnoreCase) ?? true)) LoadIcon(img, o.Id);
            else img.Source = null;
            var t = new TextBlock { Text = o.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            Grid.SetColumn(t, 1);
            g.Children.Add(img);
            g.Children.Add(t);
            if (string.Equals(o.Id, _current, StringComparison.OrdinalIgnoreCase))
            {
                var check = Ui.Glyph("", 10, "Text.Primary");
                Grid.SetColumn(check, 2);
                g.Children.Add(check);
            }
            var b = new Border { Child = g, CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 0, 6, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            b.MouseEnter += (_, _) => Select(_rows.FindIndex(r => r.row == b));
            b.MouseLeftButtonUp += (_, _) => Choose(o);
            return b;
        }

        void Select(int i)
        {
            if (_sel >= 0 && _sel < _rows.Count) Paint(_rows[_sel].row, false);
            _sel = i;
            if (i < 0 || i >= _rows.Count) return;
            Paint(_rows[i].row, true);
            _rows[i].row.BringIntoView();
        }

        static void Paint(Border row, bool on)
        {
            var tb = ((Grid)row.Child).Children.OfType<TextBlock>().ToList();
            if (on)
            {
                row.SetResourceReference(Border.BackgroundProperty, "Accent");
                foreach (var t in tb) t.Foreground = Brushes.White;
            }
            else
            {
                row.Background = Brushes.Transparent;
                foreach (var t in tb) t.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            }
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down: Select(Math.Min(_rows.Count - 1, _sel + 1)); e.Handled = true; break;
                case Key.Up: Select(Math.Max(0, _sel - 1)); e.Handled = true; break;
                case Key.Enter: if (_sel >= 0 && _sel < _rows.Count) Choose(_rows[_sel].opt); e.Handled = true; break;
                case Key.Escape: _popup.IsOpen = false; e.Handled = true; break;
            }
        }

        void Choose(Option o)
        {
            _popup.IsOpen = false;
            _current = o.Id;
            UpdateButton();
            Picked?.Invoke(o.Id);
        }

        /// <summary>Every installed app (for "Add App to Dock").</summary>
        public static List<Option> AllApps() => AppCatalog.Apps.Select(a => new Option(a.Id, a.Name, false)).ToList();

        /// <summary>Options for a Dock slot: suggested apps for the role, its website, then every installed app.</summary>
        public static List<Option> ForRole(string roleKey)
        {
            var def = Roles.Def(roleKey);
            var list = new List<Option>();
            var candidates = Roles.Candidates(roleKey);
            foreach (var c in candidates) list.Add(new Option(c.Id, c.Name, true));
            if (def?.FallbackUrl != null)
                list.Add(new Option(def.FallbackUrl, def.FallbackUrl.StartsWith("http") ? "Website — " + new Uri(def.FallbackUrl).Host : "Windows Settings", true));
            foreach (var a in AppCatalog.Apps.Where(a => !candidates.Contains(a))) list.Add(new Option(a.Id, a.Name, false));
            return list;
        }
    }
}
