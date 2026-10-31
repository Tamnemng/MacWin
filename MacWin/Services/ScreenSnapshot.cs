using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MacWin.Native;

namespace MacWin.Services
{
    /// <summary>
    /// Frosted background for rounded panels: the screen area behind the panel is captured once when the panel
    /// opens (while it is still fully transparent), blurred at half resolution and drawn inside the panel's own
    /// anti-aliased shape. Unlike the DWM blur, this follows any corner shape exactly — no square corners.
    /// </summary>
    public static class ScreenSnapshot
    {
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);

        const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

        /// <summary>Captures a screen rectangle (physical pixels) and returns a blurred image whose DIP size equals the area.</summary>
        public static ImageSource CaptureBlurred(int x, int y, int w, int h, double scale, double blurDip = 26, int down = 2)
        {
            if (w < 2 || h < 2) return null;
            BitmapSource raw;
            IntPtr screen = Win32.GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero;
            try
            {
                mem = CreateCompatibleDC(screen);
                bmp = CreateCompatibleBitmap(screen, w, h);
                var old = SelectObject(mem, bmp);
                BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT);
                SelectObject(mem, old);
                raw = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch { return null; }
            finally
            {
                if (bmp != IntPtr.Zero) Win32.DeleteObject(bmp);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                Win32.ReleaseDC(IntPtr.Zero, screen);
            }

            // render at reduced resolution: blur hides the difference and it costs a fraction of the work
            double dipW = w / scale, dipH = h / scale;
            int pw = Math.Max(1, w / down), ph = Math.Max(1, h / down);
            double dpi = 96 * scale / down;
            var v = new DrawingVisual();
            using (var dc = v.RenderOpen())
            {
                // draw a little larger so the blur doesn't pull dark edges in from outside the capture
                double pad = blurDip * 0.9;
                dc.DrawImage(raw, new Rect(-pad, -pad, dipW + 2 * pad, dipH + 2 * pad));
            }
            v.Effect = new BlurEffect { Radius = blurDip, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
            var rtb = new RenderTargetBitmap(pw, ph, dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(v);
            rtb.Freeze();
            return rtb;
        }
    }
}
