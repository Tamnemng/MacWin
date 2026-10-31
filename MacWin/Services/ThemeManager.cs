using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MacWin.Services
{
    /// <summary>Light / dark palettes (classic frosted or liquid glass) exposed as DynamicResource brushes.</summary>
    public static class ThemeManager
    {
        public static bool IsDark { get; private set; }
        public static bool IsLiquid { get; private set; }
        public static Color Accent { get; private set; }
        public static event Action Changed;

        public static readonly Dictionary<string, Color> Accents = new()
        {
            ["Blue"] = Color.FromRgb(0x0A, 0x84, 0xFF),
            ["Purple"] = Color.FromRgb(0xA5, 0x50, 0xE6),
            ["Pink"] = Color.FromRgb(0xF7, 0x4F, 0x9E),
            ["Red"] = Color.FromRgb(0xFF, 0x52, 0x57),
            ["Orange"] = Color.FromRgb(0xF7, 0x82, 0x1B),
            ["Yellow"] = Color.FromRgb(0xFF, 0xC6, 0x00),
            ["Green"] = Color.FromRgb(0x62, 0xBA, 0x46),
            ["Graphite"] = Color.FromRgb(0x8C, 0x8C, 0x8C),
        };

        public static bool ReadSystemDark()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        public static bool ResolveDark(AppSettings s)
        {
            return s.Appearance switch
            {
                AppearanceMode.Dark => true,
                AppearanceMode.Light => false,
                _ => DateTime.Now.Hour < 7 || DateTime.Now.Hour >= 19,
            };
        }

        static SolidColorBrush B(uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }

        static Color W(byte a) => Color.FromArgb(a, 255, 255, 255);

        public static void Apply(bool dark, string accentName)
        {
            var s = App.Settings;
            IsDark = dark;
            IsLiquid = s?.LiquidGlass ?? false;
            double clarity = Math.Clamp(s?.GlassClarity ?? 0.6, 0, 1);
            Accent = Accents.TryGetValue(accentName ?? "Blue", out var c) ? c : Accents["Blue"];
            var r = Application.Current.Resources;

            // surface alpha: classic value, or interpolated towards "clear" for liquid glass
            uint Surf(uint argb, byte clearAlpha)
            {
                if (!IsLiquid) return argb;
                byte a = (byte)(argb >> 24);
                byte na = (byte)Math.Round(a + (clearAlpha - a) * clarity);
                return ((uint)na << 24) | (argb & 0xFFFFFF);
            }

            var accent = new SolidColorBrush(Accent); accent.Freeze();
            r["Accent"] = accent;
            r["AccentColor"] = Accent;

            if (dark)
            {
                r["Bar.Background"] = B(Surf(0x8C1E1E1E, 0x14));
                r["Bar.Foreground"] = B(0xF2FFFFFF);
                r["Bar.Hover"] = B(0x33FFFFFF);
                r["Text.Primary"] = B(0xFFF2F2F2);
                r["Text.Secondary"] = B(0x99FFFFFF);
                r["Text.Tertiary"] = B(0x66FFFFFF);
                r["Panel.Background"] = B(Surf(0xD9262628, 0x70));
                r["Panel.Solid"] = B(Surf(0xFF1E1E1E, 0xE0));
                r["Panel.Sidebar"] = B(Surf(0xB3262628, 0x60));
                r["Panel.Border"] = B(0x80000000);
                r["Panel.Highlight"] = B(0x24FFFFFF);
                r["Menu.Background"] = B(Surf(0xF22A2A2D, 0xEC));
                r["Separator"] = B(0x24FFFFFF);
                r["Tile.Background"] = B(Surf(0x33FFFFFF, 0x1C));
                r["Tile.Off"] = B(0x40FFFFFF);
                r["Control.Background"] = B(0xFF3A3A3C);
                r["Control.Border"] = B(0x33FFFFFF);
                r["Input.Background"] = B(0x1FFFFFFF);
                r["Dock.Shelf"] = B(Surf(0x59303032, 0x1A));
                r["Dock.Border"] = B(0x40FFFFFF);
                r["Dock.Outer"] = B(0x80000000);
                r["Dock.Indicator"] = B(0xE6FFFFFF);
                r["Label.Background"] = B(Surf(0xF0303033, 0xB0));
                r["Label.Foreground"] = B(0xFFF2F2F2);
                r["Selection"] = B(0x33FFFFFF);
            }
            else
            {
                r["Bar.Background"] = B(Surf(0x8CF6F6F6, 0x14));
                r["Bar.Foreground"] = B(0xF0000000);
                r["Bar.Hover"] = B(0x26000000);
                r["Text.Primary"] = B(0xFF1D1D1F);
                r["Text.Secondary"] = B(0x8C000000);
                r["Text.Tertiary"] = B(0x59000000);
                r["Panel.Background"] = B(Surf(0xD9F2F2F4, 0x80));
                r["Panel.Solid"] = B(Surf(0xFFFFFFFF, 0xE6));
                r["Panel.Sidebar"] = B(Surf(0xB3EBEBED, 0x70));
                r["Panel.Border"] = B(0x33000000);
                r["Panel.Highlight"] = B(0x80FFFFFF);
                r["Menu.Background"] = B(Surf(0xF5F2F2F3, 0xF0));
                r["Separator"] = B(0x1F000000);
                r["Tile.Background"] = B(Surf(0x8CFFFFFF, 0x40));
                r["Tile.Off"] = B(0x1F000000);
                r["Control.Background"] = B(0xFFFFFFFF);
                r["Control.Border"] = B(0x29000000);
                r["Input.Background"] = B(0x14000000);
                r["Dock.Shelf"] = B(Surf(0x66F4F4F6, 0x26));
                r["Dock.Border"] = B(0x66FFFFFF);
                r["Dock.Outer"] = B(0x33000000);
                r["Dock.Indicator"] = B(0xCC1D1D1F);
                r["Label.Background"] = B(Surf(0xF0F0F0F2, 0xB8));
                r["Label.Foreground"] = B(0xFF1D1D1F);
                r["Selection"] = B(0x1A000000);
            }

            // tint inside glass tiles: almost clear for liquid glass, frosted for the classic style
            r["Glass.Tint"] = IsLiquid ? B(dark ? 0x33101014u : 0x26FFFFFFu) : B(dark ? 0xB3262628u : 0xB3F2F2F4u);

            // ---- glass edge treatment
            if (IsLiquid)
            {
                // a specular rim: bright where light hits (top-left), fading, a softer glint bottom-right
                byte hi = dark ? (byte)0x8C : (byte)0xE6, mid = dark ? (byte)0x14 : (byte)0x30, lo = dark ? (byte)0x50 : (byte)0x90;
                var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                rim.GradientStops.Add(new GradientStop(W(hi), 0));
                rim.GradientStops.Add(new GradientStop(W(mid), 0.35));
                rim.GradientStops.Add(new GradientStop(W((byte)(mid / 2)), 0.65));
                rim.GradientStops.Add(new GradientStop(W(lo), 1));
                rim.Freeze();
                r["Glass.Frame"] = rim;
                r["Glass.Rim"] = rim;
                var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                sheen.GradientStops.Add(new GradientStop(W(dark ? (byte)0x1E : (byte)0x55), 0));
                sheen.GradientStops.Add(new GradientStop(W(0), 0.55));
                sheen.Freeze();
                r["Glass.Sheen"] = sheen;
                r["Glass.Thickness"] = new Thickness(1);
                r["Glass.Stroke"] = 1.3;
                r["Glass.Outer"] = B(dark ? 0x73000000u : 0x30000000u);
                r["Menu.Radius"] = new CornerRadius(12);
                r["Menu.RadiusValue"] = 12.0;
            }
            else
            {
                r["Glass.Frame"] = r["Panel.Border"];
                r["Glass.Rim"] = r["Panel.Highlight"];
                r["Glass.Sheen"] = Brushes.Transparent;
                r["Glass.Thickness"] = new Thickness(0.5);
                r["Glass.Stroke"] = 0.6;
                r["Glass.Outer"] = Brushes.Transparent;
                r["Menu.Radius"] = new CornerRadius(9);
                r["Menu.RadiusValue"] = 9.0;
            }
            Changed?.Invoke();
        }

        /// <summary>Tint used by DWM under the WPF content (lighter for liquid glass).</summary>
        public static Color BlurTint(byte alpha = 0x30)
        {
            if (IsLiquid) alpha = (byte)(alpha / 2);
            return IsDark ? Color.FromArgb(alpha, 0x20, 0x20, 0x22) : Color.FromArgb(alpha, 0xF0, 0xF0, 0xF2);
        }

        /// <summary>Corner radius for floating panels in the current style.</summary>
        public static double PanelRadius(double classic) => (App.Settings?.LiquidGlass ?? false) ? Math.Round(classic * 1.6) : classic;
    }
}
