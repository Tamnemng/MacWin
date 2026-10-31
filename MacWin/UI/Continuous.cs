using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MacWin.Native;
using MacWin.Services;

namespace MacWin.UI
{
    /// <summary>
    /// "Continuous" rounded rectangles: the curvature eases in over ~1.5× the radius instead of
    /// switching abruptly from a straight edge to a circular arc, which is what makes modern
    /// desktop corners look soft. Built from three cubic Béziers per corner.
    /// </summary>
    public static class ContinuousCorners
    {
        const double K0 = 1.52866, K1 = 1.08849, K2 = 0.86840, K3 = 0.63149, K4 = 0.07491, K5 = 0.37282, K6 = 0.16897;

        public static Geometry Create(Rect rc, double radius)
        {
            double w = rc.Width, h = rc.Height, x = rc.X, y = rc.Y;
            if (w <= 0 || h <= 0) return Geometry.Empty;
            double half = Math.Min(w, h) / 2;
            if (radius >= half - 0.5)
            {
                var pill = new RectangleGeometry(rc, half, half); // capsule: fully round ends
                pill.Freeze();
                return pill;
            }
            double r = Math.Min(radius, half / K0);
            if (r <= 0.01) { var g0 = new RectangleGeometry(rc); g0.Freeze(); return g0; }

            static Point P(double a, double b) => new(a, b);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(P(x + K0 * r, y), true, true);
                c.LineTo(P(x + w - K0 * r, y), true, true);
                c.BezierTo(P(x + w - K1 * r, y), P(x + w - K2 * r, y), P(x + w - K3 * r, y + K4 * r), true, true);
                c.BezierTo(P(x + w - K5 * r, y + K6 * r), P(x + w - K6 * r, y + K5 * r), P(x + w - K4 * r, y + K3 * r), true, true);
                c.BezierTo(P(x + w, y + K2 * r), P(x + w, y + K1 * r), P(x + w, y + K0 * r), true, true);
                c.LineTo(P(x + w, y + h - K0 * r), true, true);
                c.BezierTo(P(x + w, y + h - K1 * r), P(x + w, y + h - K2 * r), P(x + w - K4 * r, y + h - K3 * r), true, true);
                c.BezierTo(P(x + w - K6 * r, y + h - K5 * r), P(x + w - K5 * r, y + h - K6 * r), P(x + w - K3 * r, y + h - K4 * r), true, true);
                c.BezierTo(P(x + w - K2 * r, y + h), P(x + w - K1 * r, y + h), P(x + w - K0 * r, y + h), true, true);
                c.LineTo(P(x + K0 * r, y + h), true, true);
                c.BezierTo(P(x + K1 * r, y + h), P(x + K2 * r, y + h), P(x + K3 * r, y + h - K4 * r), true, true);
                c.BezierTo(P(x + K5 * r, y + h - K6 * r), P(x + K6 * r, y + h - K5 * r), P(x + K4 * r, y + h - K3 * r), true, true);
                c.BezierTo(P(x, y + h - K2 * r), P(x, y + h - K1 * r), P(x, y + h - K0 * r), true, true);
                c.LineTo(P(x, y + K0 * r), true, true);
                c.BezierTo(P(x, y + K1 * r), P(x, y + K2 * r), P(x + K4 * r, y + K3 * r), true, true);
                c.BezierTo(P(x + K6 * r, y + K5 * r), P(x + K5 * r, y + K6 * r), P(x + K3 * r, y + K4 * r), true, true);
                c.BezierTo(P(x + K2 * r, y), P(x + K1 * r, y), P(x + K0 * r, y), true, true);
            }
            g.Freeze();
            return g;
        }

        [DllImport("gdi32.dll")] static extern IntPtr CreatePolygonRgn(Win32.POINT[] pts, int count, int fillMode);

        /// <summary>Window region (physical pixels) following the same continuous shape, inset so the
        /// region's hard edge hides under the anti-aliased rim drawn by WPF on top.</summary>
        public static IntPtr CreateRegion(int widthPx, int heightPx, double radiusPx, double insetPx)
        {
            var geo = Create(new Rect(insetPx, insetPx, Math.Max(1, widthPx - 2 * insetPx), Math.Max(1, heightPx - 2 * insetPx)), Math.Max(0, radiusPx - insetPx));
            var flat = geo.GetFlattenedPathGeometry(0.35, ToleranceType.Absolute);
            var pts = new List<Win32.POINT>();
            foreach (var fig in flat.Figures)
            {
                pts.Add(new Win32.POINT { X = (int)Math.Round(fig.StartPoint.X), Y = (int)Math.Round(fig.StartPoint.Y) });
                foreach (var seg in fig.Segments)
                {
                    IEnumerable<Point> list = seg switch
                    {
                        System.Windows.Media.PolyLineSegment pl => pl.Points,
                        System.Windows.Media.LineSegment l => new[] { l.Point },
                        _ => Array.Empty<Point>()
                    };
                    foreach (var p in list) pts.Add(new Win32.POINT { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) });
                }
            }
            return pts.Count >= 3 ? CreatePolygonRgn(pts.ToArray(), pts.Count, 2 /*WINDING*/) : Win32.CreateRoundRectRgn(0, 0, widthPx, heightPx, 0, 0);
        }
    }

    /// <summary>
    /// A border with continuous corners, anti-aliased edges, an optional top sheen (under the content)
    /// and a rim stroke drawn above the content so it always reads as the glass edge.
    /// </summary>
    public class ContinuousBorder : FrameworkElement
    {
        readonly DrawingVisual _overlay = new();
        UIElement _child;

        public ContinuousBorder() { AddVisualChild(_overlay); }

        public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(nameof(Background), typeof(Brush), typeof(ContinuousBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        /// <summary>Drawn first, under the tint: the blurred snapshot of what's behind the panel.</summary>
        public static readonly DependencyProperty BackdropProperty = DependencyProperty.Register(nameof(Backdrop), typeof(Brush), typeof(ContinuousBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public Brush Backdrop { get => (Brush)GetValue(BackdropProperty); set => SetValue(BackdropProperty, value); }
        public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(nameof(BorderBrush), typeof(Brush), typeof(ContinuousBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty OuterBrushProperty = DependencyProperty.Register(nameof(OuterBrush), typeof(Brush), typeof(ContinuousBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty SheenProperty = DependencyProperty.Register(nameof(Sheen), typeof(Brush), typeof(ContinuousBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(nameof(BorderThickness), typeof(double), typeof(ContinuousBorder), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(ContinuousBorder), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));
        public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(nameof(Padding), typeof(Thickness), typeof(ContinuousBorder), new FrameworkPropertyMetadata(new Thickness(), FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty ClipContentProperty = DependencyProperty.Register(nameof(ClipContent), typeof(bool), typeof(ContinuousBorder), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

        public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
        public Brush BorderBrush { get => (Brush)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
        public Brush OuterBrush { get => (Brush)GetValue(OuterBrushProperty); set => SetValue(OuterBrushProperty, value); }
        public Brush Sheen { get => (Brush)GetValue(SheenProperty); set => SetValue(SheenProperty, value); }
        public double BorderThickness { get => (double)GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
        public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
        public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
        public bool ClipContent { get => (bool)GetValue(ClipContentProperty); set => SetValue(ClipContentProperty, value); }

        public UIElement Child
        {
            get => _child;
            set
            {
                if (_child == value) return;
                if (_child != null) { RemoveVisualChild(_child); RemoveLogicalChild(_child); }
                _child = value;
                if (_child != null) { AddLogicalChild(_child); AddVisualChild(_child); }
                InvalidateMeasure();
            }
        }

        protected override IEnumerator LogicalChildren => (_child == null ? Array.Empty<object>() : new object[] { _child }).GetEnumerator();
        protected override int VisualChildrenCount => _child == null ? 1 : 2;
        protected override Visual GetVisualChild(int index) => _child == null ? _overlay : index == 0 ? _child : _overlay;

        protected override Size MeasureOverride(Size available)
        {
            var p = Padding;
            double bt = BorderThickness;
            double extraW = p.Left + p.Right + 2 * bt, extraH = p.Top + p.Bottom + 2 * bt;
            if (_child == null) return new Size(extraW, extraH);
            _child.Measure(new Size(Math.Max(0, available.Width - extraW), Math.Max(0, available.Height - extraH)));
            return new Size(_child.DesiredSize.Width + extraW, _child.DesiredSize.Height + extraH);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var p = Padding;
            double bt = BorderThickness;
            if (_child != null)
            {
                var rc = new Rect(bt + p.Left, bt + p.Top, Math.Max(0, final.Width - 2 * bt - p.Left - p.Right), Math.Max(0, final.Height - 2 * bt - p.Top - p.Bottom));
                _child.Arrange(rc);
                if (ClipContent)
                {
                    var clip = ContinuousCorners.Create(new Rect(bt, bt, Math.Max(0, final.Width - 2 * bt), Math.Max(0, final.Height - 2 * bt)), Math.Max(0, CornerRadius - bt)).Clone();
                    clip.Transform = new TranslateTransform(-rc.X, -rc.Y);
                    clip.Freeze();
                    _child.Clip = clip;
                }
                else _child.Clip = null;
            }
            return final;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            var shape = ContinuousCorners.Create(new Rect(0, 0, w, h), CornerRadius);
            if (Backdrop != null) dc.DrawGeometry(Backdrop, null, shape);
            if (Background != null) dc.DrawGeometry(Background, null, shape);
            if (Sheen != null)
            {
                dc.PushClip(shape);
                dc.DrawRectangle(Sheen, null, new Rect(0, 0, w, h));
                dc.Pop();
            }
            // rim above the content
            using var o = _overlay.RenderOpen();
            double bt = BorderThickness;
            if (BorderBrush != null && bt > 0)
                o.DrawGeometry(null, new Pen(BorderBrush, bt), ContinuousCorners.Create(new Rect(bt / 2, bt / 2, w - bt, h - bt), Math.Max(0, CornerRadius - bt / 2)));
            if (OuterBrush != null)
                o.DrawGeometry(null, new Pen(OuterBrush, 0.6), ContinuousCorners.Create(new Rect(0.3, 0.3, w - 0.6, h - 0.6), Math.Max(0, CornerRadius - 0.3)));
        }

        /// <summary>Glass panel frame using the theme resources.</summary>
        public static ContinuousBorder Glass(double radius, string background = "Panel.Background")
        {
            var b = new ContinuousBorder { CornerRadius = radius };
            if (background != null) b.SetResourceReference(BackgroundProperty, background);
            b.SetResourceReference(BorderBrushProperty, "Glass.Frame");
            b.SetResourceReference(BorderThicknessProperty, "Glass.Stroke");
            b.SetResourceReference(SheenProperty, "Glass.Sheen");
            b.SetResourceReference(OuterBrushProperty, "Glass.Outer");
            return b;
        }
    }

    /// <summary>
    /// A blurred window that sits directly beneath a floating panel (or the Dock shelf). The panel itself has no
    /// window region, so its rounded edges stay anti-aliased; this underlay supplies the DWM blur, clipped to
    /// the same continuous shape and inset by a pixel so its hard edge hides under the panel's rim.
    ///
    /// It is a plain (non-layered) native window on purpose: DWM ignores the window region of layered
    /// windows when drawing the blur, which left square corners showing behind rounded panels.
    /// </summary>
    public sealed class BlurUnderlay
    {
        [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int L, R, T, B; }
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

        readonly byte _tint;
        HwndSource _src;
        int _w, _h, _r;
        bool _topmost;

        public bool IsVisible { get; private set; }

        public BlurUnderlay(byte tintAlpha)
        {
            _tint = tintAlpha;
            ThemeManager.Changed += ApplyBlur;
        }

        IntPtr Handle
        {
            get
            {
                if (_src == null)
                {
                    var p = new HwndSourceParameters("MacWinBlur")
                    {
                        WindowStyle = unchecked((int)0x80000000),                     // WS_POPUP
                        ExtendedWindowStyle = 0x80 | 0x08000000 | 0x20,               // TOOLWINDOW | NOACTIVATE | TRANSPARENT
                        PositionX = -32000, PositionY = -32000, Width = 1, Height = 1,
                        UsesPerPixelOpacity = false
                    };
                    _src = new HwndSource(p);
                    _src.CompositionTarget.BackgroundColor = Colors.Transparent;
                    // let DWM paint the whole client area so the accent blur shows through
                    var m = new MARGINS { L = -1, R = -1, T = -1, B = -1 };
                    DwmExtendFrameIntoClientArea(_src.Handle, ref m);
                    int pref = 1; // no system rounding: our own region defines the shape
                    Win32.DwmSetWindowAttribute(_src.Handle, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
                    _src.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                    {
                        if (msg == 0x0084) { handled = true; return new IntPtr(-1); } // WM_NCHITTEST → HTTRANSPARENT
                        return IntPtr.Zero;
                    });
                    ApplyBlur();
                }
                return _src.Handle;
            }
        }

        public void ApplyBlur()
        {
            if (_src == null) return;
            if (App.Settings.RealBlur) Backdrop.Enable(_src.Handle, ThemeManager.BlurTint(_tint));
            else { Backdrop.Disable(_src.Handle); Hide(); }
        }

        /// <summary>Places the blur exactly under <paramref name="above"/> (screen pixels).</summary>
        public void Place(IntPtr above, int x, int y, int w, int h, double radiusPx, bool topmost)
        {
            if (!App.Settings.RealBlur || w < 2 || h < 2) { Hide(); return; }
            var hwnd = Handle;
            if (topmost != _topmost)
            {
                _topmost = topmost;
                Win32.SetWindowPos(hwnd, topmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            }
            int r = (int)Math.Round(radiusPx);
            if (w != _w || h != _h || r != _r)
            {
                _w = w; _h = h; _r = r;
                var rgn = ContinuousCorners.CreateRegion(w, h, radiusPx, 1.0);
                if (Win32.SetWindowRgn(hwnd, rgn, true) == 0) Win32.DeleteObject(rgn);
            }
            Win32.SetWindowPos(hwnd, above, x, y, w, h, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
            IsVisible = true;
        }

        public void Hide()
        {
            if (_src == null || !IsVisible) return;
            Win32.ShowWindow(_src.Handle, Win32.SW_HIDE);
            IsVisible = false;
        }

        public void Close()
        {
            ThemeManager.Changed -= ApplyBlur;
            _src?.Dispose();
            _src = null;
            IsVisible = false;
        }
    }
}
