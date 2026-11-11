using System;
using System.Runtime.InteropServices;
using Aether.Core;
using Aether.Interop;

namespace Aether.Shell;

/// <summary>
/// Hides the Windows taskbar while Aether's Dock is in charge, and puts it back exactly
/// as it was on exit. Auto-hide frees the screen space; hiding the window stops it
/// sliding up when the mouse reaches the bottom edge.
/// </summary>
public static class Taskbar
{
    public static bool IsHiddenByUs { get; private set; }

    private static IntPtr Tray => Native.FindWindow("Shell_TrayWnd", null);

    public static void Hide()
    {
        var s = SettingsStore.Current;
        if (s.TaskbarWasAutoHide is null)
        {
            s.TaskbarWasAutoHide = GetAutoHide();
            SettingsStore.SaveNow(); // persist before touching anything, in case we crash
        }
        SetAutoHide(true);
        SetTrayVisible(false);
        IsHiddenByUs = true;
    }

    public static void Restore()
    {
        var s = SettingsStore.Current;
        SetTrayVisible(true);
        if (s.TaskbarWasAutoHide is bool was)
        {
            SetAutoHide(was);
            s.TaskbarWasAutoHide = null;
            SettingsStore.SaveNow();
        }
        IsHiddenByUs = false;
    }

    private static DateTime _peekUntil;

    /// <summary>True for a few seconds after <see cref="PeekTray"/> (the Dock steps aside).</summary>
    public static bool IsPeeking => DateTime.Now < _peekUntil;

    /// <summary>
    /// Briefly bring back the Windows taskbar with keyboard focus on the tray (Win+B), so tray
    /// icons (WeChat, QQ, …) are reachable while the Dock replaces the taskbar.
    /// </summary>
    public static void PeekTray()
    {
        _peekUntil = DateTime.Now.AddSeconds(12);
        SetTrayVisible(true);
        Native.keybd_event(0x5B, 0, 0, UIntPtr.Zero);      // Win down
        Native.keybd_event(0x42, 0, 0, UIntPtr.Zero);      // B
        Native.keybd_event(0x42, 0, 2, UIntPtr.Zero);
        Native.keybd_event(0x5B, 0, 2, UIntPtr.Zero);      // Win up
    }

    /// <summary>Called periodically: Explorer re-shows the taskbar after the Start menu etc.</summary>
    public static void Enforce()
    {
        if (!IsHiddenByUs) return;
        if (IsPeeking) return;
        var tray = Tray;
        if (tray == IntPtr.Zero || !Native.IsWindowVisible(tray)) return;
        // Leave it alone while the user is in Start / search (they live on the taskbar thread).
        var fg = Native.GetForegroundWindow();
        var cls = Native.ClassOf(fg);
        if (fg == tray || cls is "Windows.UI.Core.CoreWindow" or "Shell_TrayWnd" or "XamlExplorerHostIslandWindow") return;
        SetTrayVisible(false);
    }

    private static void SetTrayVisible(bool visible)
    {
        int cmd = visible ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE;
        var tray = Tray;
        if (tray != IntPtr.Zero) Native.ShowWindow(tray, cmd);
        var second = IntPtr.Zero;
        while ((second = Native.FindWindowEx(IntPtr.Zero, second, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            Native.ShowWindow(second, cmd);
    }

    private static bool GetAutoHide()
    {
        var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = Tray };
        long state = Native.SHAppBarMessage(Native.ABM_GETSTATE, ref abd).ToInt64();
        return (state & Native.ABS_AUTOHIDE) != 0;
    }

    private static void SetAutoHide(bool on)
    {
        var abd = new Native.APPBARDATA
        {
            cbSize = Marshal.SizeOf<Native.APPBARDATA>(),
            hWnd = Tray,
            lParam = new IntPtr(on ? Native.ABS_AUTOHIDE : Native.ABS_ALWAYSONTOP),
        };
        Native.SHAppBarMessage(Native.ABM_SETSTATE, ref abd);
    }
}
