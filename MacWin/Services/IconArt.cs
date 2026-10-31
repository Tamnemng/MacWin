using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace MacWin.Services
{
    /// <summary>
    /// Original, procedurally drawn icons in a "rounded squircle" desktop style.
    /// Everything is vector-drawn at runtime, so no external image assets are shipped.
    /// </summary>
    public static class IconArt
    {
        public const int Size = 144;
        static readonly FontFamily Glyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");
        static readonly FontFamily Ui = new("SF Pro Display, SF Pro, Segoe UI Variable Display, Segoe UI");
        static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

        // Squircle occupies ~80% of the canvas, leaving room for the soft shadow (desktop icon grid).
        const double Inset = 0.098;

        public static Geometry Squircle(Rect r, double n = 5.0)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                double a = r.Width / 2, b = r.Height / 2, cx = r.X + a, cy = r.Y + b;
                const int N = 120;
                for (int i = 0; i <= N; i++)
                {
                    double t = i * 2 * Math.PI / N;
                    double ct = Math.Cos(t), st = Math.Sin(t);
                    double x = cx + a * Math.Sign(ct) * Math.Pow(Math.Abs(ct), 2 / n);
                    double y = cy + b * Math.Sign(st) * Math.Pow(Math.Abs(st), 2 / n);
                    if (i == 0) c.BeginFigure(new Point(x, y), true, true);
                    else c.LineTo(new Point(x, y), true, false);
                }
            }
            g.Freeze();
            return g;
        }

        static Rect PlateRect => new(Size * Inset, Size * Inset * 0.85, Size * (1 - 2 * Inset), Size * (1 - 2 * Inset));

        static LinearGradientBrush V(string top, string bottom)
        {
            var b = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 90);
            b.Freeze();
            return b;
        }

        static SolidColorBrush S(string c)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c));
            b.Freeze();
            return b;
        }

        static BitmapSource Render(Action<DrawingContext, Rect, Geometry> body, bool shadow = true)
        {
            var visual = new DrawingVisual();
            var plate = PlateRect;
            var shape = Squircle(plate);
            using (var dc = visual.RenderOpen())
            {
                dc.PushClip(shape);
                body(dc, plate, shape);
                // glossy top light + hairline edge
                var gloss = new LinearGradientBrush(Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
                dc.DrawRectangle(gloss, null, new Rect(plate.X, plate.Y, plate.Width, plate.Height * 0.5));
                if (App.Settings?.LiquidGlass ?? false)
                {
                    // glass edge: bright specular rim (inner half of the stroke, thanks to the clip) + soft top glow
                    var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                    rim.GradientStops.Add(new GradientStop(Color.FromArgb(210, 255, 255, 255), 0));
                    rim.GradientStops.Add(new GradientStop(Color.FromArgb(40, 255, 255, 255), 0.4));
                    rim.GradientStops.Add(new GradientStop(Color.FromArgb(15, 255, 255, 255), 0.62));
                    rim.GradientStops.Add(new GradientStop(Color.FromArgb(130, 255, 255, 255), 1));
                    dc.DrawGeometry(null, new Pen(rim, plate.Width * 0.045), shape);
                    var glow = new RadialGradientBrush(Color.FromArgb(60, 255, 255, 255), Color.FromArgb(0, 255, 255, 255))
                    { Center = new Point(0.5, 0), GradientOrigin = new Point(0.5, 0), RadiusX = 0.75, RadiusY = 0.55 };
                    dc.DrawRectangle(glow, null, plate);
                }
                dc.Pop();
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(28, 0, 0, 0)), 0.8), shape);
            }
            if (shadow)
                visual.Effect = new DropShadowEffect { BlurRadius = 9, ShadowDepth = 2.5, Direction = 270, Opacity = 0.28, Color = Colors.Black };
            var rtb = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        static void Glyph(DrawingContext dc, Rect r, string glyph, Brush fill, double scale = 0.5, double dy = 0, FontFamily font = null, FontWeight? weight = null)
        {
            var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(font ?? Glyphs, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
                r.Width * scale, fill, 1.0);
            dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2 + dy * r.Height));
        }

        static void GlyphShadowed(DrawingContext dc, Rect r, string glyph, Brush fill, double scale = 0.5, double dy = 0)
        {
            Glyph(dc, r, glyph, new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), scale, dy + 0.012);
            Glyph(dc, r, glyph, fill, scale, dy);
        }

        // ------------------------------------------------------------------ roles & system apps

        public static BitmapSource ForKey(string key) => key switch
        {
            "files" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#6CC4FF", "#1E7BEA"), null, r);
                // folder shape
                double w = r.Width, x = r.X, y = r.Y;
                var back = new RectangleGeometry(new Rect(x + w * 0.18, y + w * 0.27, w * 0.64, w * 0.48), w * 0.05, w * 0.05);
                dc.DrawGeometry(S("#D6ECFF"), null, back);
                var tab = new RectangleGeometry(new Rect(x + w * 0.18, y + w * 0.22, w * 0.26, w * 0.12), w * 0.04, w * 0.04);
                dc.DrawGeometry(S("#D6ECFF"), null, tab);
                dc.DrawRoundedRectangle(V("#FFFFFF", "#E8F3FF"), null, new Rect(x + w * 0.18, y + w * 0.36, w * 0.64, w * 0.39), w * 0.05, w * 0.05);
                dc.DrawRoundedRectangle(S("#7DBBF5"), null, new Rect(x + w * 0.42, y + w * 0.47, w * 0.16, w * 0.03), w * 0.015, w * 0.015);
            }),
            "launchpad" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#5B5F6E", "#2B2D36"), null, r);
                string[] colors = { "#FF5F57", "#FEBC2E", "#28C840", "#32ADE6", "#5E5CE6", "#FF375F", "#FF9F0A", "#64D2FF", "#BF5AF2" };
                double cell = r.Width * 0.17, gap = r.Width * 0.07, start = r.X + (r.Width - (cell * 3 + gap * 2)) / 2;
                double top = r.Y + (r.Height - (cell * 3 + gap * 2)) / 2;
                for (int i = 0; i < 9; i++)
                {
                    var rc = new Rect(start + (i % 3) * (cell + gap), top + (i / 3) * (cell + gap), cell, cell);
                    var c = (Color)ColorConverter.ConvertFromString(colors[i]);
                    var b = new LinearGradientBrush(Lighten(c, 0.25), c, 90);
                    dc.DrawGeometry(b, null, Squircle(rc, 4));
                }
            }),
            "browser" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(S("#FFFFFF"), null, r);
                var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
                double R = r.Width * 0.39;
                var disc = new RadialGradientBrush(Color.FromRgb(0x7F, 0xD7, 0xFF), Color.FromRgb(0x10, 0x6B, 0xE8)) { GradientOrigin = new Point(0.5, 0.25) };
                dc.DrawEllipse(disc, null, c, R, R);
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), r.Width * 0.012);
                dc.DrawEllipse(null, pen, c, R * 0.45, R);
                dc.DrawLine(pen, new Point(c.X - R, c.Y), new Point(c.X + R, c.Y));
                dc.DrawLine(pen, new Point(c.X, c.Y - R), new Point(c.X, c.Y + R));
                dc.DrawLine(pen, new Point(c.X - R * 0.86, c.Y - R * 0.5), new Point(c.X + R * 0.86, c.Y - R * 0.5));
                dc.DrawLine(pen, new Point(c.X - R * 0.86, c.Y + R * 0.5), new Point(c.X + R * 0.86, c.Y + R * 0.5));
                // orbit swoosh
                var orbit = new Pen(V("#FFB340", "#FF5F57"), r.Width * 0.045) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    gc.BeginFigure(new Point(c.X - R * 1.05, c.Y + R * 0.55), false, false);
                    gc.BezierTo(new Point(c.X - R * 0.2, c.Y + R * 0.1), new Point(c.X + R * 0.5, c.Y - R * 0.5), new Point(c.X + R * 1.0, c.Y - R * 0.95), true, true);
                }
                dc.DrawGeometry(null, orbit, g);
            }),
            "messages" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#6BEA7A", "#1DB83A"), null, r);
                var c = new Point(r.X + r.Width / 2, r.Y + r.Height * 0.47);
                dc.DrawEllipse(Brushes.White, null, c, r.Width * 0.33, r.Width * 0.27);
                var tail = new StreamGeometry();
                using (var gc = tail.Open())
                {
                    gc.BeginFigure(new Point(c.X - r.Width * 0.2, c.Y + r.Width * 0.17), true, true);
                    gc.LineTo(new Point(c.X - r.Width * 0.27, c.Y + r.Width * 0.31), true, false);
                    gc.LineTo(new Point(c.X - r.Width * 0.06, c.Y + r.Width * 0.24), true, false);
                }
                dc.DrawGeometry(Brushes.White, null, tail);
                for (int i = -1; i <= 1; i++)
                    dc.DrawEllipse(S("#2FC04A"), null, new Point(c.X + i * r.Width * 0.11, c.Y), r.Width * 0.035, r.Width * 0.035);
            }),
            "mail" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#5AC8FA", "#0A6CF0"), null, r);
                var env = new Rect(r.X + r.Width * 0.17, r.Y + r.Height * 0.29, r.Width * 0.66, r.Height * 0.44);
                dc.DrawRoundedRectangle(V("#FFFFFF", "#E9F2FF"), null, env, r.Width * 0.04, r.Width * 0.04);
                var pen = new Pen(S("#9CC3F5"), r.Width * 0.025) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var flap = new StreamGeometry();
                using (var gc = flap.Open())
                {
                    gc.BeginFigure(new Point(env.X + env.Width * 0.06, env.Y + env.Height * 0.1), false, false);
                    gc.LineTo(new Point(env.X + env.Width / 2, env.Y + env.Height * 0.55), true, true);
                    gc.LineTo(new Point(env.Right - env.Width * 0.06, env.Y + env.Height * 0.1), true, true);
                }
                dc.DrawGeometry(null, pen, flap);
            }),
            "maps" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#A8E6A1", "#5CC16A"), null, r);
                var road = new Pen(S("#FFFFFF"), r.Width * 0.09);
                dc.DrawLine(road, new Point(r.X + r.Width * 0.6, r.Y), new Point(r.X + r.Width * 0.35, r.Bottom));
                dc.DrawLine(new Pen(S("#FFD54A"), r.Width * 0.07), new Point(r.X, r.Y + r.Height * 0.62), new Point(r.Right, r.Y + r.Height * 0.48));
                dc.DrawRectangle(S("#7FC8F8"), null, new Rect(r.Right - r.Width * 0.3, r.Y, r.Width * 0.3, r.Height * 0.28));
                GlyphShadowed(dc, r, "", S("#FF3B30"), 0.42, -0.06);
            }),
            "photos" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#FFFFFF", "#F1F1F4"), null, r);
                var frame = new Rect(r.X + r.Width * 0.15, r.Y + r.Height * 0.2, r.Width * 0.7, r.Height * 0.6);
                dc.DrawRoundedRectangle(V("#8FD3FF", "#FFD3A8"), null, frame, r.Width * 0.06, r.Width * 0.06);
                dc.PushClip(new RectangleGeometry(frame, r.Width * 0.06, r.Width * 0.06));
                dc.DrawEllipse(S("#FFE066"), null, new Point(frame.X + frame.Width * 0.72, frame.Y + frame.Height * 0.3), frame.Width * 0.1, frame.Width * 0.1);
                var m = new StreamGeometry();
                using (var gc = m.Open())
                {
                    gc.BeginFigure(new Point(frame.X, frame.Bottom), true, true);
                    gc.LineTo(new Point(frame.X + frame.Width * 0.35, frame.Y + frame.Height * 0.4), true, false);
                    gc.LineTo(new Point(frame.X + frame.Width * 0.6, frame.Y + frame.Height * 0.72), true, false);
                    gc.LineTo(new Point(frame.X + frame.Width * 0.75, frame.Y + frame.Height * 0.55), true, false);
                    gc.LineTo(new Point(frame.Right, frame.Y + frame.Height * 0.85), true, false);
                    gc.LineTo(new Point(frame.Right, frame.Bottom), true, false);
                }
                dc.DrawGeometry(V("#4CBF7A", "#2D8F57"), null, m);
                dc.Pop();
            }),
            "video" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#5EE07A", "#14A44D"), null, r);
                var body = new Rect(r.X + r.Width * 0.17, r.Y + r.Height * 0.32, r.Width * 0.44, r.Height * 0.36);
                dc.DrawRoundedRectangle(Brushes.White, null, body, r.Width * 0.07, r.Width * 0.07);
                var lens = new StreamGeometry();
                using (var gc = lens.Open())
                {
                    gc.BeginFigure(new Point(body.Right + r.Width * 0.03, r.Y + r.Height * 0.45), true, true);
                    gc.LineTo(new Point(r.X + r.Width * 0.83, r.Y + r.Height * 0.33), true, false);
                    gc.LineTo(new Point(r.X + r.Width * 0.83, r.Y + r.Height * 0.67), true, false);
                    gc.LineTo(new Point(body.Right + r.Width * 0.03, r.Y + r.Height * 0.55), true, false);
                }
                dc.DrawGeometry(Brushes.White, new Pen(Brushes.White, r.Width * 0.03) { LineJoin = PenLineJoin.Round }, lens);
            }),
            "calendar" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(S("#FFFFFF"), null, r);
                var now = DateTime.Now;
                var head = new FormattedText(now.ToString("ddd", CultureInfo.CurrentCulture).ToUpperInvariant(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(Ui, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), r.Width * 0.15, S("#FF3B30"), 1.0);
                dc.DrawText(head, new Point(r.X + (r.Width - head.Width) / 2, r.Y + r.Height * 0.1));
                var day = new FormattedText(now.Day.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(Ui, FontStyles.Normal, FontWeights.Light, FontStretches.Normal), r.Width * 0.5, S("#1D1D1F"), 1.0);
                dc.DrawText(day, new Point(r.X + (r.Width - day.Width) / 2, r.Y + r.Height * 0.24));
            }),
            "reminders" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(S("#FFFFFF"), null, r);
                string[] cols = { "#0A84FF", "#FF453A", "#FF9F0A" };
                for (int i = 0; i < 3; i++)
                {
                    double y = r.Y + r.Height * (0.28 + i * 0.22);
                    dc.DrawEllipse(null, new Pen(S(cols[i]), r.Width * 0.03), new Point(r.X + r.Width * 0.26, y), r.Width * 0.06, r.Width * 0.06);
                    if (i == 0) dc.DrawEllipse(S(cols[i]), null, new Point(r.X + r.Width * 0.26, y), r.Width * 0.035, r.Width * 0.035);
                    dc.DrawRoundedRectangle(S("#D9D9DE"), null, new Rect(r.X + r.Width * 0.38, y - r.Width * 0.02, r.Width * 0.4, r.Width * 0.04), 2, 2);
                }
            }),
            "notes" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#FFFFFF", "#F6F6F2"), null, r);
                dc.DrawRectangle(V("#FFE27A", "#FFC93C"), null, new Rect(r.X, r.Y, r.Width, r.Height * 0.27));
                var line = new Pen(S("#E3E3E0"), r.Width * 0.012);
                for (int i = 0; i < 5; i++)
                {
                    double y = r.Y + r.Height * (0.42 + i * 0.12);
                    dc.DrawLine(line, new Point(r.X + r.Width * 0.08, y), new Point(r.Right - r.Width * 0.08, y));
                }
                var dots = new Pen(S("#C9C9C6"), r.Width * 0.02) { DashStyle = new DashStyle(new double[] { 0.1, 2.2 }, 0), DashCap = PenLineCap.Round };
                dc.DrawLine(dots, new Point(r.X + r.Width * 0.08, r.Y + r.Height * 0.29), new Point(r.Right, r.Y + r.Height * 0.29));
            }),
            "music" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#FF6B81", "#F5264B"), null, r);
                GlyphShadowed(dc, r, "", Brushes.White, 0.5);
            }),
            "tv" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#2C2C2E", "#0B0B0C"), null, r);
                var play = new StreamGeometry();
                using (var gc = play.Open())
                {
                    gc.BeginFigure(new Point(r.X + r.Width * 0.4, r.Y + r.Height * 0.3), true, true);
                    gc.LineTo(new Point(r.X + r.Width * 0.7, r.Y + r.Height * 0.5), true, false);
                    gc.LineTo(new Point(r.X + r.Width * 0.4, r.Y + r.Height * 0.7), true, false);
                }
                dc.DrawGeometry(Brushes.White, new Pen(Brushes.White, r.Width * 0.05) { LineJoin = PenLineJoin.Round }, play);
            }),
            "code" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#3C7BFF", "#1747C9"), null, r);
                Glyph(dc, r, "</>", Brushes.White, 0.3, -0.01, Mono, FontWeights.Bold);
            }),
            "store" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#4FB7FF", "#0A63F5"), null, r);
                var bag = new Rect(r.X + r.Width * 0.24, r.Y + r.Height * 0.36, r.Width * 0.52, r.Height * 0.4);
                dc.DrawRoundedRectangle(Brushes.White, null, bag, r.Width * 0.05, r.Width * 0.05);
                dc.DrawEllipse(null, new Pen(Brushes.White, r.Width * 0.045), new Point(r.X + r.Width / 2, bag.Y), r.Width * 0.12, r.Width * 0.12);
                dc.DrawRectangle(Brushes.White, null, new Rect(bag.X, bag.Y, bag.Width, r.Height * 0.06));
                Glyph(dc, bag, "A", S("#0A63F5"), 0.55, 0.03, Ui, FontWeights.Bold);
            }),
            "settings" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#B9BBC1", "#7A7D86"), null, r);
                var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
                dc.DrawEllipse(V("#5D6068", "#3A3C42"), null, c, r.Width * 0.36, r.Width * 0.36);
                GlyphShadowed(dc, r, "", V("#F5F5F7", "#C9CBD1"), 0.62);
            }),
            "terminal" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#3A3A3C", "#121214"), null, r);
                var ft = new FormattedText(">_", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(Mono, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), r.Width * 0.26, S("#E5E5EA"), 1.0);
                dc.DrawText(ft, new Point(r.X + r.Width * 0.14, r.Y + r.Height * 0.12));
                dc.DrawRectangle(S("#3A3A3C"), null, new Rect(r.X, r.Bottom - r.Height * 0.06, r.Width, r.Height * 0.06));
            }),
            "calculator" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#4A4A4E", "#1F1F22"), null, r);
                double cell = r.Width * 0.15, gap = r.Width * 0.05;
                double sx = r.X + (r.Width - (cell * 4 + gap * 3)) / 2, sy = r.Y + r.Height * 0.2;
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 4; col++)
                    {
                        string c = col == 3 ? "#FF9F0A" : row == 0 ? "#A5A5AA" : "#5C5C61";
                        dc.DrawEllipse(S(c), null, new Point(sx + col * (cell + gap) + cell / 2, sy + row * (cell + gap) + cell / 2), cell / 2, cell / 2);
                    }
            }),
            "textedit" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#E8EEF7", "#C7D2E3"), null, r);
                var page = new Rect(r.X + r.Width * 0.22, r.Y + r.Height * 0.14, r.Width * 0.56, r.Height * 0.72);
                dc.DrawRectangle(Brushes.White, new Pen(S("#D0D0D5"), 1), page);
                var line = new Pen(S("#9AA4B2"), r.Width * 0.018);
                for (int i = 0; i < 6; i++)
                    dc.DrawLine(line, new Point(page.X + page.Width * 0.12, page.Y + page.Height * (0.18 + i * 0.12)),
                        new Point(page.Right - page.Width * (i == 5 ? 0.4 : 0.12), page.Y + page.Height * (0.18 + i * 0.12)));
                Glyph(dc, r, "", S("#F2A33A"), 0.3, 0.2);
            }),
            "office" => Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#5AA0FF", "#2463D6"), null, r);
                Glyph(dc, r, "", Brushes.White, 0.48);
            }),
            "downloads" => RenderFolderGlyph(""),
            _ => null,
        };

        static BitmapSource RenderFolderGlyph(string glyph)
        {
            var visual = new DrawingVisual();
            double w = Size;
            using (var dc = visual.RenderOpen())
            {
                var back = new RectangleGeometry(new Rect(w * 0.1, w * 0.2, w * 0.8, w * 0.62), w * 0.06, w * 0.06);
                dc.DrawGeometry(V("#7CC3FF", "#3E95F0"), null, back);
                dc.DrawRoundedRectangle(V("#7CC3FF", "#3E95F0"), null, new Rect(w * 0.1, w * 0.15, w * 0.32, w * 0.12), w * 0.04, w * 0.04);
                dc.DrawRoundedRectangle(V("#A9D8FF", "#5AAAF7"), null, new Rect(w * 0.1, w * 0.3, w * 0.8, w * 0.54), w * 0.06, w * 0.06);
                Glyph(dc, new Rect(w * 0.1, w * 0.3, w * 0.8, w * 0.54), glyph, new SolidColorBrush(Color.FromArgb(200, 22, 92, 190)), 0.42);
            }
            visual.Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Direction = 270, Opacity = 0.25 };
            var rtb = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        /// <summary>A translucent wire-mesh bin; <paramref name="full"/> shows crumpled paper inside.</summary>
        public static BitmapSource Trash(bool full)
        {
            var visual = new DrawingVisual();
            double w = Size;
            using (var dc = visual.RenderOpen())
            {
                var body = new StreamGeometry();
                using (var gc = body.Open())
                {
                    gc.BeginFigure(new Point(w * 0.2, w * 0.2), true, true);
                    gc.LineTo(new Point(w * 0.8, w * 0.2), true, true);
                    gc.LineTo(new Point(w * 0.73, w * 0.9), true, true);
                    gc.LineTo(new Point(w * 0.27, w * 0.9), true, true);
                }
                if (full)
                {
                    dc.DrawEllipse(V("#FFFFFF", "#D8D8DC"), null, new Point(w * 0.4, w * 0.25), w * 0.13, w * 0.1);
                    dc.DrawEllipse(V("#F7F7F9", "#CFCFD4"), null, new Point(w * 0.6, w * 0.22), w * 0.12, w * 0.11);
                    dc.DrawEllipse(V("#FFFFFF", "#E0E0E4"), null, new Point(w * 0.5, w * 0.18), w * 0.1, w * 0.08);
                }
                dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(150, 235, 240, 248), Color.FromArgb(200, 190, 198, 210), 90),
                    new Pen(new SolidColorBrush(Color.FromArgb(160, 140, 148, 160)), 1.2), body);
                var mesh = new Pen(new SolidColorBrush(Color.FromArgb(110, 120, 128, 140)), 1.1);
                for (int i = 1; i < 8; i++)
                {
                    double t = i / 8.0;
                    dc.DrawLine(mesh, new Point(w * (0.2 + 0.6 * t), w * 0.2), new Point(w * (0.27 + 0.46 * t), w * 0.9));
                }
                for (int i = 1; i < 6; i++)
                {
                    double y = w * (0.2 + 0.7 * i / 6.0), inset = w * 0.07 * i / 6.0;
                    dc.DrawLine(mesh, new Point(w * 0.2 + inset, y), new Point(w * 0.8 - inset, y));
                }
                dc.DrawRoundedRectangle(V("#F4F5F7", "#B9BEC7"), new Pen(new SolidColorBrush(Color.FromArgb(120, 110, 115, 125)), 1),
                    new Rect(w * 0.17, w * 0.16, w * 0.66, w * 0.07), w * 0.03, w * 0.03);
            }
            visual.Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Direction = 270, Opacity = 0.25 };
            var rtb = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        /// <summary>Places any app icon on a light squircle plate so every Dock icon shares the same shape.</summary>
        public static BitmapSource Plate(BitmapSource icon)
        {
            return Render((dc, r, s) =>
            {
                dc.DrawRectangle(V("#FFFFFF", "#ECECEF"), null, r);
                if (icon != null)
                {
                    double inner = r.Width * 0.66;
                    dc.DrawImage(icon, new Rect(r.X + (r.Width - inner) / 2, r.Y + (r.Height - inner) / 2, inner, inner));
                }
            });
        }

        /// <summary>Unplated icon with the same shadow and grid.</summary>
        public static BitmapSource Plain(BitmapSource icon)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                double inner = Size * 0.8;
                if (icon != null) dc.DrawImage(icon, new Rect((Size - inner) / 2, (Size - inner) / 2 - 2, inner, inner));
            }
            visual.Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Direction = 270, Opacity = 0.22 };
            var rtb = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        public static BitmapSource Placeholder() => Render((dc, r, s) => dc.DrawRectangle(V("#E5E5EA", "#C7C7CC"), null, r), false);

        static Color Lighten(Color c, double f) => Color.FromRgb(
            (byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));

        /// <summary>Maps well-known Windows system apps to a matching role-style icon.</summary>
        public static string SystemAppKey(string id)
        {
            if (id == null) return null;
            string s = id.ToLowerInvariant();
            if (s == "microsoft.windows.explorer") return "files";
            if (s.Contains("windowscalculator")) return "calculator";
            if (s.Contains("immersivecontrolpanel")) return "settings";
            if (s.Contains("windowsterminal")) return "terminal";
            if (s.Contains("windowsstore")) return "store";
            if (s.Contains("windows.photos")) return "photos";
            if (s.Contains("windowsmaps")) return "maps";
            if (s.Contains("windowsnotepad") || s.EndsWith("\\notepad.exe")) return "textedit";
            if (s.Contains("microsoftstickynotes")) return "notes";
            if (s.Contains("todos")) return "reminders";
            if (s.Contains("windowscamera")) return "video";
            if (s.Contains("zunemusic")) return "music";
            if (s.Contains("zunevideo")) return "tv";
            if (s.Contains("windowslive.calendar")) return "calendar";
            if (s.Contains("windowslive.mail") || s.Contains("outlookforwindows")) return "mail";
            return null;
        }
    }
}
