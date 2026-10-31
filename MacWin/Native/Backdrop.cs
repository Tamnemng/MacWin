using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MacWin.Native
{
    /// <summary>
    /// Frosted-glass ("vibrancy") effects via the DWM accent policy, plus rounded window regions.
    /// </summary>
    internal static class Backdrop
    {
        enum AccentState { Disabled = 0, Gradient = 1, TransparentGradient = 2, BlurBehind = 3, AcrylicBlurBehind = 4 }

        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy { public AccentState State; public int Flags; public uint GradientColor; public int AnimationId; }

        [StructLayout(LayoutKind.Sequential)]
        struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        /// <summary>Real blur-behind. <paramref name="tint"/> is applied by DWM on top of the blur.</summary>
        public static void Enable(IntPtr hwnd, Color tint, bool acrylic = true)
        {
            if (hwnd == IntPtr.Zero) return;
            if (!App.Settings.RealBlur)
            {
                Apply(hwnd, AccentState.Disabled, 0);
                return;
            }
            byte a = Math.Max((byte)1, tint.A);
            uint abgr = ((uint)a << 24) | ((uint)tint.B << 16) | ((uint)tint.G << 8) | tint.R;
            Apply(hwnd, acrylic ? AccentState.AcrylicBlurBehind : AccentState.BlurBehind, abgr);
        }

        public static void Disable(IntPtr hwnd) => Apply(hwnd, AccentState.Disabled, 0);

        static void Apply(IntPtr hwnd, AccentState state, uint color)
        {
            var accent = new AccentPolicy { State = state, Flags = state == AccentState.AcrylicBlurBehind ? 2 : 0, GradientColor = color };
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = ptr, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        /// <summary>Clips the window (and therefore its blur) to a rounded rectangle given in physical pixels.</summary>
        public static void SetRoundedRegion(IntPtr hwnd, int widthPx, int heightPx, int radiusPx)
        {
            if (hwnd == IntPtr.Zero || widthPx <= 0 || heightPx <= 0) return;
            IntPtr rgn = radiusPx > 0
                ? Win32.CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, radiusPx * 2, radiusPx * 2)
                : Win32.CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, 0, 0);
            if (Win32.SetWindowRgn(hwnd, rgn, true) == 0) Win32.DeleteObject(rgn);
        }

        /// <summary>Keeps a WPF window's region rounded as it resizes.</summary>
        public static void AttachRoundedRegion(Window w, double radiusDip)
        {
            void Update()
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                double s = VisualTreeHelper.GetDpi(w).DpiScaleX;
                SetRoundedRegion(hwnd, (int)Math.Ceiling(w.ActualWidth * s), (int)Math.Ceiling(w.ActualHeight * s), (int)Math.Round(radiusDip * s));
            }
            w.SizeChanged += (_, _) => Update();
            w.SourceInitialized += (_, _) => Update();
            w.DpiChanged += (_, _) => Update();
        }

        /// <summary>Hides a window from Alt+Tab / taskbar and optionally prevents it from taking focus.</summary>
        public static void MakeToolWindow(Window w, bool noActivate)
        {
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            long flags = Win32.WS_EX_TOOLWINDOW;
            if (noActivate) flags |= Win32.WS_EX_NOACTIVATE;
            Win32.AddExStyle(hwnd, flags);
            int pref = 1; // DWMWCP_DONOTROUND - we draw our own corners
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
        }
    }
}
