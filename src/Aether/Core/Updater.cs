using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Aether.Core;

/// <summary>
/// Updates from GitHub Releases: the repo is baked in at build time (AssemblyMetadata "UpdateRepo",
/// e.g. "name/aether-desktop"). Checks the latest release (tag "v1.2.3", one .zip asset of the
/// program files), downloads it, and swaps the program files after Aether quits — Data stays.
/// </summary>
public static class Updater
{
    public static string Repo { get; } =
        typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateRepo")?.Value ?? "";

    public static bool Configured => Repo.Contains('/');

    public sealed record Release(Version Version, string Tag, string Notes, string ZipUrl, long Size);

    public static Release? Available { get; private set; }
    public static string Status { get; private set; } = "";
    public static bool Busy { get; private set; }
    /// <summary>Status / Available changed (UI thread).</summary>
    public static event Action? Changed;

    private static DispatcherTimer? _timer;
    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Aether-Desktop-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version Current => typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>Check 1 min after start, then every 12 h (only for the installed copy).</summary>
    public static void Start()
    {
        if (!Configured || !Setup.RunningInstalled) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(12);
            if (SettingsStore.Current.AutoCheckUpdates) await CheckAsync(quiet: true);
        };
        _timer.Start();
    }

    public static void Stop() => _timer?.Stop();

    private static void Set(string status) { Status = status; Changed?.Invoke(); }

    public static async Task CheckAsync(bool quiet = false)
    {
        if (!Configured) { Set("还没有设置更新地址"); return; }
        if (Busy) return;
        Busy = true;
        if (!quiet) Set("正在检查更新…");
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) { Set("更新信息看不懂：" + tag); return; }
            var asset = r.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind == JsonValueKind.Undefined) { Set("最新版本没有安装包"); return; }
            var rel = new Release(v, tag, r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                asset.GetProperty("browser_download_url").GetString() ?? "", asset.GetProperty("size").GetInt64());
            if (v > Current)
            {
                Available = rel;
                Set($"有新版本 {v}");
            }
            else
            {
                Available = null;
                Set($"已经是最新版本（{Current}）");
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { Set("还没有发布任何版本"); }
        catch (Exception ex) { Log.Info("[update] check " + ex.Message); if (!quiet) Set("检查失败：" + ex.Message); }
        finally { Busy = false; Changed?.Invoke(); }
    }

    /// <summary>Download, unpack, hand over to a helper that waits for us to quit, copies, restarts.</summary>
    public static async Task InstallAsync()
    {
        if (Available is not { } rel || Busy) return;
        Busy = true;
        try
        {
            var work = Path.Combine(Path.GetTempPath(), "AetherUpdate");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            var zip = Path.Combine(work, "update.zip");
            Set($"正在下载 {rel.Version}…");
            using (var resp = await Http.GetAsync(rel.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? rel.Size;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(zip);
                var buf = new byte[81920];
                long done = 0; int n, lastPct = -1;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    int pct = total > 0 ? (int)(done * 100 / total) : 0;
                    if (pct != lastPct && pct % 5 == 0) { lastPct = pct; Set($"正在下载 {rel.Version}… {pct}%"); }
                }
            }
            Set("正在解压…");
            var unpacked = Path.Combine(work, "new");
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, unpacked));
            // Zip may hold the files at its root or inside one folder.
            var root = File.Exists(Path.Combine(unpacked, "Aether.exe")) ? unpacked
                : Directory.EnumerateDirectories(unpacked).FirstOrDefault(d => File.Exists(Path.Combine(d, "Aether.exe")));
            if (root is null) { Set("安装包里没有 Aether.exe"); return; }

            // The helper is a copy of the new Aether.exe run with --apply-update: it waits for this
            // process to exit, mirrors the program files (keeping Data) and starts the new version.
            Set("准备重启更新…");
            var target = Paths.AppDir;
            Process.Start(new ProcessStartInfo(Path.Combine(root, "Aether.exe"),
                $"--apply-update \"{root}\" \"{target}\" {Environment.ProcessId}")
            { UseShellExecute = false, WorkingDirectory = root });
            Aether.App.Quit();
        }
        catch (Exception ex)
        {
            Log.Error("update", ex);
            Set("更新失败：" + ex.Message);
        }
        finally { Busy = false; }
    }

    /// <summary>Runs in the new version's temp copy: wait, copy, restart. Returns the exit code.</summary>
    public static int Apply(string from, string to, int waitPid)
    {
        try
        {
            try { using var p = Process.GetProcessById(waitPid); p.WaitForExit(20000); } catch { }
            System.Threading.Thread.Sleep(500);
            Exception? last = null;
            for (int i = 0; i < 10; i++) // files may stay locked for a moment
            {
                try { Setup.CopyProgram(from, to); last = null; break; }
                catch (Exception ex) { last = ex; System.Threading.Thread.Sleep(700); }
            }
            if (last is not null) throw last;
            if (Setup.TestMode) return 0;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.Combine(to, "Aether.exe")}\"") { UseShellExecute = false });
            return 0;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("更新没装上：" + ex.Message + "\n\n旧版本还能用，从开始菜单重新打开 Aether 即可。", Setup.ProductName);
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.Combine(to, "Aether.exe")}\"") { UseShellExecute = false }); } catch { }
            return 1;
        }
    }
}
