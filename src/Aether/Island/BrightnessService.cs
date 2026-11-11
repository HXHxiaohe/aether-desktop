using System;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows;
using Aether.Core;

namespace Aether.Island;

/// <summary>
/// Built-in display brightness via WMI (laptops). External monitors don't expose this, in which
/// case <see cref="Available"/> stays false and the island just hides the brightness slider.
/// </summary>
public static class BrightnessService
{
    private static ManagementEventWatcher? _watcher;

    public static bool Available { get; private set; }
    public static int Level { get; private set; }

    /// <summary>Raised on the UI thread when the brightness changes (keys, Windows settings, us).</summary>
    public static event Action<int>? Changed;

    public static void Start()
    {
        Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                var first = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (first is null) return;
                Level = Convert.ToInt32(first["CurrentBrightness"]);
                Available = true;

                _watcher = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                _watcher.EventArrived += (_, e) =>
                {
                    try
                    {
                        int v = Convert.ToInt32(e.NewEvent["Brightness"]);
                        if (v == Level) return;
                        Level = v;
                        Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke(v));
                    }
                    catch { }
                };
                _watcher.Start();
            }
            catch (ManagementException)
            {
                Available = false; // desktop monitor: not supported
            }
            catch (Exception ex)
            {
                Log.Error("brightness-start", ex);
            }
        });
    }

    public static void Set(int level)
    {
        level = Math.Clamp(level, 0, 100);
        Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject m in searcher.Get())
                {
                    m.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)level });
                    break;
                }
            }
            catch (Exception ex) { Log.Error("brightness-set", ex); }
        });
    }

    public static void Stop()
    {
        try { _watcher?.Stop(); _watcher?.Dispose(); } catch { }
        _watcher = null;
    }
}
