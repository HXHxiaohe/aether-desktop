using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Aether.Interop;

namespace Aether.Shell;

public sealed class TaskWindow
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; set; } = "";
    public string? ExePath { get; init; }
    public string? AppId { get; init; }
    /// <summary>Grouping key: AUMID for packaged apps, else exe path.</summary>
    public string Key => AppId ?? ExePath ?? Hwnd.ToString();
}

/// <summary>
/// Tracks windows that would appear on the taskbar. Event-driven (WinEvent hooks)
/// with a short debounce, so it costs nothing while nothing changes.
/// </summary>
public static class TaskWindows
{
    private static readonly List<IntPtr> Hooks = new();
    private static Native.WinEventProc? _proc; // keep delegate alive
    private static DispatcherTimer? _debounce;
    private static readonly int OwnPid = Environment.ProcessId;
    private static readonly Dictionary<uint, string?> PathCache = new();
    private static readonly Dictionary<IntPtr, string?> AppIdCache = new();

    public static List<TaskWindow> Windows { get; private set; } = new();
    public static IntPtr Foreground { get; private set; }

    public static event Action? Changed;
    public static event Action? ForegroundChanged;

    public static void Start()
    {
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        _proc = OnEvent;
        uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
        Hooks.Add(Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, flags));
        Hooks.Add(Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _proc, 0, 0, flags));
        Hooks.Add(Native.SetWinEventHook(Native.EVENT_OBJECT_CLOAKED, Native.EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, _proc, 0, 0, flags));
        // Window created / destroyed / title changed come from the shell's taskbar notifications:
        // far rarer than system-wide show/hide/name-change WinEvents (caret blinks, tooltips…).
        ShellHook.Start();
        ShellHook.Message += OnShell;
        // Safety net for anything the notifications miss.
        _safety = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _safety.Tick += (_, _) => Refresh();
        _safety.Start();
        Foreground = Native.GetForegroundWindow();
        Refresh();
    }

    private static DispatcherTimer? _safety;

    private static void OnShell(int code, IntPtr hwnd)
    {
        if (code is ShellHook.HSHELL_WINDOWCREATED or ShellHook.HSHELL_WINDOWDESTROYED or ShellHook.HSHELL_REDRAW or ShellHook.HSHELL_WINDOWREPLACED)
            if (_debounce is { IsEnabled: false }) _debounce.Start();
    }

    public static void Stop()
    {
        ShellHook.Message -= OnShell;
        _safety?.Stop();
        foreach (var h in Hooks) if (h != IntPtr.Zero) Native.UnhookWinEvent(h);
        Hooks.Clear();
    }

    private static void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return; // OBJID_WINDOW only
        if (evt == Native.EVENT_SYSTEM_FOREGROUND)
        {
            if (Foreground != hwnd)
            {
                Foreground = hwnd;
                ForegroundChanged?.Invoke();
            }
            if (Known(hwnd)) return;
        }
        else if (evt == Native.EVENT_OBJECT_UNCLOAKED)
        {
            if (Known(hwnd) || !IsTaskbarWindow(hwnd)) return;
        }
        else if (!Known(hwnd))
        {
            return; // hide / destroy / rename / cloak / minimize of something we don't list
        }
        if (_debounce is { IsEnabled: false }) _debounce.Start();
    }

    private static bool Known(IntPtr hwnd)
    {
        foreach (var w in Windows) if (w.Hwnd == hwnd) return true;
        return false;
    }

    public static void Refresh()
    { Aether.Core.Diag.Hit("TaskWindows.Refresh");
        var list = new List<TaskWindow>();
        var seenPids = new HashSet<uint>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!IsTaskbarWindow(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == OwnPid) return true;
            seenPids.Add(pid);
            var exe = PathOf(pid);
            if (!AppIdCache.TryGetValue(hwnd, out var appId))
                AppIdCache[hwnd] = appId = Native.WindowAppId(hwnd);
            if (exe is not null && Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                exe = UwpChildPath(hwnd, pid) ?? exe;
            list.Add(new TaskWindow { Hwnd = hwnd, Title = Native.WindowTitle(hwnd), ExePath = exe, AppId = appId });
            return true;
        }, IntPtr.Zero);

        foreach (var dead in PathCache.Keys.Where(p => !seenPids.Contains(p)).ToList()) PathCache.Remove(dead);
        var live = list.Select(w => w.Hwnd).ToHashSet();
        foreach (var dead in AppIdCache.Keys.Where(h => !live.Contains(h)).ToList()) AppIdCache.Remove(dead);

        bool changed = list.Count != Windows.Count || list.Where((w, i) => w.Hwnd != Windows[i].Hwnd || w.Title != Windows[i].Title).Any();
        Windows = list;
        if (changed) Changed?.Invoke();
    }

    private static string? PathOf(uint pid)
    {
        if (PathCache.TryGetValue(pid, out var p)) return p;
        p = Native.ProcessPath(pid);
        PathCache[pid] = p;
        return p;
    }

    private static string? UwpChildPath(IntPtr frame, uint framePid)
    {
        string? found = null;
        Native.EnumChildWindows(frame, (child, _) =>
        {
            Native.GetWindowThreadProcessId(child, out uint pid);
            if (pid != framePid) { found = PathOf(pid); return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (!Native.IsWindowVisible(hwnd) || Native.IsCloaked(hwnd)) return false;
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if (!appWindow)
        {
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;
            if ((ex & Native.WS_EX_NOACTIVATE) != 0) return false;
            if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return false;
        }
        if (Native.GetWindowTextLength(hwnd) == 0) return false;
        var cls = Native.ClassOf(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow") return false;
        return true;
    }

    /// <summary>Taskbar-like click: focus, or minimize if it's already in front.</summary>
    public static void ToggleOrActivate(TaskWindow w)
    {
        if (!Native.IsWindow(w.Hwnd)) return;
        if (Foreground == w.Hwnd && !Native.IsIconic(w.Hwnd))
            Native.ShowWindow(w.Hwnd, Native.SW_MINIMIZE);
        else
            Native.Activate(w.Hwnd);
    }

    public static void Close(TaskWindow w) => Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>True when the foreground window covers its whole monitor (games, video).</summary>
    public static bool ForegroundIsFullscreen()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var cls = Native.ClassOf(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == OwnPid) return false;
        // With the taskbar hidden a maximized window covers the whole monitor too. Keep those
        // apart: maximized windows keep their caption / sizing frame; real fullscreen (games,
        // F11 in a browser) drops both.
        if (Native.IsZoomed(fg))
        {
            long style = Native.GetWindowLongPtr(fg, Native.GWL_STYLE).ToInt64();
            const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
            if ((style & WS_CAPTION) == WS_CAPTION || (style & WS_THICKFRAME) != 0) return false;
        }
        if (!Native.GetWindowRect(fg, out var r)) return false;
        var m = Native.MonitorBounds(fg);
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }
}
