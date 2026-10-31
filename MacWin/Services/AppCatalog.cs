using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MacWin.Native;

namespace MacWin.Services
{
    /// <summary>Single background STA thread for shell COM work (icons, app enumeration).</summary>
    public static class StaWorker
    {
        static readonly BlockingCollection<Action> Queue = new();
        static readonly BlockingCollection<Action> Priority = new();

        static StaWorker()
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    Action a;
                    if (!Priority.TryTake(out a)) BlockingCollection<Action>.TakeFromAny(new[] { Priority, Queue }, out a);
                    try { a(); } catch { }
                }
            }) { IsBackground = true, Name = "MacWin.Shell", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        public static Task<T> Run<T>(Func<T> f, bool urgent = false)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            (urgent ? Priority : Queue).Add(() =>
            {
                try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); }
            });
            return tcs.Task;
        }
    }

    public class AppEntry
    {
        public string Id { get; init; }          // parsing name inside shell:AppsFolder
        public string Name { get; init; }
        public string ExePath { get; init; }     // resolved for Win32 apps, null for packaged apps
        public bool IsUrl => Id.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        public override string ToString() => Name;
    }

    /// <summary>All installed apps — the same list as Start ▸ All apps (shell:AppsFolder).</summary>
    public static class AppCatalog
    {
        public static List<AppEntry> Apps { get; private set; } = new();
        public static event Action Loaded;
        static Dictionary<string, AppEntry> _byId = new(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, AppEntry> _byExe = new(StringComparer.OrdinalIgnoreCase);

        static readonly Regex Junk = new(@"uninstall|gỡ cài đặt|readme|release notes|documentation|help$|website|license|manual",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static DateTime LoadedAt { get; private set; } = DateTime.MinValue;
        static bool _loading;
        static FileSystemWatcher[] _watchers;
        static System.Windows.Threading.DispatcherTimer _reload;

        /// <summary>Reloads the app list when Start-menu shortcuts are added / removed (apps installed or uninstalled).</summary>
        public static void Watch()
        {
            if (_watchers != null) return;
            _reload = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _reload.Tick += (_, _) => { _reload.Stop(); _ = LoadAsync(); };
            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            };
            var list = new List<FileSystemWatcher>();
            foreach (var f in folders)
            {
                try
                {
                    if (!Directory.Exists(f)) continue;
                    var w = new FileSystemWatcher(f) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                    FileSystemEventHandler h = (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(ScheduleReload);
                    w.Created += h; w.Deleted += h;
                    w.Renamed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(ScheduleReload);
                    w.EnableRaisingEvents = true;
                    list.Add(w);
                }
                catch { }
            }
            _watchers = list.ToArray();
        }

        static void ScheduleReload()
        {
            _reload?.Stop();
            _reload?.Start();
        }

        /// <summary>Background refresh when the list is older than <paramref name="maxAge"/> (Store apps don't touch the Start-menu folders).</summary>
        public static void RefreshIfStale(TimeSpan maxAge)
        {
            if (!_loading && DateTime.Now - LoadedAt > maxAge) _ = LoadAsync();
        }

        public static async Task LoadAsync()
        {
            if (_loading) return;
            _loading = true;
            List<AppEntry> list;
            try { list = await StaWorker.Run(Enumerate, urgent: true); }
            finally { _loading = false; }
            LoadedAt = DateTime.Now;
            Apps = list;
            _byId = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            _byExe = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in list)
            {
                _byId.TryAdd(a.Id, a);
                if (a.ExePath != null) _byExe.TryAdd(a.ExePath, a);
            }
            Loaded?.Invoke();
        }

        static List<AppEntry> Enumerate()
        {
            var result = new List<AppEntry>();
            var t = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(t);
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            dynamic items = folder.Items();
            int count = items.Count;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic item = items.Item(i);
                    string name = item.Name;
                    string id = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                    if (Junk.IsMatch(name)) continue;
                    string ext = Path.GetExtension(id).ToLowerInvariant();
                    if (ext is ".url" or ".chm" or ".txt" or ".html" or ".htm" or ".pdf" or ".rtf" or ".ini") continue;
                    if (!seen.Add(name)) continue;
                    result.Add(new AppEntry { Id = id, Name = name, ExePath = ResolvePath(id) });
                }
                catch { }
            }
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        static readonly Regex KnownFolderPrefix = new(@"^\{([0-9A-Fa-f\-]{36})\}(.*)$", RegexOptions.Compiled);

        /// <summary>Turns "{KnownFolderGuid}\Vendor\app.exe" into a real path.</summary>
        public static string ResolvePath(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var m = KnownFolderPrefix.Match(id);
            if (m.Success)
            {
                var root = Win32.KnownFolder(new Guid(m.Groups[1].Value));
                if (root != null) return Path.GetFullPath(root + m.Groups[2].Value);
                return null;
            }
            if (id.Length > 2 && id[1] == ':' && File.Exists(id)) return Path.GetFullPath(id);
            return null;
        }

        public static AppEntry ById(string id) => id != null && _byId.TryGetValue(id, out var a) ? a : null;
        public static AppEntry ByExe(string exe) => exe != null && _byExe.TryGetValue(exe, out var a) ? a : null;

        /// <summary>Finds the catalog id that represents a running window's app.</summary>
        public static string Identify(string aumid, string exePath)
        {
            if (aumid != null && _byId.ContainsKey(aumid)) return _byId[aumid].Id;
            if (exePath != null && _byExe.TryGetValue(exePath, out var e)) return e.Id;
            return aumid ?? exePath;
        }

        public static AppEntry FindByName(string pattern)
        {
            var rx = new Regex(pattern, RegexOptions.IgnoreCase);
            return Apps.FirstOrDefault(a => rx.IsMatch(a.Name));
        }

        static readonly ConcurrentDictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Friendly name for any app id (cached: reading version info from disk on every keystroke caused lag).</summary>
        public static string DisplayName(string id)
        {
            var a = ById(id);
            if (a != null) return a.Name;
            if (id == null) return "";
            return NameCache.GetOrAdd(id, ComputeName);
        }

        static string ComputeName(string id)
        {
            if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return Uri.TryCreate(id, UriKind.Absolute, out var u) ? u.Host : id;
            try
            {
                if (File.Exists(id))
                {
                    var d = System.Diagnostics.FileVersionInfo.GetVersionInfo(id).FileDescription;
                    if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
                }
                return Path.GetFileNameWithoutExtension(id);
            }
            catch { return id; }
        }

        /// <summary>Launches an app id the same way the Start menu does.</summary>
        public static void Launch(string id, string args = null)
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase) || id.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
                {
                    Shell.Open(id);
                    return;
                }
                var entry = ById(id);
                if (args != null && (entry?.ExePath ?? (File.Exists(id) ? id : null)) is string exe)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = true });
                    return;
                }
                if (entry != null)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + id) { UseShellExecute = true });
                    return;
                }
                Shell.Open(id);
            }
            catch { }
        }
    }
}
