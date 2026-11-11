using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Aether.Core;

namespace Aether.Island;

/// <summary>
/// Time-synced lyrics from LRCLIB (lrclib.net — an open lyrics database with a public API).
/// Only the song title, artist and duration are sent. Results are cached in Data\Lyrics.
/// </summary>
public static class LyricsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static CancellationTokenSource? _cts;
    private static List<(TimeSpan At, string Text)> _lines = new();
    private static string _key = "";

    static LyricsService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("AetherDesktop/1.0 (personal desktop widget)");
    }

    public static bool HasLyrics => _lines.Count > 0;

    /// <summary>Raised (thread-pool) when lyrics for the current song arrive or are cleared.</summary>
    public static event Action? Changed;

    private static string Dir => Path.Combine(Paths.DataDir, "Lyrics");

    public static void OnTrack(MediaSnapshot? m)
    {
        var key = m is null ? "" : m.Title + "\u0001" + m.Artist;
        if (key == _key) return;
        _key = key;
        _lines = new();
        Changed?.Invoke();
        _cts?.Cancel();
        if (m is null || m.Title.Length == 0 || !SettingsStore.Current.Island.OnlineLyrics) return;
        _cts = new CancellationTokenSource();
        _ = LoadAsync(m, key, _cts.Token);
    }

    /// <summary>The line being sung at <paramref name="pos"/>, or null.</summary>
    public static string? LineAt(TimeSpan pos)
    {
        var lines = _lines;
        if (lines.Count == 0) return null;
        int lo = 0, hi = lines.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].At <= pos) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best < 0 ? null : lines[best].Text;
    }

    private static async Task LoadAsync(MediaSnapshot m, string key, CancellationToken ct)
    {
        try
        {
            var (title, artist) = Clean(m.Title, m.Artist);
            Directory.CreateDirectory(Dir);
            var file = Path.Combine(Dir, Hash(title + "|" + artist) + ".lrc");
            var miss = file + ".none";
            string? lrc = null;
            if (File.Exists(file)) lrc = await File.ReadAllTextAsync(file, ct);
            else if (File.Exists(miss) && (DateTime.Now - File.GetLastWriteTime(miss)).TotalDays < 7) return;
            else
            {
                lrc = await FetchAsync(title, artist, m.Duration, ct);
                if (lrc is null) { await File.WriteAllTextAsync(miss, "", ct); return; }
                await File.WriteAllTextAsync(file, lrc, ct);
            }
            if (ct.IsCancellationRequested || key != _key) return;
            _lines = Parse(lrc);
            Changed?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("lyrics", ex); }
    }

    private static async Task<string?> FetchAsync(string title, string artist, TimeSpan duration, CancellationToken ct)
    {
        string Q(string s) => Uri.EscapeDataString(s);
        // 1) exact lookup
        var url = $"https://lrclib.net/api/get?track_name={Q(title)}&artist_name={Q(artist)}";
        if (duration > TimeSpan.Zero) url += $"&duration={(int)Math.Round(duration.TotalSeconds)}";
        using (var resp = await Http.GetAsync(url, ct))
        {
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("syncedLyrics", out var s) && s.GetString() is { Length: > 0 } synced) return synced;
            }
        }
        // 2) search, prefer synced results with a close duration
        var search = $"https://lrclib.net/api/search?track_name={Q(title)}" + (artist.Length > 0 ? $"&artist_name={Q(artist)}" : "");
        using var r2 = await Http.GetAsync(search, ct);
        if (!r2.IsSuccessStatusCode) return null;
        using var list = JsonDocument.Parse(await r2.Content.ReadAsStringAsync(ct));
        string? best = null;
        double bestDiff = double.MaxValue;
        foreach (var item in list.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("syncedLyrics", out var s) || s.GetString() is not { Length: > 0 } synced) continue;
            double d = item.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetDouble() : 0;
            double diff = duration > TimeSpan.Zero && d > 0 ? Math.Abs(d - duration.TotalSeconds) : 1;
            if (duration > TimeSpan.Zero && d > 0 && diff > 6) continue;
            if (diff < bestDiff) { bestDiff = diff; best = synced; }
        }
        return best;
    }

    /// <summary>Tidy up titles from YouTube & co: "Artist - Song (Official Video)" etc.</summary>
    public static (string Title, string Artist) Clean(string title, string artist)
    {
        var t = Regex.Replace(title, @"\s*[\(\[【（](official|offical|lyric|lyrics|mv|m/v|audio|video|visualizer|hd|4k|官方|歌词|动态歌词|完整版)[^\)\]】）]*[\)\]】）]", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*(feat\.|ft\.).*$", "", RegexOptions.IgnoreCase).Trim();
        var a = Regex.Replace(artist ?? "", @"\s*(- Topic|VEVO|Official)$", "", RegexOptions.IgnoreCase).Trim();
        var dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && (a.Length == 0 || t[..dash].Contains(a, StringComparison.OrdinalIgnoreCase) || artist!.Contains("VEVO", StringComparison.OrdinalIgnoreCase) || artist.EndsWith("Topic", StringComparison.OrdinalIgnoreCase)))
        {
            a = t[..dash].Trim();
            t = t[(dash + 3)..].Trim();
        }
        return (t, a);
    }

    private static List<(TimeSpan, string)> Parse(string lrc)
    {
        var result = new List<(TimeSpan, string)>();
        var tag = new Regex(@"\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]");
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var matches = tag.Matches(line);
            if (matches.Count == 0) continue;
            var text = tag.Replace(line, "").Trim();
            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var frac = m.Groups[3].Success ? m.Groups[3].Value.PadRight(3, '0')[..3] : "0";
                result.Add((new TimeSpan(0, 0, min, sec, int.Parse(frac, CultureInfo.InvariantCulture)), text));
            }
        }
        result.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return result;
    }

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant())))[..20];
}
