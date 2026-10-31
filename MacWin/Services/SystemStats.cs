using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using MacWin.Native;
using Microsoft.Win32;

namespace MacWin.Services
{
    public record ProcessUsage(string Name, double Cpu, long MemoryBytes, int Pid);

    /// <summary>
    /// CPU / memory / GPU / VRAM / disk / network figures for the menu bar and the Activity panel.
    /// Light mode (menu bar only): CPU + memory every 2 s via two cheap kernel calls.
    /// Detailed mode (panel open): adds GPU counters, network, disk and top processes every second,
    /// all collected on a background thread and released again when the panel closes.
    /// </summary>
    public static class SystemStats
    {
        public const int HistoryLength = 60;
        public static double Cpu { get; private set; }
        public static double MemoryPercent { get; private set; }
        public static ulong MemoryUsed { get; private set; }
        public static ulong MemoryTotal { get; private set; }
        public static double Gpu { get; private set; } = double.NaN;
        public static ulong VramUsed { get; private set; }
        public static ulong VramTotal { get; private set; }
        public static double NetDown { get; private set; }   // bytes / second
        public static double NetUp { get; private set; }
        public static ulong DiskUsed { get; private set; }
        public static ulong DiskTotal { get; private set; }
        public static IReadOnlyList<ProcessUsage> TopProcesses { get; private set; } = Array.Empty<ProcessUsage>();
        public static readonly Queue<double> CpuHistory = new(), MemHistory = new(), GpuHistory = new(), NetHistory = new();
        public static event Action Updated;

        static DispatcherTimer _timer;
        static bool _detailed, _busy;
        static long _lastIdle, _lastTotal;
        static long _lastRx, _lastTx;
        static DateTime _lastNet;
        static List<PerformanceCounter> _gpuEngines, _vram;
        static DateTime _gpuListAt;
        static readonly Dictionary<int, TimeSpan> _procCpu = new();
        static DateTime _procAt;

        [StructLayout(LayoutKind.Sequential)] struct FILETIME { public uint Low, High; public long Value => ((long)High << 32) | Low; }
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        public static void Start()
        {
            if (_timer != null) return;
            VramTotal = ReadVramTotal();
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += async (_, _) => await Sample();
            _timer.Start();
            _ = Sample();
        }

        public static void Stop()
        {
            _timer?.Stop();
            _timer = null;
            SetDetailed(false);
        }

        /// <summary>Detailed sampling only while the Activity panel is open.</summary>
        public static void SetDetailed(bool on)
        {
            _detailed = on;
            if (_timer != null) _timer.Interval = TimeSpan.FromSeconds(on ? 1 : 2);
            if (!on)
            {
                DisposeCounters();
                lock (_procCpu) _procCpu.Clear();
            }
            else _ = Sample();
        }

        static async Task Sample()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                SampleCpuMemory();
                if (_detailed) await Task.Run(SampleDetailed);
                Push(CpuHistory, Cpu);
                Push(MemHistory, MemoryPercent);
                if (_detailed) { Push(GpuHistory, double.IsNaN(Gpu) ? 0 : Gpu); Push(NetHistory, NetDown + NetUp); }
                Updated?.Invoke();
            }
            catch (Exception e) { App.Log(e); }
            finally { _busy = false; }
        }

        static void Push(Queue<double> q, double v)
        {
            q.Enqueue(v);
            while (q.Count > HistoryLength) q.Dequeue();
        }

        static void SampleCpuMemory()
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                long total = kernel.Value + user.Value; // kernel time includes idle time
                long dIdle = idle.Value - _lastIdle, dTotal = total - _lastTotal;
                if (_lastTotal != 0 && dTotal > 0) Cpu = Math.Clamp(100.0 * (1 - (double)dIdle / dTotal), 0, 100);
                _lastIdle = idle.Value;
                _lastTotal = total;
            }
            var m = new Win32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
            if (Win32.GlobalMemoryStatusEx(ref m))
            {
                MemoryTotal = m.ullTotalPhys;
                MemoryUsed = m.ullTotalPhys - m.ullAvailPhys;
                MemoryPercent = m.dwMemoryLoad;
            }
        }

        static void SampleDetailed()
        {
            // ---- GPU (3D engines) and dedicated video memory, from Windows performance counters
            try
            {
                if (_gpuEngines == null || (DateTime.Now - _gpuListAt).TotalSeconds > 10)
                {
                    DisposeCounters();
                    var engines = new PerformanceCounterCategory("GPU Engine").GetInstanceNames().Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase));
                    _gpuEngines = engines.Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true)).ToList();
                    _vram = new PerformanceCounterCategory("GPU Adapter Memory").GetInstanceNames()
                        .Select(n => new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", n, true)).ToList();
                    foreach (var c in _gpuEngines) try { c.NextValue(); } catch { }
                    _gpuListAt = DateTime.Now;
                }
                else
                {
                    double sum = 0;
                    foreach (var c in _gpuEngines) try { sum += c.NextValue(); } catch { }
                    Gpu = Math.Clamp(sum, 0, 100);
                }
                double used = 0;
                foreach (var c in _vram ?? new List<PerformanceCounter>()) try { used = Math.Max(used, c.NextValue()); } catch { }
                VramUsed = (ulong)used;
            }
            catch { Gpu = double.NaN; }

            // ---- network throughput
            try
            {
                long rx = 0, tx = 0;
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var s = n.GetIPStatistics();
                    rx += s.BytesReceived;
                    tx += s.BytesSent;
                }
                var now = DateTime.Now;
                if (_lastNet != default)
                {
                    double dt = Math.Max(0.2, (now - _lastNet).TotalSeconds);
                    NetDown = Math.Max(0, (rx - _lastRx) / dt);
                    NetUp = Math.Max(0, (tx - _lastTx) / dt);
                }
                _lastRx = rx; _lastTx = tx; _lastNet = now;
            }
            catch { }

            // ---- system drive
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
                DiskTotal = (ulong)d.TotalSize;
                DiskUsed = (ulong)(d.TotalSize - d.TotalFreeSpace);
            }
            catch { }

            // ---- top processes by CPU (memory as tie-breaker)
            try
            {
                var now = DateTime.Now;
                double elapsed = _procAt == default ? 0 : (now - _procAt).TotalMilliseconds;
                _procAt = now;
                var list = new List<ProcessUsage>();
                var seen = new HashSet<int>();
                foreach (var p in Process.GetProcesses())
                {
                    using (p)
                    {
                        try
                        {
                            if (p.Id == 0 || p.Id == 4) continue;
                            seen.Add(p.Id);
                            double cpu = 0;
                            TimeSpan t;
                            try { t = p.TotalProcessorTime; } catch { t = TimeSpan.Zero; }
                            lock (_procCpu)
                            {
                                if (elapsed > 0 && _procCpu.TryGetValue(p.Id, out var last) && t > last)
                                    cpu = (t - last).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100;
                                _procCpu[p.Id] = t;
                            }
                            list.Add(new ProcessUsage(p.ProcessName, cpu, p.WorkingSet64, p.Id));
                        }
                        catch { }
                    }
                }
                lock (_procCpu) foreach (var dead in _procCpu.Keys.Where(k => !seen.Contains(k)).ToList()) _procCpu.Remove(dead);
                // merge processes of the same app (browsers run many)
                TopProcesses = list.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new ProcessUsage(g.Key, g.Sum(x => x.Cpu), g.Sum(x => x.MemoryBytes), g.First().Pid))
                    .OrderByDescending(x => x.Cpu).ThenByDescending(x => x.MemoryBytes).Take(6).ToList();
            }
            catch { }
        }

        static void DisposeCounters()
        {
            foreach (var c in _gpuEngines ?? new List<PerformanceCounter>()) c.Dispose();
            foreach (var c in _vram ?? new List<PerformanceCounter>()) c.Dispose();
            _gpuEngines = null;
            _vram = null;
        }

        /// <summary>Largest dedicated video memory reported by the display drivers.</summary>
        static ulong ReadVramTotal()
        {
            ulong best = 0;
            try
            {
                using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (cls == null) return 0;
                foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
                {
                    try
                    {
                        using var k = cls.OpenSubKey(sub);
                        var v = k?.GetValue("HardwareInformation.qwMemorySize");
                        ulong size = v switch { long l => (ulong)l, byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0), int i => (uint)i, _ => 0 };
                        best = Math.Max(best, size);
                    }
                    catch { }
                }
            }
            catch { }
            return best;
        }

        public static string Bytes(double b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return i <= 1 ? $"{b:0} {u[i]}" : $"{b:0.0} {u[i]}";
        }
    }
}
