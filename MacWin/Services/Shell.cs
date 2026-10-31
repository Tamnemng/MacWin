using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MacWin.Native;

namespace MacWin.Services
{
    /// <summary>System actions shared by the menu bar, Dock, Spotlight, hot corners and shortcuts.</summary>
    public static class Shell
    {
        public static void Open(string target, string args = null)
        {
            if (string.IsNullOrEmpty(target)) return;
            try { Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true }); } catch { }
        }

        public static void Reveal(string path)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
        }

        public static void MissionControl() => Input.Chord(Win32.VK_LWIN, Win32.VK_TAB);
        public static void ShowDesktop() => Input.Chord(Win32.VK_LWIN, 0x44 /*D*/);
        public static void NotificationCenter() => App.ToggleNotificationCenter();
        public static void WindowsNotificationCenter() => Input.Chord(Win32.VK_LWIN, 0x4E /*N*/);
        public static void QuickSettings() => Input.Chord(Win32.VK_LWIN, 0x41 /*A*/);
        public static void Screenshot() => Input.Chord(Win32.VK_LWIN, Win32.VK_SHIFT, 0x53 /*S*/);
        public static void Cast() => Input.Chord(Win32.VK_LWIN, 0x4B /*K*/);
        public static void EmojiPanel() => Input.Chord(Win32.VK_LWIN, 0xBE /*.*/);
        public static void Lock() => Win32.LockWorkStation();
        public static void Sleep() => Win32.SetSuspendState(false, false, false);
        public static void Restart() => Open("shutdown.exe", "/r /t 0");
        public static void ShutDown() => Open("shutdown.exe", "/s /t 0");
        public static void LogOut() => Win32.ExitWindowsEx(0, 0);
        public static void TaskManager() => Open("taskmgr.exe");
        public static void EmptyTrash() => Win32.SHEmptyRecycleBin(IntPtr.Zero, null, 0);
        public static void OpenTrash() => Open("explorer.exe", "shell:RecycleBinFolder");

        /// <summary>Sends a keyboard shortcut to the app the user was working in (menu bar File/Edit/View commands).</summary>
        public static async void SendToApp(params ushort[] keys)
        {
            var target = WindowTracker.LastExternalForeground;
            if (target == IntPtr.Zero) return;
            if (Win32.GetForegroundWindow() != target)
            {
                Input.Activate(target);
                await Task.Delay(60);
            }
            Input.Chord(keys);
        }

        public static void Run(CornerAction a)
        {
            switch (a)
            {
                case CornerAction.MissionControl: MissionControl(); break;
                case CornerAction.Desktop: ShowDesktop(); break;
                case CornerAction.Launchpad: App.ToggleLaunchpad(); break;
                case CornerAction.Spotlight: App.ToggleSpotlight(); break;
                case CornerAction.NotificationCenter: NotificationCenter(); break;
                case CornerAction.ControlCenter: App.ToggleControlCenter(); break;
                case CornerAction.LockScreen: Lock(); break;
            }
        }
    }
}
