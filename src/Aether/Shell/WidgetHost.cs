using System;
using System.Collections.Generic;
using System.Linq;
using Aether.Core;
using Aether.Widgets.Clock;

namespace Aether.Shell;

/// <summary>Creates / closes widget windows so they match the settings.</summary>
public static class WidgetHost
{
    private static readonly Dictionary<string, WidgetWindow> Open = new();

    private static WidgetWindow? Create(WidgetConfig cfg) => cfg.Kind switch
    {
        "clock" => new ClockWidget(cfg),
        "calendar" => new Widgets.CalendarWidget(cfg),
        "weather" => new Widgets.WeatherWidget(cfg),
        "music" => new Widgets.MusicWidget(cfg),
        "notes" => new Widgets.NotesWidget(cfg),
        "mixer" => new Widgets.MixerWidget(cfg),
        "quick" => new Widgets.QuickWidget(cfg),
        _ => null,
    };

    /// <summary>Kinds shown in settings, in order: (kind, name, can have several).</summary>
    public static readonly (string Kind, string Name, bool Multi)[] Kinds =
    {
        ("clock", "时钟", false), ("calendar", "日历", false), ("weather", "天气", false),
        ("music", "音乐", false), ("notes", "便签 / 待办", true), ("mixer", "音量混音", false), ("quick", "快速访问", false),
    };

    public static bool IsShown(string kind) => SettingsStore.Current.Widgets.Any(w => w.Kind == kind && w.Visible);

    /// <summary>Show a single-instance kind (or hide it).</summary>
    public static void SetShown(string kind, bool shown)
    {
        var list = SettingsStore.Current.Widgets.Where(w => w.Kind == kind).ToList();
        if (list.Count == 0 && shown) { Add(kind); return; }
        foreach (var w in list) w.Visible = shown;
        SettingsStore.Touch();
        Sync();
    }

    /// <summary>Add a new widget, cascaded near the top-left so it doesn't land on another one.</summary>
    public static void Add(string kind)
    {
        int n = SettingsStore.Current.Widgets.Count(w => w.Visible);
        var cfg = new WidgetConfig { Kind = kind, Left = 380 + (n % 5) * 34, Top = 120 + (n % 5) * 34 };
        if (kind == "clock") cfg.Clock = new ClockOptions();
        SettingsStore.Current.Widgets.Add(cfg);
        SettingsStore.Touch();
        Sync();
    }

    /// <summary>Change an option on every widget of a kind (e.g. all notes) and redraw them.</summary>
    public static void Edit(string kind, Action<WidgetConfig> edit)
    {
        foreach (var cfg in SettingsStore.Current.Widgets.Where(w => w.Kind == kind))
        {
            edit(cfg);
            if (Open.TryGetValue(cfg.Id, out var w)) w.Refresh();
        }
        SettingsStore.Touch();
    }

    /// <summary>First widget of a kind (to read its current options).</summary>
    public static WidgetConfig? First(string kind) => SettingsStore.Current.Widgets.FirstOrDefault(w => w.Kind == kind);

    /// <summary>Remove a widget entirely (e.g. a deleted note).</summary>
    public static void Remove(string id)
    {
        SettingsStore.Current.Widgets.RemoveAll(w => w.Id == id);
        SettingsStore.Touch();
        Sync();
    }

    public static void Sync()
    {
        var wanted = SettingsStore.Current.Widgets.Where(w => w.Visible).ToDictionary(w => w.Id);

        foreach (var id in Open.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            Open[id].Close();
            Open.Remove(id);
        }

        foreach (var cfg in wanted.Values)
        {
            if (Open.ContainsKey(cfg.Id)) continue;
            try
            {
                var w = Create(cfg);
                if (w is null) continue;
                Open[cfg.Id] = w;
                w.Show();
            }
            catch (Exception ex)
            {
                Log.Error("widget-create " + cfg.Kind, ex);
            }
        }
    }

    public static void Refresh(string id)
    {
        if (Open.TryGetValue(id, out var w)) w.Refresh();
    }

    /// <summary>Close and reopen one widget (same config, same position).</summary>
    public static void Recreate(string id)
    {
        if (Open.TryGetValue(id, out var w))
        {
            Open.Remove(id);
            w.Close();
        }
        Sync();
    }

    public static void ResetPositions()
    {
        foreach (var cfg in SettingsStore.Current.Widgets)
        {
            cfg.Left = null;
            cfg.Top = null;
        }
        SettingsStore.Touch();
        foreach (var w in Open.Values.ToList()) w.Close();
        Open.Clear();
        Sync();
    }

    public static void CloseAll()
    {
        foreach (var w in Open.Values.ToList()) w.Close();
        Open.Clear();
    }
}
