using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Aether.Core;

namespace Aether.Apps;

/// <summary>The drawer's app list: import from 0.8.1, Start-menu scan, auto grouping, launching.</summary>
public static class AppCatalog
{
    public const string Ungrouped = "未分类";

    public static readonly string[] DefaultGroups =
    {
        "游戏", "社交", "浏览器", "办公", "创作", "影音", "开发", "网盘下载", "系统工具",
    };

    public static event Action? Changed;

    public static List<AppEntry> Apps => SettingsStore.Current.Apps;

    public static void NotifyChanged()
    {
        SettingsStore.Touch();
        Changed?.Invoke();
    }

    /// <summary>First run: pull apps + groups + dock order from Aether 0.8.1 if present, then scan.</summary>
    public static void Initialize()
    {
        var s = SettingsStore.Current;
        if (!s.AppsImported)
        {
            try { ImportLegacy(); } catch (Exception ex) { Log.Error("import-legacy", ex); }
            s.AppsImported = true;
        }
        if (s.Groups.Count == 0) s.Groups.AddRange(DefaultGroups);
        foreach (var a in Apps)
            if (a.Target is null && a.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                a.Target = ShellLink.Resolve(a.Path)?.Target;
        try { ScanStartMenu(); } catch (Exception ex) { Log.Error("scan", ex); }
        RemoveDuplicates();
        foreach (var a in Apps)
        {
            if (string.IsNullOrEmpty(a.Group)) a.Group = AppClassifier.Guess(a.Name, a.Target ?? a.Path);
            a.Group = Canonical(a.Group);
            EnsureGroup(a.Group);
        }
        // Drop groups that only existed because of an alias (e.g. 影音 when the user has 视频).
        foreach (var g in s.Groups.Where(g => Canonical(g) != g).ToList()) s.Groups.Remove(g);
        SettingsStore.Touch();
    }

    /// <summary>Map the classifier's group names onto ones the user already uses.</summary>
    public static string Canonical(string group)
    {
        var groups = SettingsStore.Current.Groups;
        foreach (var set in GroupAliases)
            if (set.Contains(group))
                foreach (var alt in set)
                    if (alt != group && groups.Contains(alt) && groups.IndexOf(alt) < (groups.Contains(group) ? groups.IndexOf(group) : int.MaxValue))
                        return alt;
        return group;
    }

    private static readonly string[][] GroupAliases =
    {
        new[] { "视频", "影音", "音乐" },
        new[] { "开发", "编程" },
        new[] { "网盘下载", "下载", "网盘" },
        new[] { "创作", "设计" },
    };

    /// <summary>Same exe or same name = same app; keep the first (imported / pinned ones win).</summary>
    private static void RemoveDuplicates()
    {
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var keep = new List<AppEntry>();
        foreach (var a in Apps.OrderBy(a => a.DockOrder is null ? 1 : 0))
        {
            bool dupTarget = a.Target is { Length: > 0 } t && !IsLauncherHost(t) && !seenTargets.Add(t);
            bool dupName = !seenNames.Add(a.Name.Trim());
            if (dupTarget || dupName) continue;
            keep.Add(a);
        }
        if (keep.Count == Apps.Count) return;
        var order = Apps.ToList();
        Apps.Clear();
        Apps.AddRange(order.Where(keep.Contains));
    }

    /// <summary>Exes that launch many different things (shortcuts to them aren't duplicates).</summary>
    private static bool IsLauncherHost(string target)
    {
        var f = System.IO.Path.GetFileName(target);
        return f.Equals("control.exe", StringComparison.OrdinalIgnoreCase) || f.Equals("mmc.exe", StringComparison.OrdinalIgnoreCase)
            || f.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) || f.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase)
            || f.Equals("steam.exe", StringComparison.OrdinalIgnoreCase) || f.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || f.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) || f.Equals("wscript.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Old Aether 0.8.1 layout to import on first run (AETHER_LEGACY_LAYOUT=path\data\layout.json).</summary>
    private static readonly string[] LegacyLayouts =
        Environment.GetEnvironmentVariable("AETHER_LEGACY_LAYOUT") is { Length: > 0 } p ? new[] { p } : Array.Empty<string>();

    private static void ImportLegacy()
    {
        var file = LegacyLayouts.FirstOrDefault(File.Exists);
        if (file is null) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        var s = SettingsStore.Current;

        if (root.TryGetProperty("Groups", out var groups))
            foreach (var g in groups.EnumerateArray())
                if (g.GetString() is { Length: > 0 } name && !s.Groups.Contains(name)) s.Groups.Add(name);

        var dockOrder = new List<string>();
        if (root.TryGetProperty("DockOrder", out var order))
            dockOrder.AddRange(order.EnumerateArray().Select(e => e.GetString() ?? ""));

        if (!root.TryGetProperty("Apps", out var apps)) return;
        foreach (var a in apps.EnumerateArray())
        {
            var path = a.TryGetProperty("Path", out var p) ? p.GetString() ?? "" : "";
            if (path.Length == 0 || Apps.Any(x => Same(x.Path, path))) continue;
            var entry = new AppEntry
            {
                Name = a.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : System.IO.Path.GetFileNameWithoutExtension(path),
                Path = path,
                Target = a.TryGetProperty("LaunchTarget", out var t) && t.GetString() is { Length: > 0 } tt ? tt : null,
                Group = a.TryGetProperty("Group", out var gr) ? gr.GetString() ?? "" : "",
            };
            int idx = dockOrder.FindIndex(d => Same(d, path));
            if (idx >= 0) entry.DockOrder = idx;
            Apps.Add(entry);
        }
    }

    private static readonly string[] SkipWords =
    {
        "uninstall", "卸载", "readme", "help", "帮助", "website", "官网", "documentation", "license", "release notes",
        "manual", "说明", "修复", "repair", "safe mode", "安全模式",
    };

    /// <summary>Adds Start-menu shortcuts that aren't in the drawer yet (and weren't removed by the user).</summary>
    public static int ScanStartMenu()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        var s = SettingsStore.Current;
        int added = 0;
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in SafeFiles(root))
            {
                var ext = System.IO.Path.GetExtension(file);
                if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) continue;
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (SkipWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                if (Apps.Any(a => Same(a.Path, file)) || s.RemovedApps.Any(r => Same(r, file))) continue;

                string? target = null;
                if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    target = ShellLink.Resolve(file)?.Target;
                    // Skip shortcuts to documents / web pages / missing files.
                    if (target is not null)
                    {
                        var te = System.IO.Path.GetExtension(target);
                        if (!string.IsNullOrEmpty(te) && !te.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !te.Equals(".msc", StringComparison.OrdinalIgnoreCase)) continue;
                        if (target.Length > 3 && !File.Exists(target) && !target.StartsWith("::", StringComparison.Ordinal)) continue;
                    }
                }
                // Same program under another shortcut, or same name → already in the drawer.
                if (target is not null && !IsLauncherHost(target) && Apps.Any(a => Same(a.Target, target))) continue;
                if (Apps.Any(a => string.Equals(a.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase))) continue;
                Apps.Add(new AppEntry { Name = name, Path = file, Target = target, Group = Canonical(AppClassifier.Guess(name, target ?? file)) });
                added++;
            }
        }
        if (added > 0) Changed?.Invoke();
        return added;
    }

    private static IEnumerable<string> SafeFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files = Array.Empty<string>(), dirs = Array.Empty<string>();
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); } catch { }
            foreach (var f in files) yield return f;
            foreach (var d in dirs) stack.Push(d);
        }
    }

    public static AppEntry? AddFile(string path)
    {
        if (Apps.FirstOrDefault(a => Same(a.Path, path)) is { } existing) return existing;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        string? target = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShellLink.Resolve(path)?.Target : path;
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.FileDescription)) name = info.FileDescription!;
            }
            catch { }
        }
        var entry = new AppEntry { Name = name, Path = path, Target = target, Group = AppClassifier.Guess(name, target ?? path) };
        EnsureGroup(entry.Group);
        Apps.Add(entry);
        SettingsStore.Current.RemovedApps.RemoveAll(r => Same(r, path));
        NotifyChanged();
        return entry;
    }

    public static void Remove(AppEntry app)
    {
        Apps.Remove(app);
        if (!SettingsStore.Current.RemovedApps.Any(r => Same(r, app.Path)))
            SettingsStore.Current.RemovedApps.Add(app.Path);
        NotifyChanged();
    }

    public static void EnsureGroup(string group)
    {
        if (string.IsNullOrEmpty(group) || group == Ungrouped) return;
        if (!SettingsStore.Current.Groups.Contains(group)) SettingsStore.Current.Groups.Add(group);
    }

    /// <summary>Rename a group; renaming onto an existing group merges them.</summary>
    public static void RenameGroup(string from, string to)
    {
        var groups = SettingsStore.Current.Groups;
        int i = groups.IndexOf(from);
        if (i < 0 || string.IsNullOrWhiteSpace(to) || to == Ungrouped) return;
        if (groups.Contains(to)) groups.RemoveAt(i);
        else groups[i] = to;
        foreach (var a in Apps.Where(a => a.Group == from)) a.Group = to;
        NotifyChanged();
    }

    /// <summary>Delete a group; its apps become ungrouped (nothing is removed from the drawer).</summary>
    public static void DeleteGroup(string group)
    {
        SettingsStore.Current.Groups.Remove(group);
        foreach (var a in Apps.Where(a => a.Group == group)) a.Group = "";
        NotifyChanged();
    }

    // ---------- dock pins ----------
    public static List<AppEntry> Pinned() => Apps.Where(a => a.DockOrder is not null).OrderBy(a => a.DockOrder).ToList();

    public static void Pin(AppEntry app, int? index = null)
    {
        var pins = Pinned();
        pins.Remove(app);
        pins.Insert(Math.Clamp(index ?? pins.Count, 0, pins.Count), app);
        for (int i = 0; i < pins.Count; i++) pins[i].DockOrder = i;
        NotifyChanged();
    }

    public static void Unpin(AppEntry app)
    {
        app.DockOrder = null;
        var pins = Pinned();
        for (int i = 0; i < pins.Count; i++) pins[i].DockOrder = i;
        NotifyChanged();
    }

    // ---------- launching ----------
    public static void Launch(AppEntry app) => Launch(app.Path, app.Arguments);

    public static void Launch(string path, string? args = null)
    {
        try
        {
            Aether.Interop.Native.LendForeground();
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Arguments = args ?? "", WorkingDirectory = SafeDir(path) });
        }
        catch (Exception ex)
        {
            Log.Error("launch " + path, ex);
        }
    }

    private static string SafeDir(string path)
    {
        try { return System.IO.Path.GetDirectoryName(path) ?? ""; } catch { return ""; }
    }

    public static void RevealInExplorer(AppEntry app)
    {
        var file = app.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? null : app.Path;
        if (file is not null && File.Exists(file))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
    }

    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
