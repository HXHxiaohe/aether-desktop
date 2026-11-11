using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aether.Core;
using Aether.Interop;

namespace Aether.Theme;

/// <summary>
/// Finds an accent color for the current desktop background.
/// Order: Wallpaper Engine preview image -> Windows wallpaper file -> screen snapshot.
/// Everything works on a tiny (≤96px) decode so a pick costs a few milliseconds.
/// </summary>
public static class WallpaperColor
{
    public sealed record Result(Color Accent, string Source);

    public static Result? Pick()
    {
        try
        {
            var we = WallpaperEngine.CurrentPreview();
            if (we is not null)
            {
                var c = FromFile(we);
                if (c is not null) return new Result(c.Value, "Wallpaper Engine");
            }
        }
        catch (Exception ex) { Log.Error("pick-we", ex); }

        try
        {
            var file = WindowsWallpaperFile();
            if (file is not null)
            {
                var c = FromFile(file);
                if (c is not null) return new Result(c.Value, "Windows 壁纸");
            }
        }
        catch (Exception ex) { Log.Error("pick-wallpaper", ex); }

        try
        {
            var c = FromScreen();
            if (c is not null) return new Result(c.Value, "屏幕取样");
        }
        catch (Exception ex) { Log.Error("pick-screen", ex); }

        return null;
    }

    public static string? WindowsWallpaperFile()
    {
        var sb = new StringBuilder(520);
        if (!Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0)) return null;
        var path = sb.ToString();
        return File.Exists(path) ? path : null;
    }

    private static Color? FromFile(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        img.DecodePixelWidth = 96;
        img.UriSource = new Uri(path, UriKind.Absolute);
        img.EndInit();
        img.Freeze();
        return FromBitmap(img);
    }

    private static Color? FromScreen()
    {
        int w = (int)SystemParameters.PrimaryScreenWidth, h = (int)SystemParameters.PrimaryScreenHeight;
        using var bmp = new System.Drawing.Bitmap(96, Math.Max(1, 96 * h / Math.Max(1, w)));
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        using (var full = new System.Drawing.Bitmap(w, h))
        {
            using (var gf = System.Drawing.Graphics.FromImage(full))
                gf.CopyFromScreen(0, 0, 0, 0, full.Size);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(full, 0, 0, bmp.Width, bmp.Height);
        }
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return Analyze(bytes, bmp.Width, bmp.Height, data.Stride);
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Dominant, readable accent of any image (used for album-art tinting).</summary>
    public static Color? FromImage(BitmapSource src)
    {
        try
        {
            var small = src.PixelWidth > 64 ? new TransformedBitmap(src, new ScaleTransform(64.0 / src.PixelWidth, 64.0 / src.PixelWidth)) : src;
            return FromBitmap(small);
        }
        catch { return null; }
    }

    private static Color? FromBitmap(BitmapSource src)
    {
        var bgra = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var bytes = new byte[stride * h];
        bgra.CopyPixels(bytes, stride, 0);
        return Analyze(bytes, w, h, stride);
    }

    /// <summary>
    /// Hue histogram weighted by saturation*brightness; the winning hue bucket's
    /// average color becomes the accent, then normalized so it reads well on dark glass.
    /// </summary>
    private static Color? Analyze(byte[] px, int w, int h, int stride)
    {
        const int buckets = 36;
        var weight = new double[buckets];
        var sumR = new double[buckets];
        var sumG = new double[buckets];
        var sumB = new double[buckets];
        double avgR = 0, avgG = 0, avgB = 0;
        int n = 0;

        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                avgR += r; avgG += g; avgB += b; n++;
                RgbToHsv(r, g, b, out var hue, out var sat, out var val);
                if (sat < 0.18 || val < 0.18) continue;
                double wgt = sat * sat * val;
                int k = (int)(hue / 360.0 * buckets) % buckets;
                weight[k] += wgt;
                sumR[k] += r * wgt; sumG[k] += g * wgt; sumB[k] += b * wgt;
            }
        }
        if (n == 0) return null;

        // Smooth neighbouring buckets so a gradient sky doesn't split its vote.
        int best = -1;
        double bestScore = 0;
        for (int k = 0; k < buckets; k++)
        {
            double s = weight[k] + 0.5 * (weight[(k + 1) % buckets] + weight[(k + buckets - 1) % buckets]);
            if (s > bestScore) { bestScore = s; best = k; }
        }

        double rr, gg, bb;
        if (best < 0 || bestScore < n * 0.004)
        {
            // Almost colorless wallpaper: a calm blue-grey derived from the average brightness.
            return Color.FromRgb(0x9A, 0xA8, 0xC7);
        }
        rr = sumR[best] / weight[best];
        gg = sumG[best] / weight[best];
        bb = sumB[best] / weight[best];

        RgbToHsv(rr, gg, bb, out var h2, out var s2, out var v2);
        s2 = Math.Clamp(s2, 0.38, 0.72);
        v2 = Math.Clamp(Math.Max(v2, 0.82), 0.82, 0.96);
        HsvToRgb(h2, s2, v2, out rr, out gg, out bb);
        return Color.FromRgb((byte)(rr * 255), (byte)(gg * 255), (byte)(bb * 255));
    }

    public static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 1e-6) { h = 0; return; }
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    public static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        r += m; g += m; b += m;
    }
}

/// <summary>Reads Wallpaper Engine's config to find the preview image of the active wallpaper.</summary>
public static class WallpaperEngine
{
    public static string? ConfigPath()
    {
        foreach (var name in new[] { "wallpaper64", "wallpaper32" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (exe is null) continue;
                    var cfg = Path.Combine(Path.GetDirectoryName(exe)!, "config.json");
                    if (File.Exists(cfg)) return cfg;
                }
                catch { /* access denied etc. */ }
                finally { p.Dispose(); }
            }
        }
        return null;
    }

    public static string? CurrentPreview()
    {
        var cfg = ConfigPath();
        if (cfg is null) return null;

        using var stream = new FileStream(cfg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(stream);
        foreach (var user in doc.RootElement.EnumerateObject())
        {
            if (user.Value.ValueKind != JsonValueKind.Object) continue;
            if (!user.Value.TryGetProperty("general", out var general)) continue;
            if (!general.TryGetProperty("wallpaperconfig", out var wc)) continue;
            if (!wc.TryGetProperty("selectedwallpapers", out var sel) || sel.ValueKind != JsonValueKind.Object) continue;

            // Prefer the primary monitor entry, else first.
            var monitors = sel.EnumerateObject().ToList();
            var pick = monitors.FirstOrDefault(m => m.Name.EndsWith("0", StringComparison.Ordinal));
            if (pick.Value.ValueKind == JsonValueKind.Undefined && monitors.Count > 0) pick = monitors[0];
            if (pick.Value.ValueKind != JsonValueKind.Object) continue;
            if (!pick.Value.TryGetProperty("file", out var fileEl)) continue;

            var file = fileEl.GetString();
            if (string.IsNullOrEmpty(file)) continue;
            var dir = Path.GetDirectoryName(file.Replace('/', '\\'));
            if (dir is null || !Directory.Exists(dir)) continue;

            foreach (var candidate in new[] { "preview.jpg", "preview.png", "preview.gif", "preview.jpeg" })
            {
                var path = Path.Combine(dir, candidate);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }
}
