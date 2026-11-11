using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Aether.Apps;
using Aether.Core;
using Aether.Interop;
using Aether.Shell;

namespace Aether.Dock;

/// <summary>Launchpad-style app drawer that rises above the Dock.</summary>
public partial class DrawerWindow : Window
{
    private const string All = "全部";
    private string _group = All;
    private bool _closing;
    private readonly List<(AppEntry App, Button Tile)> _tiles = new();

    public DrawerWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => { if (!_menuOpen) Dismiss(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; } };
        AppCatalog.Changed += OnCatalog;
        Closed += (_, _) => AppCatalog.Changed -= OnCatalog;
        BuildGroups();
        BuildTiles();
        // Mouse wheel scrolls the group bar sideways.
        GroupBar.PreviewMouseWheel += (_, e) =>
        {
            if (GroupBar.Parent is ScrollViewer sv) { sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 2.0); e.Handled = true; }
        };
    }

    private bool _menuOpen;
    private bool _dragged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW, Native.WS_EX_APPWINDOW);
        WindowEffects.SetDarkMode(hwnd, true);
        WindowEffects.MakeTransparent(this);
        WindowEffects.SetCorners(hwnd, Native.DWMWCP_ROUND);
        ApplyLook();
    }

    /// <summary>Tint (0–100) and blur from Dock settings; re-applied every time it opens.</summary>
    private void ApplyLook()
    {
        var d = SettingsStore.Current.Dock;
        byte alpha = (byte)Math.Round(Math.Clamp(d.DrawerOpacity, 0, 100) * 2.55);
        Root.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x10, 0x13, 0x20));
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) WindowEffects.SetBackdrop(hwnd, d.DrawerBlur ? Native.DWMSBT_TRANSIENTWINDOW : Native.DWMSBT_NONE);
    }

    private void OnCatalog() => Dispatcher.BeginInvoke(() => { BuildGroups(); BuildTiles(); }, DispatcherPriority.Background);

    // ---------- open / close ----------
    public void ShowAnimated()
    {
        _closing = false;
        var dock = DockWindow.ShownBounds;
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        Height = Math.Min(560, sh - 160);
        Left = Math.Round((sw - Width) / 2);
        Top = Math.Round((dock?.Top ?? sh - 80) - Height - 14);
        Search.Text = "";
        ApplyLook();
        if (_group != All && !SettingsStore.Current.Groups.Contains(_group) && _group != AppCatalog.Ungrouped) _group = All;
        ApplyFilter();
        Opacity = 0;
        Show();
        Activate();
        Search.Focus();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    public void Dismiss()
    {
        if (_closing || !IsVisible) return;
        _closing = true;
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        a.Completed += (_, _) => { if (_closing) Hide(); };
        BeginAnimation(OpacityProperty, a);
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 16, TimeSpan.FromMilliseconds(120)));
    }

    public bool IsOpenNow => IsVisible && !_closing;

    // ---------- groups ----------
    private IEnumerable<string> GroupNames()
    {
        yield return All;
        var used = AppCatalog.Apps.Select(a => string.IsNullOrEmpty(a.Group) ? AppCatalog.Ungrouped : a.Group).ToHashSet();
        foreach (var g in SettingsStore.Current.Groups.Where(used.Contains)) yield return g;
        if (used.Contains(AppCatalog.Ungrouped)) yield return AppCatalog.Ungrouped;
    }

    private void BuildGroups()
    {
        GroupBar.Children.Clear();
        foreach (var g in GroupNames())
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = g, GroupName = "drawergroup", IsChecked = g == _group };
            var name = g;
            rb.Checked += (_, _) => { _group = name; ApplyFilter(); Scroller.ScrollToTop(); };
            if (name != All)
            {
                // Drop a tile on a group chip to move the app there.
                rb.AllowDrop = true;
                rb.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DockWindow.AppDragFormat) ? DragDropEffects.Move : DragDropEffects.None; rb.Opacity = 0.7; e.Handled = true; };
                rb.DragLeave += (_, _) => rb.Opacity = 1;
                rb.Drop += (_, e) =>
                {
                    rb.Opacity = 1;
                    if (e.Data.GetData(DockWindow.AppDragFormat) is string path && AppCatalog.Apps.FirstOrDefault(a => a.Path == path) is { } app)
                    {
                        app.Group = name == AppCatalog.Ungrouped ? "" : name;
                        AppCatalog.NotifyChanged();
                    }
                    e.Handled = true;
                };
            }
            if (name != All && name != AppCatalog.Ungrouped)
                rb.MouseRightButtonUp += (_, e) => { GroupMenu(name, rb); e.Handled = true; };
            GroupBar.Children.Add(rb);
        }
    }

    private void GroupMenu(string group, FrameworkElement chip)
    {
        var m = new ContextMenu { PlacementTarget = chip };
        m.Opened += (_, _) => _menuOpen = true;
        m.Closed += (_, _) => { _menuOpen = false; Activate(); };
        var rename = new MenuItem { Header = "重命名分组…" };
        rename.Click += (_, _) =>
        {
            var name = Prompt("重命名分组", "新的分组名称（和已有分组同名会合并）", group);
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == group) return;
            AppCatalog.RenameGroup(group, name.Trim());
            if (_group == group) _group = name.Trim();
        };
        var delete = new MenuItem { Header = "删除分组（应用移到未分组）" };
        delete.Click += (_, _) =>
        {
            AppCatalog.DeleteGroup(group);
            if (_group == group) _group = All;
        };
        m.Items.Add(rename);
        m.Items.Add(delete);
        m.IsOpen = true;
    }

    // ---------- tiles ----------
    private void BuildTiles()
    {
        AppGrid.Children.Clear();
        _tiles.Clear();
        // zh-CN collation: Latin A-Z first, then Chinese by pinyin.
        var collation = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), true);
        foreach (var app in AppCatalog.Apps.OrderBy(a => Pinyin.SortKey(a.Name), StringComparer.Ordinal).ThenBy(a => a.Name, collation))
        {
            var tile = MakeTile(app);
            _tiles.Add((app, tile));
            AppGrid.Children.Add(tile);
        }
        CountText.Text = $"{AppCatalog.Apps.Count} 个应用";
        ApplyFilter();
        // Load icons after the window is up, a few per frame, so opening never stutters.
        Dispatcher.BeginInvoke(LoadIconsSlowly, DispatcherPriority.Background);
    }

    private Button MakeTile(AppEntry app)
    {
        var img = new Image { Width = 46, Height = 46, Margin = new Thickness(0, 14, 0, 8) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var text = new TextBlock
        {
            Text = app.Name, FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32, Margin = new Thickness(6, 0, 6, 0),
        };
        var stack = new StackPanel();
        stack.Children.Add(img);
        stack.Children.Add(text);
        var b = new Button { Style = (Style)FindResource("Tile"), Content = stack, ToolTip = app.Name, Tag = img };
        b.Click += (_, _) => { if (!_dragged) { AppCatalog.Launch(app); Dismiss(); } _dragged = false; };
        b.MouseRightButtonUp += (_, e) => { OpenMenu(app, b); e.Handled = true; };
        // Drag a tile onto the Dock to pin it, or onto a group chip to move it.
        Point? down = null;
        b.PreviewMouseLeftButtonDown += (_, e) => { down = e.GetPosition(this); _dragged = false; };
        b.PreviewMouseMove += (_, e) =>
        {
            if (down is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - d.X) < 8 && Math.Abs(p.Y - d.Y) < 8) return;
            down = null;
            _dragged = true;
            var data = new DataObject(DockWindow.AppDragFormat, app.Path);
            _menuOpen = true; // keep the drawer open while dragging
            try { DragDrop.DoDragDrop(b, data, DragDropEffects.Link | DragDropEffects.Move); }
            finally { _menuOpen = false; }
        };
        b.PreviewMouseLeftButtonUp += (_, _) => down = null;
        return b;
    }

    private void LoadIconsSlowly()
    {
        var pending = _tiles.Where(t => t.Tile.Tag is Image { Source: null }).Take(8).ToList();
        foreach (var (app, tile) in pending)
            ((Image)tile.Tag).Source = ShellIcons.Get(app.Path, 96);
        if (pending.Count > 0) Dispatcher.BeginInvoke(LoadIconsSlowly, DispatcherPriority.Background);
    }

    private void ApplyFilter()
    {
        var q = Search.Text.Trim();
        foreach (var (app, tile) in _tiles)
        {
            var g = string.IsNullOrEmpty(app.Group) ? AppCatalog.Ungrouped : app.Group;
            bool groupOk = q.Length > 0 || _group == All || g == _group;
            bool textOk = q.Length == 0 || app.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                          || System.IO.Path.GetFileNameWithoutExtension(app.Target ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                          || (q.All(char.IsAsciiLetter) && Pinyin.Initials(app.Name).Contains(q, StringComparison.OrdinalIgnoreCase));
            tile.Visibility = groupOk && textOk ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var first = _tiles.FirstOrDefault(t => t.Tile.Visibility == Visibility.Visible);
            if (first.App is not null) { AppCatalog.Launch(first.App); Dismiss(); }
            e.Handled = true;
        }
    }

    // ---------- context menu ----------
    private void OpenMenu(AppEntry app, Button tile)
    {
        var m = new ContextMenu { PlacementTarget = tile };
        m.Opened += (_, _) => _menuOpen = true;
        m.Closed += (_, _) => { _menuOpen = false; Activate(); };

        MenuItem Item(string h, Action a) { var i = new MenuItem { Header = h }; i.Click += (_, _) => a(); return i; }

        m.Items.Add(Item("打开", () => { AppCatalog.Launch(app); Dismiss(); }));
        m.Items.Add(app.DockOrder is null
            ? Item("固定到 Dock", () => AppCatalog.Pin(app))
            : Item("从 Dock 取消固定", () => AppCatalog.Unpin(app)));

        var move = new MenuItem { Header = "移动到分组" };
        foreach (var g in SettingsStore.Current.Groups.Append(AppCatalog.Ungrouped))
        {
            var target = g;
            var gi = new MenuItem { Header = g, IsCheckable = true, IsChecked = (string.IsNullOrEmpty(app.Group) ? AppCatalog.Ungrouped : app.Group) == g };
            gi.Click += (_, _) => { app.Group = target; AppCatalog.NotifyChanged(); };
            move.Items.Add(gi);
        }
        move.Items.Add(new Separator());
        move.Items.Add(Item("新建分组…", () =>
        {
            var name = Prompt("新建分组", "分组名称");
            if (string.IsNullOrWhiteSpace(name)) return;
            AppCatalog.EnsureGroup(name.Trim());
            app.Group = name.Trim();
            AppCatalog.NotifyChanged();
        }));
        m.Items.Add(move);

        m.Items.Add(Item("重命名…", () =>
        {
            var name = Prompt("重命名", "显示名称", app.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            app.Name = name.Trim();
            AppCatalog.NotifyChanged();
        }));
        m.Items.Add(Item("打开文件位置", () => { AppCatalog.RevealInExplorer(app); Dismiss(); }));
        m.Items.Add(new Separator());
        m.Items.Add(Item("从抽屉移除", () => AppCatalog.Remove(app)));
        m.IsOpen = true;
    }

    /// <summary>Tiny inline input dialog in the app's style.</summary>
    private string? Prompt(string title, string label, string initial = "")
    {
        _menuOpen = true;
        try
        {
            var box = new TextBox { Style = (Style)FindResource("HexBox"), FontFamily = (FontFamily)FindResource("UiFont"), Text = initial, Width = 260 };
            var ok = new Button { Style = (Style)FindResource("AccentButton"), Content = "确定", IsDefault = true, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = label, Foreground = (Brush)FindResource("SubTextBrush"), Margin = new Thickness(0, 0, 0, 8) });
            panel.Children.Add(box);
            panel.Children.Add(ok);
            var dlg = new Window
            {
                Title = title, Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x20, 0x2C)), Foreground = Brushes.White, Content = panel,
                FontFamily = (FontFamily)FindResource("UiFont"),
            };
            dlg.SourceInitialized += (_, _) => WindowEffects.SetDarkMode(new WindowInteropHelper(dlg).Handle, true);
            ok.Click += (_, _) => dlg.DialogResult = true;
            dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            return dlg.ShowDialog() == true ? box.Text : null;
        }
        finally
        {
            _menuOpen = false;
        }
    }

    // ---------- footer ----------
    private void OnScan(object sender, RoutedEventArgs e)
    {
        int n = AppCatalog.ScanStartMenu();
        if (n > 0) AppCatalog.NotifyChanged();
        CountText.Text = n > 0 ? $"新增 {n} 个应用" : "没有发现新应用";
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        _menuOpen = true;
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要添加的程序或快捷方式",
                Filter = "程序和快捷方式|*.exe;*.lnk;*.url|所有文件|*.*",
                Multiselect = true,
            };
            if (dlg.ShowDialog(this) == true)
                foreach (var f in dlg.FileNames) AppCatalog.AddFile(f);
        }
        finally
        {
            _menuOpen = false;
            Activate();
        }
    }
}

/// <summary>Single drawer instance shared by the Dock and hotkeys.</summary>
public static class DrawerHost
{
    private static DrawerWindow? _drawer;
    private static DispatcherTimer? _release;

    public static bool IsOpen => _drawer?.IsOpenNow == true;

    public static void Toggle()
    {
        _release?.Stop();
        if (_drawer is null)
        {
            _drawer = new DrawerWindow();
            // Once it has been closed for a while, free the whole window (icons stay cached).
            _drawer.IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue) return;
                _release ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
                _release.Tick -= OnRelease;
                _release.Tick += OnRelease;
                _release.Start();
            };
        }
        if (_drawer.IsOpenNow) _drawer.Dismiss();
        else _drawer.ShowAnimated();
    }

    private static void OnRelease(object? sender, EventArgs e)
    {
        _release?.Stop();
        if (_drawer is { IsVisible: false })
        {
            _drawer.Close();
            _drawer = null;
            Memory.TrimSoon();
        }
    }

    public static void Close()
    {
        _drawer?.Close();
        _drawer = null;
    }
}
