using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Aether.Core;
using Microsoft.Win32;

namespace Aether.Theme;

/// <summary>
/// Owns the accent color and the shared brushes (as application resources, so every
/// window updates through DynamicResource). Also runs the optional auto-follow mode.
/// </summary>
public static class ThemeService
{
    private static FileSystemWatcher? _weWatcher;
    private static DispatcherTimer? _autoDebounce;
    private static DispatcherTimer? _autoPoll;
    private static string? _lastWeConfig;

    public static Color Accent { get; private set; } = Color.FromRgb(0x8C, 0x9E, 0xFF);

    public static event Action? Changed;

    public static void Init()
    {
        Apply();
        UpdateAutoMode();
    }

    /// <summary>Recompute brushes from settings.</summary>
    public static void Apply()
    { Aether.Core.Diag.Hit("Theme.Apply");
        var t = SettingsStore.Current.Theme;
        var hex = t.Mode == ColorMode.Fixed ? t.FixedColor : t.PickedColor;
        Accent = Parse(hex) ?? Color.FromRgb(0x8C, 0x9E, 0xFF);

        var res = Application.Current.Resources;
        res["AccentColor"] = Accent;
        res["AccentBrush"] = Frozen(new SolidColorBrush(Accent));
        res["AccentSoftBrush"] = Frozen(new SolidColorBrush(WithAlpha(Accent, 0x38)));
        res["AccentFaintBrush"] = Frozen(new SolidColorBrush(WithAlpha(Accent, 0x1A)));

        res["GlassBrush"] = MakeGlass(t.GlassOpacity);
        res["GlassBorderBrush"] = Frozen(new LinearGradientBrush(
            Color.FromArgb(Light ? (byte)0x70 : (byte)0x55, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 90));

        Changed?.Invoke();
    }

    /// <summary>A glass brush with its own strength (the Dock has a separate slider).</summary>
    private static bool Light => SettingsStore.Current.Theme.GlassTone != "dark";

    /// <summary>
    /// Glass fill. Light tone = frosted white haze with a hint of the accent (0 = just the blur);
    /// dark tone = smoked glass. Strength 0-100.
    /// </summary>
    public static Brush MakeGlass(int opacity)
    {
        double s = Math.Clamp(opacity, 0, 100) / 100.0;
        if (Light)
        {
            var c = Mix(Colors.White, Accent, 0.14);
            byte top = (byte)(255 * (0.10 + 0.45 * s)), bottom = (byte)(255 * (0.04 + 0.40 * s));
            return Frozen(new LinearGradientBrush(WithAlpha(c, top), WithAlpha(c, bottom), 90));
        }
        var baseColor = Mix(Color.FromRgb(0x12, 0x15, 0x22), Accent, 0.16);
        byte a = (byte)(255 * (0.04 + 0.86 * s));
        return Frozen(new LinearGradientBrush(
            WithAlpha(Mix(baseColor, Colors.White, 0.08), a),
            WithAlpha(baseColor, a), 90));
    }

    /// <summary>Pick from the desktop now (off the UI thread), store and apply.</summary>
    public static async Task<WallpaperColor.Result?> PickNowAsync()
    {
        var result = await Task.Run(WallpaperColor.Pick);
        if (result is not null)
        {
            var t = SettingsStore.Current.Theme;
            t.PickedColor = ToHex(result.Accent);
            t.PickedSource = result.Source;
            SettingsStore.Touch();
            Apply();
        }
        return result;
    }

    /// <summary>Start/stop the watchers depending on ColorMode.Auto.</summary>
    public static void UpdateAutoMode()
    {
        bool auto = SettingsStore.Current.Theme.Mode == ColorMode.Auto;
        if (!auto)
        {
            StopWatchers();
            return;
        }

        _autoDebounce ??= CreateDebounce();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // Wallpaper Engine may start after us; re-check its config location once a minute (cheap).
        if (_autoPoll is null)
        {
            _autoPoll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
            _autoPoll.Tick += (_, _) => EnsureWeWatcher();
        }
        _autoPoll.Start();
        EnsureWeWatcher();
        Schedule();
    }

    private static DispatcherTimer CreateDebounce()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        t.Tick += async (_, _) =>
        {
            t.Stop();
            await PickNowAsync();
        };
        return t;
    }

    private static void Schedule()
    {
        if (_autoDebounce is null) return;
        _autoDebounce.Stop();
        _autoDebounce.Start();
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.General)
            Application.Current?.Dispatcher.BeginInvoke(Schedule);
    }

    private static void EnsureWeWatcher()
    {
        string? cfg = null;
        try { cfg = WallpaperEngine.ConfigPath(); } catch { }
        if (cfg == _lastWeConfig) return;
        _lastWeConfig = cfg;
        _weWatcher?.Dispose();
        _weWatcher = null;
        if (cfg is null) return;

        _weWatcher = new FileSystemWatcher(Path.GetDirectoryName(cfg)!, Path.GetFileName(cfg))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _weWatcher.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(Schedule);
        _weWatcher.Created += (_, _) => Application.Current?.Dispatcher.BeginInvoke(Schedule);
        _weWatcher.Renamed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(Schedule);
        Schedule();
    }

    private static void StopWatchers()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _autoPoll?.Stop();
        _weWatcher?.Dispose();
        _weWatcher = null;
        _lastWeConfig = null;
    }

    public static void Shutdown() => StopWatchers();

    // ---- color helpers ----
    public static Color? Parse(string? hex)
    {
        try { return hex is null ? null : (Color)ColorConverter.ConvertFromString(hex); }
        catch { return null; }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
