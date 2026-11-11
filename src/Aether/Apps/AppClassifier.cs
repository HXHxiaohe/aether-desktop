using System;
using System.Linq;

namespace Aether.Apps;

/// <summary>Keyword rules for putting apps into drawer groups. Unknown apps go to 未分类.</summary>
public static class AppClassifier
{
    private static readonly (string Group, string[] Words)[] Rules =
    {
        ("游戏", new[] { "steam", "epic games", "battle.net", "riot", "league of legends", "英雄联盟", "wegame", "uu加速", "加速器", "ubisoft", "origin", "ea app", "gog galaxy", "minecraft", "genshin", "原神", "崩坏", "xbox", "游戏", "game", "\\steamapps\\", "\\steam\\", "wallpaper engine", "大富翁", "poker", "扑克", "轮盘", "影之刃", "暴雪", "blizzard", "网易云游戏", "osu" }),
        ("社交", new[] { "微信", "wechat", "weixin", "qq", "tim", "discord", "kook", "telegram", "whatsapp", "slack", "飞书", "lark", "钉钉", "dingtalk", "企业微信", "line", "skype", "teams", "zoom", "腾讯会议" }),
        ("浏览器", new[] { "chrome", "edge", "firefox", "opera", "brave", "vivaldi", "浏览器", "browser", "夸克", "quark", "arc" }),
        ("办公", new[] { "word", "excel", "powerpoint", "outlook", "onenote", "access", "publisher", "wps", "notion", "obsidian", "sticky notes", "便笺", "便签", "pdf", "acrobat", "印象笔记", "evernote", "todo", "calendar", "日历", "邮件", "mail", "typora", "xmind", "visio", "project" }),
        ("创作", new[] { "capcut", "剪映", "photoshop", "illustrator", "premiere", "after effects", "lightroom", "figma", "blender", "davinci", "obs", "audition", "canva", "krita", "gimp", "sketch", "paint", "画图", "c4d", "cinema 4d", "maya", "unity", "unreal", "pr ", "ps " }),
        ("影音", new[] { "抖音", "douyin", "bilibili", "哔哩", "网易云", "cloudmusic", "qq音乐", "qqmusic", "酷狗", "酷我", "spotify", "potplayer", "vlc", "爱奇艺", "腾讯视频", "优酷", "芒果", "media player", "播放器", "music", "音乐", "视频", "video", "youtube", "netflix", "斗鱼", "虎牙" }),
        ("开发", new[] { "visual studio", "vs code", "code", "cursor", "jetbrains", "intellij", "pycharm", "rider", "webstorm", "git", "github", "terminal", "powershell", "python", "node", "docker", "postman", "android studio", "sublime", "notepad++", "wsl", "claude", "codex", "deepseek", "chatgpt", "ollama" }),
        ("网盘下载", new[] { "百度网盘", "baidunetdisk", "阿里云盘", "迅雷", "thunder", "idm", "internet download", "onedrive", "dropbox", "google drive", "115", "123云盘", "qbittorrent", "bitcomet", "motrix", "网盘", "云盘" }),
        ("系统工具", new[] { "7-zip", "winrar", "bandizip", "控制面板", "control panel", "command prompt", "命令提示符", "注册表", "regedit", "registry", "file explorer", "资源管理器", "task manager", "任务管理器", "disk", "磁盘", "defrag", "dfrgui", "everything", "powertoys", "geforce", "nvidia", "amd software", "intel", "administrative tools", "管理工具", "系统", "system", "设置", "settings", "character map", "字符映射", "snipping", "截图", "计算器", "calculator", "remote desktop", "远程桌面", "recovery", "odbc", "iscsi", "services", "event viewer", "performance monitor", "resource monitor", "memory diagnostic", "windows tools", "msconfig", "cleanup", "清理", "driver", "驱动", "clash", "v2ray", "vpn" }),
    };

    public static string Guess(string name, string? path)
    {
        // Only the name, the containing folder and the file name — full paths contain noise
        // like "Microsoft", "Programs", "System32".
        string tail = "";
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                var dir = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? "");
                tail = dir + " " + System.IO.Path.GetFileNameWithoutExtension(path);
                if (path.Contains("\\steamapps\\", StringComparison.OrdinalIgnoreCase)) tail += " steam";
            }
            catch { }
        }
        var hay = (" " + name + " " + tail + " ").ToLowerInvariant();
        foreach (var (group, words) in Rules)
            if (words.Any(w => Matches(hay, w.Trim()))) return AppCatalog.Canonical(group);
        return AppCatalog.Ungrouped;
    }

    private static bool Matches(string hay, string word)
    {
        // Latin words must match as whole words ("access" ≠ "accessibility", "qq" ≠ "qqq").
        if (word.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == '-')))
        {
            int i = 0;
            while ((i = hay.IndexOf(word, i, StringComparison.Ordinal)) >= 0)
            {
                bool left = i == 0 || !char.IsLetterOrDigit(hay[i - 1]);
                int end = i + word.Length;
                bool right = end >= hay.Length || !char.IsLetterOrDigit(hay[end]);
                if (left && right) return true;
                i = end;
            }
            return false;
        }
        return hay.Contains(word, StringComparison.Ordinal);
    }
}
