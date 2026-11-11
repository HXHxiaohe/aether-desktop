using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Aether.Core;

namespace Aether.Island;

public sealed class ShelfItem
{
    public string Path { get; set; } = "";
    public DateTime Added { get; set; } = DateTime.Now;
    /// <summary>Display name for apps dropped from the drawer (files use their file name).</summary>
    public string? Title { get; set; }

    /// <summary>A store/packaged app ("shell:AppsFolder\…") — opens, but can't be dragged out as a file.</summary>
    public bool IsShellApp => Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
    public string Name => Title ?? (System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path);
    public bool Exists => IsShellApp || File.Exists(Path) || Directory.Exists(Path);
}

/// <summary>
/// 置物架: a temporary shelf of files/folders dropped on the island. Only remembers where the
/// files are (Data\shelf.json) — removing an entry never touches the file itself.
/// </summary>
public static class ShelfService
{
    public const int Max = 60;
    private static List<ShelfItem> _items = new();

    public static IReadOnlyList<ShelfItem> Items => _items;
    public static event Action? Changed;

    public static void Start() => _items = JsonFile.Load<List<ShelfItem>>("shelf.json");

    /// <summary>Adds paths (newest first, duplicates move to the front). Returns what was added.</summary>
    public static List<ShelfItem> Add(IEnumerable<string> paths)
    {
        var added = new List<ShelfItem>();
        foreach (var p in paths.Reverse())
        {
            if (string.IsNullOrWhiteSpace(p) || !(File.Exists(p) || Directory.Exists(p))) continue;
            _items.RemoveAll(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase));
            var item = new ShelfItem { Path = p };
            _items.Insert(0, item);
            added.Add(item);
        }
        if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
        if (added.Count > 0) Save();
        return added;
    }

    /// <summary>An app dragged from the drawer: its shortcut / exe, or a packaged app's shell path.</summary>
    public static ShelfItem? AddApp(string path, string name)
    {
        bool shell = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
        if (!shell && !File.Exists(path)) return null;
        _items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        var item = new ShelfItem { Path = path, Title = name };
        _items.Insert(0, item);
        if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
        Save();
        return item;
    }

    public static void Remove(ShelfItem i) { _items.Remove(i); Save(); }

    public static void Clear() { _items.Clear(); Save(); }

    /// <summary>Forget entries whose file was moved or deleted.</summary>
    public static void Prune()
    {
        if (_items.RemoveAll(i => !i.Exists) > 0) Save();
    }

    public static void Open(ShelfItem i)
    {
        if (i.IsShellApp) { Apps.AppCatalog.Launch(i.Path); return; }
        try { Interop.Native.LendForeground(); Process.Start(new ProcessStartInfo(i.Path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Info("[shelf] open " + ex.Message); }
    }

    public static void Reveal(ShelfItem i)
    {
        if (i.IsShellApp) return; // no file to show
        try { Interop.Native.LendForeground(); Process.Start("explorer.exe", $"/select,\"{i.Path}\""); }
        catch (Exception ex) { Log.Info("[shelf] reveal " + ex.Message); }
    }

    private static void Save()
    {
        JsonFile.Save("shelf.json", _items);
        Changed?.Invoke();
    }
}
