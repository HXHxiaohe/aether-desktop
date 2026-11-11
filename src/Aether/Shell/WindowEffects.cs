using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Aether.Interop;

namespace Aether.Shell;

/// <summary>DWM helpers: transparent client area, rounded corners, blur, desktop pinning.</summary>
internal static class WindowEffects
{
    /// <summary>Make the WPF client area see-through (non-layered, DWM-composited).</summary>
    public static void MakeTransparent(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
            target.BackgroundColor = Colors.Transparent;
        var m = new Native.MARGINS(-1);
        Native.DwmExtendFrameIntoClientArea(hwnd, ref m);
    }

    public static void SetCorners(IntPtr hwnd, int preference)
    {
        int v = preference;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int));
    }

    public static void SetBorderColor(IntPtr hwnd, int colorRef)
    {
        int v = colorRef;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref v, sizeof(int));
    }

    public static void SetDarkMode(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
    }

    public static void SetBackdrop(IntPtr hwnd, int type)
    {
        int v = type;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref v, sizeof(int));
    }

    /// <summary>
    /// Blur-behind via the accent policy. Unlike the DWM system backdrops this keeps
    /// blurring while the window is inactive, which desktop widgets always are.
    /// </summary>
    /// <summary>Debug switches for testing visuals (never set in normal use).</summary>
    public static string GlassMode => Environment.GetEnvironmentVariable("AETHER_GLASS") ?? "blur";
    public static bool DebugWidgetsOnTop => Environment.GetEnvironmentVariable("AETHER_WIDGET_TOP") == "1";
    public static bool DebugDockPinned => Environment.GetEnvironmentVariable("AETHER_DOCK_PIN") == "1";
    public static bool Layered => Environment.GetEnvironmentVariable("AETHER_LAYERED") == "1";

    public static void SetBlur(IntPtr hwnd, bool enabled, uint tintAbgr = 0)
    {
        // NOTE: once blur has been on, DWM can't make the window clear again (DISABLED → black,
        // TRANSPARENTGRADIENT → opaque). Callers recreate the window instead of turning blur off.
        int state = !enabled || GlassMode == "none" ? Native.ACCENT_DISABLED
            : GlassMode == "acrylic" ? Native.ACCENT_ENABLE_ACRYLICBLURBEHIND
            : Native.ACCENT_ENABLE_BLURBEHIND;
        var accent = new Native.AccentPolicy
        {
            AccentState = state,
            AccentFlags = state == Native.ACCENT_ENABLE_ACRYLICBLURBEHIND ? 2 : 0,
            GradientColor = tintAbgr,
        };
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new Native.WindowCompositionAttributeData
            {
                Attribute = Native.WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size,
            };
            Native.SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static void AddExStyle(IntPtr hwnd, long add, long remove = 0)
    {
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex = (ex | add) & ~remove;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>
    /// Owner = the desktop's Progman window. Windows owned by the desktop are not
    /// hidden by Win+D / "show desktop", and stay above the wallpaper.
    /// </summary>
    public static void PinToDesktop(IntPtr hwnd)
    {
        var progman = Native.FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
            Native.SetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT, progman);
    }
}
