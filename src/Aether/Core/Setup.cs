using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Aether.Core;

/// <summary>
/// Install / uninstall without a separate installer: a friend unzips the release anywhere and
/// runs Aether.exe; it offers to install itself to %LOCALAPPDATA%\Programs\Aether Desktop
/// (per-user, no admin), adds Start-menu shortcuts, autostart and an entry in
/// Settings › Apps so it can be removed the normal way. Uninstall restores the taskbar first.
/// </summary>
public static class Setup
{
    public const string ProductName = "Aether Desktop";
    private const string QuitEventName = @"Local\Aether.Desktop.v1.Quit";

    /// <summary>
    /// Test mode (AETHER_INSTALL_DIR set): install somewhere else and leave the real install alone —
    /// no autostart, no taskbar changes, no quitting/starting Aether, separate registry/menu names.
    /// </summary>
    public static bool TestMode { get; } = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AETHER_INSTALL_DIR"));
    private static string UninstallKey => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AetherDesktop" + (TestMode ? "Test" : "");

    public static string InstallDir { get; } = Environment.GetEnvironmentVariable("AETHER_INSTALL_DIR") is { Length: > 0 } t ? t
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName);
    public static string InstallExe => Path.Combine(InstallDir, "Aether.exe");
    private static string MenuDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName + (TestMode ? " Test" : ""));

    public static string Version => typeof(Setup).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool RunningInstalled => Same(Paths.AppDir, InstallDir);

    /// <summary>Started from an unzipped release (not the installed copy, not a dev build).</summary>
    public static bool ShouldOfferInstall
    {
        get
        {
            if (RunningInstalled || Environment.GetEnvironmentVariable("AETHER_NO_SETUP") == "1") return false;
            var dir = Paths.AppDir.ToLowerInvariant();
            if (dir.Contains(@"\bin\") || dir.Contains(@"\dist\")) return false; // developer builds
            return File.Exists(Path.Combine(Paths.AppDir, "Aether.dll"));
        }
    }

    // =====================================================================
    // install
    // =====================================================================
    /// <summary>First-run dialog. Returns true when the installed copy was started (this one should exit).</summary>
    public static bool OfferInstall()
    {
        bool update = File.Exists(InstallExe);
        var keep = new CheckBox
        {
            Content = "用 Aether 的 Dock 代替 Windows 任务栏（随时可在设置里改回来）",
            IsChecked = true, Foreground = Brushes.White, Margin = new Thickness(0, 14, 0, 0),
        };
        var body = update
            ? $"本机已经装了 Aether，要用这个版本（{Version}）覆盖更新吗？\n你的设置、便签和置物架都会保留。"
            : $"Aether {Version} 会安装到你的用户目录（不需要管理员权限），\n并添加开始菜单快捷方式和开机自启。\n\n卸载：设置 › 应用，或 Aether 设置 › 关于 › 卸载。";
        int choice = Ask(update ? "更新 Aether Desktop" : "安装 Aether Desktop", body, update ? null : keep,
            update ? "更新" : "安装", "先不装，直接试用");
        if (choice == 0) { Environment.Exit(0); return true; } // closed the dialog
        if (choice == 2) return false;                         // run portable from here
        try
        {
            Install(keepTaskbar: !update && keep.IsChecked != true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("install", ex);
            MessageBox.Show("安装失败：" + ex.Message, ProductName);
            return true;
        }
    }

    public static void Install(bool keepTaskbar)
    {
        QuitRunning();
        Directory.CreateDirectory(InstallDir);
        CopyProgram(Paths.AppDir, InstallDir);
        CreateShortcuts();
        RegisterUninstall();
        if (TestMode) return;
        // Autostart is set by the installed exe itself (it registers its own path).
        Run(InstallExe, "--autostart-mode high", wait: true);
        // This process exits right away, so the new one doesn't stay tied to it.
        Process.Start(new ProcessStartInfo(InstallExe, keepTaskbar ? "--keep-taskbar" : "") { UseShellExecute = true, WorkingDirectory = InstallDir });
    }

    /// <summary>Mirror program files (everything except Data), removing files the new version no longer has.</summary>
    public static void CopyProgram(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, dir);
            if (IsData(rel)) continue;
            Directory.CreateDirectory(Path.Combine(to, rel));
        }
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, file);
            if (IsData(rel)) continue;
            File.Copy(file, Path.Combine(to, rel), overwrite: true);
        }
        foreach (var file in Directory.EnumerateFiles(to, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(to, file);
            if (IsData(rel) || File.Exists(Path.Combine(from, rel))) continue;
            try { File.Delete(file); } catch { }
        }
    }

    private static bool IsData(string rel) => rel.Equals("Data", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("Data\\", StringComparison.OrdinalIgnoreCase);

    private static void CreateShortcuts()
    {
        Directory.CreateDirectory(MenuDir);
        Shortcut(Path.Combine(MenuDir, "Aether Desktop.lnk"), InstallExe, "", "Aether Desktop");
        Shortcut(Path.Combine(MenuDir, "Aether 恢复任务栏（应急）.lnk"), InstallExe, "--restore-taskbar", "把 Windows 任务栏恢复原样");
        Shortcut(Path.Combine(MenuDir, "卸载 Aether Desktop.lnk"), InstallExe, "--uninstall", "卸载 Aether Desktop");
    }

    private static void Shortcut(string lnk, string target, string args, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic s = shell.CreateShortcut(lnk);
        s.TargetPath = target;
        s.Arguments = args;
        s.WorkingDirectory = Path.GetDirectoryName(target);
        s.Description = description;
        s.Save();
    }

    private static void RegisterUninstall()
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", ProductName);
        k.SetValue("DisplayVersion", Version);
        k.SetValue("Publisher", "Aether");
        k.SetValue("DisplayIcon", InstallExe);
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("UninstallString", $"\"{InstallExe}\" --uninstall");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try
        {
            long bytes = Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories).Where(f => !IsData(Path.GetRelativePath(InstallDir, f))).Sum(f => new FileInfo(f).Length);
            k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
        }
        catch { }
    }

    /// <summary>Keep the Apps-list version in step after an update.</summary>
    public static void RefreshRegistration()
    {
        if (!RunningInstalled) return;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (k is null || k.GetValue("DisplayVersion") as string != Version) RegisterUninstall();
        }
        catch { }
    }

    // =====================================================================
    // uninstall
    // =====================================================================
    /// <summary>`Aether.exe --uninstall` (Start menu / Settings › Apps / Aether settings).</summary>
    public static void RunUninstall()
    {
        var wipe = new CheckBox { Content = "同时删除我的设置、便签和置物架记录", Foreground = Brushes.White, Margin = new Thickness(0, 14, 0, 0) };
        int choice = Ask("卸载 Aether Desktop", "会恢复 Windows 任务栏，删除开机自启、开始菜单快捷方式和程序文件。", wipe, "卸载", "取消");
        if (choice != 1) return;
        QuitRunning();
        Uninstall(wipe.IsChecked == true);
        MessageBox.Show("Aether Desktop 已卸载。", ProductName);
    }

    public static void Uninstall(bool wipeData)
    {
        if (!TestMode)
        {
            try { Shell.Taskbar.Restore(); } catch { }
            try { Autostart.Set(AutostartMode.Off); } catch { }
        }
        try { if (Directory.Exists(MenuDir)) Directory.Delete(MenuDir, true); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        // This exe may be the one being deleted: let a hidden helper remove the folder after we exit.
        var target = InstallDir.Replace("'", "''");
        var script = wipeData
            ? $"Start-Sleep 2; Remove-Item -LiteralPath '{target}' -Recurse -Force -ErrorAction SilentlyContinue"
            : $"Start-Sleep 2; Get-ChildItem -LiteralPath '{target}' -Force | Where-Object {{ $_.Name -ne 'Data' }} | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue";
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{script}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
        });
    }

    // =====================================================================
    // helpers
    // =====================================================================
    /// <summary>Ask a running Aether (installed or not) to quit gracefully, so files can be replaced.</summary>
    public static void QuitRunning()
    {
        if (TestMode) return;
        var me = Environment.ProcessId;
        var others = Process.GetProcessesByName("Aether").Where(p => p.Id != me).ToList();
        if (others.Count == 0) return;
        try { using var q = EventWaitHandle.OpenExisting(QuitEventName); q.Set(); } catch { }
        foreach (var p in others)
        {
            try { if (!p.WaitForExit(6000)) p.Kill(); } catch { }
        }
    }

    private static void Run(string exe, string args, bool wait)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
            if (wait) p?.WaitForExit(15000);
        }
        catch (Exception ex) { Log.Error("setup-run " + args, ex); }
    }

    /// <summary>Small dark dialog. Returns 1 = primary, 2 = secondary, 0 = closed.</summary>
    public static int Ask(string title, string body, UIElement? extra, string primary, string secondary)
    {
        int result = 0;
        var panel = new StackPanel { Margin = new Thickness(24, 22, 24, 20), MaxWidth = 460 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = body, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCC, 0xD8)), LineHeight = 20 });
        if (extra is not null) panel.Children.Add(extra);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var win = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true,
            Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x20, 0x2C)), Content = panel,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
        };
        Button Make(string text, bool accent, int value)
        {
            var b = new Button { Content = text, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 0, 0), IsDefault = accent };
            var styleKey = accent ? "AccentButton" : "GhostButton";
            if (Application.Current?.TryFindResource(styleKey) is Style st) b.Style = st;
            b.Click += (_, _) => { result = value; win.Close(); };
            return b;
        }
        buttons.Children.Add(Make(secondary, false, 2));
        buttons.Children.Add(Make(primary, true, 1));
        panel.Children.Add(buttons);
        win.SourceInitialized += (_, _) => Shell.WindowEffects.SetDarkMode(new WindowInteropHelper(win).Handle, true);
        win.ShowDialog();
        return result;
    }
}
