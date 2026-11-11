using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Aether.Apps;
using Aether.Core;

namespace Aether.Island;

public sealed record NotificationItem(long Id, string AppId, string AppName, string Title, string Body, DateTime Time)
{
    public ImageSource? Icon => ShellIcons.Get("shell:AppsFolder\\" + AppId, 48);
}

/// <summary>
/// Windows notifications (toasts) read from the user's own notification database
/// (%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db) with Windows' built-in
/// SQLite (winsqlite3.dll), read-only. No app identity, certificate or system change needed,
/// and it keeps working with 勿扰 on (notifications still land in the notification center).
/// Woken by file changes, not polling.
/// </summary>
public static class NotificationService
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications");
    private static string Db => Path.Combine(Folder, "wpndatabase.db");

    private static FileSystemWatcher? _watcher;
    private static DispatcherTimer? _debounce, _safety;
    private static long _lastId;
    private static bool _busy;
    private static readonly List<NotificationItem> Recent = new();
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<NotificationItem> Items => Recent;
    public static int Unseen { get; private set; }

    /// <summary>New notification (UI thread).</summary>
    public static event Action<NotificationItem>? Arrived;
    /// <summary>List or unseen count changed (UI thread).</summary>
    public static event Action? Changed;

    public static void Start()
    {
        if (!File.Exists(Db)) { Log.Info("[notify] database not found"); return; }
        // Start after what is already there; keep the last few as history for the list page.
        var existing = Query(0);
        foreach (var n in existing.TakeLast(10)) Recent.Insert(0, n);
        _lastId = existing.Count > 0 ? existing.Max(n => n.Id) : MaxId();

        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = CheckAsync(); };
        _watcher = new FileSystemWatcher(Folder, "wpndatabase.db*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
        _watcher.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => { if (_debounce is { IsEnabled: false }) _debounce.Start(); });
        // Windows doesn't reliably report writes to files it keeps open, so also look every
        // 3 s (one indexed query on a tiny database: well under a millisecond).
        _safety = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _safety.Tick += (_, _) => _ = CheckAsync();
        _safety.Start();
    }

    public static void Stop()
    {
        _watcher?.Dispose();
        _safety?.Stop();
        _debounce?.Stop();
    }

    private static async Task CheckAsync()
    {
        if (_busy || Shell.GameMode.Active) return;
        _busy = true;
        try
        {
            long after = _lastId;
            var fresh = await Task.Run(() => Query(after));
            foreach (var n in fresh)
            {
                _lastId = Math.Max(_lastId, n.Id);
                Recent.Insert(0, n);
                if (Recent.Count > 30) Recent.RemoveAt(Recent.Count - 1);
                Unseen++;
                Arrived?.Invoke(n);
            }
            if (fresh.Count > 0) Changed?.Invoke();
        }
        catch (Exception ex) { Log.Error("notify-check", ex); }
        finally { _busy = false; }
    }

    public static void MarkSeen()
    {
        if (Unseen == 0) return;
        Unseen = 0;
        Changed?.Invoke();
    }

    public static void ClearList()
    {
        Recent.Clear();
        Unseen = 0;
        Changed?.Invoke();
    }

    public static void Open(NotificationItem n) => AppCatalog.Launch("shell:AppsFolder\\" + n.AppId);

    // ---------------- database ----------------
    private static long MaxId()
    {
        long max = 0;
        Sql.Read(Db, "select max(Id) from Notification", st => max = Sql.Int64(st, 0));
        return max;
    }

    private static List<NotificationItem> Query(long afterId)
    {
        var list = new List<NotificationItem>();
        Sql.Read(Db,
            "select n.Id, h.PrimaryId, n.Payload, n.ArrivalTime from Notification n join NotificationHandler h on n.HandlerId = h.RecordId " +
            $"where n.Type = 'toast' and n.Id > {afterId} order by n.Id",
            st =>
            {
                long id = Sql.Int64(st, 0);
                string app = Sql.Text(st, 1) ?? "";
                var payload = Sql.Blob(st, 2);
                long ft = Sql.Int64(st, 3);
                if (IsSystem(app)) return;
                var (title, body) = Parse(payload);
                if (title.Length == 0 && body.Length == 0) return;
                DateTime time;
                try { time = ft > 0 ? DateTime.FromFileTimeUtc(ft).ToLocalTime() : DateTime.Now; } catch { time = DateTime.Now; }
                list.Add(new NotificationItem(id, app, AppName(app), title, body, time));
            });
        return list;
    }

    /// <summary>Toast XML → (title, body): first &lt;text&gt; is the title, the rest the body.</summary>
    private static (string Title, string Body) Parse(byte[]? payload)
    {
        if (payload is null || payload.Length == 0) return ("", "");
        try
        {
            bool utf16 = payload.Length > 1 && payload[1] == 0;
            var xml = (utf16 ? Encoding.Unicode : Encoding.UTF8).GetString(payload).Trim('\0', '﻿', ' ', '\r', '\n');
            var doc = XDocument.Parse(xml);
            var texts = doc.Descendants().Where(e => e.Name.LocalName == "text" && (string?)e.Attribute("placement") != "attribution")
                .Select(e => e.Value.Trim()).Where(t => t.Length > 0).ToList();
            if (texts.Count == 0) return ("", "");
            return (texts[0], string.Join(" ", texts.Skip(1)));
        }
        catch { return ("", ""); }
    }

    /// <summary>Windows' own toasts (安全中心、系统提示、无名 GUID 来源、自己发的测试) — not "messages".</summary>
    private static readonly string[] SystemPrefixes =
    {
        "Windows.", "Microsoft.Windows.", "MicrosoftWindows.", "SecurityCenter",
        // Microsoft's built-in system apps / plumbing (not people messaging you)
        "Microsoft.SecHealthUI", "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller",
        "Microsoft.Winget", "Microsoft.WinAppRuntime", "MicrosoftCorporationII.WinAppRuntime", "Microsoft.WindowsFeedbackHub",
        "Microsoft.GetHelp", "Microsoft.StartExperiencesApp", "Microsoft.XboxGameCallableUI", "Microsoft.LockApp",
        "Microsoft.AAD.", "Microsoft.AccountsControl", "Microsoft.BioEnrollment", "Microsoft.CredDialogHost", "Microsoft.ECApp",
        "Microsoft.Win32WebViewHost", "Microsoft.AsyncTextService", "Microsoft.ApplicationCompatibilityEnhancements",
        "Microsoft.WidgetsPlatformRuntime", "Microsoft.LanguageExperiencePack", "Microsoft.ConnectedDevicePlatform",
        "Microsoft.OfficePushNotificationUtility", "Microsoft.Office.ActionsServer",
    };

    /// <summary>
    /// Windows' own toasts (安全中心、更新、系统提示、商店、托盘气泡、无名 GUID 来源、自己发的测试) — not "messages".
    /// Everything from a real app (Discord, 邮件, 手机连接, 网盘…) passes with its content.
    /// </summary>
    private static bool IsSystem(string appId)
    {
        if (appId.Length == 0) return true;
        if (appId.StartsWith('{'))
        {
            // "{knownfolder-guid}\path\app.exe" is a desktop app registered by path; a bare GUID is anonymous.
            int slash = appId.IndexOf('\\');
            if (slash < 0) return true;
            var path = appId[slash..];
            if (path.Contains("powershell", StringComparison.OrdinalIgnoreCase) || path.Contains("\\cmd.exe", StringComparison.OrdinalIgnoreCase)
                || path.Contains("\\wscript.exe", StringComparison.OrdinalIgnoreCase) || path.Contains("\\Windows", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        if (SystemPrefixes.Any(p => appId.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;
        // Packaged Windows components share Microsoft's system publisher id.
        if (appId.Contains("_cw5n1h2txyewy!", StringComparison.OrdinalIgnoreCase)) return true;
        var name = AppName(appId);
        return name.StartsWith('{') || name.Equals("SecurityCenter", StringComparison.OrdinalIgnoreCase);
    }

    private static string AppName(string appId)
    {
        if (Names.TryGetValue(appId, out var n)) return n;
        n = ShellIcons.DisplayName("shell:AppsFolder\\" + appId);
        if (string.IsNullOrWhiteSpace(n))
        {
            var last = appId.Contains('!') ? appId[(appId.IndexOf('!') + 1)..] : appId;
            n = last.Split('.', '\\').LastOrDefault(p => p.Length > 2 && !p.Equals("exe", StringComparison.OrdinalIgnoreCase)) ?? appId;
        }
        Names[appId] = n!;
        return n!;
    }
}

/// <summary>Minimal read-only access to SQLite through Windows' own winsqlite3.dll.</summary>
internal static class Sql
{
    private const string Dll = "winsqlite3.dll";
    [DllImport(Dll, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.StdCall)] private static extern int Open(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Dll, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.StdCall)] private static extern int Prepare(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
    [DllImport(Dll, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.StdCall)] private static extern int Step(IntPtr stmt);
    [DllImport(Dll, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.StdCall)] private static extern int Finalize(IntPtr stmt);
    [DllImport(Dll, EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.StdCall)] private static extern int Close(IntPtr db);
    [DllImport(Dll, EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.StdCall)] private static extern int BusyTimeout(IntPtr db, int ms);
    [DllImport(Dll, EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.StdCall)] private static extern long ColInt64(IntPtr stmt, int i);
    [DllImport(Dll, EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.StdCall)] private static extern IntPtr ColText(IntPtr stmt, int i);
    [DllImport(Dll, EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.StdCall)] private static extern IntPtr ColBlob(IntPtr stmt, int i);
    [DllImport(Dll, EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.StdCall)] private static extern int ColBytes(IntPtr stmt, int i);

    private const int SQLITE_OPEN_READONLY = 0x01, SQLITE_OPEN_URI = 0x40, SQLITE_ROW = 100;

    public static void Read(string file, string sql, Action<IntPtr> row)
    {
        var uri = "file:" + file.Replace('\\', '/') + "?mode=ro";
        if (Open(Encoding.UTF8.GetBytes(uri + "\0"), out var db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, IntPtr.Zero) != 0) { Close(db); return; }
        try
        {
            BusyTimeout(db, 500);
            if (Prepare(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out var st, IntPtr.Zero) != 0) return;
            try { while (Step(st) == SQLITE_ROW) row(st); }
            finally { Finalize(st); }
        }
        finally { Close(db); }
    }

    public static long Int64(IntPtr st, int i) => ColInt64(st, i);

    public static string? Text(IntPtr st, int i)
    {
        var p = ColText(st, i);
        return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
    }

    public static byte[]? Blob(IntPtr st, int i)
    {
        var p = ColBlob(st, i);
        int n = ColBytes(st, i);
        if (p == IntPtr.Zero || n <= 0) return null;
        var b = new byte[n];
        Marshal.Copy(p, b, 0, n);
        return b;
    }
}
