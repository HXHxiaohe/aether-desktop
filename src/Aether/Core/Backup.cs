using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace Aether.Core;

/// <summary>
/// 备份与导出: the user's own data (settings, notes, Dock groups and drawer, shelf, clipboard
/// history, mailbox list) in one .zip — not caches (icons, lyrics), logs or screenshots.
/// Import replaces the Data folder's files after Aether has quit, then starts it again.
/// </summary>
public static class Backup
{
    private static readonly string[] Files = { "settings.json", "notes.json", "shelf.json", "clipboard.json", "mail.json" };
    private const string ClipDir = "Clipboard";
    private const string Manifest = "aether-backup.json";

    public static string SuggestedName => $"Aether 备份 {DateTime.Now:yyyy-MM-dd}.zip";

    /// <summary>Write the backup. Returns how many items went in.</summary>
    public static int Export(string zipPath)
    {
        SettingsStore.SaveNow();
        if (File.Exists(zipPath)) File.Delete(zipPath);
        int n = 0;
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var f in Files)
        {
            var p = Path.Combine(Paths.DataDir, f);
            if (!File.Exists(p)) continue;
            zip.CreateEntryFromFile(p, f);
            n++;
        }
        var clip = Path.Combine(Paths.DataDir, ClipDir);
        if (Directory.Exists(clip))
            foreach (var img in Directory.EnumerateFiles(clip, "*.png"))
                zip.CreateEntryFromFile(img, ClipDir + "/" + Path.GetFileName(img));
        var info = zip.CreateEntry(Manifest);
        using (var w = new StreamWriter(info.Open()))
            w.Write(JsonSerializer.Serialize(new { app = "Aether Desktop", version = Setup.Version, created = DateTime.Now, machine = Environment.MachineName.Length > 0 }));
        return n;
    }

    /// <summary>Check a file is an Aether backup. Returns null if fine, otherwise why not.</summary>
    public static string? Check(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.GetEntry(Manifest) is null || zip.GetEntry("settings.json") is null) return "这不是 Aether 的备份文件";
            return null;
        }
        catch (Exception ex) { return "打不开这个文件：" + ex.Message; }
    }

    /// <summary>
    /// Unpack next to the program, then hand over to a hidden helper that waits for Aether to
    /// exit, copies the files into Data and starts Aether again. The caller quits right after.
    /// </summary>
    public static void ImportAndRestart(string zipPath)
    {
        var stage = Path.Combine(Path.GetTempPath(), "AetherRestore");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        ZipFile.ExtractToDirectory(zipPath, stage);
        File.Delete(Path.Combine(stage, Manifest));
        string Q(string s) => s.Replace("'", "''");
        var exe = Environment.ProcessPath ?? Path.Combine(Paths.AppDir, "Aether.exe");
        var script =
            $"$p = Get-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; if ($p) {{ $p.WaitForExit(20000) | Out-Null }}; " +
            $"Start-Sleep -Milliseconds 500; " +
            $"$clip = Join-Path '{Q(Paths.DataDir)}' 'Clipboard'; if (Test-Path (Join-Path '{Q(stage)}' 'Clipboard')) {{ if (Test-Path $clip) {{ Remove-Item $clip -Recurse -Force }} }}; " +
            $"Copy-Item -Path (Join-Path '{Q(stage)}' '*') -Destination '{Q(Paths.DataDir)}' -Recurse -Force; " +
            $"Remove-Item -LiteralPath '{Q(Paths.DataDir)}\\settings.json.bak' -ErrorAction SilentlyContinue; " +
            $"Start-Process explorer.exe -ArgumentList ('\"' + '{Q(exe)}' + '\"')";
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{script.Replace("\"", "\\\"")}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
        });
    }
}
