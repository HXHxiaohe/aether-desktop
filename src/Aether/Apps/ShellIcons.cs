using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Aether.Apps;

/// <summary>
/// High-quality icons for files, shortcuts and packaged apps via IShellItemImageFactory.
/// Results are cached (frozen bitmaps, safe across threads).
/// </summary>
public static class ShellIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(int sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false, EntryPoint = "SHCreateItemFromParsingName")]
    private static extern void CreateItem(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    /// <summary>Friendly name of a file / app ("shell:AppsFolder\AUMID" → "Microsoft Outlook"), or null.</summary>
    public static string? DisplayName(string path)
    {
        try
        {
            CreateItem(path, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
            try { item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var name); return name; }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch { return null; }
    }

    private const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_ICONONLY = 0x4, SIIGBF_BIGGERSIZEOK = 0x1;

    public static ImageSource? Get(string path, int size = 96)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var key = path + "|" + size;
        lock (Gate)
            if (Cache.TryGetValue(key, out var hit)) return hit;

        var diskFile = DiskCacheFile(path, size);
        ImageSource? img = diskFile is not null ? ReadPng(diskFile) : null;
        if (img is null)
        {
            // Internet shortcuts (Steam games…) point at their own .ico; the shell renders those framed.
            if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) && UrlIconFile(path) is { } ico)
                img = ico.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ? LoadIco(ico) ?? Load(ico, size) : Load(ico, size);
            img ??= Load(path, size);
            if (img is BitmapSource bs && diskFile is not null) WritePng(bs, diskFile);
        }
        lock (Gate) Cache[key] = img;
        return img;
    }

    // ---- disk cache: Data\IconCache\<hash>.png, invalidated when the file changes ----
    private static string? DiskCacheFile(string path, int size)
    {
        try
        {
            long stamp = 0;
            if (System.IO.File.Exists(path)) stamp = System.IO.File.GetLastWriteTimeUtc(path).Ticks;
            var raw = System.Text.Encoding.UTF8.GetBytes($"{path.ToLowerInvariant()}|{size}|{stamp}|v2");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(raw))[..20];
            var dir = System.IO.Path.Combine(Core.Paths.DataDir, "IconCache");
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, hash + ".png");
        }
        catch { return null; }
    }

    private static ImageSource? ReadPng(string file)
    {
        if (!System.IO.File.Exists(file)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(file);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    private static void WritePng(BitmapSource bmp, string file)
    {
        try
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = System.IO.File.Create(file);
            enc.Save(fs);
        }
        catch { }
    }

    /// <summary>Largest frame of an .ico file, decoded directly (no shell framing).</summary>
    private static ImageSource? LoadIco(string file)
    {
        try
        {
            using var fs = System.IO.File.OpenRead(file);
            var dec = new IconBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapFrame? best = null;
            foreach (var f in dec.Frames)
                if (best is null || f.PixelWidth * (f.Format.BitsPerPixel + 1) > best.PixelWidth * (best.Format.BitsPerPixel + 1)) best = f;
            best?.Freeze();
            return best;
        }
        catch
        {
            return null;
        }
    }

    private static string? UrlIconFile(string urlFile)
    {
        try
        {
            foreach (var line in System.IO.File.ReadLines(urlFile))
            {
                if (!line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) continue;
                var p = Environment.ExpandEnvironmentVariables(line[9..].Trim().Trim('"'));
                return System.IO.File.Exists(p) ? p : null;
            }
        }
        catch { }
        return null;
    }

    private static ImageSource? Load(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK | SIIGBF_RESIZETOFIT, out hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // HBITMAPs from the shell carry premultiplied alpha but WPF reads them as Bgr32/Bgra32 inconsistently.
            var fixedSrc = FixAlpha(src, out bool framed);
            if (framed && size > 48)
            {
                // Program only has small icons: the shell drew a 32px icon inside a frame. Use the 48px one instead.
                DeleteObject(hbm);
                hbm = IntPtr.Zero;
                return Load(path, 48);
            }
            fixedSrc.Freeze();
            return fixedSrc;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    private static BitmapSource FixAlpha(BitmapSource src, out bool framed)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        conv.CopyPixels(px, stride, 0);
        bool anyAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        framed = LooksFramed(px, w, h, stride);
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, stride);
    }

    /// <summary>
    /// The shell's "small icon in a jumbo slot" rendering: a thin opaque light frame
    /// around a mostly empty square. Detect it by looking for an opaque, light,
    /// uniform border inset by a pixel or two on all four sides.
    /// </summary>
    private static bool LooksFramed(byte[] px, int w, int h, int stride)
    {
        if (w < 64 || h < 64) return false;
        int Alpha(int x, int y) => px[y * stride + x * 4 + 3];
        int Lum(int x, int y) { int i = y * stride + x * 4; return (px[i] + px[i + 1] + px[i + 2]) / 3; }

        for (int inset = 0; inset <= 6; inset++)
        {
            int top = inset, bottom = h - 1 - inset, left = inset, right = w - 1 - inset;
            int hits = 0, total = 0;
            for (int x = left + 4; x < right - 4; x += 3)
            {
                total += 2;
                if (Alpha(x, top) > 200 && Lum(x, top) > 150) hits++;
                if (Alpha(x, bottom) > 200 && Lum(x, bottom) > 150) hits++;
            }
            for (int y = top + 4; y < bottom - 4; y += 3)
            {
                total += 2;
                if (Alpha(left, y) > 200 && Lum(left, y) > 150) hits++;
                if (Alpha(right, y) > 200 && Lum(right, y) > 150) hits++;
            }
            if (total > 0 && hits > total * 0.9)
            {
                // And the area just inside the frame must be mostly transparent or flat.
                int inner = inset + 4, transparent = 0, samples = 0;
                for (int x = inner; x < w - inner; x += 4)
                {
                    samples++;
                    if (Alpha(x, inner) < 40 || Math.Abs(Lum(x, inner) - Lum(x, top)) < 12) transparent++;
                }
                return samples > 0 && transparent > samples * 0.7;
            }
        }
        return false;
    }
}
