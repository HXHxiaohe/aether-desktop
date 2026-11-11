using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Aether.Core;

namespace Aether.Widgets;

public sealed class TodoItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

public sealed class Note
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; set; } = "便签";
    /// <summary>"text" (free notepad) or "todo" (checklist).</summary>
    public string Mode { get; set; } = "text";
    public string Text { get; set; } = "";
    public List<TodoItem> Items { get; set; } = new();
    public DateTime Updated { get; set; } = DateTime.Now;
}

/// <summary>All notes in Data\notes.json (kept apart from settings; saved 0.5 s after typing stops).</summary>
public static class NotesStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static List<Note>? _notes;
    private static DispatcherTimer? _save;

    private static string File => Path.Combine(Paths.DataDir, "notes.json");

    public static List<Note> All
    {
        get
        {
            if (_notes is not null) return _notes;
            try
            {
                if (System.IO.File.Exists(File))
                    _notes = JsonSerializer.Deserialize<List<Note>>(System.IO.File.ReadAllText(File), Json);
            }
            catch (Exception ex) { Log.Error("notes-load", ex); }
            _notes ??= new List<Note>();
            return _notes;
        }
    }

    public static Note Get(string? id)
    {
        var n = All.FirstOrDefault(x => x.Id == id);
        if (n is null)
        {
            n = new Note { Title = All.Count == 0 ? "便签" : $"便签 {All.Count + 1}" };
            All.Add(n);
            Touch();
        }
        return n;
    }

    public static void Delete(Note n)
    {
        All.Remove(n);
        Touch();
    }

    public static void Touch()
    {
        if (_save is null)
        {
            _save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _save.Tick += (_, _) => { _save!.Stop(); SaveNow(); };
        }
        _save.Stop();
        _save.Start();
    }

    public static void SaveNow()
    {
        try
        {
            Paths.Ensure();
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(All, Json));
            if (System.IO.File.Exists(File)) System.IO.File.Replace(tmp, File, File + ".bak");
            else System.IO.File.Move(tmp, File);
        }
        catch (Exception ex) { Log.Error("notes-save", ex); }
    }

    public static string Export(Note n, bool markdown)
    {
        var sb = new StringBuilder();
        sb.AppendLine(markdown ? $"# {n.Title}" : n.Title);
        sb.AppendLine();
        if (n.Mode == "todo")
            foreach (var i in n.Items)
                sb.AppendLine(markdown ? $"- [{(i.Done ? "x" : " ")}] {i.Text}" : $"{(i.Done ? "☑" : "☐")} {i.Text}");
        else sb.AppendLine(n.Text);
        return sb.ToString();
    }
}
