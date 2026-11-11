using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Aether.Core;

namespace Aether.Island;

public sealed record QuotaMeter(string Label, double Percent, DateTimeOffset? ResetsAt);

public sealed class QuotaProvider
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Plan { get; set; } = "";
    /// <summary>"ok", "signed-out", "error", "loading".</summary>
    public string State { get; set; } = "loading";
    public string Message { get; set; } = "";
    public List<QuotaMeter> Meters { get; set; } = new();
    public DateTime Updated { get; set; }
    public double Highest => Meters.Count == 0 ? 0 : Meters.Max(m => m.Percent);
}

/// <summary>
/// AI 额度: reads the sign-ins Codex and Claude already keep on this PC and asks their own
/// usage endpoints (same approach as BrimDeck / 岛仔). Tokens are only held in memory for the
/// request — never written, logged or sent anywhere else. Refreshes at most every 5 minutes,
/// only while the page or the compact readout needs it, and backs off on 429.
/// </summary>
public static class QuotaService
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly Dictionary<string, DateTime> HoldUntil = new();
    private static DateTime _lastRefresh = DateTime.MinValue;
    private static bool _busy;
    private static DispatcherTimer? _timer;

    public static List<QuotaProvider> Providers { get; } = new()
    {
        new QuotaProvider { Id = "claude", Name = "Claude" },
        new QuotaProvider { Id = "codex", Name = "Codex" },
    };

    public static event Action? Changed;

    public static void Start()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("AetherDesktop/1.0");
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(5) };
        _timer.Tick += (_, _) => { if (SettingsStore.Current.Island is { Quota: true, QuotaInCompact: true } && !Shell.GameMode.Active) _ = RefreshAsync(false); };
        _timer.Start();
        if (SettingsStore.Current.Island is { Quota: true, QuotaInCompact: true }) _ = RefreshAsync(false);
    }

    public static void Stop() => _timer?.Stop();

    /// <summary>Page opened: refresh if the last reading is older than 3 minutes.</summary>
    public static void RefreshSoon()
    {
        if (!SettingsStore.Current.Island.Quota) return;
        if ((DateTime.Now - _lastRefresh).TotalMinutes >= 3) _ = RefreshAsync(false);
    }

    public static async Task RefreshAsync(bool manual)
    {
        if (_busy) return;
        _busy = true;
        _lastRefresh = DateTime.Now;
        try
        {
            foreach (var p in Providers) if (p.Meters.Count == 0) p.State = "loading";
            Changed?.Invoke();
            await Task.WhenAll(Run("claude", ReadClaudeAsync), Run("codex", ReadCodexAsync));
        }
        finally { _busy = false; Changed?.Invoke(); }
    }

    private static async Task Run(string id, Func<QuotaProvider, Task> read)
    {
        var p = Providers.First(x => x.Id == id);
        if (HoldUntil.TryGetValue(id, out var until) && DateTime.Now < until)
        {
            if (p.Meters.Count > 0) p.State = "ok";
            p.Message = $"请求太频繁，{until:HH:mm} 后再试";
            return;
        }
        try { await read(p); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            HoldUntil[id] = DateTime.Now.AddMinutes(10);
            p.State = p.Meters.Count > 0 ? "ok" : "error";
            p.Message = "请求太频繁，10 分钟后再试";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            p.State = "signed-out";
            p.Message = $"登录已过期，打开 {p.Name} 重新登录一次";
            p.Meters.Clear();
        }
        catch (Exception ex)
        {
            p.State = p.Meters.Count > 0 ? "ok" : "error";
            p.Message = ex is TaskCanceledException ? "网络超时" : "暂时读取不到";
            Log.Info($"[quota] {id}: {ex.GetType().Name} {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ Codex
    private static async Task ReadCodexAsync(QuotaProvider p)
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } ch ? ch : Path.Combine(Home, ".codex");
        var authFile = Path.Combine(codexHome, "auth.json");
        if (!File.Exists(authFile)) { SignedOut(p, "没有找到 Codex 登录"); return; }
        string token, account;
        using (var auth = JsonDocument.Parse(ReadShared(authFile)))
        {
            var tokens = Get(auth.RootElement, "tokens");
            token = Str(tokens, "access_token");
            account = Str(tokens, "account_id");
        }
        if (token.Length == 0) { SignedOut(p, "Codex 未登录（或使用 API Key）"); return; }

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (account.Length > 0) req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account);
        using var doc = await Send(req);
        var root = doc.RootElement;
        var limits = Get(root, "rate_limit");
        var meters = new List<QuotaMeter>();
        foreach (var field in new[] { "primary_window", "secondary_window" })
        {
            var w = Get(limits, field);
            if (Num(w, "used_percent") is not { } used) continue;
            int? minutes = Num(w, "limit_window_seconds") is { } s ? (int)(s / 60) : null;
            DateTimeOffset? reset = Num(w, "reset_at") is { } at ? DateTimeOffset.FromUnixTimeSeconds((long)at)
                : Num(w, "reset_after_seconds") is { } after ? DateTimeOffset.Now.AddSeconds(after) : null;
            meters.Add(new QuotaMeter(WindowLabel(minutes, field.StartsWith("primary")), Math.Clamp(used, 0, 100), reset));
        }
        if (meters.Count == 0) throw new InvalidDataException("no meters");
        p.Meters = meters;
        p.Plan = PlanName(Str(root, "plan_type"));
        Ok(p);
    }

    // ------------------------------------------------------------------ Claude
    private static async Task ReadClaudeAsync(QuotaProvider p)
    {
        var tokens = ClaudeTokens();
        if (tokens.Count == 0) { SignedOut(p, "没有找到 Claude 登录（Claude Code 或桌面版）"); return; }
        HttpRequestException? last = null;
        foreach (var (token, plan) in tokens)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                using var doc = await Send(req);
                var meters = ParseClaude(doc.RootElement);
                if (meters.Count == 0) throw new InvalidDataException("no meters");
                p.Meters = meters;
                p.Plan = plan;
                Ok(p);
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { last = ex; }
        }
        if (last is not null) throw last;
    }

    private static List<QuotaMeter> ParseClaude(JsonElement root)
    {
        var list = new List<QuotaMeter>();
        void Add(string label, double? pct, DateTimeOffset? reset)
        {
            if (pct is not { } v || v < 0 || list.Any(m => m.Label == label)) return;
            list.Add(new QuotaMeter(label, Math.Clamp(v, 0, 100), reset));
        }
        foreach (var (key, label) in new[] { ("five_hour", "5 小时"), ("seven_day", "每周") })
            Add(label, Num(Get(root, key), "utilization"), Date(Get(root, key), "resets_at"));
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in limits.EnumerateArray())
            {
                string kind = Str(l, "kind"), group = Str(l, "group");
                string model = Str(Get(Get(l, "scope"), "model"), "display_name");
                if (kind == "session") Add("5 小时", Num(l, "percent"), Date(l, "resets_at"));
                else if (kind == "weekly_all") Add("每周", Num(l, "percent"), Date(l, "resets_at"));
                else if (model.Length > 0) Add(model + (group == "weekly" || kind == "weekly_scoped" ? " 每周" : ""), Num(l, "percent"), Date(l, "resets_at"));
            }
        }
        foreach (var (key, name) in new[] { ("seven_day_opus", "Opus 每周"), ("seven_day_sonnet", "Sonnet 每周") })
            Add(name, Num(Get(root, key), "utilization"), Date(Get(root, key), "resets_at"));
        return list;
    }

    /// <summary>Claude Code's file sign-in, then the desktop app's (Chromium-encrypted, user DPAPI).</summary>
    private static List<(string Token, string Plan)> ClaudeTokens()
    {
        var list = new List<(string, string)>();
        try
        {
            var claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } c ? c : Path.Combine(Home, ".claude");
            var cred = Path.Combine(claudeHome, ".credentials.json");
            if (File.Exists(cred))
            {
                using var doc = JsonDocument.Parse(ReadShared(cred));
                var o = Get(doc.RootElement, "claudeAiOauth");
                var t = Str(o, "accessToken");
                if (t.Length > 0) list.Add((t, PlanName(Str(o, "subscriptionType"))));
            }
        }
        catch (Exception ex) { Log.Info("[quota] claude code sign-in: " + ex.GetType().Name); }

        var profiles = new List<string> { Path.Combine(Roaming, "Claude") };
        try
        {
            var packages = Path.Combine(Local, "Packages");
            if (Directory.Exists(packages))
                foreach (var d in Directory.EnumerateDirectories(packages, "*Claude_*"))
                    profiles.Add(Path.Combine(d, "LocalCache", "Roaming", "Claude"));
        }
        catch { }
        foreach (var profile in profiles)
        {
            byte[]? key = null, plain = null;
            try
            {
                var config = Path.Combine(profile, "config.json");
                var state = Path.Combine(profile, "Local State");
                if (!File.Exists(config) || !File.Exists(state)) continue;
                using var cfg = JsonDocument.Parse(ReadShared(config));
                var enc = Str(cfg.RootElement, "oauth:tokenCacheV2");
                if (enc.Length == 0) enc = Str(cfg.RootElement, "oauth:tokenCache");
                if (enc.Length == 0) continue;
                using (var st = JsonDocument.Parse(ReadShared(state)))
                {
                    var wrapped = Convert.FromBase64String(Str(Get(st.RootElement, "os_crypt"), "encrypted_key"));
                    if (wrapped.Length < 6 || Encoding.ASCII.GetString(wrapped, 0, 5) != "DPAPI") continue;
                    key = ProtectedData.Unprotect(wrapped.AsSpan(5).ToArray(), null, DataProtectionScope.CurrentUser);
                }
                var data = Convert.FromBase64String(enc);
                if (data.Length < 31 || Encoding.ASCII.GetString(data, 0, 3) != "v10") continue;
                plain = new byte[data.Length - 31];
                using (var aes = new AesGcm(key, 16)) aes.Decrypt(data.AsSpan(3, 12), data.AsSpan(15, plain.Length), data.AsSpan(data.Length - 16), plain);
                using var cache = JsonDocument.Parse(plain);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var item in cache.RootElement.EnumerateObject())
                {
                    if (!item.Name.Contains(":https://api.anthropic.com:", StringComparison.Ordinal) || !item.Name.Contains("user:profile", StringComparison.Ordinal)) continue;
                    var t = Str(item.Value, "token");
                    if (t.Length == 0 || Num(item.Value, "expiresAt") is not { } exp || exp <= now) continue;
                    list.Add((t, PlanName(Str(item.Value, "subscriptionType"))));
                }
            }
            catch (Exception ex) { Log.Info("[quota] claude desktop sign-in: " + ex.GetType().Name); }
            finally
            {
                if (key is not null) CryptographicOperations.ZeroMemory(key);
                if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            }
        }
        return list.DistinctBy(x => x.Item1).ToList();
    }

    // ------------------------------------------------------------------ helpers
    private static async Task<JsonDocument> Send(HttpRequestMessage req)
    {
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException(resp.ReasonPhrase, null, resp.StatusCode);
        await using var s = await resp.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(s);
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    private static void Ok(QuotaProvider p) { p.State = "ok"; p.Message = ""; p.Updated = DateTime.Now; }
    private static void SignedOut(QuotaProvider p, string msg) { p.State = "signed-out"; p.Message = msg; p.Meters.Clear(); }

    private static string WindowLabel(int? minutes, bool primary) => minutes switch
    {
        300 => "5 小时", 10080 => "每周", >= 1440 => $"{minutes / 1440} 天", > 0 => $"{minutes / 60} 小时",
        _ => primary ? "主要" : "次要",
    };

    private static string PlanName(string raw) => raw.ToLowerInvariant() switch
    {
        "" => "", "pro" => "Pro", "plus" => "Plus", "max" => "Max", "team" => "Team", "enterprise" => "Enterprise", "free" => "Free",
        var r => char.ToUpperInvariant(r[0]) + r[1..],
    };

    private static JsonElement Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static string Str(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";

    private static double? Num(JsonElement e, string name)
    {
        var v = Get(e, name);
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    private static DateTimeOffset? Date(JsonElement e, string name)
    {
        var v = Get(e, name);
        if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d)) return d;
        if (v.ValueKind == JsonValueKind.Number) return DateTimeOffset.FromUnixTimeSeconds((long)v.GetDouble());
        return null;
    }
}
