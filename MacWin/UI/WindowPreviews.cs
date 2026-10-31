using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacWin.Native;
using MacWin.Services;

namespace MacWin.UI
{
    /// <summary>
    /// Hovering a Dock icon whose app has two or more windows: a glass strip of live thumbnails floats above the
    /// icon; click one to bring that window to the front, × to close it. The strip never takes focus, follows the
    /// pointer from icon to icon (sliding instead of reopening) and fades out once the pointer leaves both.
    /// </summary>
    public class WindowPreviews : PanelWindow
    {
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        const uint PW_RENDERFULLCONTENT = 2;

        const double ThumbW = 200, ThumbH = 124, Gap = 14;
        static readonly Dictionary<IntPtr, BitmapSource> LastGood = new();   // minimised windows can't be captured: show their last look

        readonly ContinuousBorder _frame;
        readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
        readonly ScaleTransform _scale = new(0.92, 0.92);
        readonly DispatcherTimer _leave = new() { Interval = TimeSpan.FromMilliseconds(240) };
        readonly DispatcherTimer _refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
        readonly List<Tile> _tiles = new();
        readonly Func<DockItem, bool> _anchorHovered;
        ImageSource _icon;

        /// <summary>The Dock item the strip currently belongs to (null when closed).</summary>
        public DockItem Item { get; private set; }

        public WindowPreviews(Func<DockItem, bool> anchorHovered) : base(ThemeManager.PanelRadius(16), true, 0x40)
        {
            Title = "MacWin Window Previews";
            _anchorHovered = anchorHovered;
            SizeToContent = SizeToContent.WidthAndHeight;
            _frame = ContinuousBorder.Glass(ThemeManager.PanelRadius(16));
            _frame.Padding = new Thickness(10);
            _frame.Child = _strip;
            _frame.RenderTransform = _scale;
            _frame.RenderTransformOrigin = new Point(0.5, 1);
            Content = _frame;
            Opacity = 0;

            _leave.Tick += (_, _) => { if (!PointerInside() && !(Item != null && _anchorHovered(Item))) HideAnimated(); };
            _refresh.Tick += (_, _) => { foreach (var t in _tiles) t.Refresh(false); };
            MouseEnter += (_, _) => _leave.Stop();
            MouseLeave += (_, _) => _leave.Start();
            WindowTracker.Changed += OnWindowsChanged;
            Closed += (_, _) => WindowTracker.Changed -= OnWindowsChanged;
        }

        // ================================================================== open / move / close

        /// <summary>Opens the strip for <paramref name="item"/>, or slides it over when it is already open for another icon.</summary>
        public void ShowFor(DockItem item, FrameworkElement anchor, DockPosition pos)
        {
            var wins = WindowTracker.WindowsOf(item.AppId);
            if (wins.Count < 2) { HideAnimated(); return; }
            bool moving = IsOpen && Item != item;
            Item = item;
            _icon = item.Img?.Source;
            _leave.Stop();
            Build(wins);

            if (!IsVisible || !IsOpen)
            {
                MarkShown();
                Opacity = 0;
                Show();
                UpdateLayout();
                Place(anchor, pos, animate: false);
                RefreshBackdrop(_frame);
                _scale.ScaleX = _scale.ScaleY = 0.92;
                Ui.Animate(this, OpacityProperty, 1, 170, new CubicEase { EasingMode = EasingMode.EaseOut });
                var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
                Ui.Animate(_scale, ScaleTransform.ScaleXProperty, 1, 260, spring);
                Ui.Animate(_scale, ScaleTransform.ScaleYProperty, 1, 260, spring);
            }
            else
            {
                UpdateLayout();
                Place(anchor, pos, animate: moving);
                if (moving)
                {
                    // the contents are new: fade them in while the frame glides over
                    _strip.Opacity = 0;
                    Ui.Animate(_strip, OpacityProperty, 1, 200);
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (IsOpen) RefreshBackdrop(_frame); });
                }
            }
            if (!_refresh.IsEnabled) _refresh.Start();
        }

        /// <summary>The pointer left the Dock: close unless it is heading onto the strip.</summary>
        public void HideSoon()
        {
            if (!IsOpen) return;
            _leave.Start();
        }

        public void HideAnimated()
        {
            _leave.Stop();
            _refresh.Stop();
            Item = null;
            if (!IsVisible) return;
            MarkHidden();
            Ui.Animate(_scale, ScaleTransform.ScaleXProperty, 0.94, 140);
            Ui.Animate(_scale, ScaleTransform.ScaleYProperty, 0.94, 140);
            Ui.Animate(this, OpacityProperty, 0, 140, new CubicEase { EasingMode = EasingMode.EaseIn }, () => { if (!IsOpen) Hide(); });
        }

        void Place(FrameworkElement anchor, DockPosition pos, bool animate)
        {
            var tl = anchor.PointToScreen(new Point(0, 0));
            double s = Ui.Scale(anchor);
            double ax = tl.X / s, ay = tl.Y / s, aw = anchor.ActualWidth, ah = anchor.ActualHeight;
            double left, top;
            switch (pos)
            {
                case DockPosition.Bottom: left = ax + aw / 2 - ActualWidth / 2; top = ay - ActualHeight - Gap; _frame.RenderTransformOrigin = new Point(0.5, 1); break;
                case DockPosition.Left: left = ax + aw + Gap; top = ay + ah / 2 - ActualHeight / 2; _frame.RenderTransformOrigin = new Point(0, 0.5); break;
                default: left = ax - ActualWidth - Gap; top = ay + ah / 2 - ActualHeight / 2; _frame.RenderTransformOrigin = new Point(1, 0.5); break;
            }
            left = Math.Clamp(left, 6, Math.Max(6, SystemParameters.PrimaryScreenWidth - ActualWidth - 6));
            top = Math.Clamp(top, MenuBarWindow.BarHeight + 6, Math.Max(MenuBarWindow.BarHeight + 6, SystemParameters.PrimaryScreenHeight - ActualHeight - 6));
            if (animate && !App.Settings.ReduceMotion)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                Ui.Animate(this, LeftProperty, left, 240, ease);
                Ui.Animate(this, TopProperty, top, 240, ease);
            }
            else
            {
                Ui.Stop(this, LeftProperty); Ui.Stop(this, TopProperty);
                Left = left; Top = top;
            }
        }

        bool PointerInside()
        {
            if (!Win32.GetCursorPos(out var cp)) return false;
            var h = Ui.Hwnd(this);
            return h != IntPtr.Zero && Win32.GetWindowRect(h, out var r) && cp.X >= r.Left && cp.X < r.Right && cp.Y >= r.Top && cp.Y < r.Bottom;
        }

        void OnWindowsChanged()
        {
            if (!IsOpen || Item == null) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsOpen || Item == null) return;
                var wins = WindowTracker.WindowsOf(Item.AppId);
                if (wins.Count < 2) { HideAnimated(); return; }
                if (wins.Count != _tiles.Count || wins.Where((w, i) => _tiles[i].Hwnd != w.Hwnd).Any())
                {
                    Build(wins);
                    UpdateLayout();
                }
                else foreach (var t in _tiles) t.UpdateTitle();
            });
        }

        // ================================================================== tiles

        void Build(List<AppWindow> wins)
        {
            _strip.Children.Clear();
            _tiles.Clear();
            foreach (var w in wins.Take(8))
            {
                var t = new Tile(this, w, _icon);
                _tiles.Add(t);
                _strip.Children.Add(t);
            }
            foreach (var t in _tiles) t.Refresh(true);
        }

        sealed class Tile : Border
        {
            readonly WindowPreviews _owner;
            readonly AppWindow _win;
            readonly Image _img = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            readonly Image _fallback = new() { Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
            readonly TextBlock _title;
            readonly Border _hover;
            readonly Border _close;
            bool _hasShot;

            public IntPtr Hwnd => _win.Hwnd;

            public Tile(WindowPreviews owner, AppWindow w, ImageSource icon)
            {
                _owner = owner; _win = w;
                Margin = new Thickness(4, 0, 4, 0);
                Padding = new Thickness(8, 8, 8, 6);
                CornerRadius = new CornerRadius(12);
                Background = Brushes.Transparent;
                Cursor = Cursors.Hand;
                RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
                RenderOptions.SetBitmapScalingMode(_fallback, BitmapScalingMode.HighQuality);
                _fallback.Source = icon;

                _hover = new Border
                {
                    CornerRadius = new CornerRadius(12), Opacity = 0, IsHitTestVisible = false,
                    Background = new SolidColorBrush(ThemeManager.IsDark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0, 0, 0))
                };

                var shot = new Grid { Width = ThumbW, Height = ThumbH, Background = new SolidColorBrush(ThemeManager.IsDark ? Color.FromArgb(0x30, 0, 0, 0) : Color.FromArgb(0x14, 0, 0, 0)) };
                shot.Clip = new RectangleGeometry(new Rect(0, 0, ThumbW, ThumbH), 8, 8);
                shot.Children.Add(_fallback);
                shot.Children.Add(_img);

                _title = Ui.Text("", 12, null, "Text.Primary");
                _title.TextTrimming = TextTrimming.CharacterEllipsis;
                _title.TextAlignment = TextAlignment.Center;
                _title.MaxWidth = ThumbW;
                _title.Margin = new Thickness(0, 7, 0, 0);
                UpdateTitle();

                // × appears on hover (top-left, like a Mac window's close button)
                var x = Ui.Text("✕", 9, FontWeights.Bold, "Text.Primary");
                x.HorizontalAlignment = HorizontalAlignment.Center; x.VerticalAlignment = VerticalAlignment.Center;
                _close = new Border
                {
                    Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Opacity = 0, Margin = new Thickness(2, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x3A, 0x3A, 0x3C)), Child = x, Cursor = Cursors.Arrow
                };
                _close.MouseEnter += (_, _) => _close.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x57));
                _close.MouseLeave += (_, _) => _close.Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x3A, 0x3A, 0x3C));
                _close.MouseLeftButtonUp += (_, e) => { e.Handled = true; WindowTracker.Close(_win.Hwnd); };
                _close.MouseLeftButtonDown += (_, e) => e.Handled = true;

                var body = new StackPanel();
                body.Children.Add(shot);
                body.Children.Add(_title);
                var g = new Grid();
                g.Children.Add(_hover);
                g.Children.Add(body);
                g.Children.Add(_close);
                Child = g;

                MouseEnter += (_, _) => { Ui.Animate(_hover, OpacityProperty, 1, 120); Ui.Animate(_close, OpacityProperty, 1, 120); };
                MouseLeave += (_, _) => { Ui.Animate(_hover, OpacityProperty, 0, 160); Ui.Animate(_close, OpacityProperty, 0, 160); };
                MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    WindowTracker.Activate(_win);
                    _owner.HideAnimated();
                };
                MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; WindowTracker.Close(_win.Hwnd); } };
            }

            public void UpdateTitle()
            {
                string t = string.IsNullOrWhiteSpace(_win.Title) ? (_owner.Item?.Label ?? "") : _win.Title;
                _title.Text = t;
                ToolTip = t.Length > 28 ? t : null;
            }

            /// <summary>Grabs a fresh picture of the window; minimised windows keep their last one (or show the app icon).</summary>
            public void Refresh(bool first)
            {
                BitmapSource shot = null;
                if (!Win32.IsIconic(_win.Hwnd) && Win32.IsWindow(_win.Hwnd)) shot = Capture(_win.Hwnd);
                if (shot != null) LastGood[_win.Hwnd] = shot;
                else if (!first && _hasShot) return;                                        // keep what we have
                else LastGood.TryGetValue(_win.Hwnd, out shot);

                if (shot != null)
                {
                    bool fade = !_hasShot;
                    _img.Source = shot;
                    _hasShot = true;
                    _fallback.Opacity = 0;
                    if (fade) { _img.Opacity = 0; Ui.Animate(_img, OpacityProperty, 1, 180); }
                }
                else if (!_hasShot)
                {
                    _fallback.Opacity = 1;
                }
            }
        }

        // ================================================================== capture

        /// <summary>Renders the window (even when covered by others) and scales it down to thumbnail size.</summary>
        static BitmapSource Capture(IntPtr hwnd)
        {
            if (!Win32.GetWindowRect(hwnd, out var full)) return null;
            int fw = full.Width, fh = full.Height;
            if (fw < 8 || fh < 8) return null;
            IntPtr screen = Win32.GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero;
            try
            {
                mem = CreateCompatibleDC(screen);
                bmp = CreateCompatibleBitmap(screen, fw, fh);
                var old = SelectObject(mem, bmp);
                bool ok = PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT);
                SelectObject(mem, old);
                if (!ok) return null;
                BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                // cut off the invisible resize borders Windows 10/11 add around the visible frame
                if (Win32.GetVisibleRect(hwnd, out var vis))
                {
                    int x = Math.Clamp(vis.Left - full.Left, 0, fw - 1), y = Math.Clamp(vis.Top - full.Top, 0, fh - 1);
                    int w = Math.Clamp(vis.Width, 1, fw - x), h = Math.Clamp(vis.Height, 1, fh - y);
                    if (x > 0 || y > 0 || w < fw || h < fh) src = new CroppedBitmap(src, new Int32Rect(x, y, w, h));
                }
                double k = Math.Min(ThumbW * 2 / src.PixelWidth, ThumbH * 2 / src.PixelHeight);   // 2× for crisp HiDPI
                if (k < 1) src = new TransformedBitmap(src, new ScaleTransform(k, k));
                var copy = new WriteableBitmap(src);   // detach from the HBITMAP before it is deleted
                copy.Freeze();
                return copy;
            }
            catch { return null; }
            finally
            {
                if (bmp != IntPtr.Zero) Win32.DeleteObject(bmp);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                Win32.ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
