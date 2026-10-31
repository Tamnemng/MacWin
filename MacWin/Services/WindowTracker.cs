using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using MacWin.Native;

namespace MacWin.Services
{
    public class AppWindow
    {
        public IntPtr Hwnd { get; init; }
        public string Title { get; set; }
        public string AppId { get; init; }
        public uint Pid { get; init; }
        public bool Minimized { get; set; }
    }

    /// <summary>
    /// Event-driven list of top-level app windows (the same set Alt+Tab shows), grouped by app.
    /// Refreshes are debounced, so the cost is near zero while nothing changes.
    /// </summary>
    public static class WindowTracker
    {
        public static List<AppWindow> Windows { get; private set; } = new();
        public static IntPtr LastExternalForeground { get; private set; }
        public static string ForegroundAppId { get; private set; }
        public static event Action Changed;
        public static event Action ForegroundChanged;

        static WinEvents _events;
        static DispatcherTimer _debounce, _safety;
        static readonly uint OwnPid = (uint)Environment.ProcessId;
        static readonly Dictionary<uint, (string exe, string aumid)> ProcCache = new();
        static readonly Dictionary<IntPtr, string> WindowAppCache = new();

        public static void Start()
        {
            _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
            _safety = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
            _safety.Tick += (_, _) => Refresh();
            _safety.Start();

            _events = new WinEvents();
            _events.Event += (ev, hwnd) =>
            {
                // Explorer showed its taskbar again (Win key, a toast, Explorer restart…): hide it right away
                if (ev == WinEvents.EVENT_OBJECT_SHOW && SystemTweaks.TaskbarHidden && SystemTweaks.IsTaskbarWindow(hwnd))
                {
                    SystemTweaks.HideTaskbarNow();
                    return;
                }
                if (ev == WinEvents.EVENT_SYSTEM_FOREGROUND) OnForeground(hwnd);
                if (ev == WinEvents.EVENT_OBJECT_NAMECHANGE)
                {
                    // title changes are frequent (browsers); only care about tracked windows
                    var w = Windows.FirstOrDefault(x => x.Hwnd == hwnd);
                    if (w == null) return;
                }
                _debounce.Stop();
                _debounce.Start();
            };
            Refresh();
            OnForeground(Win32.GetForegroundWindow());
        }

        public static void Stop()
        {
            _events?.Dispose();
            _safety?.Stop();
        }

        public static void RequestRefresh()
        {
            _debounce?.Stop();
            _debounce?.Start();
        }

        static void OnForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == OwnPid) return;
            LastExternalForeground = hwnd;
            string cls = Win32.GetClass(hwnd);
            ForegroundAppId = cls is "Progman" or "WorkerW" or "Shell_TrayWnd" ? "desktop" : IdentifyWindow(hwnd, pid, cls);
            ForegroundChanged?.Invoke();
        }

        static bool IsAppWindow(IntPtr h)
        {
            if (!Win32.IsWindowVisible(h)) return false;
            long ex = Win32.GetExStyle(h);
            bool appWindow = (ex & Win32.WS_EX_APPWINDOW) != 0;
            if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
            if ((ex & Win32.WS_EX_NOACTIVATE) != 0 && !appWindow) return false;
            if (Win32.GetWindow(h, Win32.GW_OWNER) != IntPtr.Zero && !appWindow) return false;
            if (Win32.GetWindowTextLength(h) == 0) return false;
            if (Win32.IsCloaked(h)) return false;
            return true;
        }

        public static void Refresh()
        {
            var list = new List<AppWindow>();
            var alive = new HashSet<IntPtr>();
            Win32.EnumWindows((h, _) =>
            {
                if (!IsAppWindow(h)) return true;
                Win32.GetWindowThreadProcessId(h, out uint pid);
                if (pid == OwnPid) return true;
                string cls = Win32.GetClass(h);
                if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return true;
                alive.Add(h);
                list.Add(new AppWindow
                {
                    Hwnd = h,
                    Pid = pid,
                    Title = Win32.GetText(h),
                    AppId = IdentifyWindow(h, pid, cls),
                    Minimized = Win32.IsIconic(h)
                });
                return true;
            }, IntPtr.Zero);

            foreach (var dead in WindowAppCache.Keys.Where(k => !alive.Contains(k)).ToList()) WindowAppCache.Remove(dead);
            if (ProcCache.Count > 256) ProcCache.Clear();

            bool changed = list.Count != Windows.Count || list.Where((w, i) =>
                w.Hwnd != Windows[i].Hwnd || w.AppId != Windows[i].AppId || w.Minimized != Windows[i].Minimized || w.Title != Windows[i].Title).Any();
            Windows = list;
            if (changed) Changed?.Invoke();
        }

        static (string exe, string aumid) Proc(uint pid)
        {
            if (!ProcCache.TryGetValue(pid, out var p))
            {
                p = (Win32.GetProcessPath(pid), Win32.GetProcessAumid(pid));
                ProcCache[pid] = p;
            }
            return p;
        }

        public static string IdentifyWindow(IntPtr h, uint pid, string cls)
        {
            if (WindowAppCache.TryGetValue(h, out var cached)) return cached;
            var (exe, procAumid) = Proc(pid);
            string aumid = ShellInterop.GetWindowAumid(h);

            if (aumid == null && cls == "ApplicationFrameWindow")
            {
                // UWP: the real app lives in a child CoreWindow owned by another process
                Win32.EnumChildWindows(h, (c, _) =>
                {
                    Win32.GetWindowThreadProcessId(c, out uint cpid);
                    if (cpid != pid)
                    {
                        var cp = Proc(cpid);
                        aumid = cp.aumid;
                        exe = cp.exe;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            }
            aumid ??= procAumid;
            if (aumid == null && cls == "CabinetWClass") aumid = "Microsoft.Windows.Explorer";

            string id = AppCatalog.Identify(aumid, exe);
            if (id != null && AppCatalog.Apps.Count > 0) WindowAppCache[h] = id;
            return id;
        }

        public static void ClearCache() => WindowAppCache.Clear();

        public static List<AppWindow> WindowsOf(string appId) =>
            appId == null ? new List<AppWindow>() : Windows.Where(w => string.Equals(w.AppId, appId, StringComparison.OrdinalIgnoreCase)).ToList();

        // ---------- actions ----------
        public static void Activate(AppWindow w)
        {
            if (w == null) return;
            if (Win32.IsIconic(w.Hwnd)) Win32.ShowWindow(w.Hwnd, Win32.SW_RESTORE);
            Input.Activate(w.Hwnd);
        }

        public static void Minimize(IntPtr h) => Win32.PostMessage(h, Win32.WM_SYSCOMMAND, new IntPtr(Win32.SC_MINIMIZE), IntPtr.Zero);
        public static void Close(IntPtr h) => Win32.PostMessage(h, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        public static void ToggleZoom(IntPtr h) =>
            Win32.PostMessage(h, Win32.WM_SYSCOMMAND, new IntPtr(Win32.IsZoomed(h) ? Win32.SC_RESTORE : Win32.SC_MAXIMIZE), IntPtr.Zero);

        public static void QuitApp(string appId)
        {
            foreach (var w in WindowsOf(appId)) Close(w.Hwnd);
        }

        public static void HideApp(string appId)
        {
            foreach (var w in WindowsOf(appId)) Minimize(w.Hwnd);
        }

        public static void HideOthers(string appId)
        {
            foreach (var w in Windows.Where(x => !string.Equals(x.AppId, appId, StringComparison.OrdinalIgnoreCase))) Minimize(w.Hwnd);
        }

        public static void ForceQuit(uint pid)
        {
            try { Process.GetProcessById((int)pid).Kill(true); } catch { }
        }
    }
}
