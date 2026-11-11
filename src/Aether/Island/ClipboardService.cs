using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Aether.Core;

namespace Aether.Island;

public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Text, or null for an image.</summary>
    public string? Text { get; set; }
    /// <summary>PNG file name inside Data\Clipboard (images only).</summary>
    public string? Image { get; set; }
    public int ImageW { get; set; }
    public int ImageH { get; set; }
    public bool Pinned { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;

    public string? ImagePath => Image is null ? null : Path.Combine(ClipboardService.ImageDir, Image);
}

/// <summary>
/// Clipboard history (置物架 › 剪贴板). Event-driven via AddClipboardFormatListener — no polling.
/// Skips anything a password manager / remote desktop marks as private
/// (ExcludeClipboardContentFromMonitorProcessing, Clipboard Viewer Ignore, CanIncludeInClipboardHistory = 0).
/// Copying an entry back from the list doesn't reorder it (our own marker format).
/// Kept locally in Data\clipboard.json (+ Data\Clipboard\*.png for images).
/// </summary>
public static class ClipboardService
{
    public const int MaxRecent = 30, MaxPinned = 20, MaxChars = 20000;
    private const string OurMarker = "Aether.ClipboardHistoryCopy";
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    internal static string ImageDir => Path.Combine(Paths.DataDir, "Clipboard");
    private static List<ClipItem> _items = new();
    private static HwndSource? _source;
    private static DispatcherTimer? _saveTimer, _readTimer;

    public static IReadOnlyList<ClipItem> Items => _items;
    public static event Action? Changed;

    private static IslandSettings S => SettingsStore.Current.Island;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public static void Start()
    {
        _items = JsonFile.Load<List<ClipItem>>("clipboard.json");
        _items.RemoveAll(i => i.Text is null && (i.ImagePath is null || !File.Exists(i.ImagePath)));
        var p = new HwndSourceParameters("AetherClipboard") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, Width = 0, Height = 0 };
        _source = new HwndSource(p);
        _source.AddHook(Hook);
        AddClipboardFormatListener(_source.Handle);
        // Owners often write several formats in a row; read once things settle.
        _readTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _readTimer.Tick += (_, _) => { _readTimer.Stop(); Read(); };
        CleanScreenshots();
    }

    public static void Stop()
    {
        if (_source is not null) { RemoveClipboardFormatListener(_source.Handle); _source.Dispose(); _source = null; }
        if (_saveTimer is { IsEnabled: true }) { _saveTimer.Stop(); Save(); }
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && (S.ClipboardHistory || S.ScreenshotShelf) && !Shell.GameMode.Active)
        {
            _owner = OwnerName(); // the owner is right now; by the time we read it may be gone
            _readTimer?.Stop(); _readTimer?.Start();
        }
        return IntPtr.Zero;
    }

    // ---------------- screenshots → 置物架 ----------------
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    private static string _owner = "";

    /// <summary>Win+Shift+S / PrtSc go through the Snipping Tool (or the older screen-clip host).</summary>
    private static readonly string[] ScreenshotTools = { "SnippingTool", "ScreenClippingHost", "ScreenSketch" };

    internal static string ShotDir => Path.Combine(Paths.DataDir, "Screenshots");

    /// <summary>A screenshot was saved and put on the shelf (path of the PNG).</summary>
    public static event Action<string>? ScreenshotTaken;

    private static string OwnerName()
    {
        try
        {
            var h = GetClipboardOwner();
            if (h == IntPtr.Zero) return "";
            GetWindowThreadProcessId(h, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    private static void SaveScreenshot(BitmapSource img)
    {
        Directory.CreateDirectory(ShotDir);
        // A readable name: it becomes the file name when dragged into WeChat / a folder.
        var path = Path.Combine(ShotDir, $"截图 {DateTime.Now:yyyy-MM-dd HHmmss}.png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using (var fs = File.Create(path)) enc.Save(fs);
        ShelfService.Add(new[] { path });
        ScreenshotTaken?.Invoke(path);
    }

    /// <summary>Screenshots older than 7 days that are no longer on the shelf are deleted.</summary>
    private static void CleanScreenshots()
    {
        try
        {
            if (!Directory.Exists(ShotDir)) return;
            var onShelf = ShelfService.Items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(ShotDir, "*.png"))
                if (!onShelf.Contains(f) && File.GetCreationTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f);
        }
        catch (Exception ex) { Log.Info("[shots] clean " + ex.Message); }
    }

    private static void Read()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || data.GetDataPresent(OurMarker) || IsPrivate(data)) return;
            bool shot = S.ScreenshotShelf && ScreenshotTools.Any(t => _owner.Equals(t, StringComparison.OrdinalIgnoreCase))
                        && data.GetDataPresent(DataFormats.Bitmap);
            if (shot)
            {
                var img = Clipboard.GetImage();
                if (img is not null) SaveScreenshot(img);
                if (!S.ClipboardHistory || !S.ClipboardImages) return;
            }
            if (!S.ClipboardHistory) return;
            if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = data.GetData(DataFormats.UnicodeText) as string;
                if (string.IsNullOrWhiteSpace(text) || text.Length > MaxChars) return;
                Push(new ClipItem { Text = text });
            }
            else if (S.ClipboardImages && data.GetDataPresent(DataFormats.Bitmap) && !data.GetDataPresent(DataFormats.FileDrop))
            {
                var img = Clipboard.GetImage();
                if (img is null || img.PixelWidth * (long)img.PixelHeight > 40_000_000) return;
                Directory.CreateDirectory(ImageDir);
                var name = Guid.NewGuid().ToString("N") + ".png";
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using (var fs = File.Create(Path.Combine(ImageDir, name))) enc.Save(fs);
                Push(new ClipItem { Image = name, ImageW = img.PixelWidth, ImageH = img.PixelHeight });
            }
        }
        catch (Exception ex) { Log.Info("[clipboard] " + ex.Message); }
    }

    /// <summary>Password managers and remote desktop tag secrets so history tools leave them alone.</summary>
    private static bool IsPrivate(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore")) return true;
        if (!data.GetDataPresent("CanIncludeInClipboardHistory")) return false;
        try
        {
            if (data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
            {
                var b = ms.ToArray();
                return BitConverter.ToInt32(b, 0) == 0;
            }
        }
        catch { }
        return true;
    }

    private static void Push(ClipItem item)
    {
        var same = item.Text is not null ? _items.FirstOrDefault(i => i.Text == item.Text) : null;
        if (same is not null)
        {
            _items.Remove(same);
            same.Time = DateTime.Now;
            item = same;
        }
        _items.Insert(0, item);
        Trim();
        Touch();
    }

    private static void Trim()
    {
        int recent = 0;
        foreach (var i in _items.ToList())
        {
            if (i.Pinned) continue;
            if (++recent > MaxRecent) Drop(i);
        }
    }

    private static void Drop(ClipItem i)
    {
        _items.Remove(i);
        if (i.ImagePath is { } p) try { File.Delete(p); } catch { }
    }

    private static void Touch()
    {
        Changed?.Invoke();
        _saveTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _saveTimer.Tick -= OnSave;
        _saveTimer.Tick += OnSave;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static void OnSave(object? s, EventArgs e) { _saveTimer!.Stop(); Save(); }
    private static void Save() => JsonFile.Save("clipboard.json", _items);

    /// <summary>Put an entry back on the clipboard without moving it in the list.</summary>
    public static bool Copy(ClipItem i)
    {
        try
        {
            var d = new DataObject();
            if (i.Text is not null) d.SetData(DataFormats.UnicodeText, i.Text);
            else if (i.ImagePath is { } p && File.Exists(p))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(p); bmp.EndInit();
                d.SetImage(bmp);
            }
            else return false;
            d.SetData(OurMarker, "1");
            Clipboard.SetDataObject(d, true);
            return true;
        }
        catch (Exception ex) { Log.Info("[clipboard] copy " + ex.Message); return false; }
    }

    public static bool TogglePin(ClipItem i)
    {
        if (!i.Pinned && _items.Count(x => x.Pinned) >= MaxPinned) return false;
        i.Pinned = !i.Pinned;
        Trim();
        Touch();
        return true;
    }

    public static void Remove(ClipItem i) { Drop(i); Touch(); }

    /// <summary>Clear everything that isn't pinned.</summary>
    public static void ClearRecent()
    {
        foreach (var i in _items.Where(i => !i.Pinned).ToList()) Drop(i);
        Touch();
    }

    /// <summary>One-line preview: newlines and runs of whitespace collapsed.</summary>
    public static string Preview(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
