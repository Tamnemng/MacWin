using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MacWin.Services
{
    /// <summary>
    /// "Open MacWin when you log in". Preferred: a per-user Task Scheduler log-on task, which fires the
    /// moment the session starts. The classic HKCU\...\Run entry is only a fallback: Explorer launches
    /// those one after another, well after the desktop appears, so with many start-up apps MacWin could
    /// take minutes to show up. Never leave both in place or the shell would start twice.
    /// </summary>
    public static class StartupRegistration
    {
        public const string TaskName = "MacWin";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "MacWin";

        public static void Set(bool on)
        {
            string exe = Installer.InstalledExe ?? Environment.ProcessPath;
            if (!on) { Remove(); return; }
            if (RegisterTask(exe)) DeleteRunValue();
            else SetRunValue(exe);   // Task Scheduler unavailable (service disabled, policy): keep the old behaviour
        }

        public static void Remove()
        {
            DeleteTask();
            DeleteRunValue();
        }

        public static bool IsEnabled()
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); if (k?.GetValue(RunValue) != null) return true; } catch { }
            return TaskExists();
        }

        // ---------------- Task Scheduler (Schedule.Service COM, no admin needed for a task that runs as the current user)

        static bool RegisterTask(string exe)
        {
            dynamic service = null;
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                service.Connect();
                dynamic folder = service.GetFolder("\\");
                dynamic def = service.NewTask(0);

                def.RegistrationInfo.Description = "Starts the MacWin desktop when you sign in.";
                def.RegistrationInfo.Author = "MacWin";

                string user = Environment.UserDomainName + "\\" + Environment.UserName;
                dynamic trigger = def.Triggers.Create(9 /* TASK_TRIGGER_LOGON */);
                trigger.UserId = user;
                trigger.Id = "Logon";

                dynamic action = def.Actions.Create(0 /* TASK_ACTION_EXEC */);
                action.Path = exe;
                action.Arguments = "--startup";
                action.WorkingDirectory = System.IO.Path.GetDirectoryName(exe);

                var s = def.Settings;
                s.Enabled = true;
                s.Hidden = false;
                s.Priority = 5;                             // Normal-ish; scheduled tasks default to Below Normal (7)
                s.DisallowStartIfOnBatteries = false;
                s.StopIfGoingOnBatteries = false;
                s.StartWhenAvailable = true;
                s.RunOnlyIfNetworkAvailable = false;
                s.ExecutionTimeLimit = "PT0S";              // never kill the shell
                s.MultipleInstances = 2;                    // TASK_INSTANCES_IGNORE_NEW
                s.AllowDemandStart = true;
                s.RunOnlyIfIdle = false;
                s.WakeToRun = false;

                def.Principal.UserId = user;
                def.Principal.LogonType = 3;                // TASK_LOGON_INTERACTIVE_TOKEN: runs only while the user is signed in
                def.Principal.RunLevel = 0;                 // TASK_RUNLEVEL_LUA: no elevation

                folder.RegisterTaskDefinition(TaskName, def, 6 /* TASK_CREATE_OR_UPDATE */, null, null, 3, null);
                return true;
            }
            catch (Exception e)
            {
                App.Log(new Exception("Could not create the log-on task; falling back to the Run key", e));
                return false;
            }
            finally
            {
                if (service != null) try { Marshal.ReleaseComObject(service); } catch { }
            }
        }

        static bool TaskExists()
        {
            try
            {
                dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                service.Connect();
                dynamic folder = service.GetFolder("\\");
                try { folder.GetTask(TaskName); return true; } catch { return false; }
            }
            catch { return false; }
        }

        static void DeleteTask()
        {
            try
            {
                dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                service.Connect();
                dynamic folder = service.GetFolder("\\");
                folder.DeleteTask(TaskName, 0);
            }
            catch { }
        }

        // ---------------- Run key fallback

        static void SetRunValue(string exe)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                k.SetValue(RunValue, $"\"{exe}\" --startup");
            }
            catch { }
        }

        static void DeleteRunValue()
        {
            try { Registry.CurrentUser.OpenSubKey(RunKey, true)?.DeleteValue(RunValue, false); } catch { }
        }
    }
}
