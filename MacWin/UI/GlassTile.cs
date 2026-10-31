using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using MacWin.Services;

namespace MacWin.UI
{
    /// <summary>
    /// A standalone piece of liquid glass: it shows exactly the part of its window's backdrop snapshot that lies
    /// behind it, bent at the rim by the GPU lens shader, with a light tint, a specular rim and a top sheen.
    /// Controls float directly over the desktop, each as its own glass shape (no shared panel behind them).
    /// </summary>
    public class GlassTile : ContinuousBorder
    {
        readonly Grid _host = new();
        readonly Rectangle _lens = new() { IsHitTestVisible = false };
        readonly Rectangle _tint = new() { IsHitTestVisible = false };
        readonly ImageBrush _brush = new() { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        LiquidGlassEffect _fx;
        PanelWindow _window;
        UIElement _inner;

        public GlassTile(double radius)
        {
            CornerRadius = radius;
            ClipContent = true;
            _lens.Fill = _brush;
            _tint.SetResourceReference(Shape.FillProperty, "Glass.Tint");
            _host.Children.Add(_lens);
            _host.Children.Add(_tint);
            Child = _host;
            SetResourceReference(BorderBrushProperty, "Glass.Rim");
            SetResourceReference(BorderThicknessProperty, "Glass.Stroke");
            SetResourceReference(SheenProperty, "Glass.Sheen");
            SetResourceReference(OuterBrushProperty, "Glass.Outer");
            Loaded += (_, _) =>
            {
                _window = Window.GetWindow(this) as PanelWindow;
                if (_window != null) _window.SnapshotChanged += Align;
                Align();
            };
            Unloaded += (_, _) => { if (_window != null) _window.SnapshotChanged -= Align; };
            SizeChanged += (_, _) => Align();
            ThemeManager.Changed += Align;
        }

        /// <summary>The tile's content (drawn above the glass layers).</summary>
        public UIElement Inner
        {
            get => _inner;
            set
            {
                if (_inner != null) _host.Children.Remove(_inner);
                _inner = value;
                if (value != null) _host.Children.Add(value);
            }
        }

        /// <summary>Colours the glass (e.g. the accent colour for an "on" control) instead of the neutral tint.</summary>
        public void SetTint(Brush b)
        {
            if (b == null) _tint.SetResourceReference(Shape.FillProperty, "Glass.Tint");
            else _tint.Fill = b;
        }

        void Align()
        {
            var img = _window?.Snapshot;
            if (img == null || ActualWidth < 1 || _window.Content is not Visual root)
            {
                _lens.Visibility = Visibility.Collapsed;
                return;
            }
            Point p;
            try { p = TransformToAncestor(root).Transform(new Point(0, 0)); } catch { return; }
            _brush.ImageSource = img;
            double bt = BorderThickness;
            _brush.Viewbox = new Rect(p.X + bt, p.Y + bt, Math.Max(1, ActualWidth - 2 * bt), Math.Max(1, ActualHeight - 2 * bt));
            _lens.Visibility = Visibility.Visible;

            if (ThemeManager.IsLiquid && LiquidGlassEffect.Available)
            {
                _fx ??= new LiquidGlassEffect();
                _lens.Effect = _fx;
                double s = Ui.Scale(this);
                double w = ActualWidth, h = ActualHeight, r = Math.Min(CornerRadius, Math.Min(w, h) / 2);
                double clarity = App.Settings.GlassClarity;
                _fx.SizeRadius = new Point4D(w * s, h * s, r * s, Math.Min(Math.Min(w, h) * 0.38, 20) * s);
                // thickness · index of refraction · specular · tint (liquid-glass defaults, scaled to small controls)
                _fx.Params = new Point4D((10 + 16 * clarity) * s, 3.0, ThemeManager.IsDark ? 0.38 : 0.55, ThemeManager.IsDark ? 0 : 0.05);
                _fx.LightDir = new Point4D(0.5, -0.7, 1.5 * s, 0.15);
            }
            else _lens.Effect = null;
        }
    }
}
