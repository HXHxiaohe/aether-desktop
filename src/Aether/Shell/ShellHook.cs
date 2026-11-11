using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Aether.Core;
using Aether.Interop;

namespace Aether.Shell;

/// <summary>
/// The shell's own taskbar notifications (window created / destroyed / activated / title
/// redrawn / flashing). These are what real taskbars use: rare, precise events — unlike
/// system-wide WinEvent hooks, which fire for every caret blink and tooltip.
/// </summary>
public static class ShellHook
{
    public const int HSHELL_WINDOWCREATED = 1, HSHELL_WINDOWDESTROYED = 2, HSHELL_WINDOWACTIVATED = 4,
        HSHELL_REDRAW = 6, HSHELL_WINDOWREPLACED = 13, HSHELL_RUDEAPPACTIVATED = 0x8004, HSHELL_FLASH = 0x8006;

    [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(IntPtr hwnd);

    private static HwndSource? _sink;
    private static uint _msg;

    /// <summary>(code, hwnd) on the UI thread.</summary>
    public static event Action<int, IntPtr>? Message;

    public static void Start()
    {
        if (_sink is not null) return;
        try
        {
            // Message-only window: receives shell hook messages, never shown.
            _sink = new HwndSource(new HwndSourceParameters("AetherShellHook") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
            _msg = Native.RegisterWindowMessage("SHELLHOOK");
            _sink.AddHook(WndProc);
            RegisterShellHookWindow(_sink.Handle);
        }
        catch (Exception ex)
        {
            Log.Error("shellhook-start", ex);
        }
    }

    public static void Stop()
    {
        if (_sink is null) return;
        DeregisterShellHookWindow(_sink.Handle);
        _sink.Dispose();
        _sink = null;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_msg != 0 && msg == _msg) Message?.Invoke(wParam.ToInt32() & 0x7FFF | (wParam.ToInt32() & 0x8000), lParam);
        return IntPtr.Zero;
    }
}
