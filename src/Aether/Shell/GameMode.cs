using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Aether.Core;

namespace Aether.Shell;

/// <summary>
/// Fullscreen app in front (games, fullscreen video) → Aether gets out of the way: the island
/// and Dock hide (they already do), polling slows to once a second, background cards pause,
/// audio capture stops, memory is trimmed and Windows is told to run Aether in power-saving
/// (EcoQoS) mode. Everything resumes when the fullscreen app leaves the foreground.
/// </summary>
public static class GameMode
{
    public static bool Active { get; private set; }

    /// <summary>Raised on the UI thread when entering (true) or leaving (false).</summary>
    public static event Action<bool>? Changed;

    private static DispatcherTimer? _timer;
    private static int _confirm;

    public static void Start()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    public static void Stop() => _timer?.Stop();

    private static void Check()
    {
        bool fs = TaskWindows.ForegroundIsFullscreen();
        // Require two consecutive seconds before switching, so alt-tab flicker doesn't toggle it.
        if (fs == Active) { _confirm = 0; return; }
        if (++_confirm < 2) return;
        _confirm = 0;
        Active = fs;
        SetEcoQoS(fs);
        Island.AudioService.SetPaused(fs);
        Changed?.Invoke(fs);
        if (fs) Memory.TrimSoon();
        Log.Info($"[game] {(fs ? "enter" : "leave")}");
    }

    // ---- EcoQoS: lower clocks / efficiency cores for our process while gaming ----
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version, ControlMask, StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    private static void SetEcoQoS(bool on)
    {
        try
        {
            var s = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = on ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            };
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref s, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch { }
    }
}
