using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Aether.Island;

/// <summary>
/// CPU / memory / network / battery readings, sampled once a second — but only while
/// something on screen actually shows them (<see cref="Need"/>), so it costs nothing otherwise.
/// </summary>
public static class SystemStats
{
    public static double Cpu { get; private set; }
    public static double Memory { get; private set; }
    public static double MemoryUsedGb { get; private set; }
    public static double MemoryTotalGb { get; private set; }
    public static double DownBps { get; private set; }
    public static double UpBps { get; private set; }
    public static int? Battery { get; private set; }
    public static bool Charging { get; private set; }
    public static readonly double[] CpuHistory = new double[40];

    public static event Action? Updated;

    /// <summary>Set by whoever displays network speed (it is by far the most expensive reading).</summary>
    public static bool WantNetwork { get; set; }

    private static DispatcherTimer? _timer;
    private static long _idle, _kernel, _user;
    private static long _rx, _tx;
    private static DateTime _netAt;
    private static readonly System.Collections.Generic.HashSet<object> Needs = new();

    /// <summary>Register/unregister a consumer; sampling runs while anyone needs it.</summary>
    public static void Need(object who, bool needed)
    {
        if (needed) Needs.Add(who); else Needs.Remove(who);
        if (Needs.Count > 0)
        {
            if (_timer is null)
            {
                // Every 2 s: each reading repaints the island, and 1 s cost ~2× the idle CPU.
                _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
                _timer.Tick += (_, _) => Sample();
            }
            if (!_timer.IsEnabled)
            {
                // Start first and sample asynchronously: listeners call Need() again from
                // Updated, which must not re-enter Sample() (that recursed into a stack overflow).
                _timer.Start();
                _timer.Dispatcher.BeginInvoke(Sample, DispatcherPriority.Background);
            }
        }
        else _timer?.Stop();
    }

    private static bool _sampling;

    private static void Sample()
    {
        if (_sampling) return;
        _sampling = true;
        try { SampleCore(); } finally { _sampling = false; }
    }

    private static void SampleCore()
    {
        // CPU from system times (no performance counters needed).
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
            if (_kernel != 0 && dk + du > 0) Cpu = Math.Clamp(100.0 * (dk + du - di) / (dk + du), 0, 100);
            _idle = idle; _kernel = kernel; _user = user;
            Array.Copy(CpuHistory, 1, CpuHistory, 0, CpuHistory.Length - 1);
            CpuHistory[^1] = Cpu;
        }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            Memory = mem.dwMemoryLoad;
            MemoryTotalGb = mem.ullTotalPhys / 1073741824.0;
            MemoryUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
        }

        // Enumerating network adapters costs tens of milliseconds: only do it when shown.
        if (WantNetwork) try
        {
            long rx = 0, tx = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var st = nic.GetIPStatistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
            var now = DateTime.UtcNow;
            if (_netAt != default)
            {
                double secs = Math.Max(0.2, (now - _netAt).TotalSeconds);
                DownBps = Math.Max(0, (rx - _rx) / secs);
                UpBps = Math.Max(0, (tx - _tx) / secs);
            }
            _rx = rx; _tx = tx; _netAt = now;
        }
        catch { }

        if (GetSystemPowerStatus(out var ps) && ps.BatteryFlag != 128 && ps.BatteryLifePercent <= 100)
        {
            Battery = ps.BatteryLifePercent;
            Charging = ps.ACLineStatus == 1;
        }
        else Battery = null;

        Updated?.Invoke();
    }

    public static string Speed(double bps) => bps switch
    {
        >= 1048576 => $"{bps / 1048576:0.0}M",
        >= 1024 => $"{bps / 1024:0}K",
        _ => $"{bps:0}B",
    };

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
}
