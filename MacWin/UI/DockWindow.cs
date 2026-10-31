using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacWin.Native;
using MacWin.Services;

namespace MacWin.UI
{
    public enum DockItemKind { App, Role, Launchpad, Separator, Downloads, Trash }

    public class DockItem
    {
        public string Key;
        public DockItemKind Kind;
        public string AppId;            // the Windows app behind the item (for running detection / launch)
        public string Label;
        public bool Pinned;
        public Grid View;
        public Image Img;
        public Rectangle Dim;
        public Ellipse Dot;
        public Rectangle SepLine;
        public double Presence, PresenceTarget = 1;
        public double DisplayPos = double.NaN;
        public double Size;
        public double BounceStart = -1;
        public double DotOpacity;
        public bool Running;
        public bool IsIcon => Kind != DockItemKind.Separator;
    }

    /// <summary>
    /// The Dock. Rendering is done in a transparent topmost window; the frosted blur behind the shelf is a
    /// separate native window (<see cref="BlurUnderlay"/>). The per-frame loop only runs while something moves.
    /// </summary>
    public class DockWindow : Window
    {
        readonly Canvas _canvas = new() { ClipToBounds = false };
        readonly ContinuousBorder _shelfOuter = new() { ClipContent = true };
        readonly Rectangle _glassFill = new() { IsHitTestVisible = false, Opacity = 0 }; // refracted wallpaper (liquid glass)
        readonly ImageBrush _glassBrush = new() { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        LiquidGlassEffect _glassFx;
        LiveBackdrop _live;                 // live capture of what is behind the shelf (windows, video…)
        ImageSource _liveImage;
        Win32.RECT _shelfPx;                // shelf in screen pixels, for the live capture
        double _glassTarget;
        readonly Grid _shelfInner = new();
        readonly Border _sheen = new() { IsHitTestVisible = false };
        readonly Ellipse _specular = new() { IsHitTestVisible = false, Opacity = 0 };
        readonly TranslateTransform _specularShift = new();
        Size _shelfInnerSize;
        readonly Border _label = new() { Padding = new Thickness(10, 3, 10, 4), CornerRadius = new CornerRadius(6), IsHitTestVisible = false, Opacity = 0 };
        readonly TextBlock _labelText = new() { FontSize = 13 };
        readonly Image _ghost = new() { IsHitTestVisible = false, Opacity = 0 };
        readonly List<DockItem> _items = new();
        readonly AppBar _appBar;
        readonly BlurUnderlay _backdrop = new(0x10);   // frosted blur behind the shelf (clipped to its rounded shape)
        readonly DispatcherTimer _idle = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(70) };
        readonly DispatcherTimer _trashTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(6) };

        bool _looping;
        TimeSpan _lastRender;
        double _clock;                // seconds since start of loop epoch
        double _influence;            // 0..1 magnification strength
        double _mouseAlong = double.NaN;
        bool _hovering;
        bool _menuOpen;
        double _hideOffset, _hideTarget;
        DateTime _lastHover = DateTime.Now;
        bool _trashFull;
        bool _hiddenForFullscreen;
        bool _covered;                                  // smart hide: front window is maximised / overlaps the Dock
        (double start, double len) _baseShelf = (0, 0); // un-magnified shelf span (DIPs, along axis)

        // drag state
        DockItem _pressed;
        Point _pressPoint;
        DockItem _dragging;
        bool _dragOut;
        DockItem _dropTarget;

        // geometry (DIPs)
        double B => App.Settings.DockSize;
        double M => App.Settings.Magnification ? Math.Max(App.Settings.MagnifiedSize, B) : B;
        double Pad => Math.Round(B * 0.12);
        double Gap => Math.Round(B * 0.1);
        double SepW => Math.Round(B * 0.36);
        bool Liquid => App.Settings.LiquidGlass;
        double EdgeMargin => Liquid ? 9 : 5;                        // liquid glass Dock floats higher
        double ShelfRadius => Math.Round(T * (Liquid ? 0.42 : 0.3));
        double T => B + Pad * 2;
        DockPosition Pos => App.Settings.DockPosition;
        bool Horizontal => Pos == DockPosition.Bottom;
        double WinAlong => Horizontal ? ActualWidth : ActualHeight;
        double WinAcross => Horizontal ? ActualHeight : ActualWidth;

        public DockWindow()
        {
            Title = "MacWin Dock";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            AllowDrop = true;
            UseLayoutRounding = false;
            FontFamily = Ui.Font;
            Content = _canvas;
            _appBar = new AppBar(this);

            // shelf: continuous-corner glass with anti-aliased rim drawn above its layers
            _shelfOuter.SetResourceReference(ContinuousBorder.OuterBrushProperty, "Dock.Outer");
            _shelfOuter.SetResourceReference(ContinuousBorder.SheenProperty, "Glass.Sheen");
            // layers inside the shelf: refracted wallpaper (liquid glass) + a specular glint that follows the pointer
            _glassFill.Fill = _glassBrush;
            _specular.HorizontalAlignment = HorizontalAlignment.Left;
            _specular.VerticalAlignment = VerticalAlignment.Top;
            _specular.RenderTransform = _specularShift;
            _shelfInner.Children.Add(_glassFill);
            var tint = new Rectangle { IsHitTestVisible = false };
            tint.SetResourceReference(Shape.FillProperty, "Dock.Shelf"); // frosted tint over the wallpaper layer
            _shelfInner.Children.Add(tint);
            _shelfInner.Children.Add(_specular);
            _shelfOuter.Child = _shelfInner;
            WallpaperBackdrop.Changed += () => { if (_liveImage == null) _glassBrush.ImageSource = WallpaperBackdrop.Image; StartLoop(); };
            _shelfOuter.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowDockMenu(); };
            _canvas.Children.Add(_shelfOuter);

            _label.SetResourceReference(Border.BackgroundProperty, "Label.Background");
            _label.SetResourceReference(Border.BorderBrushProperty, "Glass.Frame");
            _label.SetResourceReference(Border.BorderThicknessProperty, "Glass.Thickness");
            _labelText.SetResourceReference(TextBlock.ForegroundProperty, "Label.Foreground");
            _label.Child = _labelText;
            _label.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.2, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
            Panel.SetZIndex(_label, 50);
            Panel.SetZIndex(_ghost, 60);
            _canvas.Children.Add(_label);
            _canvas.Children.Add(_ghost);
            RenderOptions.SetBitmapScalingMode(_canvas, BitmapScalingMode.HighQuality);

            SourceInitialized += (_, _) =>
            {
                Backdrop.MakeToolWindow(this, noActivate: true);
                // whenever the Dock moves in the z-order, slide the blur back underneath it
                System.Windows.Interop.HwndSource.FromHwnd(Ui.Hwnd(this))?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (msg == 0x0047 /*WM_WINDOWPOSCHANGED*/) { _lastBackdrop = Rect.Empty; StartLoop(); }
                    return IntPtr.Zero;
                });
            };
            Loaded += (_, _) =>
            {
                ApplySettings();
            };
            Closed += (_, _) => { _appBar.Unregister(); _backdrop.Close(); };

            MouseEnter += (_, _) => StartLoop();
            MouseLeave += (_, _) => StartLoop();
            PreviewMouseMove += OnMouseMove;
            PreviewMouseLeftButtonUp += OnMouseUp;
            DragOver += OnDragOver;
            DragLeave += (_, _) => SetDropTarget(null);
            Drop += OnDrop;

            _idle.Tick += (_, _) => IdleTick();
            _trashTimer.Tick += (_, _) => UpdateTrash();
            _trashTimer.Start();

            WindowTracker.Changed += SyncRunning;
            WindowTracker.ForegroundChanged += IdleTick; // react instantly when another window comes to the front
            ThemeManager.Changed += () => { UpdateShelfFill(); UpdateSpecularBrush(); _backdrop.ApplyBlur(); };
            SystemParameters.StaticPropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SystemParameters.PrimaryScreenWidth) || e.PropertyName == nameof(SystemParameters.PrimaryScreenHeight)) PlaceWindow();
            };
        }

        public void Unregister() => _appBar.Unregister();

        // ================================================================== configuration

        public void ApplySettings()
        {
            var s = App.Settings;
            UpdateShelfFill();
            _shelfOuter.CornerRadius = ShelfRadius;
            _shelfOuter.SetResourceReference(ContinuousBorder.BorderBrushProperty, s.LiquidGlass ? "Glass.Rim" : "Dock.Border");
            _shelfOuter.BorderThickness = s.LiquidGlass ? 1.3 : 0.8;
            // real lens refraction needs pixel shader 3.0; otherwise the frosted DWM blur alone is used
            _glassFx = s.LiquidGlass && LiquidGlassEffect.Available ? (_glassFx ?? new LiquidGlassEffect()) : null;
            _glassFill.Effect = _glassFx;
            _glassBrush.ImageSource = WallpaperBackdrop.Image;
            ApplyLiveGlass();
            UpdateSpecularBrush();
            _shelfInnerSize = Size.Empty;
            _label.Padding = new Thickness(10, 3, 10, 4);
            Rebuild();
            PlaceWindow();
            if (s.DockAutoHide || s.DockSmartHide)
            {
                // no reserved strip: maximised windows reach the bottom of the screen and the Dock slides over them
                _appBar.Unregister();
                _hideTarget = s.DockAutoHide ? HiddenOffset : 0;
                _idle.Start();
                IdleTick();
            }
            else
            {
                _hideTarget = 0;
                // liquid glass still needs to know when a window sits behind the Dock
                if (s.LiquidGlass) _idle.Start(); else _idle.Stop();
                var edge = Pos switch { DockPosition.Left => AppBarEdge.Left, DockPosition.Right => AppBarEdge.Right, _ => AppBarEdge.Bottom };
                if (_appBar.IsRegistered) _appBar.Update(edge, EdgeMargin + T + 3);
                else _appBar.Register(edge, EdgeMargin + T + 3);
            }
            UpdateTrash();
            StartProcessWatch();
            StartLoop();
        }

        /// <summary>Adds an app (catalog id or file path) to the end of the pinned section.</summary>
        public void PinApp(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return;
            var role = Roles.ForApp(appId);
            string key = role != null ? "role:" + role.Role : "app:" + appId;
            Pin(key, double.MaxValue);
        }

        double HiddenOffset => -(T + EdgeMargin + 6);

        void UpdateShelfFill()
        {
            // the shelf colour shows only while the frosted wallpaper layer is off (window behind the Dock / blur off),
            // so it is a solid-looking frosted tint rather than a see-through one
            var b = (SolidColorBrush)Application.Current.Resources["Dock.Shelf"];
            var c = b.Color;
            c.A = 0xE6;
            var solid = new SolidColorBrush(c);
            solid.Freeze();
            _shelfOuter.Background = solid;
        }

        void PlaceWindow()
        {
            double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
            double across = EdgeMargin + T + Math.Max(M - B, B * 0.55) + 56;
            int icons = _items.Count(i => i.IsIcon && i.PresenceTarget > 0);
            double baseLen = Pad * 2 + icons * (B + Gap) + 3 * (SepW + Gap);
            double along = baseLen + (M - B) * 6 + 260;
            if (Horizontal)
            {
                Width = Math.Min(sw, along);
                Height = across;
                Left = (sw - Width) / 2;
                Top = sh - Height;
            }
            else
            {
                double top = MenuBarWindow.BarHeight;
                Width = across;
                Height = Math.Min(sh - top, along);
                Top = top + (sh - top - Height) / 2;
                Left = Pos == DockPosition.Left ? 0 : sw - Width;
            }
            _ghost.Width = _ghost.Height = M;
            StartLoop();
        }

        // ================================================================== item list

        static string KeyToAppId(string key)
        {
            if (key.StartsWith("role:")) return Roles.Binding(key[5..])?.AppId;
            if (key.StartsWith("app:")) return key[4..];
            return null;
        }

        public void Rebuild()
        {
            var s = App.Settings;
            var desired = new List<string>();
            foreach (var k in s.PinnedApps ?? new List<string> { "sys:launchpad" })
            {
                if (k.StartsWith("role:") && KeyToAppId(k) == null) continue;
                if (!desired.Contains(k)) desired.Add(k);
            }
            var pinnedAppIds = new HashSet<string>(desired.Select(KeyToAppId).Where(x => x != null), StringComparer.OrdinalIgnoreCase);
            var running = new List<string>();
            if (s.ShowRunningApps)
            {
                foreach (var w in WindowTracker.Windows)
                {
                    if (w.AppId == null || pinnedAppIds.Contains(w.AppId)) continue;
                    var k = "app:" + w.AppId;
                    if (!running.Contains(k)) running.Add(k);
                }
            }
            // just launched, window not shown yet
            foreach (var id in _launching.Keys)
            {
                if (pinnedAppIds.Contains(id)) continue;
                var k = "app:" + id;
                if (!running.Contains(k)) running.Add(k);
            }
            if (running.Count > 0) { desired.Add("sep:a"); desired.AddRange(running); }
            desired.Add("sep:b");
            desired.Add("sys:downloads");
            desired.Add("sys:trash");

            var next = new List<DockItem>();
            foreach (var key in desired)
            {
                var item = _items.FirstOrDefault(i => i.Key == key) ?? Create(key);
                item.Pinned = s.PinnedApps?.Contains(key) == true;
                item.PresenceTarget = 1;
                if (item.Kind == DockItemKind.Role)
                {
                    var b = Roles.Binding(key[5..]);
                    var newId = b?.AppId;
                    if (newId != item.AppId) { item.AppId = newId; LoadIcon(item); }
                    item.Label = b?.Label ?? item.Label;
                }
                next.Add(item);
            }
            // items that disappear shrink away in place
            for (int i = 0; i < _items.Count; i++)
            {
                var old = _items[i];
                if (next.Contains(old)) continue;
                old.PresenceTarget = 0;
                next.Insert(Math.Min(i, next.Count), old);
            }
            _items.Clear();
            _items.AddRange(next);
            SyncRunning();
            StartLoop();
        }

        DockItem Create(string key)
        {
            var item = new DockItem { Key = key, Presence = _items.Count == 0 && !IsLoaded ? 1 : 0 };
            if (key.StartsWith("sep:"))
            {
                item.Kind = DockItemKind.Separator;
                item.SepLine = new Rectangle { IsHitTestVisible = false };
                item.SepLine.SetResourceReference(Shape.FillProperty, "Separator");
                _canvas.Children.Add(item.SepLine);
                return item;
            }
            item.Kind = key switch
            {
                "sys:launchpad" => DockItemKind.Launchpad,
                "sys:downloads" => DockItemKind.Downloads,
                "sys:trash" => DockItemKind.Trash,
                _ when key.StartsWith("role:") => DockItemKind.Role,
                _ => DockItemKind.App
            };
            item.AppId = KeyToAppId(key);
            item.Label = item.Kind switch
            {
                DockItemKind.Launchpad => "Launchpad",
                DockItemKind.Downloads => "Downloads",
                DockItemKind.Trash => "Trash",
                DockItemKind.Role => Roles.Binding(key[5..])?.Label ?? key[5..],
                _ => AppCatalog.DisplayName(item.AppId)
            };

            item.Img = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = true };
            RenderOptions.SetBitmapScalingMode(item.Img, BitmapScalingMode.HighQuality);
            item.Dim = new Rectangle { Fill = Brushes.Black, Opacity = 0, IsHitTestVisible = false };
            item.View = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Arrow };
            item.View.Children.Add(item.Img);
            item.View.Children.Add(item.Dim);
            item.Dot = new Ellipse { Width = 4, Height = 4, Opacity = 0, IsHitTestVisible = false };
            item.Dot.SetResourceReference(Shape.FillProperty, "Dock.Indicator");
            _canvas.Children.Add(item.View);
            _canvas.Children.Add(item.Dot);

            item.View.MouseEnter += (_, _) => StartLoop();
            item.View.MouseLeftButtonDown += (_, e) =>
            {
                _pressed = item;
                _pressPoint = e.GetPosition(this);
                item.Dim.Opacity = 0.32;
                item.View.CaptureMouse();
                e.Handled = true;
            };
            item.View.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowItemMenu(item); };
            LoadIcon(item);
            return item;
        }

        async void LoadIcon(DockItem item)
        {
            try
            {
                ImageSource img = item.Kind switch
                {
                    DockItemKind.Launchpad => await IconProvider.ForKey("launchpad"),
                    DockItemKind.Downloads => await IconProvider.ForKey("downloads"),
                    DockItemKind.Trash => await IconProvider.Trash(_trashFull),
                    DockItemKind.Role => await IconProvider.ForRole(item.Key[5..]),
                    _ => await IconProvider.ForApp(item.AppId)
                };
                if (img == null) return;
                item.Img.Source = img;
                item.Dim.OpacityMask = new ImageBrush(img);
            }
            catch { }
        }

        public void ReloadIcons()
        {
            foreach (var i in _items.Where(x => x.IsIcon)) LoadIcon(i);
        }

        void SyncRunning()
        {
            // running apps that are not pinned come and go
            var s = App.Settings;
            if (s.ShowRunningApps)
            {
                var shown = new HashSet<string>(_items.Where(i => i.PresenceTarget > 0 && i.AppId != null).Select(i => i.AppId), StringComparer.OrdinalIgnoreCase);
                bool needRebuild = WindowTracker.Windows.Any(w => w.AppId != null && !shown.Contains(w.AppId))
                    || _items.Any(i => i.Kind == DockItemKind.App && !i.Pinned && i.PresenceTarget > 0 && WindowTracker.WindowsOf(i.AppId).Count == 0
                                       && !(i.AppId != null && _launching.ContainsKey(i.AppId)));
                if (needRebuild) { RebuildSoon(); }
            }
            foreach (var i in _items)
            {
                bool hasWindow = i.AppId != null && WindowTracker.WindowsOf(i.AppId).Count > 0;
                if (hasWindow && i.AppId != null) _launching.Remove(i.AppId); // the launch finished
                // pinned apps also count as open while their process runs without a window (tray apps)
                bool running = hasWindow || (i.Pinned && ProcessRunning(i));
                if (running != i.Running) { i.Running = running; StartLoop(); }
            }
        }

        bool _rebuildQueued;
        void RebuildSoon()
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _rebuildQueued = false; Rebuild(); });
        }

        void UpdateTrash()
        {
            bool full = Win32.QueryRecycleBin().count > 0;
            if (full == _trashFull) return;
            _trashFull = full;
            var t = _items.FirstOrDefault(i => i.Kind == DockItemKind.Trash);
            if (t != null) LoadIcon(t);
        }

        // ================================================================== animation loop

        void StartLoop()
        {
            if (_looping || !IsLoaded) return;
            _looping = true;
            _lastRender = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
        }

        void StopLoop()
        {
            if (!_looping) return;
            _looping = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        static double Approach(double cur, double target, double rate, double dt)
        {
            double k = 1 - Math.Exp(-rate * dt);
            double v = cur + (target - cur) * k;
            return Math.Abs(v - target) < 0.002 ? target : v;
        }

        void OnRendering(object sender, EventArgs e)
        {
            var rt = ((RenderingEventArgs)e).RenderingTime;
            if (rt == _lastRender) return;
            double dt = _lastRender == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.05, (rt - _lastRender).TotalSeconds);
            _lastRender = rt;
            _clock += dt;
            bool reduce = App.Settings.ReduceMotion;

            // ---- pointer
            Win32.GetCursorPos(out var cp);
            var p = PointFromScreen(new Point(cp.X, cp.Y));
            double along = Horizontal ? p.X : p.Y;
            double across = Pos switch { DockPosition.Bottom => ActualHeight - p.Y, DockPosition.Left => p.X, _ => ActualWidth - p.X };
            var (shelfStart, shelfLen) = _lastShelf;
            double maxIcon = _items.Count > 0 ? _items.Max(i => i.Size) : B;
            _hovering = !_hiddenForFullscreen && along >= shelfStart - 2 && along <= shelfStart + shelfLen + 2 &&
                        across >= -2 && across <= EdgeMargin + _hideOffset + T + Math.Max(0, maxIcon - B) + 2;
            if (_hovering) { _mouseAlong = along; _lastHover = DateTime.Now; }

            bool magnify = App.Settings.Magnification && _hovering && !_menuOpen && _dragging == null && !reduce;
            _influence = reduce ? (magnify ? 1 : 0) : Approach(_influence, magnify ? 1 : 0, magnify ? 16 : 10, dt);

            // ---- auto-hide (always) or smart hide (only while a window covers the Dock)
            if (App.Settings.DockAutoHide || (App.Settings.DockSmartHide && _covered))
            {
                bool keep = _hovering || _menuOpen || _dragging != null || _items.Any(i => i.BounceStart >= 0);
                if (!keep && (DateTime.Now - _lastHover).TotalMilliseconds > 500) _hideTarget = HiddenOffset;
                else if (keep) _hideTarget = 0;
            }
            else _hideTarget = 0;
            if (_hiddenForFullscreen) _hideTarget = HiddenOffset;
            _hideOffset = reduce ? _hideTarget : Approach(_hideOffset, _hideTarget, 14, dt);

            bool busy = false;
            foreach (var i in _items)
            {
                double target = i == _dragging && !_dragOut ? 1 : i.PresenceTarget;
                if (i == _dragging && _dragOut) target = i.Pinned && Removable(i) ? 0.0001 : 1;
                i.Presence = reduce ? target : Approach(i.Presence, target, 12, dt);
                i.DotOpacity = Approach(i.DotOpacity, i.Running && App.Settings.ShowIndicators ? 1 : 0, 10, dt);
                if (Math.Abs(i.Presence - target) > 0.001 || (i.DotOpacity > 0 && i.DotOpacity < 1)) busy = true;
            }
            // purge fully removed items
            foreach (var dead in _items.Where(i => i.PresenceTarget == 0 && i.Presence <= 0.002).ToList())
            {
                _items.Remove(dead);
                if (dead.View != null) { _canvas.Children.Remove(dead.View); _canvas.Children.Remove(dead.Dot); }
                if (dead.SepLine != null) _canvas.Children.Remove(dead.SepLine);
            }

            bool moving = Layout(dt);
            UpdateLabel();
            UpdateDrag(p);

            bool settled = !busy && !moving && !_hovering && _influence == 0 && _hideOffset == _hideTarget &&
                           _dragging == null && !_items.Any(i => i.BounceStart >= 0);
            if (settled) StopLoop();
        }

        (double start, double len) _lastShelf = (0, 0);

        /// <summary>Computes magnified sizes and positions; returns true while positions are still easing.</summary>
        bool Layout(double dt)
        {
            double b = B, m = M, gap = Gap, pad = Pad;
            int n = _items.Count;
            if (n == 0) return false;

            var baseLen = new double[n];
            var slot = new double[n];
            double baseTotal = pad * 2 - gap;
            for (int i = 0; i < n; i++)
            {
                var it = _items[i];
                baseLen[i] = (it.IsIcon ? b : SepW) * it.Presence;
                slot[i] = baseLen[i] + gap * it.Presence;
                baseTotal += slot[i];
            }
            double baseStart = (WinAlong - baseTotal) / 2;
            _baseShelf = (baseStart, baseTotal);

            // magnified sizes (cosine falloff around the pointer, measured in the un-magnified layout => no feedback jitter)
            double xb = double.IsNaN(_mouseAlong) ? -1e9 : _mouseAlong - baseStart;
            double range = (b + gap) * 2.7;
            var mag = new double[n];
            var mslot = new double[n];
            double cursor = pad, magTotal = pad * 2 - gap;
            for (int i = 0; i < n; i++)
            {
                var it = _items[i];
                double center = cursor + baseLen[i] / 2;
                double size = baseLen[i];
                if (it.IsIcon && _influence > 0)
                {
                    double d = Math.Abs(xb - center);
                    double f = d < range ? (Math.Cos(Math.PI * d / range) + 1) / 2 : 0;
                    size = (b + (m - b) * f * _influence) * it.Presence;
                }
                mag[i] = size;
                mslot[i] = size + gap * it.Presence;
                magTotal += mslot[i];
                cursor += slot[i];
            }

            // anchor: the point under the pointer stays under the pointer while the Dock grows around it
            double start = baseStart;
            if (_influence > 0 && !double.IsNaN(_mouseAlong))
            {
                double mapped = MapToMagnified(xb, baseLen, slot, mag, mslot, pad);
                start = baseStart + (xb - mapped) * 1.0;
                start = Math.Clamp(start, 0, Math.Max(0, WinAlong - magTotal));
            }
            _lastShelf = (start, magTotal);

            // ---- shelf
            double off = _hideOffset;
            var shelfRect = Map(start, magTotal, EdgeMargin + off, T);
            PlaceRect(_shelfOuter, shelfRect);
            SyncBackdrop(shelfRect);
            bool glassAnimating = UpdateGlass(shelfRect, dt);
            UpdateSpecular(shelfRect, start, magTotal);

            // ---- items
            bool moving = false;
            double pos = start + pad;
            for (int i = 0; i < n; i++)
            {
                var it = _items[i];
                double target = pos;
                it.DisplayPos = double.IsNaN(it.DisplayPos) ? target : Approach(it.DisplayPos, target, _influence > 0 && _dragging == null ? 60 : 22, dt);
                if (Math.Abs(it.DisplayPos - target) > 0.3) moving = true;
                double at = it.DisplayPos;
                it.Size = mag[i];

                if (it.Kind == DockItemKind.Separator)
                {
                    double len = T * 0.62;
                    var r = Map(at + mag[i] / 2 - 0.5, 1, EdgeMargin + off + (T - len) / 2, len);
                    PlaceRect(it.SepLine, r);
                    it.SepLine.Opacity = it.Presence;
                }
                else
                {
                    double bounce = 0;
                    if (it.BounceStart >= 0)
                    {
                        double el = _clock - it.BounceStart;
                        const double period = 0.62;
                        double phase = el % period / period;
                        bounce = Math.Sin(Math.PI * phase) * b * 0.5;
                        bool done = el > 5.5 || (it.Running && el > period && phase < 0.08);
                        if (done) { it.BounceStart = -1; bounce = 0; }
                    }
                    double size = Math.Max(0, mag[i]);
                    var r = Map(at, size, EdgeMargin + off + pad + bounce, size);
                    PlaceRect(it.View, r);
                    it.View.Opacity = it == _dragging ? 0 : Math.Min(1, it.Presence * 1.4);
                    var dr = Map(at + size / 2 - 2, 4, EdgeMargin + off + Math.Max(1.5, pad * 0.25), 4);
                    PlaceRect(it.Dot, dr);
                    it.Dot.Opacity = it.DotOpacity * Math.Min(1, it.Presence);
                }
                pos += mslot[i];
            }
            return moving || glassAnimating;
        }

        /// <summary>
        /// Liquid glass: the shelf shows the (pre-blurred) wallpaper behind it, bent at the rim by the GPU shader.
        /// When an app window is behind the Dock the layer fades out and the live DWM blur takes over.
        /// Returns true while the cross-fade is still running.
        /// </summary>
        void ApplyLiveGlass()
        {
            if (LiveBackdrop.Enabled)
            {
                _live ??= new LiveBackdrop(this, () => !_hiddenForFullscreen && _glassFill.Opacity > 0.05 && _shelfPx.Height > 0 && _shelfPx.Top < Win32.GetSystemMetrics(Win32.SM_CYSCREEN) - 6, CaptureLive);
                _live.Start();
            }
            else
            {
                _live?.Stop();
                _liveImage = null;
                _glassBrush.ImageSource = WallpaperBackdrop.Image;
            }
        }

        void CaptureLive()
        {
            double s = Ui.Scale(this);
            double blur = ThemeManager.IsLiquid ? 5 + (1 - App.Settings.GlassClarity) * 22 : 26;
            var img = ScreenSnapshot.CaptureBlurred(_shelfPx.Left, _shelfPx.Top, _shelfPx.Width, _shelfPx.Height, s, blur, LiveBackdrop.Downscale);
            if (img == null) return;
            _liveImage = img;
            _glassBrush.ImageSource = img;
            _glassBrush.Viewbox = new Rect(0, 0, img.Width, img.Height);
        }

        bool UpdateGlass(Rect shelfRect, double dt)
        {
            // frosted wallpaper for every style; the lens shader is added on top when Liquid Glass is on
            bool live = _liveImage != null;
            bool on = App.Settings.RealBlur && (live || WallpaperBackdrop.Image != null && !_covered);
            _glassTarget = on ? 1 : 0;
            double o = App.Settings.ReduceMotion ? _glassTarget : Approach(_glassFill.Opacity, _glassTarget, 9, dt);
            _glassFill.Opacity = o;
            if (o <= 0.001 || shelfRect.Width < 1) return o != _glassTarget;
            var tl = PointToScreen(shelfRect.TopLeft);
            var br = PointToScreen(shelfRect.BottomRight);
            _shelfPx = new Win32.RECT { Left = (int)Math.Round(tl.X), Top = (int)Math.Round(tl.Y), Right = (int)Math.Round(br.X), Bottom = (int)Math.Round(br.Y) };
            if (!live)
            {
                double k = WallpaperBackdrop.Scale;
                _glassBrush.Viewbox = new Rect(tl.X * k, tl.Y * k, Math.Max(1, (br.X - tl.X) * k), Math.Max(1, (br.Y - tl.Y) * k));
            }
            if (_glassFx == null) return Math.Abs(o - _glassTarget) > 0.001;
            double s = Ui.Scale(this);
            double w = shelfRect.Width * s, h = shelfRect.Height * s;
            _glassFx.SizeRadius = new System.Windows.Media.Media3D.Point4D(w, h, ShelfRadius * s, Math.Min(h * 0.45, 24 * s));
            _glassFx.Params = new System.Windows.Media.Media3D.Point4D(
                (14 + 20 * App.Settings.GlassClarity) * s,            // glass thickness: clearer glass bends more
                3.0,                                                  // index of refraction
                ThemeManager.IsDark ? 0.4 : 0.55,                     // specular rim
                ThemeManager.IsDark ? 0 : 0.05);                      // tint towards white
            _glassFx.LightDir = new System.Windows.Media.Media3D.Point4D(0.5, -0.7, 1.5 * s, 0.15);
            return Math.Abs(o - _glassTarget) > 0.001;
        }

        Rect _lastBackdrop = Rect.Empty;

        /// <summary>Keeps the blur window exactly under the shelf and directly below the Dock in the z-order.</summary>
        void SyncBackdrop(Rect shelf)
        {
            // The DWM blur always fills the window's full rectangle, which showed as square corners around the
            // rounded shelf. The shelf now draws its own frosted wallpaper instead, so this stays hidden.
            const bool useDwmBlur = false;
            if (!useDwmBlur || !App.Settings.RealBlur || _hiddenForFullscreen || shelf.Width < 1) { _backdrop.Hide(); return; }
            if (_backdrop.IsVisible && Math.Abs(shelf.X - _lastBackdrop.X) < 0.5 && Math.Abs(shelf.Y - _lastBackdrop.Y) < 0.5 &&
                Math.Abs(shelf.Width - _lastBackdrop.Width) < 0.5 && Math.Abs(shelf.Height - _lastBackdrop.Height) < 0.5) return;
            _lastBackdrop = shelf;
            var tl = PointToScreen(shelf.TopLeft);
            var br = PointToScreen(shelf.BottomRight);
            int x = (int)Math.Round(tl.X), y = (int)Math.Round(tl.Y);
            _backdrop.Place(Ui.Hwnd(this), x, y, (int)Math.Round(br.X) - x, (int)Math.Round(br.Y) - y, ShelfRadius * Ui.Scale(this), true);
        }

        void UpdateSpecularBrush()
        {
            byte a = ThemeManager.IsDark ? (byte)0x55 : (byte)0xA0;
            var g = new RadialGradientBrush(Color.FromArgb(a, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
            g.GradientStops.Insert(1, new GradientStop(Color.FromArgb((byte)(a / 3), 255, 255, 255), 0.45));
            g.Freeze();
            _specular.Fill = g;
        }

        /// <summary>Moves the liquid-glass glint to the pointer and keeps the glass layers clipped to the shelf shape.</summary>
        void UpdateSpecular(Rect shelfRect, double start, double len)
        {
            if (!Liquid) { _specular.Opacity = 0; return; }
            double bt = _shelfOuter.BorderThickness;
            var inner = new Size(Math.Max(0, shelfRect.Width - 2 * bt), Math.Max(0, shelfRect.Height - 2 * bt));
            if (Math.Abs(inner.Width - _shelfInnerSize.Width) > 0.5 || Math.Abs(inner.Height - _shelfInnerSize.Height) > 0.5 || _shelfInnerSize.IsEmpty)
            {
                _shelfInnerSize = inner;
                double r = Math.Max(0, ShelfRadius - bt);
                _shelfInner.Clip = new RectangleGeometry(new Rect(inner), r, r);
            }
            double glintAlong = B * 2.6, glintAcross = T * 1.3;
            _specular.Width = Horizontal ? glintAlong : glintAcross;
            _specular.Height = Horizontal ? glintAcross : glintAlong;
            double along = (double.IsNaN(_mouseAlong) ? start + len / 2 : _mouseAlong) - start - glintAlong / 2;
            double across = -glintAcross * 0.55; // light comes from the outer (screen-facing) edge of the glass
            switch (Pos)
            {
                case DockPosition.Bottom: _specularShift.X = along; _specularShift.Y = across; break;
                case DockPosition.Left: _specularShift.X = inner.Width - glintAcross - across; _specularShift.Y = along; break;
                default: _specularShift.X = across; _specularShift.Y = along; break;
            }
            _specular.Opacity = _influence;
        }

        static double MapToMagnified(double x, double[] baseLen, double[] slot, double[] mag, double[] mslot, double pad)
        {
            double bpos = pad, mpos = pad;
            if (x < bpos) return x;
            for (int i = 0; i < baseLen.Length; i++)
            {
                if (x < bpos + slot[i])
                {
                    double within = x - bpos;
                    return within <= baseLen[i] || slot[i] <= baseLen[i]
                        ? mpos + (baseLen[i] > 0 ? within / baseLen[i] * mag[i] : 0)
                        : mpos + mag[i] + (within - baseLen[i]);
                }
                bpos += slot[i];
                mpos += mslot[i];
            }
            return mpos + (x - bpos);
        }

        /// <summary>(along, across-from-edge) → window rectangle for the current Dock position.</summary>
        Rect Map(double alongStart, double alongLen, double acrossStart, double acrossLen) => Pos switch
        {
            DockPosition.Bottom => new Rect(alongStart, ActualHeight - acrossStart - acrossLen, Math.Max(0, alongLen), Math.Max(0, acrossLen)),
            DockPosition.Left => new Rect(acrossStart, alongStart, Math.Max(0, acrossLen), Math.Max(0, alongLen)),
            _ => new Rect(ActualWidth - acrossStart - acrossLen, alongStart, Math.Max(0, acrossLen), Math.Max(0, alongLen)),
        };

        static void PlaceRect(FrameworkElement el, Rect r)
        {
            Canvas.SetLeft(el, r.X);
            Canvas.SetTop(el, r.Y);
            el.Width = r.Width;
            el.Height = r.Height;
        }

        DockItem HoveredItem()
        {
            if (!_hovering || double.IsNaN(_mouseAlong)) return null;
            foreach (var i in _items)
                if (i.IsIcon && i.PresenceTarget > 0 && _mouseAlong >= i.DisplayPos - Gap / 2 && _mouseAlong <= i.DisplayPos + i.Size + Gap / 2) return i;
            return null;
        }

        DockItem _labelFor;
        void UpdateLabel()
        {
            var item = _dragging == null && !_menuOpen ? HoveredItem() : null;
            if (_dragging != null && _dragOut && _dragging.Pinned && Removable(_dragging)) item = null;
            if (item != null)
            {
                if (_labelFor != item)
                {
                    _labelFor = item;
                    _labelText.Text = item.Kind == DockItemKind.Role ? item.Label : item.Label;
                }
                _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var sz = _label.DesiredSize;
                double top = EdgeMargin + _hideOffset + Pad + item.Size + 12;
                double center = item.DisplayPos + item.Size / 2;
                var r = Horizontal
                    ? Map(center - sz.Width / 2, sz.Width, top, sz.Height)
                    : Map(center - sz.Height / 2, sz.Height, top, sz.Width);
                Canvas.SetLeft(_label, Math.Clamp(r.X, 0, Math.Max(0, ActualWidth - sz.Width)));
                Canvas.SetTop(_label, r.Y);
                _label.Opacity = 1;
            }
            else
            {
                _labelFor = null;
                _label.Opacity = 0;
            }
        }

        void IdleTick()
        {
            var s = App.Settings;
            if (App.FullscreenAppActive || (!s.DockAutoHide && !s.DockSmartHide && !s.LiquidGlass)) return;
            if (s.DockSmartHide || s.LiquidGlass)
            {
                bool covered = ComputeCovered();
                if (covered != _covered)
                {
                    _covered = covered;
                    if (covered) _lastHover = DateTime.MinValue; // slide away right away
                    StartLoop();
                }
            }
            // reveal: pointer pushed against the Dock's screen edge while it is hidden
            if (!(s.DockAutoHide || _covered) || _looping) return;
            if (!Win32.GetCursorPos(out var cp)) return;
            int sw = Win32.GetSystemMetrics(Win32.SM_CXSCREEN), sh = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
            bool atEdge = Pos switch { DockPosition.Bottom => cp.Y >= sh - 2, DockPosition.Left => cp.X <= 1, _ => cp.X >= sw - 2 };
            if (atEdge && (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) == 0)
            {
                _hideTarget = 0;
                _lastHover = DateTime.Now;
                StartLoop();
            }
        }

        /// <summary>True when the front window is maximised or its visible frame overlaps the Dock's normal position.</summary>
        bool ComputeCovered()
        {
            try
            {
                var fg = Win32.GetForegroundWindow();
                if (fg == IntPtr.Zero) return _covered;
                Win32.GetWindowThreadProcessId(fg, out uint pid);
                if (pid == (uint)Environment.ProcessId) return _covered; // our own panels/menus: keep state
                if (!Win32.IsWindowVisible(fg) || Win32.IsIconic(fg) || Win32.IsCloaked(fg)) return false;
                if (Win32.GetClass(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
                // a window maximised / full screen on another monitor can't cover the Dock
                if (!Win32.IsOnPrimaryMonitor(fg)) return false;
                if (Win32.IsZoomed(fg)) return true;
                if (_baseShelf.len <= 0 || !Win32.GetVisibleRect(fg, out var w)) return false;
                var r = Map(_baseShelf.start, _baseShelf.len, EdgeMargin, T);
                var tl = PointToScreen(r.TopLeft);
                var br = PointToScreen(r.BottomRight);
                return w.Left < br.X && w.Right > tl.X && w.Top < br.Y && w.Bottom > tl.Y;
            }
            catch { return _covered; }
        }

        public void SetFullscreen(bool full)
        {
            _hiddenForFullscreen = full;
            Visibility = full ? Visibility.Hidden : Visibility.Visible;
            if (full) _backdrop.Hide();
            if (!full) StartLoop();
        }

        // apps launched through MacWin that have no window yet: shown in the Dock right away, bouncing
        readonly Dictionary<string, DateTime> _launching = new(StringComparer.OrdinalIgnoreCase);
        static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Called whenever an app is launched (Dock, Launchpad, Spotlight). Like macOS, an app that isn't in the Dock
        /// appears immediately and bounces until its first window shows up.
        /// </summary>
        public void BounceApp(string appId)
        {
            if (appId == null || appId.StartsWith("http", StringComparison.OrdinalIgnoreCase) || appId.StartsWith("ms-", StringComparison.OrdinalIgnoreCase)) return;
            var item = _items.FirstOrDefault(i => string.Equals(i.AppId, appId, StringComparison.OrdinalIgnoreCase) && i.PresenceTarget > 0);
            if (item != null && WindowTracker.WindowsOf(appId).Count > 0) return; // already open
            _launching[appId] = DateTime.Now;
            if (item == null)
            {
                Rebuild();
                item = _items.FirstOrDefault(i => string.Equals(i.AppId, appId, StringComparison.OrdinalIgnoreCase) && i.PresenceTarget > 0);
            }
            if (item != null && App.Settings.BounceOnLaunch) item.BounceStart = _clock;
            // give up on apps that never open a window
            var t = new DispatcherTimer { Interval = LaunchTimeout + TimeSpan.FromMilliseconds(200) };
            t.Tick += (_, _) => { t.Stop(); ExpireLaunches(); };
            t.Start();
            StartLoop();
        }

        void ExpireLaunches()
        {
            var expired = _launching.Where(kv => DateTime.Now - kv.Value >= LaunchTimeout).Select(kv => kv.Key).ToList();
            if (expired.Count == 0) return;
            foreach (var id in expired) _launching.Remove(id);
            Rebuild();
        }

        // ---- running processes (for pinned apps that live in the tray without a window)
        HashSet<string> _procNames = new(StringComparer.OrdinalIgnoreCase);
        DispatcherTimer _procTimer;
        bool _procScanning;

        void StartProcessWatch()
        {
            if (_procTimer != null) return;
            _procTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _procTimer.Tick += async (_, _) =>
            {
                if (_procScanning) return;
                _procScanning = true;
                try
                {
                    // process names only: no per-process handles are opened, so this costs next to nothing
                    var names = await System.Threading.Tasks.Task.Run(() =>
                    {
                        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in System.Diagnostics.Process.GetProcesses())
                            using (p) { try { set.Add(p.ProcessName); } catch { } }
                        return set;
                    });
                    if (!names.SetEquals(_procNames)) { _procNames = names; SyncRunning(); }
                }
                catch { }
                finally { _procScanning = false; }
            };
            _procTimer.Start();
        }

        bool ProcessRunning(DockItem i)
        {
            if (i.AppId == null) return false;
            string exe = AppCatalog.ById(i.AppId)?.ExePath ?? (File.Exists(i.AppId) ? i.AppId : null);
            if (exe == null) return false;
            return _procNames.Contains(System.IO.Path.GetFileNameWithoutExtension(exe));
        }

        // ================================================================== interaction

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            StartLoop();
            if (_pressed == null || e.LeftButton != MouseButtonState.Pressed || _dragging != null) return;
            var p = e.GetPosition(this);
            if ((p - _pressPoint).Length > 6 && CanDrag(_pressed))
            {
                _dragging = _pressed;
                _pressed.Dim.Opacity = 0;
                _ghost.Source = _pressed.Img.Source;
                _ghost.Opacity = 0.9;
            }
        }

        static bool CanDrag(DockItem i) => i.Kind is DockItemKind.App or DockItemKind.Role or DockItemKind.Launchpad;
        static bool Removable(DockItem i) => i.Pinned && !(i.Kind == DockItemKind.Role && i.Key == "role:files");

        void UpdateDrag(Point p)
        {
            if (_dragging == null) return;
            double gs = M;
            Canvas.SetLeft(_ghost, p.X - gs / 2);
            Canvas.SetTop(_ghost, p.Y - gs / 2);
            double across = Pos switch { DockPosition.Bottom => ActualHeight - p.Y, DockPosition.Left => p.X, _ => ActualWidth - p.X };
            _dragOut = across > EdgeMargin + T + B * 1.1;
            _ghost.Opacity = _dragOut && Removable(_dragging) ? 0.55 : 0.9;
            if (_dragOut && Removable(_dragging))
            {
                _labelText.Text = "Remove";
                _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(_label, p.X - _label.DesiredSize.Width / 2);
                Canvas.SetTop(_label, p.Y - gs / 2 - _label.DesiredSize.Height - 6);
                _label.Opacity = 1;
            }

            // live reordering inside the pinned section
            if (!_dragOut && _dragging.Pinned)
            {
                double along = Horizontal ? p.X : p.Y;
                var pinned = _items.Where(i => i.Pinned && i.PresenceTarget > 0).ToList();
                int target = pinned.Count - 1;
                for (int k = 0; k < pinned.Count; k++)
                {
                    var it = pinned[k];
                    if (along < it.DisplayPos + it.Size / 2) { target = k; break; }
                }
                int cur = pinned.IndexOf(_dragging);
                if (target != cur && target >= 0)
                {
                    var anchor = pinned[target];
                    _items.Remove(_dragging);
                    int idx = _items.IndexOf(anchor);
                    _items.Insert(target > cur ? idx + 1 : idx, _dragging);
                }
            }
        }

        void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            var pressed = _pressed;
            _pressed = null;
            if (pressed == null) return;
            pressed.View.ReleaseMouseCapture();
            pressed.Dim.Opacity = 0;
            if (_dragging != null)
            {
                var d = _dragging;
                _dragging = null;
                _ghost.Opacity = 0;
                var s = App.Settings;
                if (_dragOut && Removable(d))
                {
                    s.PinnedApps.Remove(d.Key);
                    d.PresenceTarget = 0;
                    _dragOut = false;
                    s.Save(false);
                    Rebuild();
                }
                else
                {
                    _dragOut = false;
                    if (!d.Pinned && d.Kind == DockItemKind.App)
                    {
                        // dropping a running app into the pinned area keeps it in the Dock
                        var along = Horizontal ? e.GetPosition(this).X : e.GetPosition(this).Y;
                        var sep = _items.FirstOrDefault(i => i.Key == "sep:a");
                        if (sep != null && along < sep.DisplayPos) Pin(d.Key, along);
                    }
                    else if (d.Pinned)
                    {
                        s.PinnedApps = _items.Where(i => i.Pinned && i.PresenceTarget > 0).Select(i => i.Key).ToList();
                        s.Save(false);
                    }
                }
                StartLoop();
                e.Handled = true;
                return;
            }
            var pos = e.GetPosition(pressed.View);
            if (pos.X >= 0 && pos.Y >= 0 && pos.X <= pressed.View.ActualWidth && pos.Y <= pressed.View.ActualHeight)
                Activate(pressed);
            e.Handled = true;
        }

        void Pin(string key, double along)
        {
            var s = App.Settings;
            s.PinnedApps ??= new List<string>();
            if (s.PinnedApps.Contains(key)) return;
            int index = s.PinnedApps.Count;
            var pinned = _items.Where(i => i.Pinned && i.PresenceTarget > 0).ToList();
            for (int k = 0; k < pinned.Count; k++)
                if (along < pinned[k].DisplayPos + pinned[k].Size / 2) { index = s.PinnedApps.IndexOf(pinned[k].Key); break; }
            s.PinnedApps.Insert(Math.Max(0, index), key);
            s.Save(false);
            Rebuild();
        }

        void Activate(DockItem item)
        {
            switch (item.Kind)
            {
                case DockItemKind.Launchpad: App.ToggleLaunchpad(); return;
                case DockItemKind.Trash: Shell.OpenTrash(); return;
                case DockItemKind.Downloads: DownloadsStack.Toggle(this, item.View); return;
            }
            var wins = WindowTracker.WindowsOf(item.AppId);
            if (wins.Count == 0)
            {
                if (item.AppId == null) return;
                AppCatalog.Launch(item.AppId);
                BounceApp(item.AppId);
                return;
            }
            bool isFront = string.Equals(WindowTracker.ForegroundAppId, item.AppId, StringComparison.OrdinalIgnoreCase)
                           && Win32.GetForegroundWindow() == WindowTracker.LastExternalForeground && !Win32.IsIconic(WindowTracker.LastExternalForeground);
            if (isFront)
            {
                if (wins.Count > 1)
                {
                    var next = wins.LastOrDefault(w => w.Hwnd != WindowTracker.LastExternalForeground) ?? wins[0];
                    WindowTracker.Activate(next);
                }
                else if (App.Settings.ClickToMinimize) WindowTracker.Minimize(wins[0].Hwnd);
                return;
            }
            if (wins.All(w => w.Minimized))
                foreach (var w in wins.Skip(1)) Win32.ShowWindowAsync(w.Hwnd, Win32.SW_SHOWNOACTIVATE);
            WindowTracker.Activate(wins[0]);
        }

        // ================================================================== menus

        void OpenMenu(ContextMenu cm, FrameworkElement anchor)
        {
            cm.PlacementTarget = anchor;
            cm.Placement = PlacementMode.Custom;
            cm.CustomPopupPlacementCallback = (popup, target, offset) =>
            {
                double gapPx = 10;
                return Pos switch
                {
                    DockPosition.Bottom => new[] { new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, -popup.Height - gapPx), PopupPrimaryAxis.Horizontal) },
                    DockPosition.Left => new[] { new CustomPopupPlacement(new Point(target.Width + gapPx, (target.Height - popup.Height) / 2), PopupPrimaryAxis.Vertical) },
                    _ => new[] { new CustomPopupPlacement(new Point(-popup.Width - gapPx, (target.Height - popup.Height) / 2), PopupPrimaryAxis.Vertical) },
                };
            };
            _menuOpen = true;
            cm.Closed += (_, _) => { _menuOpen = false; StartLoop(); };
            cm.IsOpen = true;
            StartLoop();
        }

        void ShowItemMenu(DockItem item)
        {
            var s = App.Settings;
            var cm = new ContextMenu();
            switch (item.Kind)
            {
                case DockItemKind.Trash:
                    cm.Items.Add(Menus.Item("Open", Shell.OpenTrash));
                    cm.Items.Add(Menus.Sep());
                    cm.Items.Add(Menus.Item("Empty Trash", () =>
                    {
                        if (MacDialog.Confirm("Are you sure you want to permanently erase the items in the Trash?", "Empty Trash", "You can't undo this action."))
                        { Shell.EmptyTrash(); UpdateTrash(); }
                    }, enabled: _trashFull));
                    OpenMenu(cm, item.View);
                    return;
                case DockItemKind.Downloads:
                    cm.Items.Add(Menus.Item("Open \"Downloads\"", () => Shell.Open(DownloadsStack.Folder)));
                    OpenMenu(cm, item.View);
                    return;
                case DockItemKind.Launchpad:
                    cm.Items.Add(Menus.Item("Open Launchpad", App.ToggleLaunchpad));
                    cm.Items.Add(Menus.Sep());
                    cm.Items.Add(Menus.Item("Remove from Dock", () => Unpin(item)));
                    OpenMenu(cm, item.View);
                    return;
            }

            var wins = WindowTracker.WindowsOf(item.AppId);
            if (wins.Count > 0)
            {
                foreach (var w in wins.Take(10))
                {
                    var win = w;
                    cm.Items.Add(Menus.Item(string.IsNullOrWhiteSpace(w.Title) ? item.Label : (w.Title.Length > 48 ? w.Title[..45] + "…" : w.Title),
                        () => WindowTracker.Activate(win), isChecked: w.Hwnd == WindowTracker.LastExternalForeground));
                }
                cm.Items.Add(Menus.Sep());
            }

            var options = Menus.Sub("Options");
            if (item.Kind == DockItemKind.Role)
            {
                string role = item.Key[5..];
                var b = Roles.Binding(role);
                var choose = Menus.Sub($"Open \"{b.Label}\" with");
                foreach (var c in Roles.Candidates(role))
                {
                    var entry = c;
                    choose.Items.Add(Menus.Item(c.Name, () => { b.AppId = entry.Id; s.Save(false); IconProvider.Invalidate(); Rebuild(); App.RefreshRoleUi(); }, isChecked: b.AppId == c.Id));
                }
                var url = Roles.Def(role)?.FallbackUrl;
                if (url != null && url.StartsWith("http"))
                    choose.Items.Add(Menus.Item("Website (" + new Uri(url).Host + ")", () => { b.AppId = url; s.Save(false); IconProvider.Invalidate(); Rebuild(); }, isChecked: b.AppId == url));
                choose.Items.Add(Menus.Sep());
                choose.Items.Add(Menus.Item("Other App…", () => App.ShowSettings("Apps")));
                options.Items.Add(choose);
                options.Items.Add(Menus.Item("Use Mac-style Icon", () => { b.MacIcon = !b.MacIcon; s.Save(false); IconProvider.Invalidate(); ReloadIcons(); }, isChecked: b.MacIcon));
                options.Items.Add(Menus.Item("Change Icon…", () => App.ChooseIcon(role)));
                options.Items.Add(Menus.Sep());
            }
            if (item.Pinned)
            {
                if (Removable(item)) options.Items.Add(Menus.Item("Remove from Dock", () => Unpin(item)));
            }
            else options.Items.Add(Menus.Item("Keep in Dock", () => Pin(item.Key, double.MaxValue)));
            options.Items.Add(Menus.Item("Open at Login", () => ToggleOpenAtLogin(item), isChecked: OpenAtLogin(item)));
            var exe = AppCatalog.ById(item.AppId)?.ExePath ?? (File.Exists(item.AppId ?? "") ? item.AppId : null);
            if (exe != null) options.Items.Add(Menus.Item("Show in File Explorer", () => Shell.Reveal(exe)));
            cm.Items.Add(options);
            cm.Items.Add(Menus.Sep());

            if (wins.Count > 0)
            {
                cm.Items.Add(Menus.Item("Show All Windows", Shell.MissionControl));
                cm.Items.Add(Menus.Item("Hide", () => WindowTracker.HideApp(item.AppId)));
                cm.Items.Add(Menus.Item("Quit", () => WindowTracker.QuitApp(item.AppId)));
                cm.Items.Add(Menus.Item("Force Quit", () => { foreach (var p in wins.Select(w => w.Pid).Distinct()) WindowTracker.ForceQuit(p); }));
            }
            else
            {
                cm.Items.Add(Menus.Item("Open", () => Activate(item)));
                if (item.Kind == DockItemKind.Role) cm.Items.Add(Menus.Item("Settings for This Slot…", () => App.ShowSettings("Apps")));
            }
            OpenMenu(cm, item.View);
        }

        void Unpin(DockItem item)
        {
            App.Settings.PinnedApps.Remove(item.Key);
            App.Settings.Save(false);
            Rebuild();
        }

        static string StartupName(DockItem i) => "MacWin_" + (i.Label ?? "app").Replace(' ', '_');

        static bool OpenAtLogin(DockItem i)
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return k?.GetValue(StartupName(i)) != null;
        }

        static void ToggleOpenAtLogin(DockItem i)
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (k.GetValue(StartupName(i)) != null) { k.DeleteValue(StartupName(i), false); return; }
            var exe = AppCatalog.ById(i.AppId)?.ExePath;
            k.SetValue(StartupName(i), exe != null ? $"\"{exe}\"" : $"explorer.exe shell:AppsFolder\\{i.AppId}");
        }

        void ShowDockMenu()
        {
            var s = App.Settings;
            var cm = Menus.Build(
                Menus.Item(s.DockAutoHide ? "Turn Hiding Off" : "Turn Hiding On", () => { s.DockAutoHide = !s.DockAutoHide; s.Save(); }),
                Menus.Item(s.Magnification ? "Turn Magnification Off" : "Turn Magnification On", () => { s.Magnification = !s.Magnification; s.Save(); }),
                Menus.Sep(),
                Menus.Sub("Position on Screen",
                    Menus.Item("Left", () => { s.DockPosition = DockPosition.Left; s.Save(); }, isChecked: Pos == DockPosition.Left),
                    Menus.Item("Bottom", () => { s.DockPosition = DockPosition.Bottom; s.Save(); }, isChecked: Pos == DockPosition.Bottom),
                    Menus.Item("Right", () => { s.DockPosition = DockPosition.Right; s.Save(); }, isChecked: Pos == DockPosition.Right)),
                Menus.Sep(),
                Menus.Item("Add App to Dock…", () => App.ShowSettings("Dock")),
                Menus.Item("Dock Settings…", () => App.ShowSettings("Dock")));
            OpenMenu(cm, _shelfOuter);
        }

        // ================================================================== external drag & drop

        DockItem ItemAt(Point p)
        {
            double along = Horizontal ? p.X : p.Y;
            return _items.FirstOrDefault(i => i.IsIcon && i.PresenceTarget > 0 && along >= i.DisplayPos && along <= i.DisplayPos + i.Size);
        }

        void SetDropTarget(DockItem i)
        {
            if (_dropTarget == i) return;
            if (_dropTarget != null) _dropTarget.Dim.Opacity = 0;
            _dropTarget = i;
            if (i != null) i.Dim.Opacity = 0.3;
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            StartLoop();
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            var item = ItemAt(e.GetPosition(this));
            bool canOpen = item != null && (item.Kind is DockItemKind.Trash or DockItemKind.App or DockItemKind.Role);
            SetDropTarget(canOpen ? item : null);
            e.Effects = item?.Kind == DockItemKind.Trash ? DragDropEffects.Move : DragDropEffects.Link;
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            var target = _dropTarget;
            SetDropTarget(null);
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            if (target?.Kind == DockItemKind.Trash)
            {
                Win32.MoveToRecycleBin(files);
                UpdateTrash();
                return;
            }
            if (target != null && target.AppId != null && (target.Kind is DockItemKind.App or DockItemKind.Role))
            {
                AppCatalog.Launch(target.AppId, string.Join(" ", files.Select(f => $"\"{f}\"")));
                return;
            }
            // drop on empty Dock space: keep the apps / folders in the Dock
            double along = Horizontal ? e.GetPosition(this).X : e.GetPosition(this).Y;
            foreach (var f in files)
            {
                string id = ResolveDropped(f);
                Pin("app:" + id, along);
            }
        }

        static string ResolveDropped(string path)
        {
            try
            {
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var t = Type.GetTypeFromProgID("WScript.Shell");
                    dynamic sh = Activator.CreateInstance(t);
                    string target = sh.CreateShortcut(path).TargetPath;
                    if (!string.IsNullOrEmpty(target) && File.Exists(target)) path = target;
                }
                return AppCatalog.ByExe(System.IO.Path.GetFullPath(path))?.Id ?? path;
            }
            catch { return path; }
        }
    }
}
