using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;
using MacWin.Native;
using Microsoft.Win32;

namespace MacWin.Services
{
    /// <summary>Changes MacWin makes to Windows itself — every one of them is reversible.</summary>
    public static class SystemTweaks
    {
        static DispatcherTimer _taskbarGuard;
        public static bool TaskbarHidden { get; private set; }

        // ---------------- Windows taskbar ----------------
        public static void HideTaskbar()
        {
            var s = App.Settings;
            if (s.OriginalTaskbarState == null)
            {
                s.OriginalTaskbarState = AppBar.GetTaskbarState();
                s.Save(false);
            }
            // auto-hide frees the reserved work area, then the bar itself is hidden
            AppBar.SetTaskbarState(AppBar.ABS_AUTOHIDE);
            TaskbarHidden = true;
            SetTrayVisible(false);
            if (_taskbarGuard == null)
            {
                // safety net behind the show-event hook; also re-asserts auto-hide if something reset the taskbar state
                _taskbarGuard = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
                _taskbarGuard.Tick += (_, _) =>
                {
                    if (!TaskbarHidden) return;
                    SetTrayVisible(false);
                    if (++_guardTicks % 6 == 0 && (AppBar.GetTaskbarState() & AppBar.ABS_AUTOHIDE) == 0) AppBar.SetTaskbarState(AppBar.ABS_AUTOHIDE);
                };
            }
            _taskbarGuard.Start();
        }

        static int _guardTicks;

        public static bool IsTaskbarWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            var cls = Win32.GetClass(hwnd);
            return cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
        }

        /// <summary>Immediate hide (called from the window-show event, before Explorer has painted the bar).</summary>
        public static void HideTaskbarNow()
        {
            if (TaskbarHidden) SetTrayVisible(false);
        }

        public static void RestoreTaskbar()
        {
            _taskbarGuard?.Stop();
            TaskbarHidden = false;
            SetTrayVisible(true);
            var s = App.Settings;
            AppBar.SetTaskbarState(s?.OriginalTaskbarState ?? AppBar.ABS_ALWAYSONTOP);
        }

        static void SetTrayVisible(bool visible)
        {
            var tray = Win32.FindExplorerTray();
            if (tray != IntPtr.Zero && Win32.IsWindowVisible(tray) != visible)
                Win32.ShowWindow(tray, visible ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);
            IntPtr sec = IntPtr.Zero;
            while ((sec = Win32.FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                if (Win32.IsWindowVisible(sec) != visible) Win32.ShowWindow(sec, visible ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);
        }

        // ---------------- light / dark ----------------
        const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public static void SetWindowsDark(bool dark)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
                var s = App.Settings;
                if (s.OriginalAppsLight == null)
                {
                    s.OriginalAppsLight = k.GetValue("AppsUseLightTheme") as int? ?? 1;
                    s.OriginalSystemLight = k.GetValue("SystemUsesLightTheme") as int? ?? 1;
                    s.Save(false);
                }
                int v = dark ? 0 : 1;
                if ((k.GetValue("AppsUseLightTheme") as int?) == v && (k.GetValue("SystemUsesLightTheme") as int?) == v) return;
                k.SetValue("AppsUseLightTheme", v, RegistryValueKind.DWord);
                k.SetValue("SystemUsesLightTheme", v, RegistryValueKind.DWord);
                Broadcast("ImmersiveColorSet");
            }
            catch { }
        }

        public static void RestoreWindowsTheme()
        {
            var s = App.Settings;
            if (s.OriginalAppsLight == null) return;
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
                k.SetValue("AppsUseLightTheme", s.OriginalAppsLight.Value, RegistryValueKind.DWord);
                k.SetValue("SystemUsesLightTheme", s.OriginalSystemLight ?? 1, RegistryValueKind.DWord);
                Broadcast("ImmersiveColorSet");
            }
            catch { }
        }

        static void Broadcast(string area) =>
            Win32.SendMessageTimeout(Win32.HWND_BROADCAST, Win32.WM_SETTINGCHANGE, IntPtr.Zero, area, 2 /*ABORTIFHUNG*/, 300, out _);

        // ---------------- desktop icons ----------------
        static IntPtr FindDesktopDefView()
        {
            IntPtr defView = Win32.FindWindowEx(Win32.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr worker = IntPtr.Zero;
            while (defView == IntPtr.Zero && (worker = Win32.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
                defView = Win32.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            return defView;
        }

        /// <summary>Shows or hides desktop icons (same as right-click desktop ▸ View ▸ Show desktop icons).
        /// Returns false when the desktop isn't ready yet (e.g. right after sign-in).</summary>
        public static bool SetDesktopIconsVisible(bool visible)
        {
            var defView = FindDesktopDefView();
            if (defView == IntPtr.Zero) return false;
            var lv = Win32.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            if (lv == IntPtr.Zero) return false;
            bool current = Win32.IsWindowVisible(lv);
            var s = App.Settings;
            if (!visible && s != null && s.OriginalDesktopIconsVisible == null)
            {
                s.OriginalDesktopIconsVisible = current;
                s.Save(false);
            }
            if (current != visible)
                Win32.SendMessage(defView, Win32.WM_COMMAND, new IntPtr(0x7402), IntPtr.Zero); // Explorer's "Show desktop icons" toggle
            return true;
        }

        static DispatcherTimer _iconRetry;

        /// <summary>Applies the desktop-icon setting, retrying for a while if Explorer's desktop isn't up yet.</summary>
        public static void ApplyDesktopIcons(bool hide)
        {
            _iconRetry?.Stop();
            if (SetDesktopIconsVisible(!hide)) return;
            int attempts = 0;
            _iconRetry = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _iconRetry.Tick += (_, _) =>
            {
                if (SetDesktopIconsVisible(!App.Settings.HideDesktopIcons) || ++attempts >= 15) _iconRetry.Stop();
            };
            _iconRetry.Start();
        }

        static void RestoreDesktopIcons()
        {
            _iconRetry?.Stop();
            SetDesktopIconsVisible(App.Settings?.OriginalDesktopIconsVisible ?? true);
        }

        // ---------------- start at login ----------------
        static bool? _lastStartAtLogin;

        /// <summary>Log-on task (fast) with a Run-key fallback; see <see cref="StartupRegistration"/>. Only touches the system when the value changes.</summary>
        public static void SetStartAtLogin(bool on)
        {
            if (_lastStartAtLogin == on) return;
            _lastStartAtLogin = on;
            StartupRegistration.Set(on);
        }

        // ---------------- wallpaper ----------------
        public static string GetWallpaper()
        {
            var sb = new StringBuilder(520);
            Win32.SystemParametersInfo(Win32.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0);
            return sb.ToString();
        }

        public static void SetWallpaper(string path)
        {
            var s = App.Settings;
            if (s.OriginalWallpaper == null)
            {
                s.OriginalWallpaper = GetWallpaper() ?? "";
                s.Save(false);
            }
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
                k.SetValue("WallpaperStyle", "10"); // Fill
                k.SetValue("TileWallpaper", "0");
            }
            catch { }
            Win32.SystemParametersInfo(Win32.SPI_SETDESKWALLPAPER, 0, path, Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE);
        }

        public static void RestoreWallpaper()
        {
            var o = App.Settings.OriginalWallpaper;
            if (o != null && (o.Length == 0 || File.Exists(o)))
                Win32.SystemParametersInfo(Win32.SPI_SETDESKWALLPAPER, 0, o, Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE);
        }

        // ---------------- Windows notification banners ----------------
        // While MacWin draws its own banners, Windows' banner is turned off per app ("Show notification banners" in
        // Settings): notifications still reach Notification Center, so MacWin can read them. Undone on quit.
        const string NotifyKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
        // interactive toasts (calls, alarms, snips) stay with Windows: their buttons only work there
        static readonly string[] KeepWindowsBanner = { "Calling", "WindowsAlarms", "ScreenSketch", "MacWin" };

        static bool ReplaceBanners => App.Settings is { MacNotifications: true, NotificationBanners: true, HideWindowsBanners: true };

        /// <summary>Turns off Windows' banner for every app that has posted notifications before.</summary>
        public static void ApplyWindowsBanners()
        {
            if (!ReplaceBanners || !Notifications.Running) { RestoreWindowsBanners(); return; }
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(NotifyKey);
                foreach (var app in k?.GetSubKeyNames() ?? Array.Empty<string>()) HideWindowsBanner(app, save: false);
            }
            catch (Exception e) { App.Log(e); }
            App.Settings.Save(false);
        }

        /// <summary>Turns off Windows' banner for one app (an app that just notified for the first time).</summary>
        public static void HideWindowsBanner(string aumid, bool save = true)
        {
            if (string.IsNullOrEmpty(aumid) || KeepWindowsBanner.Any(x => aumid.Contains(x, StringComparison.OrdinalIgnoreCase))) return;
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == false)
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => HideWindowsBanner(aumid, save));
                return;
            }
            var s = App.Settings;
            s.BannerOriginals ??= new();
            if (!ReplaceBanners || s.BannerOriginals.ContainsKey(aumid)) return;
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(NotifyKey + "\\" + aumid);
                var cur = k.GetValue("ShowBanner") as int?;
                if (cur == 0) return;                       // the user already turned it off: leave it alone
                s.BannerOriginals[aumid] = cur ?? -1;       // -1 = value didn't exist
                k.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
                if (save) s.Save(false);
            }
            catch (Exception e) { App.Log(e); }
        }

        public static void RestoreWindowsBanners()
        {
            var s = App.Settings;
            if (s?.BannerOriginals == null || s.BannerOriginals.Count == 0) return;
            foreach (var (aumid, orig) in s.BannerOriginals.ToList())
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(NotifyKey + "\\" + aumid, true);
                    if (k != null)
                    {
                        if (orig < 0) k.DeleteValue("ShowBanner", false);
                        else k.SetValue("ShowBanner", orig, RegistryValueKind.DWord);
                    }
                }
                catch (Exception e) { App.Log(e); }
            }
            s.BannerOriginals.Clear();
            s.Save(false);
        }

        /// <summary>Undo everything (used by Quit, uninstall and "MacWin.exe --restore").</summary>
        public static void RestoreAll(bool includeAppearance)
        {
            RestoreTaskbar();
            RestoreDesktopIcons();
            RestoreWindowsBanners();
            if (includeAppearance)
            {
                RestoreWallpaper();
                RestoreWindowsTheme();
            }
        }
    }
}
