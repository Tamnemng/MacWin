using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacWin.Native;

namespace MacWin.Services
{
    /// <summary>
    /// Resolves the image for any Dock / Launchpad entry, in this order:
    /// 1) user icon in %AppData%\MacWin\Icons (role key or app name, .png/.ico/.jpg)
    /// 2) built-in Mac-style art for roles and known system apps
    /// 3) the app's own Windows icon, optionally placed on a squircle plate.
    /// Results are cached; all shell work happens on the background STA thread.
    /// </summary>
    public static class IconProvider
    {
        static readonly ConcurrentDictionary<string, Task<ImageSource>> Cache = new(StringComparer.OrdinalIgnoreCase);
        public static string UserIconFolder => Path.Combine(AppSettings.Folder, "Icons");
        static readonly string[] Exts = { ".png", ".ico", ".jpg", ".jpeg", ".bmp" };

        public static void Invalidate() => Cache.Clear();

        static string SafeName(string s) => string.Concat((s ?? "").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

        static string FindUserIcon(params string[] names)
        {
            foreach (var n in names)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                foreach (var ext in Exts)
                {
                    var p = Path.Combine(UserIconFolder, SafeName(n) + ext);
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }

        static BitmapSource LoadFile(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.DecodePixelWidth = IconArt.Size;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>Copies a user-chosen image into the icon folder under the given key.</summary>
        public static void SetUserIcon(string key, string sourceFile)
        {
            Directory.CreateDirectory(UserIconFolder);
            foreach (var ext in Exts)
            {
                var old = Path.Combine(UserIconFolder, SafeName(key) + ext);
                if (File.Exists(old)) File.Delete(old);
            }
            if (sourceFile != null)
                File.Copy(sourceFile, Path.Combine(UserIconFolder, SafeName(key) + Path.GetExtension(sourceFile).ToLowerInvariant()), true);
            Invalidate();
        }

        public static Task<ImageSource> ForRole(string roleKey)
        {
            var b = Roles.Binding(roleKey);
            bool mac = b?.MacIcon ?? true;
            string appId = b?.AppId;
            return Cache.GetOrAdd("role:" + roleKey + "|" + mac + "|" + appId, _ => StaWorker.Run<ImageSource>(() =>
            {
                var user = FindUserIcon(roleKey, b?.Label);
                if (user != null && LoadFile(user) is BitmapSource u) return u;
                if (mac && IconArt.ForKey(roleKey) is BitmapSource art) return art;
                return BuildAppIcon(appId);
            }));
        }

        public static Task<ImageSource> ForApp(string appId)
        {
            if (appId == null) return Task.FromResult<ImageSource>(null);
            return Cache.GetOrAdd("app:" + appId + "|" + App.Settings.SquircleIcons, _ => StaWorker.Run<ImageSource>(() =>
            {
                var name = AppCatalog.ById(appId)?.Name ?? (File.Exists(appId) ? Path.GetFileNameWithoutExtension(appId) : null);
                var user = FindUserIcon(name);
                if (user != null && LoadFile(user) is BitmapSource u) return u;
                var key = IconArt.SystemAppKey(appId);
                if (key != null && App.Settings.SquircleIcons && IconArt.ForKey(key) is BitmapSource art) return art;
                // apps bound to a role share the role icon so Launchpad and Dock look consistent
                var role = Roles.ForApp(appId);
                if (role != null && role.MacIcon && App.Settings.SquircleIcons && FindUserIcon(role.Role, role.Label) is string rp && LoadFile(rp) is BitmapSource ru) return ru;
                if (role != null && role.MacIcon && App.Settings.SquircleIcons && IconArt.ForKey(role.Role) is BitmapSource rart) return rart;
                return BuildAppIcon(appId);
            }));
        }

        public static Task<ImageSource> ForKey(string key) =>
            Cache.GetOrAdd("key:" + key, _ => StaWorker.Run<ImageSource>(() =>
                FindUserIcon(key) is string p && LoadFile(p) is BitmapSource u ? u : IconArt.ForKey(key)));

        public static Task<ImageSource> Trash(bool full) =>
            Cache.GetOrAdd("trash:" + full, _ => StaWorker.Run<ImageSource>(() =>
                FindUserIcon(full ? "trash-full" : "trash") is string p && LoadFile(p) is BitmapSource u ? u : IconArt.Trash(full)));

        /// <summary>File / folder icon or thumbnail (Spotlight, Downloads stack). Not plated.</summary>
        public static Task<ImageSource> ForFile(string path, int size = 64) =>
            Cache.GetOrAdd("file:" + path + "|" + size, _ => StaWorker.Run<ImageSource>(() => ShellInterop.GetImage(path, size, iconOnly: false)));

        static ImageSource BuildAppIcon(string appId)
        {
            if (appId == null) return IconArt.Placeholder();
            BitmapSource raw = null;
            if (appId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return IconArt.ForKey("browser");
            if (AppCatalog.ById(appId) != null)
                raw = ShellInterop.GetImage("shell:AppsFolder\\" + appId, 256);
            raw ??= ShellInterop.GetImage(appId, 256);
            if (raw == null) return IconArt.Placeholder();
            return App.Settings.SquircleIcons ? IconArt.Plate(raw) : IconArt.Plain(raw);
        }
    }
}
