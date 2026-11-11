using System;
using System.IO;

namespace Aether.Core;

/// <summary>Portable layout: everything lives in a Data folder next to Aether.exe.</summary>
internal static class Paths
{
    public static string AppDir { get; } = AppContext.BaseDirectory;
    public static string DataDir { get; } = Path.Combine(AppDir, "Data");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string ErrorLog => Path.Combine(DataDir, "error.log");

    public static void Ensure() => Directory.CreateDirectory(DataDir);
}

/// <summary>Give memory back after big windows (settings, drawer) close.</summary>
internal static class Memory
{
    private static System.Windows.Threading.DispatcherTimer? _timer;

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    public static void TrimSoon()
    {
        if (_timer is null)
        {
            _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(4) };
            _timer.Tick += (_, _) => { _timer!.Stop(); TrimNow(); };
        }
        _timer.Stop();
        _timer.Start();
    }

    public static void TrimNow()
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            EmptyWorkingSet(me.Handle);
            if (Environment.GetEnvironmentVariable("AETHER_MEMLOG") == "1")
            {
                var info = GC.GetGCMemoryInfo();
                Log.Info($"[mem] managed={GC.GetTotalMemory(false) / 1048576.0:F1}MB heap={info.HeapSizeBytes / 1048576.0:F1}MB committed={info.TotalCommittedBytes / 1048576.0:F1}MB private={me.PrivateMemorySize64 / 1048576.0:F1}MB");
            }
        }
        catch { }
    }
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Error(string where, Exception ex) => Write($"[{where}] {ex}");

    public static void Info(string message) => Write(message);

    private static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Paths.Ensure();
                var file = new FileInfo(Paths.ErrorLog);
                if (file.Exists && file.Length > 512 * 1024)
                {
                    File.Copy(file.FullName, file.FullName + ".old", true);
                    file.Delete();
                }
                File.AppendAllText(Paths.ErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never crash the app
        }
    }
}
