using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Aether.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Aether.Island;

public sealed class MailAccount
{
    public string Provider { get; set; } = "qq";
    public string Email { get; set; } = "";
    /// <summary>Custom IMAP only.</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 993;
    public string User { get; set; } = "";
    /// <summary>Authorization code, DPAPI-encrypted for this Windows user (base64).</summary>
    public string Secret { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public uint LastUid { get; set; }
    public uint UidValidity { get; set; }

    public string HostName => Provider == "custom" ? Host : MailProviders.Host(Provider);
    public string LoginName => Provider == "custom" && User.Length > 0 ? User : Email;
}

public sealed class MailItem
{
    public string Account { get; init; } = "";
    public string Provider { get; init; } = "";
    public uint Uid { get; init; }
    public string From { get; init; } = "";
    public string Subject { get; init; } = "";
    public DateTimeOffset Date { get; init; }
    public string? Code { get; init; }
    /// <summary>After this, the code is masked (setting: 30 / 60 / 300 s).</summary>
    public DateTime CodeUntil { get; set; }
    public bool CodeVisible => Code is not null && DateTime.Now < CodeUntil;
}

public static class MailProviders
{
    public static readonly (string Id, string Name)[] All =
    {
        ("qq", "QQ 邮箱"), ("163", "163 邮箱"), ("126", "126 邮箱"), ("exmail", "腾讯企业邮"),
        ("gmail", "Gmail"), ("icloud", "iCloud"), ("outlook", "Outlook / Hotmail"), ("custom", "其他 IMAP"),
    };

    public static string Name(string id) => All.FirstOrDefault(p => p.Id == id).Name ?? id;

    public static string Host(string id) => id switch
    {
        "qq" => "imap.qq.com", "163" => "imap.163.com", "126" => "imap.126.com", "exmail" => "imap.exmail.qq.com",
        "gmail" => "imap.gmail.com", "icloud" => "imap.mail.me.com", "outlook" => "outlook.office365.com", _ => "",
    };

    public static string WebUrl(string id) => id switch
    {
        "qq" => "https://mail.qq.com/", "163" => "https://mail.163.com/", "126" => "https://mail.126.com/", "exmail" => "https://exmail.qq.com/",
        "gmail" => "https://mail.google.com/", "icloud" => "https://www.icloud.com/mail", "outlook" => "https://outlook.live.com/mail/", _ => "",
    };

    public static string Help(string id) => id switch
    {
        "qq" => "QQ 邮箱网页版 › 设置 › 账号 › 开启「IMAP/SMTP 服务」，按提示生成「授权码」填到这里（不是 QQ 密码）。",
        "163" or "126" => "网易邮箱网页版 › 设置 › POP3/SMTP/IMAP › 开启「IMAP/SMTP 服务」，在「授权密码管理」新增授权码填到这里。",
        "exmail" => "企业邮箱 › 设置 › 客户端设置 › 开启 IMAP；开了安全登录的话在「微信绑定 › 客户端专用密码」生成密码。",
        "gmail" => "Google 账号 › 安全 › 两步验证 › 应用专用密码，生成一个 16 位密码填到这里；Gmail 设置里需启用 IMAP。",
        "icloud" => "appleid.apple.com › 登录与安全 › App 专用密码，生成后填到这里；邮箱填 @icloud.com 地址。",
        "outlook" => "微软账号已不支持密码登录 IMAP 的个人邮箱可能连不上；企业邮箱请联系管理员开启 IMAP。",
        _ => "填写 IMAP 服务器地址（993 端口 SSL）、登录名和密码/授权码。",
    };

    /// <summary>163 / 126 refuse logins ("Unsafe Login") unless the client sends an RFC 2971 ID first.</summary>
    public static bool NeedsId(string id) => id is "163" or "126";
}

/// <summary>
/// 邮件: checks the user's IMAP inboxes (read-only — never marks mail as read), shows the latest
/// mails on the island and pops verification codes ("验证码 384920 · 点击复制") that hide again after
/// a while. Authorization codes are stored DPAPI-encrypted (only this Windows user can decrypt them).
/// </summary>
public static class MailService
{
    private static List<MailAccount> _accounts = new();
    private static readonly List<MailItem> Recent = new();
    private static DispatcherTimer? _timer;
    private static bool _busy;
    private static DateTime _lastCheck = DateTime.MinValue;
    private static readonly Dictionary<string, string> Status = new();

    public static IReadOnlyList<MailAccount> Accounts => _accounts;
    public static IReadOnlyList<MailItem> Items => Recent;
    public static int Unseen { get; private set; }
    public static string StatusOf(MailAccount a) => Status.TryGetValue(a.Email, out var s) ? s : "";

    /// <summary>A mail with a verification code arrived (UI thread).</summary>
    public static event Action<MailItem>? CodeArrived;
    public static event Action? Changed;

    private static IslandSettings S => SettingsStore.Current.Island;

    public static void Start()
    {
        _accounts = JsonFile.Load<List<MailAccount>>("mail.json");
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(Math.Clamp(S.MailInterval, 30, 1800)) };
        _timer.Tick += (_, _) => { if (S.Mail && !Shell.GameMode.Active) _ = CheckAsync(); };
        _timer.Start();
        if (S.Mail && _accounts.Any(a => a.Enabled)) Application.Current.Dispatcher.BeginInvoke(() => _ = CheckAsync(), DispatcherPriority.ApplicationIdle);
    }

    public static void Stop() => _timer?.Stop();

    public static void CheckSoon()
    {
        if (S.Mail && (DateTime.Now - _lastCheck).TotalSeconds > 20) _ = CheckAsync();
    }

    public static void MarkSeen() { if (Unseen == 0) return; Unseen = 0; Changed?.Invoke(); }

    // ------------------------------------------------------------------ accounts
    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Encoding.UTF8.GetBytes("Aether.Mail"), DataProtectionScope.CurrentUser));

    private static string Unprotect(string secret) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(secret), Encoding.UTF8.GetBytes("Aether.Mail"), DataProtectionScope.CurrentUser));

    /// <summary>Try to log in; returns null on success, else a readable reason.</summary>
    public static async Task<string?> TestAsync(MailAccount a, string password)
    {
        try
        {
            using var client = await ConnectAsync(a, password, CancellationToken.None);
            await client.DisconnectAsync(true);
            return null;
        }
        catch (Exception ex) { return Explain(ex); }
    }

    public static void Save(MailAccount a)
    {
        _accounts.RemoveAll(x => string.Equals(x.Email, a.Email, StringComparison.OrdinalIgnoreCase));
        _accounts.Add(a);
        JsonFile.Save("mail.json", _accounts);
        Changed?.Invoke();
        _ = CheckAsync();
    }

    public static void Remove(MailAccount a)
    {
        _accounts.Remove(a);
        Recent.RemoveAll(m => m.Account == a.Email);
        Status.Remove(a.Email);
        JsonFile.Save("mail.json", _accounts);
        Changed?.Invoke();
    }

    public static void SetEnabled(MailAccount a, bool on)
    {
        a.Enabled = on;
        JsonFile.Save("mail.json", _accounts);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ checking
    public static async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        _lastCheck = DateTime.Now;
        try
        {
            var accounts = _accounts.Where(a => a.Enabled && a.Secret.Length > 0).ToList();
            var results = await Task.WhenAll(accounts.Select(a => Task.Run(() => FetchAsync(a))));
            bool dirty = false;
            foreach (var (account, fresh, first) in results)
            {
                if (fresh is null) continue;
                foreach (var m in fresh.OrderBy(m => m.Date))
                {
                    if (Recent.Any(r => r.Account == m.Account && r.Uid == m.Uid)) continue;
                    Recent.Add(m);
                    dirty = true;
                    if (first) continue; // initial fill: show the list, don't pop old codes
                    Unseen++;
                    if (m.Code is not null && (DateTimeOffset.Now - m.Date).TotalMinutes < 15) CodeArrived?.Invoke(m);
                }
            }
            Recent.Sort((x, y) => y.Date.CompareTo(x.Date));
            if (Recent.Count > 40) Recent.RemoveRange(40, Recent.Count - 40);
            if (dirty) JsonFile.Save("mail.json", _accounts); // LastUid moved
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Error("mail-check", ex); }
        finally { _busy = false; }
    }

    private static async Task<(MailAccount, List<MailItem>?, bool First)> FetchAsync(MailAccount a)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            using var client = await ConnectAsync(a, Unprotect(a.Secret), cts.Token);
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, cts.Token);
            bool first = a.LastUid == 0 || a.UidValidity != inbox.UidValidity || Recent.All(r => r.Account != a.Email);
            IList<UniqueId> uids;
            if (a.LastUid == 0 || a.UidValidity != inbox.UidValidity)
            {
                var all = await inbox.SearchAsync(SearchQuery.DeliveredAfter(DateTime.Now.AddDays(-7)), cts.Token);
                uids = all.OrderBy(u => u.Id).TakeLast(10).ToList();
            }
            else
            {
                var range = new UniqueIdRange(new UniqueId(a.LastUid + 1), UniqueId.MaxValue);
                uids = (await inbox.SearchAsync(range, SearchQuery.All, cts.Token)).Where(u => u.Id > a.LastUid).ToList();
                if (first)
                {
                    // restarted: refill the list with the last few as well
                    var recent = await inbox.SearchAsync(SearchQuery.DeliveredAfter(DateTime.Now.AddDays(-7)), cts.Token);
                    uids = recent.Where(u => u.Id <= a.LastUid).OrderBy(u => u.Id).TakeLast(8).Concat(uids).ToList();
                }
            }
            var list = new List<MailItem>();
            if (uids.Count > 0)
            {
                var summaries = await inbox.FetchAsync(uids.TakeLast(20).ToList(), MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure | MessageSummaryItems.InternalDate, cts.Token);
                foreach (var s in summaries)
                {
                    string subject = s.Envelope?.Subject ?? "";
                    var from = s.Envelope?.From.Mailboxes.FirstOrDefault();
                    string sender = from is null ? "" : string.IsNullOrWhiteSpace(from.Name) ? from.Address : from.Name;
                    string? code = null;
                    bool recentMail = (DateTimeOffset.Now - (s.InternalDate ?? s.Envelope?.Date ?? DateTimeOffset.Now)).TotalMinutes < 30;
                    if (recentMail || first) code = await FindCodeAsync(inbox, s, subject, cts.Token);
                    var date = s.InternalDate ?? s.Envelope?.Date ?? DateTimeOffset.Now;
                    list.Add(new MailItem
                    {
                        Account = a.Email, Provider = a.Provider, Uid = s.UniqueId.Id, From = sender, Subject = subject, Date = date, Code = code,
                        CodeUntil = DateTime.Now.AddSeconds(Math.Clamp(S.MailCodeSeconds, 10, 3600)) - (DateTimeOffset.Now - date),
                    });
                }
                a.LastUid = Math.Max(a.LastUid, uids.Max(u => u.Id));
            }
            a.UidValidity = inbox.UidValidity;
            await client.DisconnectAsync(true, cts.Token);
            Status[a.Email] = "";
            return (a, list, first);
        }
        catch (Exception ex)
        {
            Status[a.Email] = Explain(ex);
            Log.Info($"[mail] {MailProviders.Name(a.Provider)}: {ex.GetType().Name} {ex.Message}");
            return (a, null, false);
        }
    }

    /// <summary>Only the text part is downloaded (never attachments), and only while the mail is fresh.</summary>
    private static async Task<string?> FindCodeAsync(IMailFolder inbox, IMessageSummary s, string subject, CancellationToken ct)
    {
        try
        {
            string text = "";
            List<string>? emph = null;
            if (s.TextBody is { } tb && tb.Octets < 200_000 && await inbox.GetBodyPartAsync(s.UniqueId, tb, ct) is TextPart tp) text = tp.Text ?? "";
            if (s.HtmlBody is { } hb && hb.Octets < 400_000 && await inbox.GetBodyPartAsync(s.UniqueId, hb, ct) is TextPart hp)
            {
                var html = hp.Text ?? "";
                emph = OtpExtractor.Emphasized(html);
                if (text.Length == 0) text = OtpExtractor.HtmlToText(html);
            }
            return OtpExtractor.Extract(subject, text, emph);
        }
        catch (Exception ex) { Log.Info("[mail] body: " + ex.GetType().Name); return OtpExtractor.Extract(subject, ""); }
    }

    private static async Task<ImapClient> ConnectAsync(MailAccount a, string password, CancellationToken ct)
    {
        var client = new ImapClient { Timeout = 20000 };
        try
        {
            await client.ConnectAsync(a.HostName, a.Port <= 0 ? 993 : a.Port, SecureSocketOptions.SslOnConnect, ct);
            if (MailProviders.NeedsId(a.Provider) && client.Capabilities.HasFlag(ImapCapabilities.Id))
                await client.IdentifyAsync(new ImapImplementation { Name = "Aether Desktop", Version = "1.0", Vendor = "Aether" }, ct);
            await client.AuthenticateAsync(a.LoginName, password, ct);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static string Explain(Exception ex) => ex switch
    {
        AuthenticationException => "登录失败：请确认填的是「授权码」而不是邮箱密码，且已开启 IMAP",
        ImapCommandException ice when ice.Message.Contains("Unsafe", StringComparison.OrdinalIgnoreCase) => "邮箱拒绝了登录（Unsafe Login），请在网页版确认已开启 IMAP",
        System.Net.Sockets.SocketException or System.IO.IOException => "连不上邮箱服务器，检查网络",
        OperationCanceledException => "连接超时",
        CryptographicException => "授权码解密失败，请重新填写",
        _ => "读取失败：" + ex.Message,
    };

    public static void OpenWeb(MailItem m)
    {
        var url = MailProviders.WebUrl(m.Provider);
        if (url.Length == 0) return;
        try { Interop.Native.LendForeground(); Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
