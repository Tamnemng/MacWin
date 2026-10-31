using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MacWin.Native;
using MacWin.Services;
using MacWin.UI;
using Microsoft.Win32;

namespace MacWin
{
    public partial class App : Application
    {
        public const string QuitEventName = @"Local\MacWin.Quit";
        const string ActivateEventName = @"Local\MacWin.Activate";

        public static AppSettings Settings { get; private set; }
        public static bool FullscreenAppActive { get; private set; }
        public static DockWindow Dock { get; private set; }

        static MenuBarWindow _menuBar;
        static LaunchpadWindow _launchpad;
        static SpotlightWindow _spotlight;
        static ControlCenterWindow _controlCenter;
        static SettingsWindow _settings;
        static HotKeys _hotKeys;
        static WinKeyTap _winTap;
        static Mutex _mutex;
        static DispatcherTimer _autoAppearance;
        static string _lastWallpaperKey;
        static bool _shellRunning, _quitting;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();
            _atLogon = args.Contains("--startup");
            Settings = AppSettings.Load();
            LogInfo($"Process start: {SinceProcessStart()} ms to OnStartup{(_atLogon ? " (log-on)" : "")}");
            ThemeManager.Apply(ThemeManager.ResolveDark(Settings), Settings.Accent);
            _lastLiquid = Settings.LiquidGlass;
            GlassPopups.Register();

            DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (_, ex) => { Log(ex.ExceptionObject as Exception); EmergencyRestore(); };
            TaskScheduler.UnobservedTaskException += (_, ex) => { Log(ex.Exception); ex.SetObserved(); };

            if (args.Contains("--restore"))
            {
                SystemTweaks.RestoreAll(includeAppearance: true);
                Shutdown();
                return;
            }
            if (args.Contains("--uninstall"))
            {
                if (args.Contains("--quiet"))
                {
                    Installer.StopRunningInstances();
                    SystemTweaks.RestoreAll(true);
                    Installer.Uninstall(false);
                    Shutdown();
                    return;
                }
                new SetupWindow(SetupMode.Uninstall).Show();
                return;
            }
            if (!Installer.RunningFromInstallDir && !args.Contains("--portable"))
            {
                new SetupWindow(SetupMode.Install).Show();
                return;
            }

            _mutex = new Mutex(true, @"Local\MacWin.Shell", out bool created);
            if (!created)
            {
                if (OtherInstanceHung())
                {
                    // the running shell is frozen: replace it instead of silently doing nothing
                    LogInfo("Earlier instance is not responding: replacing it");
                    Installer.StopRunningInstances();
                    try { SystemTweaks.RestoreAll(false); } catch { }
                    _mutex.Dispose();
                    _mutex = new Mutex(true, @"Local\MacWin.Shell", out created);
                }
                if (!created)
                {
                    // launched by hand: bring up Settings. Launched at log-on (e.g. both the task and an old Run entry fired): just leave.
                    if (!_atLogon) try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch { }
                    Shutdown();
                    return;
                }
            }
            ListenForSignals();
            if (_atLogon) _ = StartAfterExplorerAsync();
            else StartShell();
        }

        static bool _atLogon;

        static long SinceProcessStart()
        {
            try { return (long)(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds; } catch { return -1; }
        }

        /// <summary>
        /// The log-on task fires before Explorer has finished building the desktop. The taskbar (AppBar
        /// registration, tray) and shell:AppsFolder need Explorer, so wait for its tray window — briefly.
        /// </summary>
        static async Task StartAfterExplorerAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Win32.FindExplorerTray() == IntPtr.Zero && DateTime.UtcNow < deadline) await Task.Delay(250);
            LogInfo($"Explorer ready: {SinceProcessStart()} ms");
            StartShell();
        }

        // ================================================================== shell lifecycle

        static void StartShell()
        {
            if (Settings.BootAnimation)
            {
                if (Settings.StartupChime) Chime.Play();
                new SplashWindow(BuildShell).Show();
            }
            else
            {
                if (Settings.StartupChime) Chime.Play();
                BuildShell();
            }
        }

        static async void BuildShell()
        {
            _shellRunning = true;
            // read the installed-app list first, in the background, so search works even if a later step fails
            var catalog = LoadCatalogWithRetry();

            // each step is isolated: one failure is logged instead of stopping everything after it
            Step("system settings", ApplySystemSettings);   // hide the taskbar first so the Dock can reserve its space
            if (Settings.ShowTrayIcons) Step("tray icons", TrayHost.Start);
            Step("menu bar", () => { _menuBar = new MenuBarWindow(); _menuBar.Show(); });
            Step("dock", () => { Dock = new DockWindow(); Dock.Show(); });
            Step("window tracker", WindowTracker.Start);
            Step("fullscreen watch", StartFullscreenWatch);
            Step("hot corners", HotCorners.Start);
            Step("glass backdrop", WallpaperBackdrop.Start);
            Step("window buttons", WindowButtons.Start);
            Step("system stats", SystemStats.Start);
            Step("shortcuts", RegisterShortcuts);
            Step("notifications", ApplyNotifications);
            LogInfo($"Shell visible: {SinceProcessStart()} ms");

            await catalog;
            Roles.AutoBind();
            if (Settings.PinnedApps == null || Settings.PinnedApps.Count == 0) Settings.PinnedApps = Roles.DefaultDock();
            Settings.FirstRunDone = true;
            Settings.Save(false);
            WindowTracker.ClearCache();
            WindowTracker.Refresh();
            Dock?.Rebuild();
            LogInfo($"Dock ready: {SinceProcessStart()} ms");

            // later reloads (an app was installed / uninstalled): rebind slots and refresh the Dock
            AppCatalog.Watch();
            AppCatalog.Loaded += () => Current.Dispatcher.BeginInvoke(() =>
            {
                Roles.AutoBind();
                Settings.Save(false);
                WindowTracker.ClearCache();
                WindowTracker.Refresh();
                Dock?.Rebuild();
            });

            Settings.Changed += OnSettingsChanged;
            SystemEvents.SessionEnding += (_, _) => RestoreForExit(false);
            SystemEvents.DisplaySettingsChanged += (_, _) => Current.Dispatcher.BeginInvoke(() => { Dock?.ApplySettings(); });

            _autoAppearance = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
            _autoAppearance.Tick += (_, _) => { if (Settings.Appearance == AppearanceMode.Auto) ApplyAppearance(); };
            _autoAppearance.Start();

            // drop start-up allocations so the resident footprint stays small
            await Task.Delay(4000);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false, true);
            TrimWorkingSet();
        }

        /// <summary>True when another MacWin process exists and none of its windows answer messages.</summary>
        static bool OtherInstanceHung()
        {
            try
            {
                int me = Environment.ProcessId;
                var others = System.Diagnostics.Process.GetProcessesByName("MacWin").Where(p => p.Id != me).ToList();
                if (others.Count == 0) return false;
                bool anyWindow = false, anyAlive = false;
                Win32.EnumWindows((h, _) =>
                {
                    Win32.GetWindowThreadProcessId(h, out uint pid);
                    if (others.Any(p => p.Id == pid))
                    {
                        anyWindow = true;
                        if (Win32.SendMessageTimeout(h, 0 /*WM_NULL*/, IntPtr.Zero, null, 2 /*ABORTIFHUNG*/ | 8 /*NOTIMEOUTIFNOTHUNG*/, 1500, out _) != IntPtr.Zero) anyAlive = true;
                    }
                    return !anyAlive;
                }, IntPtr.Zero);
                return anyWindow && !anyAlive;
            }
            catch { return false; }
        }

        static void Step(string name, Action a)
        {
            try { a(); }
            catch (Exception e) { Log(new Exception("Start-up step failed: " + name, e)); }
        }

        /// <summary>Loads the app list; retries a few times (e.g. right after sign-in the shell may not be ready).</summary>
        static async Task LoadCatalogWithRetry()
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    await AppCatalog.LoadAsync();
                    LogInfo($"App list loaded: {AppCatalog.Apps.Count} apps (attempt {attempt})");
                    if (AppCatalog.Apps.Count > 0) return;
                }
                catch (Exception e) { Log(new Exception($"App list load failed (attempt {attempt})", e)); }
                await Task.Delay(3000 * attempt);
            }
        }

        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);
        static void TrimWorkingSet()
        {
            try { EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
        }

        static bool _lastLiquid;

        static void OnSettingsChanged()
        {
            if (Settings.LiquidGlass != _lastLiquid)
            {
                // panel shapes (corner radius, layout) are built at creation: rebuild them lazily in the new style
                _lastLiquid = Settings.LiquidGlass;
                _spotlight?.Close(); _spotlight = null;
                _controlCenter?.Close(); _controlCenter = null;
                _launchpad?.Close(); _launchpad = null;
                _notificationCenter?.Close(); _notificationCenter = null;
            }
            ApplySystemSettings();
            IconProvider.Invalidate();
            _menuBar?.ApplySettings();
            Dock?.ApplySettings();
            Dock?.ReloadIcons();
            RegisterShortcuts();
            WallpaperBackdrop.Refresh();
            WindowButtons.Apply();
            ApplyNotifications();
            if (Settings.ShowTrayIcons && !TrayHost.Running) TrayHost.Start();
            else if (!Settings.ShowTrayIcons && TrayHost.Running) TrayHost.Stop();
        }

        static void ApplyAppearance()
        {
            bool dark = ThemeManager.ResolveDark(Settings);
            ThemeManager.Apply(dark, Settings.Accent);
            if (Settings.ControlWindowsTheme) SystemTweaks.SetWindowsDark(dark);
            string key = $"{Settings.Wallpaper}|{Settings.CustomWallpaper}|{(Settings.DynamicWallpaper && dark)}";
            if (key != _lastWallpaperKey)
            {
                _lastWallpaperKey = key;
                _ = Wallpapers.ApplyCurrentAsync();
            }
        }

        static void ApplySystemSettings()
        {
            if (Settings.HideTaskbar) SystemTweaks.HideTaskbar();
            else if (SystemTweaks.TaskbarHidden) SystemTweaks.RestoreTaskbar();
            SystemTweaks.ApplyDesktopIcons(Settings.HideDesktopIcons);
            if (Installer.RunningFromInstallDir) SystemTweaks.SetStartAtLogin(Settings.StartAtLogin);
            ApplyAppearance();
        }

        static void RegisterShortcuts()
        {
            _hotKeys ??= new HotKeys();
            _hotKeys.Clear();
            if (Settings.MacShortcuts)
            {
                _hotKeys.Register(Win32.MOD_ALT, Win32.VK_SPACE, ToggleSpotlight);
                _hotKeys.Register(Win32.MOD_CONTROL | Win32.MOD_ALT, Win32.VK_SPACE, ToggleControlCenter);
                _hotKeys.Register(Win32.MOD_ALT, 0x51 /*Q*/, () => WindowTracker.QuitApp(WindowTracker.ForegroundAppId));
            }
            if (Settings.WinKeyLaunchpad && _winTap == null) _winTap = new WinKeyTap(ToggleLaunchpad);
            else if (!Settings.WinKeyLaunchpad && _winTap != null) { _winTap.Dispose(); _winTap = null; }
        }

        static void ListenForSignals()
        {
            var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
            var t = new Thread(() =>
            {
                var handles = new WaitHandle[] { activate, quit };
                while (true)
                {
                    int i = WaitHandle.WaitAny(handles);
                    if (i == 0) Current.Dispatcher.BeginInvoke(() => ShowSettings());
                    else { Current.Dispatcher.Invoke(() => Quit()); return; }
                }
            }) { IsBackground = true, Name = "MacWin.Signals" };
            t.Start();
        }

        static DispatcherTimer _fullscreenTimer;

        static void StartFullscreenWatch()
        {
            WindowTracker.ForegroundChanged += CheckFullscreen;
            _fullscreenTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
            _fullscreenTimer.Tick += (_, _) => CheckFullscreen();
            _fullscreenTimer.Start();
        }

        /// <summary>
        /// Hides the menu bar and Dock while the front window covers the whole primary screen
        /// (videos, games, browser F11) and shows them again as soon as it doesn't.
        /// </summary>
        public static void CheckFullscreen()
        {
            var fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            Win32.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId) return; // our own Launchpad / Spotlight: keep state
            SetFullscreen(IsFullscreenWindow(fg));
        }

        static bool IsFullscreenWindow(IntPtr h)
        {
            if (!Win32.IsWindowVisible(h) || Win32.IsIconic(h) || Win32.IsCloaked(h)) return false;
            string cls = Win32.GetClass(h);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
            if (!Win32.GetWindowRect(h, out var r)) return false;
            // full screen on a secondary monitor doesn't touch our bars on the main one
            if (!Win32.IsOnPrimaryMonitor(h)) return false;
            var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (!Win32.GetMonitorInfo(Win32.MonitorFromWindow(h, 2 /*NEAREST*/), ref mi)) return false;
            var m = mi.rcMonitor;
            // maximised windows stop at the work area (below the menu bar), so only true full screen matches
            return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        }

        public static void SetFullscreen(bool full)
        {
            if (FullscreenAppActive == full) return;
            FullscreenAppActive = full;
            if (_menuBar != null) _menuBar.Visibility = full ? Visibility.Hidden : Visibility.Visible;
            Dock?.SetFullscreen(full);
        }

        public static void RaiseDock()
        {
            if (Dock == null) return;
            var h = Ui.Hwnd(Dock);
            Win32.SetWindowPos(h, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }

        static void RestoreForExit(bool includeAppearance)
        {
            try
            {
                _menuBar?.Unregister();
                Dock?.Unregister();
                SystemTweaks.RestoreAll(includeAppearance);
            }
            catch (Exception e) { Log(e); }
        }

        static void EmergencyRestore()
        {
            try { if (_shellRunning) SystemTweaks.RestoreAll(false); } catch { }
        }

        public static void Quit(bool restoreAppearance = false)
        {
            if (_quitting) return;
            _quitting = true;
            RestoreForExit(restoreAppearance);
            Notifications.Stop();
            TrayHost.Stop();   // apps fall back to Explorer's own tray, which received every icon all along
            WindowButtons.Stop();
            WindowTracker.Stop();
            HotCorners.Stop();
            _hotKeys?.Dispose();
            _winTap?.Dispose();
            Current.Shutdown();
        }

        public static void StartUninstall()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath, "--uninstall") { UseShellExecute = true }); } catch { }
        }

        // ================================================================== panels

        public static void ToggleLaunchpad()
        {
            _launchpad ??= new LaunchpadWindow();
            if (_launchpad.IsVisible && _launchpad.Opacity > 0.5) _launchpad.CloseAnimated();
            else { _spotlight?.HideAnimated(); _launchpad.ShowAnimated(); }
        }

        public static void ToggleSpotlight()
        {
            _spotlight ??= new SpotlightWindow();
            if (_spotlight.IsOpen) _spotlight.HideAnimated();
            else if (!_spotlight.JustAutoHidden) _spotlight.ShowAnimated();
        }

        static ActivityWindow _activity;

        public static void ToggleActivity(FrameworkElement anchor = null)
        {
            _activity ??= new ActivityWindow();
            if (_activity.IsOpen) _activity.HideAnimated();
            else if (!_activity.JustAutoHidden) _activity.ShowAnimated(anchor);
        }

        /// <summary>Mac-style banners + Notification Center (reads other apps' notifications from Windows).</summary>
        static void ApplyNotifications()
        {
            if (Settings.MacNotifications)
            {
                Banners.Start();
                if (!Notifications.Running) Notifications.Start();
                else SystemTweaks.ApplyWindowsBanners();
            }
            else if (Notifications.Running)
            {
                Notifications.Stop();
                SystemTweaks.RestoreWindowsBanners();
            }
        }

        static NotificationCenterWindow _notificationCenter;
        public static bool NotificationCenterOpen => _notificationCenter?.IsOpen == true;

        /// <summary>Clicking the clock: MacWin's Notification Center, or Windows' own when reading isn't possible.</summary>
        public static void ToggleNotificationCenter()
        {
            if (!Settings.MacNotifications || !Notifications.Running) { Input.Chord(Win32.VK_LWIN, 0x4E /*N*/); return; }
            _notificationCenter ??= new NotificationCenterWindow();
            if (_notificationCenter.IsOpen) _notificationCenter.HideAnimated();
            else if (!_notificationCenter.JustAutoHidden) _notificationCenter.ShowAnimated();
        }

        public static void ToggleControlCenter() => ToggleControlCenter(null);

        /// <summary>Opens Control Center, optionally straight on the Wi-Fi or Sound page (menu bar icons).</summary>
        public static void ToggleControlCenter(string page)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    _controlCenter ??= new ControlCenterWindow();
                    LogInfo($"Control Center toggle: open={_controlCenter.IsOpen} visible={_controlCenter.IsVisible} opacity={_controlCenter.Opacity:0.00} autoHidden={_controlCenter.JustAutoHidden}");
                    if (_controlCenter.IsOpen) _controlCenter.HideAnimated();
                    else if (!_controlCenter.JustAutoHidden) _controlCenter.ShowAnimated(page);
                    return;
                }
                catch (Exception e)
                {
                    Log(new Exception("Control Center failed to open; rebuilding it", e));
                    try { _controlCenter?.Close(); } catch { }
                    _controlCenter = null;
                }
            }
        }

        public static void ShowSettings(string page = null)
        {
            if (_settings == null)
            {
                _settings = new SettingsWindow();
                _settings.Closed += (_, _) => { _settings = null; TrimWorkingSet(); };
                _settings.Show();
            }
            if (page != null) _settings.Show(page);
            if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
            Ui.ForceActivate(_settings);
        }

        public static void ShowAbout() { var w = new AboutWindow(); w.Show(); Ui.ForceActivate(w); }
        public static void ShowForceQuit() { var w = new ForceQuitWindow(); w.Show(); Ui.ForceActivate(w); }
        public static void ShowAppInfo(string appId) { var w = new AppInfoWindow(appId); w.Show(); Ui.ForceActivate(w); }

        public static void ToggleDarkMode()
        {
            Settings.Appearance = ThemeManager.IsDark ? AppearanceMode.Light : AppearanceMode.Dark;
            Settings.Save();
        }

        /// <summary>Called after a role's target or label changes.</summary>
        public static void RefreshRoleUi()
        {
            IconProvider.Invalidate();
            WindowTracker.ClearCache();
            WindowTracker.Refresh();
            Dock?.Rebuild();
            Dock?.ReloadIcons();
        }

        /// <summary>Lets the user pick their own image for a Dock slot.</summary>
        public static bool ChooseIcon(string roleKey)
        {
            var dlg = new OpenFileDialog { Title = "Choose an icon", Filter = "Images|*.png;*.ico;*.jpg;*.jpeg;*.bmp|All files|*.*" };
            if (dlg.ShowDialog() != true) return false;
            IconProvider.SetUserIcon(roleKey, dlg.FileName);
            Dock?.ReloadIcons();
            return true;
        }

        public static void LogInfo(string message)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.AppendAllText(Path.Combine(AppSettings.Folder, "log.txt"), $"[{DateTime.Now:u}] {message}\n");
            }
            catch { }
        }

        public static void Log(Exception e)
        {
            if (e == null) return;
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.AppendAllText(Path.Combine(AppSettings.Folder, "log.txt"), $"[{DateTime.Now:u}] {e}\n\n");
            }
            catch { }
        }
    }
}
