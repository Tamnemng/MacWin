using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace MacWin.Services
{
    /// <summary>One notification from another app, as shown in banners and Notification Center.</summary>
    public class MacNote
    {
        public uint Id { get; init; }
        public string AppId { get; init; }      // sender's AUMID
        public string AppName { get; init; }
        public string Title { get; init; }
        public string Body { get; init; }
        public DateTime Time { get; init; }
        public ImageSource Icon { get; set; }
    }

    /// <summary>
    /// Reads the notifications other apps post to Windows (UserNotificationListener). The change event needs package
    /// identity, so the list is polled instead — a small, cheap call every couple of seconds on a worker thread.
    /// </summary>
    public static class Notifications
    {
        static UserNotificationListener _listener;
        static Timer _poll;
        static int _busy;
        static bool _primed;
        static readonly Dictionary<uint, MacNote> _notes = new();
        static readonly Dictionary<string, ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised on the UI thread for a notification that arrived while MacWin was running.</summary>
        public static event Action<MacNote> Arrived;
        /// <summary>Raised on the UI thread whenever the list changes (new, removed or cleared).</summary>
        public static event Action Changed;

        public static bool Running => _poll != null;
        public static UserNotificationListenerAccessStatus Access { get; private set; } = UserNotificationListenerAccessStatus.Unspecified;

        /// <summary>Newest first.</summary>
        public static List<MacNote> All
        {
            get { lock (_notes) return _notes.Values.OrderByDescending(n => n.Time).ToList(); }
        }

        public static async void Start()
        {
            if (_poll != null) return;
            try
            {
                _listener = UserNotificationListener.Current;
                Access = _listener.GetAccessStatus();
                if (Access == UserNotificationListenerAccessStatus.Unspecified)
                    Access = await _listener.RequestAccessAsync();
                App.LogInfo("Notification access: " + Access);
                if (Access != UserNotificationListenerAccessStatus.Allowed) return;
                _primed = false;
                _poll = new Timer(_ => Poll(), null, 0, 2000);
                SystemTweaks.ApplyWindowsBanners();   // only now: if reading failed, Windows keeps its own banners
            }
            catch (Exception e) { App.Log(new Exception("Notification listener unavailable", e)); }
        }

        public static void Stop()
        {
            _poll?.Dispose();
            _poll = null;
            lock (_notes) _notes.Clear();
            Ui(() => Changed?.Invoke());
        }

        static async void Poll()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;   // a slow call must not pile up
            try
            {
                var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                var fresh = new List<MacNote>();
                var added = new List<(MacNote note, UserNotification raw)>();
                bool changed = false;
                var seen = new HashSet<uint>();
                foreach (var n in list)
                {
                    seen.Add(n.Id);
                    lock (_notes) if (_notes.ContainsKey(n.Id)) continue;
                    var note = Read(n);
                    if (note == null) continue;
                    lock (_notes) _notes[n.Id] = note;
                    added.Add((note, n));
                    changed = true;
                    // older than a minute = it was already there (start-up, or Windows restored it): no banner
                    if (_primed && DateTime.Now - note.Time < TimeSpan.FromMinutes(1)) fresh.Add(note);
                }
                lock (_notes)
                    foreach (var gone in _notes.Keys.Where(k => !seen.Contains(k)).ToList()) { _notes.Remove(gone); changed = true; }
                _primed = true;
                if (changed || fresh.Count > 0)
                {
                    foreach (var f in fresh) SystemTweaks.HideWindowsBanner(f.AppId);
                    Ui(async () =>
                    {
                        try
                        {
                            foreach (var (note, raw) in added) note.Icon = await IconFor(note.AppId, raw);
                            foreach (var f in fresh.OrderBy(f => f.Time)) Arrived?.Invoke(f);
                            Changed?.Invoke();
                        }
                        catch (Exception e) { App.Log(e); }
                    });
                }
            }
            catch (Exception e) { App.Log(new Exception("Notification poll failed", e)); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        static MacNote Read(UserNotification n)
        {
            try
            {
                var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
                string appName = null, appId = null;
                try { appId = n.AppInfo?.AppUserModelId; appName = n.AppInfo?.DisplayInfo?.DisplayName; } catch { }
                if (string.IsNullOrWhiteSpace(appName)) appName = appId != null ? AppCatalog.DisplayName(appId) : "Notification";
                return new MacNote
                {
                    Id = n.Id,
                    AppId = appId,
                    AppName = appName,
                    Title = texts.Count > 0 ? texts[0] : appName,
                    Body = string.Join("\n", texts.Skip(1)),
                    Time = n.CreationTime.LocalDateTime,
                };
            }
            catch { return null; }
        }

        /// <summary>The sender's icon: the same one the Dock uses when the app is known, else the logo Windows has.</summary>
        static async Task<ImageSource> IconFor(string appId, UserNotification n)
        {
            if (appId == null) return null;
            lock (_icons) if (_icons.TryGetValue(appId, out var c)) return c;
            ImageSource img = null;
            try
            {
                if (AppCatalog.ById(appId) != null) img = await IconProvider.ForApp(appId);
                else if (n?.AppInfo?.DisplayInfo != null)
                {
                    var stream = await n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64)).OpenReadAsync();
                    using var s = stream.AsStreamForRead();
                    var ms = new MemoryStream();
                    await s.CopyToAsync(ms);
                    ms.Position = 0;
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.StreamSource = ms;
                    b.DecodePixelWidth = 64;
                    b.EndInit();
                    b.Freeze();
                    img = b;
                }
            }
            catch { }
            lock (_icons) _icons[appId] = img;
            return img;
        }

        /// <summary>Removes a notification (also from Windows' own list, like dismissing it on a Mac).</summary>
        public static void Dismiss(uint id)
        {
            bool had;
            lock (_notes) had = _notes.Remove(id);
            try { _listener?.RemoveNotification(id); } catch { }
            if (had) Changed?.Invoke();
        }

        public static void DismissApp(string appId)
        {
            foreach (var n in All.Where(n => string.Equals(n.AppId, appId, StringComparison.OrdinalIgnoreCase)))
            {
                lock (_notes) _notes.Remove(n.Id);
                try { _listener?.RemoveNotification(n.Id); } catch { }
            }
            Changed?.Invoke();
        }

        public static void ClearAll()
        {
            List<uint> ids;
            lock (_notes) { ids = _notes.Keys.ToList(); _notes.Clear(); }
            foreach (var id in ids) try { _listener?.RemoveNotification(id); } catch { }
            Changed?.Invoke();
        }

        /// <summary>Brings the sending app to the front (the toast's own action needs package identity).</summary>
        public static void Open(MacNote n)
        {
            if (string.IsNullOrEmpty(n?.AppId)) return;
            try
            {
                if (AppCatalog.ById(n.AppId) != null) AppCatalog.Launch(n.AppId);
                else Shell.Open("explorer.exe", "shell:AppsFolder\\" + n.AppId);
            }
            catch (Exception e) { App.Log(e); }
        }

        static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);
    }
}
