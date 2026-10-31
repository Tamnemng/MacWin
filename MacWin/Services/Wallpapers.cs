using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacWin.Native;

namespace MacWin.Services
{
    /// <summary>
    /// Original, procedurally generated wallpapers (layered gradient waves) in light and dark variants.
    /// "Dynamic" mode swaps the variant automatically with the appearance.
    /// </summary>
    public static class Wallpapers
    {
        public record Palette(string Name, string[] LightSky, string[] LightWaves, string[] DarkSky, string[] DarkWaves, double Seed);

        public static readonly List<Palette> All = new()
        {
            new("Coast",  new[]{ "#FCE3C8", "#9CC8F2" }, new[]{ "#7DB7EE", "#4E8FDC", "#2F6CC6", "#1E4FA6", "#143A82" },
                          new[]{ "#0B1430", "#20345E" }, new[]{ "#24427A", "#1B3566", "#132A52", "#0D1F3F", "#08152E" }, 1.3),
            new("Dunes",  new[]{ "#FFE7C2", "#F7B98A" }, new[]{ "#F4A36B", "#E8834D", "#D2643A", "#B44A2E", "#8E3624" },
                          new[]{ "#1C0F1E", "#3D1C2F" }, new[]{ "#5B2638", "#4A1F33", "#3A182B", "#2B1222", "#1E0C18" }, 2.1),
            new("Aurora", new[]{ "#E3F7F1", "#B9D8F7" }, new[]{ "#8FE3C8", "#5CC9B4", "#3DA6A8", "#2D7F96", "#205D7A" },
                          new[]{ "#040B16", "#0F2133" }, new[]{ "#1B6B66", "#17565E", "#123F52", "#0C2C42", "#071D30" }, 3.7),
            new("Bloom",  new[]{ "#FFE9F1", "#E8D2FF" }, new[]{ "#F5A6C8", "#E07CB2", "#C25AA0", "#9A428A", "#733070" },
                          new[]{ "#140A1E", "#2A1340" }, new[]{ "#5A2A6E", "#4A215F", "#3A1A4E", "#2B133C", "#1D0D2A" }, 4.4),
            new("Lagoon", new[]{ "#E9FBF8", "#A6E4E8" }, new[]{ "#6FD3D0", "#3FB8BE", "#2399A8", "#17788C", "#0E5A70" },
                          new[]{ "#03141A", "#0A2A33" }, new[]{ "#0E5560", "#0B4651", "#083742", "#062A33", "#041D25" }, 5.9),
            new("Graphite", new[]{ "#F2F2F4", "#D5D6DB" }, new[]{ "#C4C6CC", "#A9ABB3", "#8D9099", "#72757F", "#575A64" },
                          new[]{ "#0D0D0F", "#1E1F23" }, new[]{ "#3A3B41", "#303137", "#26272C", "#1D1E22", "#141518" }, 6.6),
        };

        static string Folder => Path.Combine(AppSettings.Folder, "Wallpapers");

        public static string PathFor(string name, bool dark) => System.IO.Path.Combine(Folder, $"{name}-{(dark ? "dark" : "light")}.png");

        static Color C(string s) => (Color)ColorConverter.ConvertFromString(s);

        public static DrawingVisual Draw(Palette p, bool dark, double w, double h)
        {
            var sky = dark ? p.DarkSky : p.LightSky;
            var waves = dark ? p.DarkWaves : p.LightWaves;
            var v = new DrawingVisual();
            using var dc = v.RenderOpen();
            dc.DrawRectangle(new LinearGradientBrush(C(sky[0]), C(sky[1]), 90), null, new Rect(0, 0, w, h));

            // soft glow
            var glow = new RadialGradientBrush(Color.FromArgb((byte)(dark ? 70 : 120), 255, 255, 255), Color.FromArgb(0, 255, 255, 255))
            { Center = new Point(0.72, 0.28), GradientOrigin = new Point(0.72, 0.28), RadiusX = 0.45, RadiusY = 0.6 };
            dc.DrawRectangle(glow, null, new Rect(0, 0, w, h));

            var rnd = new Random((int)(p.Seed * 1000));
            for (int i = 0; i < waves.Length; i++)
            {
                double baseY = h * (0.38 + i * 0.12);
                double amp = h * (0.07 - i * 0.008);
                double f1 = 1.2 + rnd.NextDouble() * 1.4, f2 = 2.5 + rnd.NextDouble() * 2.0;
                double ph1 = rnd.NextDouble() * Math.PI * 2, ph2 = rnd.NextDouble() * Math.PI * 2;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(0, h), true, true);
                    const int steps = 160;
                    for (int s = 0; s <= steps; s++)
                    {
                        double x = w * s / steps, t = (double)s / steps;
                        double y = baseY + amp * Math.Sin(t * Math.PI * f1 + ph1) + amp * 0.45 * Math.Sin(t * Math.PI * f2 + ph2);
                        c.LineTo(new Point(x, y), true, true);
                    }
                    c.LineTo(new Point(w, h), true, false);
                }
                var top = C(waves[i]);
                var bottom = Color.FromRgb((byte)(top.R * 0.7), (byte)(top.G * 0.7), (byte)(top.B * 0.7));
                var fill = new LinearGradientBrush(Color.FromArgb(235, top.R, top.G, top.B), bottom, 90);
                dc.DrawGeometry(fill, new Pen(new SolidColorBrush(Color.FromArgb((byte)(dark ? 25 : 60), 255, 255, 255)), h * 0.0015), g);
            }
            return v;
        }

        public static BitmapSource Thumbnail(Palette p, bool dark, int w = 200, int h = 125)
        {
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(Draw(p, dark, w, h));
            rtb.Freeze();
            return rtb;
        }

        static string Generate(Palette p, bool dark)
        {
            var path = PathFor(p.Name, dark);
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(Folder);
            int w = Math.Min(Win32.GetSystemMetrics(Win32.SM_CXSCREEN), 3840);
            int h = Math.Min(Win32.GetSystemMetrics(Win32.SM_CYSCREEN), 2400);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(Draw(p, dark, w, h));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
            return path;
        }

        /// <summary>Applies the configured wallpaper for the current appearance (generation runs off the UI thread).</summary>
        public static async Task ApplyCurrentAsync()
        {
            var s = App.Settings;
            if (!string.IsNullOrEmpty(s.CustomWallpaper))
            {
                if (File.Exists(s.CustomWallpaper)) SystemTweaks.SetWallpaper(s.CustomWallpaper);
                return;
            }
            if (string.IsNullOrEmpty(s.Wallpaper) || s.Wallpaper == "None") return;
            var p = All.Find(x => x.Name == s.Wallpaper) ?? All[0];
            bool dark = s.DynamicWallpaper ? ThemeManager.IsDark : false;
            string path = await StaWorker.Run(() => Generate(p, dark));
            if (SystemTweaks.GetWallpaper() != path) SystemTweaks.SetWallpaper(path);
        }
    }
}
