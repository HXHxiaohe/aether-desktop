using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aether.Apps;
using Aether.Interop;
using Aether.Shell;

namespace Aether.Island;

/// <summary>An app asking for attention (its taskbar button would be flashing). One per process = one per account when WeChat runs twice.</summary>
public sealed record Attention(IntPtr Hwnd, uint Pid, string ExePath, string Name, DateTime Since);

/// <summary>
/// "Unread" indicator without reading anything: listens for the shell's window-flash
/// notifications (what makes taskbar buttons blink — WeChat, QQ, any app).
///
/// Lessons from WeChat 4 (and 岛仔): the flashing window is often a hidden helper, not the
/// main frame, so entries are keyed by process, cleared when ANY window of that process comes
/// to the front, and opening never shows a hidden window (that white-screens WeChat) — it
/// switches to the process' visible main window, or relaunches the exe, which brings a
/// tray-minimised single-instance client back by itself.
/// </summary>
public static class AttentionService
{
    private static readonly Dictionary<uint, Attention> Items = new();
    /// <summary>Last flash per process: a continuous blink counts once; quiet 5 s = next one is new.</summary>
    private static readonly Dictionary<uint, DateTime> LastFlash = new();
    /// <summary>Process the user just looked at → ignore its trailing flashes for a moment.</summary>
    private static readonly Dictionary<uint, DateTime> Grace = new();
    private static uint _viewing;

    public static IReadOnlyList<Attention> Current => Items.Values.OrderBy(a => a.Since).ToList();

    /// <summary>Raised on the UI thread when the list changes. Argument = newly flashing item, if any.</summary>
    public static event Action<Attention?>? Changed;

    public static void Start()
    {
        ShellHook.Start();
        ShellHook.Message += OnShell;
        TaskWindows.ForegroundChanged += OnForeground;
    }

    public static void Stop()
    {
        ShellHook.Message -= OnShell;
        TaskWindows.ForegroundChanged -= OnForeground;
    }

    private static uint PidOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    private static void OnShell(int code, IntPtr target)
    {
        switch (code)
        {
            case ShellHook.HSHELL_FLASH:
                OnFlash(target);
                break;
            case ShellHook.HSHELL_WINDOWACTIVATED:
            case ShellHook.HSHELL_RUDEAPPACTIVATED:
                OnForeground();
                break;
            case ShellHook.HSHELL_WINDOWDESTROYED:
                // Only drop an entry when its whole process is gone (closing a chat popup isn't "read").
                foreach (var a in Items.Values.Where(a => a.Hwnd == target && Native.ProcessPath(a.Pid) is null).ToList())
                    if (Items.Remove(a.Pid)) Changed?.Invoke(null);
                break;
        }
    }

    private static void OnFlash(IntPtr target)
    {
        if (!Native.IsWindow(target)) return;
        uint pid = PidOf(target);
        if (pid == 0 || pid == Environment.ProcessId) return;
        if (PidOf(Native.ForegroundSafe()) == pid) return; // already looking at it

        var now = DateTime.Now;
        bool fresh = !LastFlash.TryGetValue(pid, out var last) || (now - last).TotalSeconds >= 5;
        LastFlash[pid] = now;
        if (Grace.TryGetValue(pid, out var until) && now < until) return; // blink left over from what was just read
        if (Items.ContainsKey(pid) || !fresh) return;

        var exe = Native.ProcessPath(pid);
        if (exe is null) return;
        var a = new Attention(target, pid, exe, AudioService.FriendlyName(exe), now);
        Items[pid] = a;
        Changed?.Invoke(a);
    }

    private static void OnForeground()
    {
        uint pid = PidOf(Native.ForegroundSafe());
        if (_viewing != 0 && _viewing != pid) Grace[_viewing] = DateTime.Now.AddSeconds(4);
        _viewing = pid;
        if (pid != 0 && Items.Remove(pid)) Changed?.Invoke(null);
    }

    /// <summary>Bring the app forward (which also clears its dot).</summary>
    public static void Open(Attention a)
    {
        if (Items.Remove(a.Pid)) Changed?.Invoke(null);
        var main = MainWindowOf(a.Pid) ?? MainWindowOfExe(a.ExePath);
        if (main is IntPtr h) { Native.Activate(h); return; }
        if (File.Exists(a.ExePath)) AppCatalog.Launch(a.ExePath);
    }

    public static void Dismiss(Attention a)
    {
        if (Items.Remove(a.Pid)) Changed?.Invoke(null);
    }

    /// <summary>The largest real, user-visible top-level window of a process (minimised counts, smallest priority).</summary>
    private static IntPtr? MainWindowOf(uint pid)
    {
        IntPtr best = IntPtr.Zero; long bestArea = -1;
        Native.EnumWindows((h, _) =>
        {
            if (PidOf(h) != pid || !TaskWindows.IsTaskbarWindow(h)) return true;
            long area = 0;
            if (!Native.IsIconic(h) && Native.GetWindowRect(h, out var r)) area = (long)r.Width * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best == IntPtr.Zero ? null : best;
    }

    private static IntPtr? MainWindowOfExe(string exe)
    {
        foreach (var w in TaskWindows.Windows)
            if (Native.IsWindow(w.Hwnd) && string.Equals(Native.ProcessPath(PidOf(w.Hwnd)), exe, StringComparison.OrdinalIgnoreCase))
                return w.Hwnd;
        return null;
    }
}
