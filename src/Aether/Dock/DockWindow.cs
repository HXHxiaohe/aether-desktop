using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Aether.Apps;
using Aether.Core;
using Aether.Interop;
using Aether.Shell;
using Aether.Theme;

namespace Aether.Dock;

/// <summary>
/// Bottom-center Dock that replaces the taskbar: drawer button, pinned apps, running
/// windows, quick buttons. Hides itself when windows overlap it (smart mode) and
/// disappears entirely for fullscreen apps.
/// </summary>
public sealed class DockWindow : Window
{
    private static DockWindow? _instance;
    private static Popup? _labelPopup;
    private static TextBlock? _labelText;

    private readonly Border _pill;
    private readonly StackPanel _row;
    private readonly DispatcherTimer _poll;
    private IntPtr _hwnd;
    private bool _shown = true;
    private DateTime _wantHideSince = DateTime.MaxValue;
    private int _tick;
    private bool _menuOpen;
    private uint _taskbarCreatedMsg;

    private static DockSettings D => SettingsStore.Current.Dock;

    public DockWindow()
    {
        _instance = this;
        Title = "Aether Dock";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = WindowEffects.Layered;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");

        _row = new StackPanel { Orientation = Orientation.Horizontal };
        _pill = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 2, 8, 0),
            Child = _row,
        };
        _pill.SetResourceReference(Border.BorderBrushProperty, "GlassBorderBrush");
        _pill.MouseRightButtonUp += (_, e) => { if (!e.Handled) { DockMenu().IsOpen = true; e.Handled = true; } };
        Content = _pill;
        AllowDrop = true;
        DragEnter += OnDragOverDock;
        DragOver += OnDragOverDock;
        DragLeave += (_, _) => EndDropPreview();
        Drop += OnDropDock;

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(160) };
        _poll.Tick += (_, _) => Poll();
        // While a game is fullscreen the Dock is hidden anyway: check once a second instead.
        GameMode.Changed += OnGameMode;
        Closed += (_, _) => GameMode.Changed -= OnGameMode;

        SizeChanged += (_, _) => { Reposition(); ApplyRegion(); };
        ThemeService.Changed += OnTheme;
        TaskWindows.Changed += OnWindowsChanged;
        TaskWindows.ForegroundChanged += OnWindowsChanged;
        AppCatalog.Changed += OnCatalogChanged;
        Closed += (_, _) =>
        {
            _poll.Stop();
            ThemeService.Changed -= OnTheme;
            TaskWindows.Changed -= OnWindowsChanged;
            TaskWindows.ForegroundChanged -= OnWindowsChanged;
            AppCatalog.Changed -= OnCatalogChanged;
            if (_instance == this) _instance = null;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, Native.WS_EX_APPWINDOW);
        if (!WindowEffects.Layered) WindowEffects.MakeTransparent(this);
        WindowEffects.SetBorderColor(_hwnd, Native.DWMWA_COLOR_NONE);
        _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyLook();
        Rebuild();
        _poll.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(Native.MA_NOACTIVATE); }
        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            // Explorer restarted: hide its fresh taskbar again.
            Dispatcher.BeginInvoke(() => { if (D.HideTaskbar) Taskbar.Hide(); }, DispatcherPriority.Background);
        }
        if (msg == Native.WM_DISPLAYCHANGE) Dispatcher.BeginInvoke(Reposition);
        return IntPtr.Zero;
    }

    private void OnTheme() => Dispatcher.BeginInvoke(ApplyLook);

    private void OnGameMode(bool on) => _poll.Interval = TimeSpan.FromMilliseconds(on ? 1000 : 160);
    private void OnWindowsChanged() => Dispatcher.BeginInvoke(UpdateRunning, DispatcherPriority.Background);
    private void OnCatalogChanged() => Dispatcher.BeginInvoke(Rebuild, DispatcherPriority.Background);

    /// <summary>
    /// Blur-behind is only clipped by Windows' own corner rounding (fixed 8px), not by a window
    /// region. So with blur the Dock uses 8px corners; without blur it keeps a rounder pill.
    /// </summary>
    private double CornerRadius => BlurOn ? 8 : 16;

    /// <summary>Blur only makes sense with a frame; frameless Docks are fully clear.</summary>
    private static bool BlurOn => D.Blur && D.ShowFrame;

    private bool _blurOn;

    /// <summary>True when blur was switched off after being on: the window must be recreated.</summary>
    public bool NeedsRecreate => _blurOn && !BlurOn;

    public void ApplyLook()
    { Aether.Core.Diag.Hit("Dock.ApplyLook");
        if (NeedsRecreate) return; // App.ApplyDock replaces this window
        // Frameless: alpha-1 fill keeps the gaps between icons clickable on a layered window
        // (fully transparent pixels would let clicks fall through to the desktop).
        _pill.Background = D.ShowFrame ? ThemeService.MakeGlass(D.Opacity) : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        _pill.BorderThickness = new Thickness(D.ShowFrame ? 1 : 0);
        double pad = Math.Clamp(D.FramePadding, 0, 40);
        _pill.Padding = new Thickness(pad, Math.Max(0, pad - 6), pad, Math.Max(0, pad - 8));
        _pill.CornerRadius = new CornerRadius(CornerRadius + Math.Max(0, pad - 8) * 0.6);
        if (_hwnd == IntPtr.Zero) return;
        WindowEffects.SetCorners(_hwnd, BlurOn ? Native.DWMWCP_ROUND : Native.DWMWCP_DONOTROUND);
        ApplyRegion();
    }

    // ---------------- content ----------------
    private readonly Dictionary<AppEntry, DockItemView> _pinViews = new();
    private readonly Dictionary<string, DockItemView> _runViews = new();
    private StackPanel? _runPanel;
    private Rectangle? _runSep;

    public void Rebuild()
    { Aether.Core.Diag.Hit("Dock.Rebuild");
        _row.Children.Clear();
        _pinViews.Clear();
        _runViews.Clear();
        double size = D.IconSize;

        var drawer = new DockItemView(size, null, "") { Label = "应用抽屉", Magnify = D.Magnify };
        drawer.Click = () => DrawerHost.Toggle();
        drawer.Menu = DockMenu;
        _row.Children.Add(drawer);
        _row.Children.Add(Separator(size));

        _pinOrder.Clear();
        _pinsStart = _row.Children.Count;
        foreach (var app in AppCatalog.Pinned())
        {
            var v = new DockItemView(size, ShellIcons.Get(app.Path, 96), "") { Label = app.Name, Magnify = D.Magnify };
            var a = app;
            v.Click = () => ClickPinned(a);
            v.MiddleClick = () => AppCatalog.Launch(a);
            v.Menu = () => PinnedMenu(a);
            v.DragMove = dx => ReorderPreview(v, dx);
            v.DragEnd = dx => ReorderCommit(a, v, dx);
            _pinViews[app] = v;
            _pinOrder.Add(v);
            _row.Children.Add(v);
        }

        _runPanel = new StackPanel { Orientation = Orientation.Horizontal };
        _runSep = Separator(size);
        _row.Children.Add(_runSep);
        _row.Children.Add(_runPanel);

        _row.Children.Add(Separator(size));
        var quick = new DockItemView(size, null, "") { Label = "网络 · 音量 · 亮度", Magnify = D.Magnify };
        quick.Click = () => PressWinKey(0x41); // Win+A quick settings
        _row.Children.Add(quick);
        var settings = new DockItemView(size, null, "") { Label = "Aether 设置", Magnify = D.Magnify };
        settings.Click = () => App.ShowSettings("dock");
        settings.Menu = DockMenu;
        _row.Children.Add(settings);

        UpdateRunning();
    }

    // ---------------- drag to reorder / drop to pin ----------------
    private readonly List<DockItemView> _pinOrder = new();
    private int _pinsStart;
    private double Slot => D.IconSize + 12;

    private int TargetIndex(int from, double dx) => Math.Clamp(from + (int)Math.Round(dx / Slot), 0, _pinOrder.Count - 1);

    private static void Shift(UIElement el, double x)
    {
        if (el.RenderTransform is not TranslateTransform t) el.RenderTransform = t = new TranslateTransform();
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void ReorderPreview(DockItemView dragged, double dx)
    {
        int from = _pinOrder.IndexOf(dragged);
        if (from < 0) return;
        double min = -from * Slot - Slot * 0.3, max = (_pinOrder.Count - 1 - from) * Slot + Slot * 0.3;
        dx = Math.Clamp(dx, min, max);
        if (dragged.RenderTransform is not TranslateTransform t) dragged.RenderTransform = t = new TranslateTransform();
        t.BeginAnimation(TranslateTransform.XProperty, null);
        t.X = dx;
        int to = TargetIndex(from, dx);
        for (int i = 0; i < _pinOrder.Count; i++)
        {
            if (i == from) continue;
            double s = from < to && i > from && i <= to ? -Slot : from > to && i >= to && i < from ? Slot : 0;
            Shift(_pinOrder[i], s);
        }
    }

    private void ReorderCommit(AppEntry app, DockItemView dragged, double dx)
    {
        int from = _pinOrder.IndexOf(dragged);
        int to = from < 0 ? 0 : TargetIndex(from, dx);
        if (from < 0 || to == from)
        {
            foreach (var v in _pinOrder) Shift(v, 0);
            return;
        }
        AppCatalog.Pin(app, to); // → Changed → Rebuild with the new order
    }

    /// <summary>Insertion index for a drop at window x: where the gap opens between pinned icons.</summary>
    private int DropIndex(double x)
    {
        if (_pinOrder.Count == 0) return 0;
        for (int i = 0; i < _pinOrder.Count; i++)
        {
            var p = _pinOrder[i].TranslatePoint(new Point(Slot / 2, 0), this);
            if (x < p.X) return i;
        }
        return _pinOrder.Count;
    }

    private static AppEntry? DroppedApp(IDataObject data)
    {
        if (data.GetData(AppDragFormat) is string path) return AppCatalog.Apps.FirstOrDefault(a => a.Path == path);
        if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            var f = files[0];
            if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                return AppCatalog.Apps.FirstOrDefault(a => AppCatalog.Same(a.Path, f) || AppCatalog.Same(a.Target, f)) ?? AppCatalog.AddFile(f);
        }
        return null;
    }

    /// <summary>Drag format for an app tile dragged out of the drawer (value: AppEntry.Path).</summary>
    public const string AppDragFormat = "Aether.App";

    private static bool Droppable(IDataObject d) =>
        d.GetDataPresent(AppDragFormat) ||
        (d.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f && (f[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f[0].EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f[0].EndsWith(".url", StringComparison.OrdinalIgnoreCase)));

    private bool _dropHot;

    private void OnDragOverDock(object sender, DragEventArgs e)
    {
        if (!Droppable(e.Data)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
        _dropHot = true;
        if (!_shown) Slide(true);
        int at = DropIndex(e.GetPosition(this).X);
        // open a gap: icons left of it slide left half a slot, the rest right
        for (int i = 0; i < _pinOrder.Count; i++) Shift(_pinOrder[i], i < at ? -Slot / 2 : Slot / 2);
    }

    private void EndDropPreview()
    {
        _dropHot = false;
        foreach (var v in _pinOrder) Shift(v, 0);
    }

    private void OnDropDock(object sender, DragEventArgs e)
    {
        int at = DropIndex(e.GetPosition(this).X);
        EndDropPreview();
        var app = DroppedApp(e.Data);
        if (app is not null) AppCatalog.Pin(app, at);
        e.Handled = true;
    }

    private static Rectangle Separator(double size) => new()
    {
        Width = 1, Height = size * 0.62, Margin = new Thickness(5, 0, 5, 6),
        VerticalAlignment = VerticalAlignment.Center,
        Fill = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
    };

    private List<TaskWindow> WindowsFor(AppEntry app)
    {
        if (app.Target is null && !app.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return new();
        string? aumid = app.Path.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) ? app.Path[17..] : null;
        var exact = TaskWindows.Windows.Where(w =>
            (app.Target is not null && AppCatalog.Same(w.ExePath, app.Target)) ||
            (aumid is not null && AppCatalog.Same(w.AppId, aumid))).ToList();
        if (exact.Count > 0 || app.Target is null) return exact;
        // Launcher shortcuts (UU: uu_launcher.exe → window of uu.exe): fall back to windows
        // of a program in the same install folder.
        var dir = OwnInstallDir(app.Target);
        return dir is null ? exact
            : TaskWindows.Windows.Where(w => w.ExePath is not null && AppCatalog.Same(System.IO.Path.GetDirectoryName(w.ExePath), dir)).ToList();
    }

    private static readonly string[] SharedDirs =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    };

    /// <summary>The target's folder if it belongs to that one program (not Windows / Program Files itself).</summary>
    private string? OwnInstallDir(string target)
    {
        string? dir;
        try { dir = System.IO.Path.GetDirectoryName(target); } catch { return null; }
        if (string.IsNullOrEmpty(dir) || SharedDirs.Any(s => AppCatalog.Same(s, dir))) return null;
        if (dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        // Only when no other pinned app lives in the same folder (else it would be ambiguous).
        return _pinViews.Keys.Count(a => a.Target is { } t && AppCatalog.Same(System.IO.Path.GetDirectoryName(t), dir)) > 1 ? null : dir;
    }

    private void UpdateRunning()
    { Aether.Core.Diag.Hit("Dock.UpdateRunning");
        if (_runPanel is null) return;
        var fg = TaskWindows.Foreground;
        var claimed = new HashSet<IntPtr>();

        foreach (var (app, view) in _pinViews)
        {
            var wins = WindowsFor(app);
            foreach (var w in wins) claimed.Add(w.Hwnd);
            view.SetRunning(wins.Count == 0 ? 0 : wins.Any(w => w.Hwnd == fg) ? 2 : 1);
        }

        var groups = D.ShowRunning
            ? TaskWindows.Windows.Where(w => !claimed.Contains(w.Hwnd)).GroupBy(w => w.Key).ToList()
            : new List<IGrouping<string, TaskWindow>>();

        // Remove views for groups that are gone, add new ones, keep order stable.
        foreach (var key in _runViews.Keys.Where(k => groups.All(g => g.Key != k)).ToList())
        {
            _runPanel.Children.Remove(_runViews[key]);
            _runViews.Remove(key);
        }
        foreach (var g in groups)
        {
            if (!_runViews.TryGetValue(g.Key, out var view))
            {
                var first = g.First();
                var iconPath = first.AppId is not null && first.ExePath?.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) != false
                    ? "shell:AppsFolder\\" + first.AppId
                    : first.ExePath ?? "";
                var icon = ShellIcons.Get(iconPath, 96) ?? (first.ExePath is null ? null : ShellIcons.Get(first.ExePath, 96));
                view = new DockItemView(D.IconSize, icon, "") { Magnify = D.Magnify };
                var key = g.Key;
                view.Click = () => ClickGroup(key);
                view.Menu = () => RunningMenu(key);
                _runViews[key] = view;
                _runPanel.Children.Add(view);
            }
            var list = g.ToList();
            view.Label = list.Count == 1 ? list[0].Title : $"{FriendlyName(list[0])}（{list.Count} 个窗口）";
            view.SetRunning(list.Any(w => w.Hwnd == fg) ? 2 : 1);
        }
        bool any = _runPanel.Children.Count > 0;
        if (_runSep is not null) _runSep.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    private static string FriendlyName(TaskWindow w)
    {
        var exe = w.ExePath;
        if (exe is null) return w.Title;
        if (NameCache.TryGetValue(exe, out var n)) return n;
        try { n = FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { }
        if (string.IsNullOrWhiteSpace(n)) n = System.IO.Path.GetFileNameWithoutExtension(exe);
        NameCache[exe] = n!;
        return n!;
    }

    // ---------------- clicks ----------------
    private void ClickPinned(AppEntry app)
    {
        var wins = WindowsFor(app);
        if (wins.Count == 0) { AppCatalog.Launch(app); return; }
        CycleOrToggle(wins);
    }

    private void ClickGroup(string key)
    {
        var wins = TaskWindows.Windows.Where(w => w.Key == key).ToList();
        if (wins.Count > 0) CycleOrToggle(wins);
    }

    private static void CycleOrToggle(List<TaskWindow> wins)
    {
        var fg = TaskWindows.Foreground;
        int idx = wins.FindIndex(w => w.Hwnd == fg);
        if (wins.Count == 1 || idx < 0) TaskWindows.ToggleOrActivate(idx < 0 ? wins[0] : wins[idx]);
        else Native.Activate(wins[(idx + 1) % wins.Count].Hwnd);
    }

    private static void PressWinKey(byte vk)
    {
        Native.keybd_event(0x5B, 0, 0, UIntPtr.Zero);
        Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
        Native.keybd_event(vk, 0, 2, UIntPtr.Zero);
        Native.keybd_event(0x5B, 0, 2, UIntPtr.Zero);
    }

    // ---------------- menus ----------------
    private ContextMenu NewMenu()
    {
        var m = new ContextMenu();
        m.Opened += (_, _) => _menuOpen = true;
        m.Closed += (_, _) => _menuOpen = false;
        return m;
    }

    private static MenuItem Item(string header, Action act, bool enabled = true)
    {
        var i = new MenuItem { Header = header, IsEnabled = enabled };
        i.Click += (_, _) => act();
        return i;
    }

    private void AddWindowItems(ContextMenu m, List<TaskWindow> wins)
    {
        foreach (var w in wins.Take(10))
        {
            var t = w.Title.Length > 40 ? w.Title[..40] + "…" : w.Title;
            var hw = w;
            m.Items.Add(Item(t, () => Native.Activate(hw.Hwnd)));
        }
        if (wins.Count > 0) m.Items.Add(new Separator());
    }

    private ContextMenu PinnedMenu(AppEntry app)
    {
        var m = NewMenu();
        var wins = WindowsFor(app);
        AddWindowItems(m, wins);
        m.Items.Add(Item(wins.Count > 0 ? "新建窗口" : "打开", () => AppCatalog.Launch(app)));
        m.Items.Add(Item("从 Dock 取消固定", () => AppCatalog.Unpin(app)));
        m.Items.Add(Item("打开文件位置", () => AppCatalog.RevealInExplorer(app)));
        if (wins.Count > 0) m.Items.Add(Item(wins.Count > 1 ? "关闭所有窗口" : "关闭窗口", () => wins.ForEach(TaskWindows.Close)));
        return m;
    }

    private ContextMenu RunningMenu(string key)
    {
        var m = NewMenu();
        var wins = TaskWindows.Windows.Where(w => w.Key == key).ToList();
        AddWindowItems(m, wins);
        var exe = wins.FirstOrDefault()?.ExePath;
        if (exe is not null && !exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            m.Items.Add(Item("新建窗口", () => AppCatalog.Launch(exe)));
            m.Items.Add(Item("固定到 Dock", () =>
            {
                var app = AppCatalog.Apps.FirstOrDefault(a => AppCatalog.Same(a.Target, exe)) ?? AppCatalog.AddFile(exe);
                if (app is not null) AppCatalog.Pin(app);
            }));
        }
        if (wins.Count > 0) m.Items.Add(Item(wins.Count > 1 ? "关闭所有窗口" : "关闭窗口", () => wins.ForEach(TaskWindows.Close)));
        return m;
    }

    private ContextMenu DockMenu()
    {
        var m = NewMenu();
        m.Items.Add(Item("打开应用抽屉", () => DrawerHost.Toggle()));
        m.Items.Add(Item("Dock 设置…", () => App.ShowSettings("dock")));
        m.PlacementTarget = this;
        m.Placement = PlacementMode.Top;
        return m;
    }

    // ---------------- labels ----------------
    public static void ShowLabel(FrameworkElement target, string text)
    {
        if (!D.ShowLabels || string.IsNullOrEmpty(text) || _instance is null) return;
        if (_labelPopup is null)
        {
            _labelText = new TextBlock { Foreground = Brushes.White, FontSize = 12.5, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI") };
            _labelPopup = new Popup
            {
                AllowsTransparency = true,
                Placement = PlacementMode.Custom,
                StaysOpen = true,
                IsHitTestVisible = false,
                Child = new Border
                {
                    Background = (Brush)Application.Current.Resources["MenuBrush"],
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 5, 10, 6), Child = _labelText,
                },
                CustomPopupPlacementCallback = (popup, targetSize, _) => new[]
                {
                    new CustomPopupPlacement(new Point((targetSize.Width - popup.Width) / 2, -popup.Height - 14), PopupPrimaryAxis.Horizontal),
                },
            };
        }
        _labelText!.Text = text.Length > 60 ? text[..60] + "…" : text;
        _labelPopup.PlacementTarget = target;
        _labelPopup.IsOpen = false;
        _labelPopup.IsOpen = true;
    }

    public static void HideLabel(FrameworkElement target)
    {
        if (_labelPopup is not null && _labelPopup.PlacementTarget == target) _labelPopup.IsOpen = false;
    }

    // ---------------- placement & auto-hide ----------------
    private double ScreenW => SystemParameters.PrimaryScreenWidth;
    private double ScreenH => SystemParameters.PrimaryScreenHeight;
    private double ShownTop => ScreenH - ActualHeight - D.BottomGap;
    private double HiddenTop => ScreenH + 2;

    private void Reposition()
    {
        Left = Math.Round((ScreenW - ActualWidth) / 2);
        BeginAnimation(TopProperty, null);
        Top = _shown ? ShownTop : HiddenTop;
    }

    private void ApplyRegion()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (BlurOn)
        {
            // DWM rounding clips both content and blur; a region would fight it.
            Native.SetWindowRgn(_hwnd, IntPtr.Zero, true);
        }
        else
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
            int r = (int)Math.Round(_pill.CornerRadius.TopLeft * 2 * dpi.DpiScaleX);
            Native.SetWindowRgn(_hwnd, Native.CreateRoundRectRgn(0, 0, w + 1, h + 1, r, r), true);
        }
        if (BlurOn)
        {
            WindowEffects.SetBlur(_hwnd, true);
            _blurOn = true;
        }
    }

    private void Poll()
    {
        if (++_tick % 6 == 0 && D.HideTaskbar) Taskbar.Enforce();

        bool fullscreen = TaskWindows.ForegroundIsFullscreen();
        Native.GetCursorPos(out var cur);
        var dpi = VisualTreeHelper.GetDpi(this);
        double cx = cur.X / dpi.DpiScaleX, cy = cur.Y / dpi.DpiScaleY;

        bool atEdge = cy >= ScreenH - 3;
        double sTop = ShownTop;
        bool overDock = cx >= Left - 10 && cx <= Left + ActualWidth + 10 && cy >= sTop - 30 && cy <= ScreenH;
        bool busy = _menuOpen || DrawerHost.IsOpen || _dropHot || _pinOrder.Any(v => v.IsDragging);
        bool hover = (_shown && overDock) || (atEdge && cx >= Left - 60 && cx <= Left + ActualWidth + 60) || busy;

        bool want = D.HideMode switch
        {
            DockHideMode.Never => true,
            DockHideMode.Always => hover,
            _ => hover || !Overlapped(sTop),
        };
        if (fullscreen && !busy) want = false;
        if (Taskbar.IsPeeking) want = false; // the real taskbar is showing for tray access
        if (WindowEffects.DebugDockPinned) want = true;

        if (want)
        {
            _wantHideSince = DateTime.MaxValue;
            if (!_shown) Slide(true);
        }
        else if (_shown)
        {
            if (_wantHideSince == DateTime.MaxValue) _wantHideSince = DateTime.Now;
            int delay = fullscreen ? 0 : D.HideDelayMs;
            if ((DateTime.Now - _wantHideSince).TotalMilliseconds >= delay) Slide(false);
        }
    }

    /// <summary>Does the foreground window cover the Dock's area?</summary>
    private bool Overlapped(double shownTop)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return false;
        var cls = Native.ClassOf(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == Environment.ProcessId) return false;
        if (Native.IsIconic(fg) || !Native.IsWindowVisible(fg)) return false;
        if (Native.IsZoomed(fg)) return true;
        if (Native.DwmGetWindowAttribute(fg, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) != 0)
            Native.GetWindowRect(fg, out r);
        var dpi = VisualTreeHelper.GetDpi(this);
        double l = r.Left / dpi.DpiScaleX, t = r.Top / dpi.DpiScaleY, rr = r.Right / dpi.DpiScaleX, b = r.Bottom / dpi.DpiScaleY;
        return rr > Left && l < Left + ActualWidth && b > shownTop && t < ScreenH;
    }

    private void Slide(bool show)
    { Aether.Core.Diag.Hit("Dock.Slide");
        _shown = show;
        // Reveal with a spring: overshoot a little above its spot, then settle back.
        IEasingFunction ease = show
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 }
            : new CubicEase { EasingMode = EasingMode.EaseIn };
        var anim = new DoubleAnimation(show ? ShownTop : HiddenTop, TimeSpan.FromMilliseconds(show ? 460 : 170)) { EasingFunction = ease };
        if (show) Timeline.SetDesiredFrameRate(anim, 60);
        BeginAnimation(TopProperty, anim);
    }

    public static Rect? ShownBounds => _instance is null ? null : new Rect(_instance.Left, _instance.ShownTop, _instance.ActualWidth, _instance.ActualHeight);
}
