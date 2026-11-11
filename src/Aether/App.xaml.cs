using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Aether.Apps;
using Aether.Core;
using Aether.Dock;
using Aether.Island;
using Aether.Settings;
using Aether.Shell;
using Aether.Theme;

namespace Aether;

public partial class App : Application
{
    private const string MutexName = @"Local\Aether.Desktop.v1";
    private const string ShowEventName = @"Local\Aether.Desktop.v1.ShowSettings";
    private const string QuitEventName = @"Local\Aether.Desktop.v1.Quit";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _tray;
    private static SettingsWindow? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Installer helper: `--autostart-mode high|normal|low|off` sets the level and exits
        // (no UI, works alongside a running instance).
        int am = Array.FindIndex(e.Args, a => a.Equals("--autostart-mode", StringComparison.OrdinalIgnoreCase));
        if (am >= 0)
        {
            if (am + 1 < e.Args.Length && Enum.TryParse<AutostartMode>(e.Args[am + 1], true, out var mode))
                Autostart.Set(mode);
            Shutdown();
            return;
        }

        // Update helper (a temp copy of the new version): `--apply-update <from> <to> <pid>`.
        int au = Array.FindIndex(e.Args, a => a.Equals("--apply-update", StringComparison.OrdinalIgnoreCase));
        if (au >= 0 && au + 3 < e.Args.Length && int.TryParse(e.Args[au + 3], out int waitPid))
        {
            Shutdown(Updater.Apply(e.Args[au + 1], e.Args[au + 2], waitPid));
            return;
        }
        if (e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Setup.RunUninstall();
            Shutdown();
            return;
        }
        // Started from an unzipped release: offer to install (or update the installed copy).
        if (Setup.ShouldOfferInstall && !e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase) && Setup.OfferInstall())
        {
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running: ask that instance to show its settings, then leave.
            // (an autostart launch while already running stays quiet)
            if (!e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase))
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        // Small, mostly static windows: software rendering keeps the discrete GPU asleep
        // (on laptops WPF would otherwise wake the NVIDIA GPU and load its driver).
        if (Environment.GetEnvironmentVariable("AETHER_GPU") != "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("ui", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Error("domain", ex);
        };

        SettingsStore.Load();
        bool firstRun = !System.IO.File.Exists(Paths.SettingsFile);
        if (e.Args.Contains("--keep-taskbar", StringComparer.OrdinalIgnoreCase))
        {
            // Installer: the user chose to keep the Windows taskbar.
            SettingsStore.Current.Dock.HideTaskbar = false;
            SettingsStore.SaveNow();
        }
        Setup.RefreshRegistration();
        foreach (var arg in e.Args)
        {
            if (arg.Equals("--restore-taskbar", StringComparison.OrdinalIgnoreCase))
            {
                // Emergency: put the Windows taskbar back and exit.
                Taskbar.Restore();
                SettingsStore.Current.Dock.HideTaskbar = false;
                SettingsStore.SaveNow();
                Shutdown();
                return;
            }
        }
        ThemeService.Init();
        // Diagnostics: AETHER_DISABLE=island,dock,clock turns parts off to find what costs CPU.
        var disabled = (Environment.GetEnvironmentVariable("AETHER_DISABLE") ?? "").Split(',');
        if (!disabled.Contains("clock")) WidgetHost.Sync();
        AppCatalog.Initialize();
        TaskWindows.Start();
        GameMode.Start();
        if (!disabled.Contains("dock")) ApplyDock();
        AudioService.Start();
        BrightnessService.Start();
        AttentionService.Start();
        NotificationService.Start();
        ShelfService.Start();
        try { ClipboardService.Start(); } catch (Exception ex) { Log.Error("clipboard-start", ex); }
        QuotaService.Start();
        MailService.Start();
        TimerService.Start();
        PowerWatch.Start();
        Updater.Start();
        if (Environment.GetEnvironmentVariable("AETHER_NORADIO") != "1") _ = RadioService.StartAsync();
        _ = MediaService.StartAsync();
        if (!disabled.Contains("island")) ApplyIsland();
        _tray = new TrayIcon();
        SessionEnding += (_, _) => { if (Taskbar.IsHiddenByUs) Taskbar.Restore(); };

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => ShowSettings(null));
        }) { IsBackground = true, Name = "show-settings" };
        listener.Start();

        // Graceful quit signal (used by build.ps1 / updates) so the taskbar is always restored.
        var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        quitEvent.Reset(); // ignore a stale signal left over from a previous instance
        new Thread(() => { if (quitEvent.WaitOne()) Dispatcher.BeginInvoke(Quit); }) { IsBackground = true, Name = "quit" }.Start();

        if (firstRun)
        {
            SettingsStore.SaveNow();
            // First launch: take the color from the desktop right away, then show settings.
            _ = ThemeService.PickNowAsync();
            ShowSettings();
        }
        foreach (var arg in e.Args)
            if (arg.Equals("--settings", StringComparison.OrdinalIgnoreCase)) ShowSettings();
        // First run (or the update that brought it): play the tutorial once everything is up.
        if (!SettingsStore.Current.TutorialSeen && Environment.GetEnvironmentVariable("AETHER_ISLAND_MODE") is null)
        {
            var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            later.Tick += (_, _) => { later.Stop(); Aether.Settings.TutorialWindow.ShowTutorial(); };
            later.Start();
        }
        Memory.TrimSoon(); // drop start-up garbage (catalog scan, icon decoding)

    }

    public static void ShowSettings() => ShowSettings(null);

    public static void ShowSettings(string? page)
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => { _settings = null; Memory.TrimSoon(); };
            _settings.Show();
        }
        if (page is not null) _settings.SelectPage(page);
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    private static DockWindow? _dock;

    /// <summary>Create / remove the Dock and hide / restore the taskbar to match settings.</summary>
    public static void ApplyDock()
    {
        var d = SettingsStore.Current.Dock;
        if (d.Enabled && _dock is null)
        {
            _dock = new DockWindow();
            _dock.Show();
        }
        else if (!d.Enabled && _dock is not null)
        {
            _dock.Close();
            _dock = null;
            DrawerHost.Close();
        }
        else if (_dock is not null && _dock.NeedsRecreate)
        {
            // Blur was switched off: a fresh window is the only way back to clear glass.
            _dock.Close();
            _dock = new DockWindow();
            _dock.Show();
        }
        else if (_dock is not null)
        {
            _dock.ApplyLook();
            _dock.Rebuild();
        }

        if (d.Enabled && d.HideTaskbar) { if (!Taskbar.IsHiddenByUs) Taskbar.Hide(); }
        else if (Taskbar.IsHiddenByUs || SettingsStore.Current.TaskbarWasAutoHide is not null) Taskbar.Restore();
    }

    private static IslandWindow? _island;

    public static void ApplyIsland()
    {
        bool on = SettingsStore.Current.Island.Enabled;
        if (on && _island is null)
        {
            _island = new IslandWindow();
            _island.Show();
        }
        else if (!on && _island is not null)
        {
            _island.Close();
            _island = null;
        }
        else _island?.RefreshAll();
    }

    public static void ResetIslandPosition() => _island?.ResetPosition();

    /// <summary>Settings › 流光: play the chosen style (or a one-shot) on the island.</summary>
    public static void PreviewGlow(string? oneShot = null) => _island?.PreviewGlow(oneShot);

    public static void Quit()
    {
        SettingsStore.Flush();
        ThemeService.Shutdown();
        TaskWindows.Stop();
        _island?.Close();
        AttentionService.Stop();
        NotificationService.Stop();
        ClipboardService.Stop();
        QuotaService.Stop();
        MailService.Stop();
        TimerService.Stop();
        PowerWatch.Stop();
        AudioService.Stop();
        BrightnessService.Stop();
        if (Taskbar.IsHiddenByUs || SettingsStore.Current.TaskbarWasAutoHide is not null) Taskbar.Restore();
        _dock?.Close();
        DrawerHost.Close();
        WidgetHost.CloseAll();
        ((App)Current)._tray?.Dispose();
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Taskbar.IsHiddenByUs) Taskbar.Restore();
        SettingsStore.Flush();
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
