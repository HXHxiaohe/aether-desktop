using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Aether.Core;

public enum ColorMode
{
    Manual = 0, // user presses "pick from desktop"
    Auto = 1,   // follow wallpaper changes automatically
    Fixed = 2,  // user-chosen color
}

public sealed class ThemeSettings
{
    public ColorMode Mode { get; set; } = ColorMode.Manual;
    /// <summary>Dark halo behind desktop-card text so white text reads on light wallpapers: soft / strong / off.</summary>
    public string TextShadow { get; set; } = "soft";
    /// <summary>Desktop-card text colour: "light" (white, default) or "dark" (for light wallpapers).</summary>
    public string WidgetText { get; set; } = "light";
    /// <summary>Accent last picked from the desktop.</summary>
    public string PickedColor { get; set; } = "#8C9EFF";
    public string FixedColor { get; set; } = "#8C9EFF";
    /// <summary>Glass tint strength, 0-100.</summary>
    public int GlassOpacity { get; set; } = 38;
    public bool Blur { get; set; } = true;
    /// <summary>"light" = frosted (white haze), "dark" = smoked glass.</summary>
    public string GlassTone { get; set; } = "light";
    public string PickedSource { get; set; } = "";
}

public sealed class ClockOptions
{
    public int Style { get; set; } = 0;
    public bool Hour24 { get; set; } = true;
    public bool ShowSeconds { get; set; } = false;
    public bool ShowDate { get; set; } = true;
    public bool Card { get; set; } = false;
    public double Scale { get; set; } = 1.0;
}

public sealed class WidgetConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Kind { get; set; } = "clock";
    public bool Visible { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public ClockOptions? Clock { get; set; }

    // ---- per-widget options (only the ones a kind uses are set) ----
    /// <summary>Weather: city name as typed; Lat/Lon resolved from it.</summary>
    public string? City { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    /// <summary>Notes: which note in notes.json this card shows.</summary>
    public string? NoteId { get; set; }
    /// <summary>Quick access: folder / file / app paths.</summary>
    public List<string>? Items { get; set; }
    /// <summary>Music card: show the spectrum bars.</summary>
    public bool? Spectrum { get; set; }

    // ---- common card options (clock keeps its own in ClockOptions) ----
    /// <summary>Glass card behind the content (default on).</summary>
    public bool? Card { get; set; }
    /// <summary>Size multiplier (default 1.0).</summary>
    public double? Scale { get; set; }
}

public enum DockHideMode
{
    Smart = 0,  // hide when a window overlaps it
    Always = 1, // only show when the mouse touches the bottom edge
    Never = 2,
}

public sealed class DockSettings
{
    public bool Enabled { get; set; } = true;
    public int IconSize { get; set; } = 44;
    /// <summary>Space between the icons and the Dock's frame (DIP), independent of icon size.</summary>
    public int FramePadding { get; set; } = 8;
    /// <summary>false = no glass frame at all: icons float on the desktop like a frameless card.</summary>
    public bool ShowFrame { get; set; } = true;
    /// <summary>"此电脑" and "Windows 设置" buttons next to the quick-settings button.</summary>
    public bool ShowSystemButtons { get; set; } = true;
    /// <summary>App drawer tint, 0–100 (0 = clear).</summary>
    public int DrawerOpacity { get; set; } = 40;
    public bool DrawerBlur { get; set; } = true;
    public int Opacity { get; set; } = 30;
    public bool Blur { get; set; } = true;
    public bool Magnify { get; set; } = true;
    public bool ShowRunning { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public DockHideMode HideMode { get; set; } = DockHideMode.Smart;
    public int HideDelayMs { get; set; } = 700;
    public int BottomGap { get; set; } = 8;
    public bool HideTaskbar { get; set; } = true;
}

public sealed class IslandSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Shrink to a thin line while a maximized window is in front.</summary>
    public bool ThinLineWhenMaximized { get; set; } = false;
    /// <summary>Horizontal offset from screen center (DIP), set by dragging.</summary>
    public double OffsetX { get; set; }
    public bool ShowSpectrum { get; set; } = true;
    public bool ShowSources { get; set; } = true;
    public bool VolumeOsd { get; set; } = true;
    public bool BrightnessOsd { get; set; } = true;
    public bool TrackToast { get; set; } = true;

    /// <summary>"notch" (flush with the top edge, flared sides) or "capsule" (floating pill).</summary>
    public string Shape { get; set; } = "notch";
    /// <summary>"black" or "cover" (tinted by the album art).</summary>
    public string Skin { get; set; } = "black";

    // ---- what the collapsed island shows ----
    public bool ShowTime { get; set; } = true;
    /// <summary>0 简约, 1 数码管, 2 衬线.</summary>
    public int TimeStyle { get; set; } = 0;
    public bool ShowLyrics { get; set; } = true;
    public bool ShowUnread { get; set; } = true;
    public bool ShowStats { get; set; } = false;
    public bool StatCpu { get; set; } = true;
    public bool StatMemory { get; set; } = true;
    public bool StatNetwork { get; set; } = false;
    public bool StatBattery { get; set; } = true;

    public bool OnlineLyrics { get; set; } = true;
    public int LastPage { get; set; } = 0;

    public bool PowerToasts { get; set; } = true;
    /// <summary>Show Windows notifications on the island.</summary>
    public bool Notifications { get; set; } = true;
    /// <summary>Show what a notification says (default). Off = only which app has a new message.</summary>
    public bool NotificationContent { get; set; } = true;
    // ---- 流光 / 动效 ----
    public bool GlowEnabled { get; set; } = true;
    /// <summary>scan / comet / dual / pulse / breathe / steady — the light while music plays.</summary>
    public string GlowStyle { get; set; } = "scan";
    /// <summary>"auto" (cover / app colour), "theme", "rainbow" or "#RRGGBB".</summary>
    public string GlowColor { get; set; } = "auto";
    public int GlowStrength { get; set; } = 75;
    /// <summary>calm / balanced / snappy</summary>
    public string GlowSpeed { get; set; } = "balanced";
    public bool GlowMusic { get; set; } = true;
    public bool GlowNotify { get; set; } = true;
    public bool GlowShelf { get; set; } = true;
    public bool ReduceMotion { get; set; }
    /// <summary>Drag a window by its title bar onto the island to put it away (窗口收纳).</summary>
    public bool WindowCollect { get; set; } = true;
    /// <summary>Win+Shift+S / PrtSc screenshots are saved and put on the shelf (置物架).</summary>
    public bool ScreenshotShelf { get; set; } = true;
    public int LastTimerMinutes { get; set; } = 25;
    public List<AlarmItem> Alarms { get; set; } = new();

    // ---- 置物架 / 剪贴板 ----
    /// <summary>Remember copied text (and small images) for 置物架 › 剪贴板.</summary>
    public bool ClipboardHistory { get; set; } = true;
    public bool ClipboardImages { get; set; } = true;
    /// <summary>Last tab on the shelf page: 0 files, 1 clipboard.</summary>
    public int ShelfTab { get; set; } = 0;

    // ---- AI 额度 ----
    public bool Quota { get; set; } = true;
    /// <summary>Show the highest usage % in the collapsed island.</summary>
    public bool QuotaInCompact { get; set; } = false;

    // ---- 邮件 / 验证码 ----
    public bool Mail { get; set; } = true;
    /// <summary>Pop a verification code on the island when one arrives.</summary>
    public bool MailCodeToast { get; set; } = true;
    /// <summary>Hide a code after this many seconds (30 / 60 / 300).</summary>
    public int MailCodeSeconds { get; set; } = 60;
    /// <summary>Check interval in seconds.</summary>
    public int MailInterval { get; set; } = 60;
}

public sealed class AlarmItem
{
    public int Hour { get; set; } = 7;
    public int Minute { get; set; } = 30;
    public bool Enabled { get; set; } = true;
    /// <summary>Repeats every day; otherwise turns itself off after ringing.</summary>
    public bool Daily { get; set; }
}

public sealed class AppEntry
{
    public string Name { get; set; } = "";
    /// <summary>.lnk / .url / .exe path, or "shell:AppsFolder\AUMID".</summary>
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    /// <summary>Resolved executable (for matching running windows), if known.</summary>
    public string? Target { get; set; }
    public string Group { get; set; } = "";
    /// <summary>Position in the Dock; null = not pinned.</summary>
    public int? DockOrder { get; set; }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 3;
    public ThemeSettings Theme { get; set; } = new();
    public bool LayoutLocked { get; set; }
    public List<WidgetConfig> Widgets { get; set; } = new();
    public DockSettings Dock { get; set; } = new();
    public IslandSettings Island { get; set; } = new();
    public List<AppEntry> Apps { get; set; } = new();
    public List<string> Groups { get; set; } = new();
    /// <summary>Paths the user removed from the drawer; the scanner won't re-add them.</summary>
    public List<string> RemovedApps { get; set; } = new();
    public bool AppsImported { get; set; }
    /// <summary>The in-app tutorial has been shown once (first run / after the update that added it).</summary>
    public bool TutorialSeen { get; set; }
    /// <summary>Look for a new release on GitHub after start-up and every 12 h.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>Taskbar auto-hide state before Aether changed it (null = untouched).</summary>
    public bool? TaskbarWasAutoHide { get; set; }

    public WidgetConfig Widget(string kind)
    {
        var w = Widgets.FirstOrDefault(x => x.Kind == kind);
        if (w is null)
        {
            w = new WidgetConfig { Kind = kind };
            if (kind == "clock") w.Clock = new ClockOptions();
            Widgets.Add(w);
        }
        if (kind == "clock") w.Clock ??= new ClockOptions();
        return w;
    }
}

/// <summary>Loads/saves settings.json with debounced, atomic writes.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static DispatcherTimer? _debounce;

    public static AppSettings Current { get; private set; } = new();

    public static event Action? Changed;

    public static void Load()
    {
        Paths.Ensure();
        foreach (var file in new[] { Paths.SettingsFile, Paths.SettingsFile + ".bak" })
        {
            try
            {
                if (!File.Exists(file)) continue;
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Json);
                if (loaded is not null)
                {
                    Current = loaded;
                    // v2 (2026-11-11): notification content back on by default — only Windows'
                    // own system toasts are filtered out now.
                    if (Current.Version < 2) { Current.Island.NotificationContent = true; Current.Version = 2; }
                    // v3: keep the island normal over maximized windows (it's a drop target there too).
                    if (Current.Version < 3) { Current.Island.ThinLineWhenMaximized = false; Current.Version = 3; }
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("settings-load " + Path.GetFileName(file), ex);
            }
        }
        Current = new AppSettings();
        Current.Widget("clock");
    }

    /// <summary>Notify listeners and schedule a save.</summary>
    public static void Touch()
    {
        Changed?.Invoke();
        if (_debounce is null)
        {
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _debounce.Tick += (_, _) => { _debounce!.Stop(); SaveNow(); };
        }
        _debounce.Stop();
        _debounce.Start();
    }

    public static void SaveNow()
    {
        try
        {
            Paths.Ensure();
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            if (File.Exists(Paths.SettingsFile))
                File.Replace(tmp, Paths.SettingsFile, Paths.SettingsFile + ".bak");
            else
                File.Move(tmp, Paths.SettingsFile);
        }
        catch (Exception ex)
        {
            Log.Error("settings-save", ex);
        }
    }

    public static void Flush()
    {
        if (_debounce?.IsEnabled == true)
        {
            _debounce.Stop();
            SaveNow();
        }
    }
}
