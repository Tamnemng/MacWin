using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacWin.Native;
using MacWin.Services;

namespace MacWin.UI
{
    /// <summary>
    /// In liquid-glass mode every context menu gets real blur behind it. The blur lives in a separate
    /// click-through underlay clipped to the menu's continuous shape, so the menu's own edge stays anti-aliased.
    /// </summary>
    public static class GlassPopups
    {
        static bool _registered;
        static BlurUnderlay _underlay;
        static ContextMenu _current;

        /// <summary>
        /// Disabled: DWM blur fills the whole popup rectangle, which showed as square corners around rounded menus.
        /// Menus now use a near-opaque glass tint with an anti-aliased rim instead.
        /// </summary>
        public static void Register()
        {
            _registered = true;
        }

        static void OnOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu cm) return;
            _current = cm;
            cm.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Apply(cm));
        }

        static void OnClosed(object sender, RoutedEventArgs e)
        {
            if (sender == _current) { _current = null; _underlay?.Hide(); }
        }

        static void Apply(ContextMenu cm)
        {
            try
            {
                if (cm != _current || !cm.IsOpen) return;
                if (!(App.Settings?.LiquidGlass ?? false) || !(App.Settings?.RealBlur ?? false)) { _underlay?.Hide(); return; }
                if (PresentationSource.FromVisual(cm) is not HwndSource src || src.RootVisual == null) return;
                if (cm.Template?.FindName("Frame", cm) is not ContinuousBorder frame || frame.ActualWidth < 1) return;
                double s = VisualTreeHelper.GetDpi(cm).DpiScaleX;
                var tl = frame.PointToScreen(new Point(0, 0));
                int w = (int)Math.Round(frame.ActualWidth * s), h = (int)Math.Round(frame.ActualHeight * s);
                _underlay ??= new BlurUnderlay(0x30);
                _underlay.Place(src.Handle, (int)Math.Round(tl.X), (int)Math.Round(tl.Y), w, h, frame.CornerRadius * s, true);
            }
            catch { }
        }
    }
}
