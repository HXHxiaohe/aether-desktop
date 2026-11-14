using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Aether.Core;
using Aether.Shell;

namespace Aether.Island;

public sealed class CollectedWindow
{
    public long Handle { get; set; }
    public string Title { get; set; } = "";
    public string? ExePath { get; set; }
    /// <summary>Where the window was before it was dragged to the island (screen pixels).</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public DateTime At { get; set; } = DateTime.Now;

    [System.Text.Json.Serialization.JsonIgnore] public BitmapSource? Snapshot { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public IntPtr Hwnd => new(Handle);
}

/// <summary>
/// 窗口收纳: windows dragged onto the island are hidden (with a "genie" animation) and listed on
/// the shelf; clicking one brings it back where it was. Collected windows are always shown again
/// when Aether quits — and, should it crash, on its next start (Data\collected.json).
/// Drag detection uses EVENT_SYSTEM_MOVESIZESTART/END (no polling while nothing is dragged).
/// </summary>
public static class WindowCollector
{
    private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A, EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int SW_HIDE = 0, SW_SHOW = 5, SW_RESTORE = 9;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    // Always the *Async* variant: other apps' windows answer on their own thread — a hung app (or
    // one busy waiting on us) must never freeze Aether.
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr h);
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; public int W => R - L; public int H => B - T; }

    private static IntPtr _hook;
    private static WinEventProc? _proc; // keep the delegate alive
    private static readonly List<CollectedWindow> _items = new();

    public static IReadOnlyList<CollectedWindow> Items => _items;
    public static event Action? Changed;
    /// <summary>A window started being dragged by its title bar (hwnd, rect at start).</summary>
    public static event Action<IntPtr, RECT>? DragStarted;
    public static event Action<IntPtr>? DragEnded;

    private static IslandSettings S => SettingsStore.Current.Island;

    public static void Start()
    {
        RecoverAfterCrash();
        _proc = OnWinEvent;
        _hook = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>Quit: give every collected window back.</summary>
    public static void Stop()
    {
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        foreach (var w in _items.ToList()) ShowBack(w, activate: false);
        _items.Clear();
        Save();
    }

    private static void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero || !S.WindowCollect) return;
        if (ev == EVENT_SYSTEM_MOVESIZESTART)
        {
            if (!Eligible(hwnd)) return;
            GetWindowRect(hwnd, out var r);
            DragStarted?.Invoke(hwnd, r);
        }
        else if (ev == EVENT_SYSTEM_MOVESIZEEND) DragEnded?.Invoke(hwnd);
    }

    /// <summary>Ordinary app windows only (not Aether, not the desktop / taskbar / tool windows).</summary>
    public static bool Eligible(IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return false;
        return TaskWindows.IsTaskbarWindow(hwnd);
    }

    public static string TitleOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Visible bounds (without the invisible resize borders) in screen pixels.</summary>
    public static RECT BoundsOf(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(hwnd, out r);
        return r;
    }

    /// <summary>The window as it looks now (PW_RENDERFULLCONTENT also captures GPU-drawn windows).</summary>
    public static BitmapSource? Capture(IntPtr hwnd)
    {
        if (IsHungAppWindow(hwnd)) return null; // PrintWindow would wait on it
        GetWindowRect(hwnd, out var wr);
        var vis = BoundsOf(hwnd);
        if (wr.W <= 0 || wr.H <= 0 || wr.W > 8000 || wr.H > 8000) return null;
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, wr.W, wr.H);
        var old = SelectObject(mem, bmp);
        try
        {
            if (!PrintWindow(hwnd, mem, 2)) return null;
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // crop the invisible borders away
            int cx = Math.Max(0, vis.L - wr.L), cy = Math.Max(0, vis.T - wr.T);
            int cw = Math.Min(vis.W, wr.W - cx), ch = Math.Min(vis.H, wr.H - cy);
            BitmapSource img = cw > 0 && ch > 0 && (cw != wr.W || ch != wr.H) ? new CroppedBitmap(src, new Int32Rect(cx, cy, cw, ch)) : src;
            // the animation doesn't need full resolution
            double scale = Math.Min(1, 900.0 / Math.Max(img.PixelWidth, img.PixelHeight));
            if (scale < 1) img = new TransformedBitmap(img, new System.Windows.Media.ScaleTransform(scale, scale));
            img.Freeze();
            return img;
        }
        catch (Exception ex) { Log.Info("[collect] capture " + ex.Message); return null; }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Hide the window. Returns the entry, or null when Windows refuses (e.g. an elevated app).</summary>
    public static async System.Threading.Tasks.Task<CollectedWindow?> CollectAsync(IntPtr hwnd, RECT original, BitmapSource? snapshot)
    {
        string? exe = null;
        try { GetWindowThreadProcessId(hwnd, out uint pid); using var p = Process.GetProcessById((int)pid); exe = p.MainModule?.FileName; } catch { }
        ShowWindowAsync(hwnd, SW_HIDE);
        // UIPI may refuse (elevated apps); a hung app never gets to it — give it a moment, then judge
        for (int i = 0; i < 10 && IsWindowVisible(hwnd); i++) await System.Threading.Tasks.Task.Delay(30);
        if (IsWindowVisible(hwnd)) return null;
        // put it back where it was before the drag, so it reappears there
        if (original.W > 0 && original.H > 0)
            SetWindowPos(hwnd, IntPtr.Zero, original.L, original.T, 0, 0, 0x0001 | 0x0004 | 0x0010 | SWP_ASYNCWINDOWPOS /* NOSIZE|NOZORDER|NOACTIVATE|ASYNC */);
        var item = new CollectedWindow
        {
            Handle = hwnd.ToInt64(), Title = TitleOf(hwnd), ExePath = exe,
            X = original.L, Y = original.T, W = original.W, H = original.H, Snapshot = snapshot,
        };
        _items.RemoveAll(i => i.Handle == item.Handle);
        _items.Insert(0, item);
        Save();
        Changed?.Invoke();
        return item;
    }

    /// <summary>Show it again (after the release animation) and bring it to the front.</summary>
    public static void Release(CollectedWindow w)
    {
        ShowBack(w, activate: true);
        _items.Remove(w);
        Save();
        Changed?.Invoke();
    }

    private static void ShowBack(CollectedWindow w, bool activate)
    {
        if (!IsWindow(w.Hwnd)) return;
        ShowWindowAsync(w.Hwnd, IsIconic(w.Hwnd) ? SW_RESTORE : SW_SHOW);
        if (activate) Interop.Native.Activate(w.Hwnd);
    }

    /// <summary>Forget windows whose app has closed meanwhile.</summary>
    public static void Prune()
    {
        if (_items.RemoveAll(i => !IsWindow(i.Hwnd)) > 0) { Save(); Changed?.Invoke(); }
    }

    // ---- crash safety ----
    private static void Save() => JsonFile.Save("collected.json", _items);

    private static void RecoverAfterCrash()
    {
        var left = JsonFile.Load<List<CollectedWindow>>("collected.json");
        foreach (var w in left)
            if (IsWindow(w.Hwnd) && !IsWindowVisible(w.Hwnd)) { ShowWindowAsync(w.Hwnd, SW_SHOW); Log.Info("[collect] restored after restart: " + w.Title); }
        if (left.Count > 0) Save();
    }
}
