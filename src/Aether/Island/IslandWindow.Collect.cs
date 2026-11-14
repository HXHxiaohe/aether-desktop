using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Aether.Apps;
using Aether.Interop;

namespace Aether.Island;

/// <summary>窗口收纳: drag a window by its title bar onto the island; it's swallowed (genie) and listed on the shelf.</summary>
public sealed partial class IslandWindow
{
    private StackPanel _winList = null!;
    private FrameworkElement _winView = null!;
    private Button _shelfTabWin = null!;
    private StackPanel _cWin = null!;
    private TextBlock _cWinCount = null!;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out WindowCollector.RECT r);

    private IntPtr _winDrag;
    private WindowCollector.RECT _winDragStart;
    private bool _winHot;
    private DispatcherTimer? _winDragTimer;

    /// <summary>Third shelf tab + compact badge; called from the shelf page builder.</summary>
    private void BuildCollect(StackPanel seg, Grid body)
    {
        _shelfTabWin = Pill("窗口", false, () => SetShelfTab(2));
        seg.Children.Add(_shelfTabWin);
        _winList = new StackPanel();
        _winView = new ScrollViewer { Content = _winList, MaxHeight = 236, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        body.Children.Add(_winView);

        // Debug: AETHER_COLLECT_DEMO=<window title> collects that window 4 s after start, releases it 4 s later.
        if (Environment.GetEnvironmentVariable("AETHER_COLLECT_DEMO") is { Length: > 0 } demo)
        {
            Loaded += (_, _) =>
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t.Tick += async (_, _) =>
                {
                    t.Stop();
                    var h = FindWindow(null, demo);
                    Core.Log.Info($"[collect-demo] '{demo}' -> {h}");
                    if (h == IntPtr.Zero) return;
                    GetWindowRect(h, out var r);
                    await CollectAsync(h, r);
                    await Task.Delay(4000);
                    if (WindowCollector.Items.FirstOrDefault() is { } first) await ReleaseWindowAsync(first);
                };
                t.Start();
            };
        }
        WindowCollector.Changed += OnCollectedChanged;
        WindowCollector.DragStarted += OnWindowDragStart;
        WindowCollector.DragEnded += OnWindowDragEnd;
        Closed += (_, _) =>
        {
            WindowCollector.Changed -= OnCollectedChanged;
            WindowCollector.DragStarted -= OnWindowDragStart;
            WindowCollector.DragEnded -= OnWindowDragEnd;
        };
    }

    /// <summary>"▣ 2" on the collapsed island while windows are stored.</summary>
    private void BuildCollectBadge(StackPanel cRight)
    {
        _cWin = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var g = Glyph("", 11.5);
        g.Opacity = 0.85;
        _cWin.Children.Add(g);
        _cWinCount = Text("", 11.5, FontWeights.SemiBold);
        _cWinCount.Margin = new Thickness(4, 0, 0, 0);
        _cWin.Children.Add(_cWinCount);
        _cWin.ToolTip = "已收纳的窗口";
        cRight.Children.Insert(Math.Max(0, cRight.Children.Count - 1), _cWin);
    }

    private void OnCollectedChanged() => Dispatcher.BeginInvoke(() =>
    {
        int n = WindowCollector.Items.Count;
        _cWin.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        _cWinCount.Text = n.ToString();
        if (_page == ShelfPage) RebuildShelf();
        UpdateCompact();
    });

    private void RebuildWindows()
    {
        _winList.Children.Clear();
        WindowCollector.Prune();
        foreach (var w in WindowCollector.Items) _winList.Children.Add(CollectedRow(w));
        if (WindowCollector.Items.Count == 0)
            _winList.Children.Add(Hint("按住窗口标题栏拖到灵动岛上，松手就收纳进来；点一下放回原处"));
    }

    private FrameworkElement CollectedRow(CollectedWindow w)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand, Background = Brushes.Transparent, Tag = "nodrag", ToolTip = "放回原处" };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var face = new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)) };
        Grid.SetColumnSpan(face, 2);
        row.Children.Add(face);

        var thumb = new Border { Width = 72, Height = 44, CornerRadius = new CornerRadius(6), Margin = new Thickness(6), ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)) };
        if (w.Snapshot is not null) thumb.Child = new Image { Source = w.Snapshot, Stretch = Stretch.UniformToFill };
        row.Children.Add(thumb);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        if (w.ExePath is not null && ShellIcons.Get(w.ExePath, 32) is { } icon)
            top.Children.Add(new Image { Source = icon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0) });
        var name = Text(w.ExePath is null ? "窗口" : System.IO.Path.GetFileNameWithoutExtension(w.ExePath), 10.5, FontWeights.Normal, 0.5);
        top.Children.Add(name);
        info.Children.Add(top);
        var title = Text(w.Title.Length > 0 ? w.Title : "(无标题)", 12, FontWeights.SemiBold, 0.95);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(title);
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        row.MouseEnter += (_, _) => face.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        row.MouseLeave += (_, _) => face.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; _ = ReleaseWindowAsync(w); };
        return row;
    }

    // ------------------------------------------------------------------ geometry
    private double DipScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Where windows go in / come out: just under the island's centre, screen DIPs.</summary>
    private Point Mouth()
    {
        double bottom = Top + _surface.Margin.Top + Math.Max(_surface.ActualHeight, 24);
        return new Point(CenterX, bottom - 6);
    }

    private Rect ToDip(WindowCollector.RECT r) { double s = DipScale; return new Rect(r.L / s, r.T / s, r.W / s, r.H / s); }

    private bool CursorOverIsland()
    {
        Native.GetCursorPos(out var cur);
        double s = DipScale, x = cur.X / s, y = cur.Y / s;
        double half = Math.Max(_surface.ActualWidth / 2 + 70, 150);
        return Math.Abs(x - CenterX) < half && y < Top + _surface.Margin.Top + _surface.ActualHeight + 50;
    }

    // ------------------------------------------------------------------ drag a window in
    private void OnWindowDragStart(IntPtr hwnd, WindowCollector.RECT start)
    {
        if (_fullscreen || !S.WindowCollect) return;
        _winDrag = hwnd;
        _winDragStart = start;
        _winHot = false;
        _winDragTimer ??= new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _winDragTimer.Tick -= OnWindowDragTick;
        _winDragTimer.Tick += OnWindowDragTick;
        _winDragTimer.Start();
    }

    private void OnWindowDragTick(object? sender, EventArgs e)
    {
        if (_winDrag == IntPtr.Zero) { _winDragTimer!.Stop(); return; }
        bool hot = CursorOverIsland();
        if (hot)
        {
            // "松手收纳" while the window hovers over the island
            if (!_winHot)
            {
                _toastNote = null; _toastMail = null;
                _toastArt.Source = ExeOf(_winDrag) is { } exe ? ShellIcons.Get(exe, 96) : null;
                _toastGlyph.Text = _toastArt.Source is null ? "" : "";
                _toastBarsPanel.Visibility = Visibility.Collapsed;
                StyleToastTitle(false);
                _toastTitle.Text = "松手收纳窗口";
                var t = WindowCollector.TitleOf(_winDrag);
                _toastSub.Text = t.Length > 0 ? t : "这个窗口";
                _msgToastColor = IslandGlow.Vivid(Aether.Theme.ThemeService.Accent);
                GlowShot("bloom", _msgToastColor);
            }
            _toastUntil = _msgToastUntil = DateTime.Now.AddMilliseconds(400);
            Apply();
        }
        _winHot = hot;
    }

    private static string? ExeOf(IntPtr hwnd)
    {
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.MainModule?.FileName;
        }
        catch { return null; }
    }

    private async void OnWindowDragEnd(IntPtr hwnd)
    {
        if (hwnd != _winDrag) return;
        _winDrag = IntPtr.Zero;
        _winDragTimer?.Stop();
        bool drop = _winHot && CursorOverIsland();
        _winHot = false;
        _toastUntil = DateTime.MinValue;
        if (!drop) { Apply(); return; }
        await CollectAsync(hwnd, _winDragStart);
    }

    private async Task CollectAsync(IntPtr hwnd, WindowCollector.RECT original)
    {
        var snap = WindowCollector.Capture(hwnd);
        var now = ToDip(WindowCollector.BoundsOf(hwnd));
        var item = await WindowCollector.CollectAsync(hwnd, original, snap);
        Core.Log.Info($"[collect] {WindowCollector.TitleOf(hwnd)}: snapshot={(snap is null ? "none" : $"{snap.PixelWidth}x{snap.PixelHeight}")} hidden={item is not null}");
        if (item is null)
        {
            ShowCollectToast("这个窗口不能收纳", "它以管理员身份运行，Windows 不允许隐藏它");
            return;
        }
        if (snap is not null && !S.ReduceMotion) await GenieOverlay.Play(snap, now, Mouth(), reverse: false);
        GlowShot("converge", IslandGlow.Vivid(Aether.Theme.ThemeService.Accent));
        ShowCollectToast($"已收纳 {WindowCollector.Items.Count} 个窗口", item.Title.Length > 0 ? item.Title : "点灵动岛 › 置物架 › 窗口 放回");
    }

    private async Task ReleaseWindowAsync(CollectedWindow w)
    {
        _hover = false; Apply(); // let the island fold away while the window pours out
        var to = ToDip(new WindowCollector.RECT { L = w.X, T = w.Y, R = w.X + w.W, B = w.Y + w.H });
        GlowShot("release", IslandGlow.Vivid(Aether.Theme.ThemeService.Accent));
        if (w.Snapshot is not null && w.W > 0 && !S.ReduceMotion) await GenieOverlay.Play(w.Snapshot, to, Mouth(), reverse: true, ms: 560);
        WindowCollector.Release(w);
    }

    private void ShowCollectToast(string title, string sub)
    {
        _toastNote = null; _toastMail = null;
        _toastArt.Source = null;
        _toastGlyph.Text = "";
        _toastBarsPanel.Visibility = Visibility.Collapsed;
        StyleToastTitle(false);
        _toastTitle.Text = title;
        _toastSub.Text = sub;
        _toastUntil = DateTime.Now.AddSeconds(2.6);
        Apply();
    }
}
