using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace MacWin.Services
{
    /// <summary>
    /// Self-installer: the same single EXE is the setup program, the app and the uninstaller.
    /// Installs per-user (no admin rights) to %LocalAppData%\Programs\MacWin.
    /// </summary>
    public static class Installer
    {
        public const string AppName = "MacWin";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MacWin";

        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        public static string InstallPath => Path.Combine(InstallDir, "MacWin.exe");
        public static string InstalledExe => File.Exists(InstallPath) ? InstallPath : null;

        public static bool RunningFromInstallDir =>
            string.Equals(Path.GetDirectoryName(Environment.ProcessPath)?.TrimEnd('\\'), InstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        public static string Version => typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MacWin.lnk");
        static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MacWin.lnk");

        public static void StopRunningInstances()
        {
            int me = Environment.ProcessId;
            var others = Process.GetProcessesByName("MacWin").Where(p => p.Id != me).ToList();
            if (others.Count == 0) return;
            // ask the running shell to quit gracefully (it restores the taskbar on the way out)
            try { System.Threading.EventWaitHandle.OpenExisting(App.QuitEventName).Set(); } catch { }
            foreach (var p in others)
            {
                try { if (!p.WaitForExit(4000)) { p.Kill(); p.WaitForExit(3000); } }
                catch { }
            }
        }

        public static void Install(bool desktopShortcut, bool startMenu, bool startAtLogin, Action<double, string> progress)
        {
            progress(0.05, "Preparing…");
            StopRunningInstances();
            // a previous instance may have hidden the taskbar; it restores on exit, but be safe
            progress(0.2, "Copying files…");
            Directory.CreateDirectory(InstallDir);
            string src = Environment.ProcessPath;
            if (!string.Equals(src, InstallPath, StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; ; i++)
                {
                    try { File.Copy(src, InstallPath, true); break; }
                    catch when (i < 10) { System.Threading.Thread.Sleep(300); }
                }
            }

            progress(0.55, "Creating shortcuts…");
            if (startMenu) CreateShortcut(StartMenuLink, InstallPath, "", "macOS-style desktop for Windows");
            if (desktopShortcut) CreateShortcut(DesktopLink, InstallPath, "", "macOS-style desktop for Windows");

            progress(0.75, "Registering…");
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "MacWin");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "MacWin");
                k.SetValue("DisplayIcon", InstallPath);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", $"\"{InstallPath}\" --uninstall");
                k.SetValue("QuietUninstallString", $"\"{InstallPath}\" --uninstall --quiet");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(InstallPath).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }

            StartupRegistration.Set(startAtLogin);
            progress(1.0, "Done");
        }

        public static void Uninstall(bool removeSettings)
        {
            StopRunningInstances();
            StartupRegistration.Remove();
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            foreach (var l in new[] { StartMenuLink, DesktopLink }) try { if (File.Exists(l)) File.Delete(l); } catch { }
            if (removeSettings) try { Directory.Delete(AppSettings.Folder, true); } catch { }

            // the running exe cannot delete itself: let a short-lived cmd do it after we exit
            string cmd = $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"";
            try { Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() }); } catch { }
        }

        static void CreateShortcut(string link, string target, string args, string description)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(link));
                var t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(t);
                dynamic sc = shell.CreateShortcut(link);
                sc.TargetPath = target;
                sc.Arguments = args;
                sc.WorkingDirectory = Path.GetDirectoryName(target);
                sc.Description = description;
                sc.IconLocation = target + ",0";
                sc.Save();
                System.Runtime.InteropServices.Marshal.ReleaseComObject(sc);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
            catch { }
        }
    }
}
