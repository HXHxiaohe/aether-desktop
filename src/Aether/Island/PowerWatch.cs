using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Aether.Island;

/// <summary>Plugged in / unplugged and low-battery (20 % and 10 %) events. Silent on desktops.</summary>
public static class PowerWatch
{
    public enum Kind { Plugged, Unplugged, Low, Critical }

    /// <summary>(kind, battery percent) on the UI thread.</summary>
    public static event Action<Kind, int>? Event;

    private static bool? _ac;
    private static int _lastWarned = 101;
    private static DispatcherTimer? _timer;

    public static void Start()
    {
        Read(out var ac, out _);
        _ac = ac;
        SystemEvents.PowerModeChanged += OnPowerMode;
        // Battery level only changes slowly; a check every minute is plenty.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    public static void Stop()
    {
        SystemEvents.PowerModeChanged -= OnPowerMode;
        _timer?.Stop();
    }

    private static void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange)
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(Check);
    }

    private static void Check()
    {
        if (!Read(out var ac, out var pct)) return;
        if (_ac is bool was && was != ac)
        {
            Event?.Invoke(ac ? Kind.Plugged : Kind.Unplugged, pct);
            if (ac) _lastWarned = 101;
        }
        _ac = ac;
        if (!ac)
        {
            if (pct <= 10 && _lastWarned > 10) { _lastWarned = 10; Event?.Invoke(Kind.Critical, pct); }
            else if (pct <= 20 && _lastWarned > 20) { _lastWarned = 20; Event?.Invoke(Kind.Low, pct); }
        }
    }

    private static bool Read(out bool ac, out int pct)
    {
        ac = true; pct = 100;
        if (!GetSystemPowerStatus(out var s) || s.BatteryFlag == 128 || s.BatteryLifePercent > 100) return false;
        ac = s.ACLineStatus == 1;
        pct = s.BatteryLifePercent;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
}
