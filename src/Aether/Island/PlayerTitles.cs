using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Aether.Interop;

namespace Aether.Island;

/// <summary>
/// Fallback for music players that don't report to Windows' media controls (e.g. 网易云音乐
/// without its "开启 SMTC" option): they show "歌名 - 歌手" in their window title, and whether
/// they're playing can be told from whether they are making sound right now.
/// </summary>
public static class PlayerTitles
{
    public sealed record Found(string Exe, string Title, string Artist);

    private sealed record Player(string Process, string[] Classes, string[] IdleTitles, string? Suffix);

    private static readonly Player[] Players =
    {
        new("cloudmusic", new[] { "OrpheusBrowserHost", "icon" }, new[] { "网易云音乐", "NetEase Cloud Music" }, null),
        new("QQMusic", new[] { "QQMusic_Daemon_Wnd", "TXGuiFoundation" }, new[] { "QQ音乐", "QQ音乐 听我想听" }, null),
        new("KuGou", new[] { "kugou_ui" }, new[] { "酷狗音乐" }, " - 酷狗音乐"),
    };

    public static Found? Detect()
    {
        foreach (var pl in Players)
        {
            var procs = Process.GetProcessesByName(pl.Process);
            if (procs.Length == 0) continue;
            var pids = procs.Select(p => (uint)p.Id).ToHashSet();
            // QueryFullProcessImageName is cheap; Process.MainModule walks the module list.
            string? exe = Native.ProcessPath((uint)procs[0].Id);
            foreach (var p in procs) p.Dispose();

            string? title = null;
            Native.EnumWindows((hwnd, _) =>
            {
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (!pids.Contains(pid)) return true;
                if (!pl.Classes.Contains(Native.ClassOf(hwnd))) return true;
                var t = Native.WindowTitle(hwnd);
                if (t.Contains(" - ", StringComparison.Ordinal)) { title = t; return false; }
                return true;
            }, IntPtr.Zero);
            if (title is null || pl.IdleTitles.Contains(title)) continue;

            if (pl.Suffix is { } suf && title.EndsWith(suf, StringComparison.Ordinal)) title = title[..^suf.Length];
            int dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash <= 0) continue;
            return new Found(exe ?? pl.Process + ".exe", title[..dash].Trim(), title[(dash + 3)..].Trim());
        }
        return null;
    }

    /// <summary>Is this exe making sound right now (according to the audio session meters)?</summary>
    public static bool Audible(string exe) =>
        AudioService.Sources.Any(s => string.Equals(Path.GetFileName(s.ExePath), Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase));

    // Media keys: the players above all react to them.
    public static void PlayPause() => Key(0xB3);
    public static void Next() => Key(0xB0);
    public static void Previous() => Key(0xB1);

    private static void Key(byte vk)
    {
        Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
        Native.keybd_event(vk, 0, 2, UIntPtr.Zero);
    }
}
