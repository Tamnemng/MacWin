using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MacWin.Services
{
    public enum DockPosition { Bottom, Left, Right }
    public enum AppearanceMode { Light, Dark, Auto }
    public enum CornerAction { None, MissionControl, Desktop, Launchpad, Spotlight, NotificationCenter, ControlCenter, LockScreen }

    /// <summary>A "Mac-style app" slot: a role (Browser, Mail, ...) bound to a real Windows app.</summary>
    public class RoleBinding
    {
        public string Role { get; set; }
        public string Label { get; set; }       // user-visible name (user may rename, e.g. "Safari")
        public string AppId { get; set; }       // catalog id (AUMID / path) or URL
        public bool MacIcon { get; set; } = true;
    }

    public class AppSettings
    {
        // General
        public bool StartAtLogin { get; set; } = true;
        public bool StartupChime { get; set; } = true;
        public bool BootAnimation { get; set; } = false;
        public bool HideTaskbar { get; set; } = true;
        public bool WinKeyLaunchpad { get; set; } = true;
        public bool MacShortcuts { get; set; } = true;
        public bool HideDesktopIcons { get; set; } = true;

        // Appearance
        public AppearanceMode Appearance { get; set; } = AppearanceMode.Light;
        public bool ControlWindowsTheme { get; set; } = true;
        public string Accent { get; set; } = "Blue";
        public bool RealBlur { get; set; } = true;
        public bool LiquidGlass { get; set; } = true;
        public MacWin.UI.WindowButtonsMode WindowButtons { get; set; } = MacWin.UI.WindowButtonsMode.Right;
        public double GlassClarity { get; set; } = 0.6;    // 0 = frosted, 1 = very clear
        public bool LiveGlass { get; set; } = true;         // glass keeps showing what is really behind it
        public bool LiveGlassSmooth { get; set; } = false;  // every frame (glass is left out of screenshots) vs 10×/s
        public bool ReduceMotion { get; set; } = false;

        // Dock
        public double DockSize { get; set; } = 50;
        public bool Magnification { get; set; } = true;
        public double MagnifiedSize { get; set; } = 78;
        public DockPosition DockPosition { get; set; } = DockPosition.Bottom;
        public bool DockAutoHide { get; set; } = false;
        public bool DockSmartHide { get; set; } = true;   // hide only while a window is maximised or overlaps the Dock
        public bool ShowIndicators { get; set; } = true;
        public bool BounceOnLaunch { get; set; } = true;
        public bool ShowRunningApps { get; set; } = true;
        public bool SquircleIcons { get; set; } = true;
        public bool ClickToMinimize { get; set; } = false;   // macOS: clicking the front app's icon does nothing
        public List<string> PinnedApps { get; set; } = null;

        // Mac-style app slots
        public List<RoleBinding> Roles { get; set; } = new();

        // Menu bar
        public bool ShowDate { get; set; } = true;
        public bool ShowSeconds { get; set; } = false;
        public bool Clock24h { get; set; } = true;
        public bool ShowBatteryPercent { get; set; } = true;
        public bool ShowTrayIcons { get; set; } = true;      // Windows notification-area icons in the menu bar
        public bool ShowInputSource { get; set; } = true;    // keyboard language (EN / VI …)
        public bool ShowStatsInMenuBar { get; set; } = true; // CPU % / RAM % (click for Activity)

        // Menu bar layout
        public bool ClockCentered { get; set; } = false;
        public string ClockFormat { get; set; } = "";         // custom .NET date format, empty = built-in style
        public bool ShowSpotlightIcon { get; set; } = true;
        public bool ShowControlCenterIcon { get; set; } = true;
        public bool ShowWifiIcon { get; set; } = true;
        public bool ShowBatteryIcon { get; set; } = true;
        public bool ShowVolumeIcon { get; set; } = true;
        public bool ShowDiskIcon { get; set; } = false;
        public bool CollapseTrayIcons { get; set; } = true;   // background-app icons behind an arrow
        public List<string> AlwaysShowTray { get; set; } = new(); // "app.exe" names kept outside the arrow

        // Control Center
        public List<string> ControlCenterButtons { get; set; } = new() { "darkmode", "calculator", "clock", "screenshot" };
        public bool ControlCenterNowPlaying { get; set; } = true;
        public bool ControlCenterDisplay { get; set; } = true;
        public bool ControlCenterSound { get; set; } = true;

        // Notifications
        public bool MacNotifications { get; set; } = true;     // read other apps' notifications (banners + Notification Center)
        public bool NotificationBanners { get; set; } = true;  // Mac-style banner at the top right
        public bool HideWindowsBanners { get; set; } = true;   // turn off Windows' own banner so there aren't two
        public bool DoNotDisturb { get; set; } = false;
        public double BannerSeconds { get; set; } = 5;
        public bool NotificationPreviews { get; set; } = true; // show the message text
        public Dictionary<string, int> BannerOriginals { get; set; } = new(); // app → original ShowBanner (-1 = none)

        // Hot corners
        public CornerAction CornerTopLeft { get; set; } = CornerAction.None;
        public CornerAction CornerTopRight { get; set; } = CornerAction.None;
        public CornerAction CornerBottomLeft { get; set; } = CornerAction.None;
        public CornerAction CornerBottomRight { get; set; } = CornerAction.Desktop;

        // Wallpaper
        public string Wallpaper { get; set; } = "Coast";
        public bool DynamicWallpaper { get; set; } = true;
        public string CustomWallpaper { get; set; }

        // Spotlight
        public string SearchEngine { get; set; } = "Google";

        // Restore information (captured the first time MacWin changes something)
        public int? OriginalTaskbarState { get; set; }
        public string OriginalWallpaper { get; set; }
        public int? OriginalAppsLight { get; set; }
        public int? OriginalSystemLight { get; set; }
        public bool? OriginalDesktopIconsVisible { get; set; }
        public bool FirstRunDone { get; set; }
        public int? SettingsVersion { get; set; }
        const int CurrentVersion = 4;

        // ---------- persistence ----------
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacWin");
        static string FilePath => Path.Combine(Folder, "settings.json");
        static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static AppSettings Load()
        {
            AppSettings s = null;
            try
            {
                if (File.Exists(FilePath))
                    s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
            }
            catch { }
            s ??= new AppSettings();
            s.Migrate();
            return s;
        }

        /// <summary>Applies new defaults to settings saved by older versions.</summary>
        void Migrate()
        {
            int v = SettingsVersion ?? 0;
            if (v < 2) HideDesktopIcons = true;   // v2: clean desktop by default
            if (v < 3) ClickToMinimize = false;   // v3: Dock clicks behave like macOS
            if (v < 4) LiveGlassSmooth = false;   // v4: panels stay capturable by default
            SettingsVersion = CurrentVersion;
        }

        public event Action Changed;

        public void Save(bool notify = true)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
            }
            catch { }
            if (notify) Changed?.Invoke();
        }
    }
}
