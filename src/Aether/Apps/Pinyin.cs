using System;
using System.Text;

namespace Aether.Apps;

/// <summary>
/// First-letter (A–Z) of a name, using GB2312 code ranges for Chinese characters,
/// so the drawer can sort "Access, 百度, CapCut, 抖音…" together alphabetically.
/// Also used for search by initials ("wx" → 微信).
/// </summary>
public static class Pinyin
{
    private static readonly Encoding? Gb2312;

    static Pinyin()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Gb2312 = Encoding.GetEncoding("GB2312");
        }
        catch { Gb2312 = null; }
    }

    // GB2312 level-1 boundaries for initials A..Z (I, U, V have none).
    private static readonly int[] Starts =
    {
        0xB0A1, 0xB0C5, 0xB2C1, 0xB4EE, 0xB6EA, 0xB7A2, 0xB8C1, 0xB9FE, 0xBBF7,
        0xBFA6, 0xC0AC, 0xC2E8, 0xC4C3, 0xC5B6, 0xC5BE, 0xC6DA, 0xC8BB, 0xC8F6,
        0xCBFA, 0xCDDA, 0xCEF4, 0xD1B9, 0xD4D1,
    };
    private const string Letters = "ABCDEFGHJKLMNOPQRSTWXYZ";

    public static char Initial(char c)
    {
        if (c < 128) return char.IsLetter(c) ? char.ToUpperInvariant(c) : '#';
        if (Gb2312 is null) return '~';
        var bytes = Gb2312.GetBytes(c.ToString());
        if (bytes.Length < 2) return '~';
        int code = bytes[0] << 8 | bytes[1];
        if (code < Starts[0] || code > 0xD7F9) return '~'; // level-2 / rare characters
        for (int i = Starts.Length - 1; i >= 0; i--)
            if (code >= Starts[i]) return i < Letters.Length ? Letters[i] : 'Z';
        return '~';
    }

    public static string Initials(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            var i = Initial(c);
            if (i is >= 'A' and <= 'Z') sb.Append(i);
        }
        return sb.ToString();
    }

    public static string SortKey(string name)
    {
        var t = name.TrimStart();
        return t.Length == 0 ? "~" : Initial(t[0]).ToString();
    }
}
