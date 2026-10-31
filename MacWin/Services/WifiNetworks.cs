using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace MacWin.Services
{
    public record WifiNetwork(string Ssid, int Signal, bool Secured, bool Known, bool Connected);

    /// <summary>
    /// Nearby Wi-Fi networks and connecting to them, through the Windows Wi-Fi API (Windows.Devices.WiFi).
    /// netsh is no longer usable for this: since Windows 11 24H2 it refuses to list networks without the
    /// Location permission, which a desktop tool can't ask for. Saved profiles still come from netsh.
    /// </summary>
    public static class WifiNetworks
    {
        static WiFiAdapter _adapter;
        static DateTime _scannedAt = DateTime.MinValue;

        static async Task<WiFiAdapter> Adapter()
        {
            if (_adapter != null) return _adapter;
            try
            {
                await WiFiAdapter.RequestAccessAsync().AsTask().ConfigureAwait(false);
                var devs = await DeviceInformation.FindAllAsync(WiFiAdapter.GetDeviceSelector()).AsTask().ConfigureAwait(false);
                if (devs.Count == 0) return null;
                _adapter = await WiFiAdapter.FromIdAsync(devs[0].Id).AsTask().ConfigureAwait(false);
            }
            catch (Exception e) { App.Log(new Exception("Wi-Fi adapter unavailable", e)); }
            return _adapter;
        }

        static (string output, int code) Netsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return (o, p.HasExited ? p.ExitCode : -1);
            }
            catch { return ("", -1); }
        }

        static string Value(string line)
        {
            int i = line.IndexOf(':');
            return i < 0 ? "" : line[(i + 1)..].Trim();
        }

        /// <summary>Saved profiles: the "name" lines after the last dashed separator of "netsh wlan show profiles".</summary>
        static HashSet<string> KnownProfiles()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = Netsh("wlan show profiles").output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            int sep = lines.FindLastIndex(l => l.Trim().Length > 3 && l.Trim().All(c => c == '-'));
            for (int i = sep + 1; i < lines.Count && sep >= 0; i++)
            {
                var v = Value(lines[i]);
                if (lines[i].Contains(':') && v.Length > 0) set.Add(v);
            }
            return set;
        }

        /// <summary>Name of the network the adapter is connected to, or null.</summary>
        public static async Task<string> CurrentSsidAsync()
        {
            try
            {
                var a = await Adapter().ConfigureAwait(false);
                var prof = a == null ? null : await a.NetworkAdapter.GetConnectedProfileAsync().AsTask().ConfigureAwait(false);
                var ssid = prof?.WlanConnectionProfileDetails?.GetConnectedSsid();
                return string.IsNullOrEmpty(ssid) ? null : ssid;
            }
            catch { return null; }
        }

        static string _lastSsid;

        /// <summary>Blocking form, safe on any thread: waits at most 2 s, else returns the last known name.</summary>
        public static string CurrentSsid()
        {
            try
            {
                var t = Task.Run(CurrentSsidAsync);
                if (t.Wait(2000)) _lastSsid = t.Result;
            }
            catch { }
            return _lastSsid;
        }

        static int Percent(WiFiAvailableNetwork n)
        {
            // RSSI −100…−50 dBm → 0…100 %, falling back to the bar count
            double p = (n.NetworkRssiInDecibelMilliwatts + 100) * 2;
            if (double.IsNaN(p) || p <= 0) p = n.SignalBars * 25;
            return (int)Math.Clamp(p, 0, 100);
        }

        static Task _scan;

        /// <summary>True when the last report is older than 20 s (or empty) and a new scan is worth running.</summary>
        public static bool ReportStale => _adapter == null || DateTime.Now - _scannedAt > TimeSpan.FromSeconds(20) || _adapter.NetworkReport.AvailableNetworks.Count == 0;

        /// <summary>
        /// Starts a scan in the background (never two at once). Windows answers E_ABORT when it is already scanning
        /// or throttles us; the report still updates by itself, so that is not an error worth showing.
        /// </summary>
        public static Task RescanAsync()
        {
            if (_scan != null && !_scan.IsCompleted) return _scan;
            return _scan = Task.Run(async () =>
            {
                var a = await Adapter().ConfigureAwait(false);
                if (a == null) return;
                try
                {
                    var t = a.ScanAsync().AsTask();
                    _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);   // observe a late abort
                    if (await Task.WhenAny(t, Task.Delay(6000)).ConfigureAwait(false) == t) await t.ConfigureAwait(false);
                }
                catch (Exception e) { if ((uint)e.HResult != 0x80004004) App.Log(e); }
                _scannedAt = DateTime.Now;
            });
        }

        /// <summary>The networks in the adapter's current report, without waiting for a scan.</summary>
        public static async Task<List<WifiNetwork>> ScanAsync()
        {
            var a = await Adapter().ConfigureAwait(false);
            if (a == null) return new List<WifiNetwork>();
            if (a.NetworkReport.AvailableNetworks.Count == 0)
            {
                // nothing cached yet (first use after start-up): wait for one scan, but not forever
                await Task.WhenAny(RescanAsync(), Task.Delay(5000)).ConfigureAwait(false);
            }

            string current = await CurrentSsidAsync().ConfigureAwait(false);
            var known = await Task.Run(KnownProfiles).ConfigureAwait(false);
            var result = new Dictionary<string, WifiNetwork>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in a.NetworkReport.AvailableNetworks)
            {
                if (string.IsNullOrWhiteSpace(n.Ssid)) continue;   // hidden networks
                int sig = Percent(n);
                var auth = n.SecuritySettings.NetworkAuthenticationType;
                bool secured = auth != NetworkAuthenticationType.Open80211 && auth != NetworkAuthenticationType.None;
                if (!result.TryGetValue(n.Ssid, out var prev) || prev.Signal < sig)
                    result[n.Ssid] = new WifiNetwork(n.Ssid, sig, secured, known.Contains(n.Ssid), string.Equals(n.Ssid, current, StringComparison.OrdinalIgnoreCase));
            }
            return result.Values.OrderByDescending(x => x.Connected).ThenByDescending(x => x.Known).ThenByDescending(x => x.Signal).ToList();
        }

        /// <summary>
        /// Connects: with a saved profile (no password), or with the given password. Returns null on success,
        /// otherwise a short reason ("Incorrect password", "Needs a password", …).
        /// </summary>
        public static async Task<string> ConnectAsync(WifiNetwork n, string password = null)
        {
            var a = await Adapter().ConfigureAwait(false);
            if (a == null) return "No Wi-Fi adapter";
            var target = a.NetworkReport.AvailableNetworks.Where(x => x.Ssid == n.Ssid).OrderByDescending(Percent).FirstOrDefault();
            if (target == null) return "Network not in range";
            try
            {
                WiFiConnectionResult r;
                if (!string.IsNullOrEmpty(password))
                    r = await a.ConnectAsync(target, WiFiReconnectionKind.Automatic, new PasswordCredential { Password = password }).AsTask().ConfigureAwait(false);
                else
                    r = await a.ConnectAsync(target, WiFiReconnectionKind.Automatic).AsTask().ConfigureAwait(false);
                return r.ConnectionStatus switch
                {
                    WiFiConnectionStatus.Success => null,
                    WiFiConnectionStatus.InvalidCredential => password == null ? "Needs a password" : "Incorrect password",
                    WiFiConnectionStatus.AccessRevoked => "Access denied",
                    WiFiConnectionStatus.NetworkNotAvailable => "Network not available",
                    WiFiConnectionStatus.Timeout => "Timed out",
                    WiFiConnectionStatus.UnsupportedAuthenticationProtocol => "Unsupported security type",
                    _ => "Couldn't connect",
                };
            }
            catch (Exception e)
            {
                App.Log(e);
                // a secured network without a saved profile rejects the credential-less call
                return n.Secured && password == null ? "Needs a password" : "Couldn't connect";
            }
        }

        /// <summary>Removes the saved profile (Windows "Forget").</summary>
        public static Task<bool> ForgetAsync(string ssid) => Task.Run(() => Netsh($"wlan delete profile name=\"{ssid}\"").code == 0);

        /// <summary>Whether Windows joins the saved network by itself; null when unknown.</summary>
        public static Task<bool?> AutoJoinAsync(string ssid) => Task.Run<bool?>(() =>
        {
            // "Connection mode : Connect automatically / manually" — the first line whose value mentions "auto" or "manual"
            var o = Netsh($"wlan show profile name=\"{ssid}\"").output;
            foreach (var raw in o.Split('\n'))
            {
                var v = Value(raw).ToLowerInvariant();
                if (!raw.Contains(':')) continue;
                if (v.Contains("auto")) return true;
                if (v.Contains("manual")) return false;
            }
            return null;
        });

        public static Task<bool> SetAutoJoinAsync(string ssid, bool on) => Task.Run(() =>
            Netsh($"wlan set profileparameter name=\"{ssid}\" connectionmode={(on ? "auto" : "manual")}").code == 0);

        public static async Task DisconnectAsync()
        {
            var a = await Adapter().ConfigureAwait(false);
            try { a?.Disconnect(); } catch { }
        }
    }
}
