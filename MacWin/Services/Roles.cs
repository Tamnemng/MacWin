using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MacWin.Services
{
    public record RoleDef(string Key, string Label, string Description, string[] Candidates, string FallbackUrl, bool InDefaultDock);

    /// <summary>
    /// "Mac-style apps": each role has its own macOS-flavoured icon in the Dock and is backed by
    /// a real Windows app chosen by the user (e.g. Browser → Chrome, Edge, Cốc Cốc, Firefox...).
    /// </summary>
    public static class Roles
    {
        public static readonly RoleDef[] All =
        {
            new("files",     "Files",      "File manager",            new[]{ "^File Explorer$", "^Files$", "Directory Opus", "Total Commander" }, null, true),
            new("browser",   "Browser",    "Web browser",             new[]{ "^Google Chrome$", "^Microsoft Edge$", "C[oố]c C[oố]c", "Firefox", "^Brave", "^Opera", "^Vivaldi", "^Arc$", "Chromium" }, "https://www.google.com", true),
            new("messages",  "Messages",   "Chat app",                new[]{ "^Zalo", "Messenger", "Telegram", "WhatsApp", "Microsoft Teams", "^Discord", "^Signal", "Skype" }, "https://chat.zalo.me", true),
            new("mail",      "Mail",       "Email",                   new[]{ "^Outlook", "^Mail$", "Thunderbird", "eM Client" }, "https://mail.google.com", true),
            new("maps",      "Maps",       "Maps",                    new[]{ "^Maps$" }, "https://www.google.com/maps", true),
            new("photos",    "Photos",     "Photo viewer",            new[]{ "^Photos$", "Microsoft Photos", "ImageGlass", "IrfanView" }, "https://photos.google.com", true),
            new("video",     "Meet",       "Video calls",             new[]{ "^Zoom", "Microsoft Teams", "Skype", "^Camera$" }, "https://meet.google.com", true),
            new("calendar",  "Calendar",   "Calendar",                new[]{ "^Calendar$", "^Outlook" }, "https://calendar.google.com", true),
            new("reminders", "Reminders",  "To-do list",              new[]{ "Microsoft To Do", "^To Do$", "TickTick", "Todoist" }, "https://to-do.office.com", true),
            new("notes",     "Notes",      "Notes",                   new[]{ "Sticky Notes", "OneNote", "^Notion", "Obsidian", "^Notepad$" }, "https://keep.google.com", true),
            new("music",     "Music",      "Music player",            new[]{ "^Spotify", "Media Player", "iTunes", "Apple Music", "foobar", "AIMP" }, "https://open.spotify.com", true),
            new("tv",        "TV",         "Video player",            new[]{ "^VLC", "Movies & TV", "Films & TV", "PotPlayer", "Netflix", "MPC-HC" }, "https://www.youtube.com", false),
            new("code",      "Code",       "Code editor",             new[]{ "Visual Studio Code", "^Cursor$", "^Visual Studio 20", "Rider", "Sublime Text", "Notepad\\+\\+" }, null, true),
            new("store",     "Store",      "App store",               new[]{ "Microsoft Store" }, null, true),
            new("settings",  "Settings",   "System settings",         new[]{ "^Settings$" }, "ms-settings:", true),
            new("terminal",  "Terminal",   "Command line",            new[]{ "^Terminal$", "Windows PowerShell$", "Command Prompt", "^PowerShell" }, null, false),
            new("calculator","Calculator", "Calculator",              new[]{ "^Calculator$" }, null, false),
            new("textedit",  "TextEdit",   "Simple text editor",      new[]{ "^Notepad$", "WordPad" }, null, false),
            new("office",    "Docs",       "Word processor",          new[]{ "^Word$", "Microsoft Word", "LibreOffice Writer", "WPS" }, "https://docs.google.com", false),
        };

        public static RoleDef Def(string key) => All.FirstOrDefault(r => r.Key == key);

        /// <summary>Installed apps that fit a role, best match first.</summary>
        public static List<AppEntry> Candidates(string key)
        {
            var def = Def(key);
            var list = new List<AppEntry>();
            if (def == null) return list;
            foreach (var pattern in def.Candidates)
            {
                var rx = new Regex(pattern, RegexOptions.IgnoreCase);
                foreach (var a in AppCatalog.Apps)
                    if (rx.IsMatch(a.Name) && !list.Contains(a)) list.Add(a);
            }
            return list;
        }

        public static RoleBinding Binding(string key)
        {
            var s = App.Settings;
            var b = s.Roles.FirstOrDefault(r => r.Role == key);
            if (b == null)
            {
                var def = Def(key);
                if (def == null) return null;
                b = new RoleBinding { Role = key, Label = def.Label };
                s.Roles.Add(b);
            }
            // only auto-assign once the installed-app list is known, otherwise a website fallback would stick
            if (AppCatalog.Apps.Count > 0 && (string.IsNullOrEmpty(b.AppId) || !IsValidTarget(b.AppId)))
            {
                var c = Candidates(key).FirstOrDefault();
                b.AppId = c?.Id ?? Def(key)?.FallbackUrl;
            }
            return b;
        }

        static bool IsValidTarget(string id) =>
            id.StartsWith("http", StringComparison.OrdinalIgnoreCase) || id.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) ||
            AppCatalog.ById(id) != null || System.IO.File.Exists(id);

        /// <summary>Called once the catalog is loaded: fills in any unbound roles.</summary>
        public static void AutoBind()
        {
            foreach (var def in All) Binding(def.Key);
        }

        /// <summary>Role whose target app is the given app id (used for the menu bar title and dock indicators).</summary>
        public static RoleBinding ForApp(string appId)
        {
            if (appId == null) return null;
            return App.Settings.Roles.FirstOrDefault(r => string.Equals(r.AppId, appId, StringComparison.OrdinalIgnoreCase));
        }

        public static List<string> DefaultDock()
        {
            var list = new List<string>();
            foreach (var def in All.Where(d => d.InDefaultDock))
            {
                var b = Binding(def.Key);
                if (b?.AppId == null) continue;
                list.Add("role:" + def.Key);
                if (def.Key == "files") list.Add("sys:launchpad");
            }
            if (!list.Contains("sys:launchpad")) list.Insert(0, "sys:launchpad");
            return list;
        }
    }
}
