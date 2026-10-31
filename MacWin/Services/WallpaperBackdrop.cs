using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MacWin.Native;
using Microsoft.Win32;

namespace MacWin.Services
{
    /// <summary>
    /// A small, pre-blurred copy of the desktop wallpaper (quarter resolution, a few hundred KB).
    /// Surfaces that normally sit over the wallpaper — the Dock — sample it through the liquid-glass
    /// shader, so they can bend the background without capturing the screen.
    /// </summary>
    public static class WallpaperBackdrop
    {
        public static BitmapSource Image { get; private set; }
        public static event Action Changed;
        const int Downscale = 4;
        static string _key;
        static bool _hooked;

        public static void Start()
        {
            if (!_hooked)
            {
                _hooked = true;
                SystemEvents.UserPreferenceChanged += (_, e) =>
                {
                    if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.General)
                        Application.Current?.Dispatcher.BeginInvoke(Refresh);
                };
                SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => { _key = null; Refresh(); });
            }
            Refresh();
        }

        /// <summary>Screen pixel → bitmap pixel scale.</summary>
        public static double Scale => 1.0 / Downscale;

        static string Resolve()
        {
            var p = SystemTweaks.GetWallpaper();
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            var t = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
            return File.Exists(t) ? t : null;
        }

        public static async void Refresh()
        {
            try
            {
                string path = Resolve();
                double clarity = App.Settings?.GlassClarity ?? 0.6;
                string key = $"{path}|{(path != null ? File.GetLastWriteTimeUtc(path).Ticks : 0)}|{clarity:0.00}|{Win32.GetSystemMetrics(Win32.SM_CXSCREEN)}";
                if (key == _key) return;
                _key = key;
                var bmp = await StaWorker.Run(() => Build(path, clarity));
                if (bmp == null) return;
                Image = bmp;
                Changed?.Invoke();
            }
            catch (Exception e) { App.Log(e); }
        }

        static BitmapSource Build(string path, double clarity)
        {
            int sw = Win32.GetSystemMetrics(Win32.SM_CXSCREEN), sh = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
            int w = Math.Max(16, sw / Downscale), h = Math.Max(16, sh / Downscale);
            var v = new DrawingVisual();
            double blur = 1.2 + (1 - clarity) * 7;
            using (var dc = v.RenderOpen())
            {
                BitmapSource src = null;
                if (path != null)
                {
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                        bi.DecodePixelWidth = w * 2;
                        bi.UriSource = new Uri(path);
                        bi.EndInit();
                        bi.Freeze();
                        src = bi;
                    }
                    catch { }
                }
                // bleed past the edges so the blur doesn't darken the borders
                double pad = blur * 3;
                var target = new Rect(-pad, -pad, w + 2 * pad, h + 2 * pad);
                if (src != null)
                {
                    // "Fill" layout: scale to cover, centred
                    double s = Math.Max(target.Width / src.PixelWidth, target.Height / src.PixelHeight);
                    double dw = src.PixelWidth * s, dh = src.PixelHeight * s;
                    dc.DrawImage(src, new Rect(target.X + (target.Width - dw) / 2, target.Y + (target.Height - dh) / 2, dw, dh));
                }
                else dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x4A, 0x7A)), null, target);
            }
            v.Effect = new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(v);
            rtb.Freeze();
            return rtb;
        }
    }
}
