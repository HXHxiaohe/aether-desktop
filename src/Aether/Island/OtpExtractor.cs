using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Aether.Island;

/// <summary>
/// Finds the verification code in a mail. Weighted scoring rather than one regex (one regex
/// happily "finds" order numbers, amounts and phone numbers). Word lists and weights follow
/// 岛仔 (otp.rs, MIT) which follows pulse-mail; threshold 65 — rather miss one than show a wrong one.
/// </summary>
public static class OtpExtractor
{
    public const int Threshold = 65;

    private static readonly Regex Strong = new(
        @"验证码|校验码|确认码|动态码|动态密码|安全码|登录码|激活码|一次性(?:密码|口令)|otp|one[-\s]?time\s+(?:code|password|passcode)|" +
        @"verification\s+code|security\s+code|auth(?:entication)?\s+code|passcode|\bcode\b|認証コード|인증번호",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Weak = new(@"登录|登陆|验证|校验|身份|确认|安全|sign[-\s]?in|log[-\s]?in|verify|verification|confirm|authenticate",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Candidate = new(
        @"(?:[A-Za-z]{1,3}-)?(?:\d{3,4}[- ]\d{3,4}|\d{4}-\d{4}|[A-Za-z0-9]{3,4}-[A-Za-z0-9]{3,4}|\d{4,8}|[A-Za-z0-9]{4,8})", RegexOptions.Compiled);
    private static readonly Regex Clock = new(@"\d{1,2}:\d{2}", RegexOptions.Compiled);

    private static readonly string[] OrderWords = { "订单", "单号", "快递", "运单", "物流", "包裹", "编号", "序号", "发票", "金额", "合计", "实付", "应付", "电话", "手机号", "会议号", "会议", "卡尾号", "尾号", "邮编", "sku", "order", "track", "invoice", "amount", "phone", "meeting", "card ending" };
    private static readonly string[] MoneyWords = { "¥", "￥", "$", "€", "元", "价格", "金额", "合计", "实付", "应付" };
    private static readonly string[] PromoWords = { "优惠", "折扣", "促销", "满减", "券", "红包", "coupon", "promo", "discount", "sale" };
    private static readonly string[] ExpireWords = { "有效", "过期", "失效", "expire", "valid", "minutes", "分钟", "小时内" };

    /// <summary>The most code-like token in subject + body, or null. <paramref name="emphasized"/> = bold/large HTML snippets.</summary>
    public static string? Extract(string subject, string body, IReadOnlyList<string>? emphasized = null)
    {
        subject = Normalize(subject);
        body = Normalize(body.Length > 6000 ? body[..6000] : body);
        var hay = subject + "\n" + body;
        int subjectEnd = subject.Length + 1;
        var emph = (emphasized ?? Array.Empty<string>()).Select(Normalize).ToList();

        (int Score, int Pos, bool Subj, string Text)? best = null;
        foreach (Match m in Candidate.Matches(hay))
        {
            int start = m.Index, end = m.Index + m.Length;
            if (start > 0 && IsAsciiAlnum(hay[start - 1])) continue;
            if (end < hay.Length && IsAsciiAlnum(hay[end])) continue;
            var text = m.Value;
            if (Score(hay, start, end, text, subjectEnd, emph) is not { } s || !s.Strong) continue;
            bool inSubject = start < subjectEnd;
            bool better = best is null
                || s.Score > best.Value.Score
                || (s.Score == best.Value.Score && inSubject && !best.Value.Subj)
                || (s.Score == best.Value.Score && inSubject == best.Value.Subj && start < best.Value.Pos);
            if (better) best = (s.Score, start, inSubject, text);
        }
        return best is { } b && b.Score >= Threshold ? b.Text : null;
    }

    private static bool IsAsciiAlnum(char c) => c < 128 && char.IsLetterOrDigit(c);

    private static (int Score, bool Strong)? Score(string hay, int start, int end, string text, int subjectEnd, List<string> emph)
    {
        var core = new string(text.Where(char.IsLetterOrDigit).ToArray());
        int digits = core.Count(char.IsAsciiDigit), letters = core.Count(char.IsAsciiLetter);
        if (digits == 0) return null;
        bool mixed = letters > 0, separated = text.Contains('-') || text.Contains(' ');
        int len = core.Length, score;
        if (mixed)
        {
            if (len < 5) return null;
            score = len == 6 ? 20 : separated ? 22 : 14;
        }
        else
        {
            score = len switch { 6 => separated ? 28 : 30, 7 or 8 => separated ? 22 : 18, 5 => 20, 4 => 16, _ => -1 };
            if (score < 0) return null;
        }

        string b40 = Before(hay, start, 40), b250 = Before(hay, start, 250), b60 = Before(hay, start, 60), a40 = After(hay, end, 40);

        // hard rejects: URL / mail address / date-time / money / order-ish numbers
        int tl = start; while (tl > 0 && !char.IsWhiteSpace(hay[tl - 1])) tl--;
        int tr = end; while (tr < hay.Length && !char.IsWhiteSpace(hay[tr])) tr++;
        var token = hay[tl..tr].ToLowerInvariant();
        if (token.Contains("http") || token.Contains("www.") || token.Contains('/') || token.Contains('@')) return null;
        var near6 = Before(hay, start, 6) + After(hay, end, 6);
        if (Has(near6, "年", "月", "日", "有效期至") || Clock.IsMatch(near6) || Clock.IsMatch(Before(hay, start, 8))) return null;
        if (Has(near6, MoneyWords)) return null;
        if (Has(Before(hay, start, 20), OrderWords) || Has(After(hay, end, 8), OrderWords)) return null;
        if (len == 4 && !mixed && int.TryParse(core, out var y) && y is >= 1900 and <= 2100) score -= 35;

        bool strongBefore = Strong.IsMatch(b40);
        if (strongBefore) score += 40;
        else if (Strong.IsMatch(b250)) score += 22;
        else if (Weak.IsMatch(b60)) score += 15;
        bool strongAfter = Strong.IsMatch(a40);
        if (strongAfter) score += 28;

        int ls = hay.LastIndexOf('\n', Math.Max(0, start - 1)); ls = start == 0 ? 0 : ls + 1;
        int le = hay.IndexOf('\n', start); if (le < 0) le = hay.Length;
        var line = hay[ls..le].Trim().TrimEnd('。', '，', ',', '.', '！', '!', '、', ';', '；').Trim();
        bool ownLine = line == text;
        bool colon = hay[..start].TrimEnd().EndsWith(':');
        bool inSubject = start < subjectEnd;
        bool isEmph = emph.Any(e => e.Contains(text));
        if (mixed && !(ownLine || colon || inSubject || isEmph)) return null;
        if (ownLine) score += 15;
        if (colon) score += 10;
        if (inSubject) score += 15;
        if (isEmph) score += 25;
        if (Has(b40, "验证码是", "验证码为", "校验码是", "code is", "code:")) score += 5;
        if (Has(a40, ExpireWords) || Has(b60, ExpireWords)) score += 5;
        if (Has(Before(hay, start, 40) + After(hay, end, 40), PromoWords)) score -= 30;

        return (score, strongBefore || strongAfter || inSubject || isEmph || ownLine || colon);
    }

    private static string Before(string s, int at, int n) => s[Math.Max(0, at - n)..at];
    private static string After(string s, int at, int n) => s[at..Math.Min(s.Length, at + n)];
    private static bool Has(string window, params string[] words)
    {
        var lower = window.ToLowerInvariant();
        return words.Any(w => lower.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>Full-width → half-width, drop zero-width characters.</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '​' or '‌' or '‍' or '﻿' or '⁠') continue;
            if (c == '　') sb.Append(' ');
            else if (c >= '！' && c <= '～') sb.Append((char)(c - 0xfee0));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ HTML helpers
    private static readonly Regex ScriptStyle = new(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Breaks = new(@"<br\s*/?>|</p>|</div>|</tr>|</li>|</h[1-6]>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Emph = new(@"<(b|strong|h[1-3])\b[^>]*>(.*?)</\1>|<(span|td|div|p)\b[^>]*font-size\s*:\s*(\d+)[^>]*>(.*?)</\3>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static string HtmlToText(string html)
    {
        var s = ScriptStyle.Replace(html, " ");
        s = Breaks.Replace(s, "\n");
        s = Tags.Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        var sb = new StringBuilder();
        foreach (var line in s.Split('\n'))
        {
            var t = string.Join(" ", line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (t.Length > 0) sb.Append(t).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Snippets an HTML mail presents as "the code": bold, headings, big font.</summary>
    public static List<string> Emphasized(string html)
    {
        var list = new List<string>();
        foreach (Match m in Emph.Matches(html))
        {
            string inner;
            if (m.Groups[2].Success) inner = m.Groups[2].Value;
            else if (int.TryParse(m.Groups[4].Value, out var size) && size >= 18) inner = m.Groups[5].Value;
            else continue;
            var t = WebUtility.HtmlDecode(Tags.Replace(inner, "")).Trim();
            if (t.Length is > 0 and <= 40) list.Add(t);
        }
        return list;
    }
}
