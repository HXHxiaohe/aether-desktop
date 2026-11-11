using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Aether.Apps;

namespace Aether.Island;

/// <summary>置物架 page: files dropped on the island (with a "swallow" animation) + clipboard history.</summary>
public sealed partial class IslandWindow
{
    private const int ShelfPage = 5;

    private StackPanel _shelfFiles = null!, _clipList = null!;
    private Button _shelfTabFiles = null!, _shelfTabClip = null!;
    private FrameworkElement _shelfFilesView = null!, _clipView = null!;
    private TextBlock _shelfHint = null!;
    private FrameworkElement _shelfHintBox = null!;
    private Grid _dropZone = null!;
    private Canvas _fx = null!;
    private bool _dragHot;
    private DateTime _dragLastOver;
    private int _pageBeforeDrag = -1;
    private string? _copiedId;
    private readonly HashSet<string> _bornPaths = new(StringComparer.OrdinalIgnoreCase);

    private FrameworkElement BuildShelfPage(Grid root)
    {
        var page = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        _shelfTabFiles = Pill("文件", false, () => SetShelfTab(0));
        _shelfTabClip = Pill("剪贴板", false, () => SetShelfTab(1));
        seg.Children.Add(_shelfTabFiles);
        seg.Children.Add(_shelfTabClip);
        head.Children.Add(seg);
        var clear = Pill("清空", false, () =>
        {
            if (S.ShelfTab == 0) ShelfService.Clear(); else ClipboardService.ClearRecent();
        });
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(clear);
        page.Children.Add(head);

        var body = new Grid();
        // files
        var filesView = new Grid();
        _shelfFiles = new StackPanel();
        filesView.Children.Add(new ScrollViewer { Content = _shelfFiles, MaxHeight = 236, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var hint = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 20) };
        hint.Children.Add(new TextBlock { Text = "", FontFamily = Icons, FontSize = 26, Foreground = Brushes.White, Opacity = 0.3, HorizontalAlignment = HorizontalAlignment.Center });
        _shelfHint = Text("把文件拖到灵动岛上，先放在这里", 12, FontWeights.Normal, 0.45);
        _shelfHint.HorizontalAlignment = HorizontalAlignment.Center;
        _shelfHint.Margin = new Thickness(0, 8, 0, 0);
        hint.Children.Add(_shelfHint);
        _shelfHintBox = hint;
        filesView.Children.Add(hint);

        // shown while files are dragged over the island
        _dropZone = new Grid { MinHeight = 104, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        var zoneBg = new Border { CornerRadius = new CornerRadius(14) };
        zoneBg.SetResourceReference(Border.BackgroundProperty, "AccentFaintBrush");
        _dropZone.Children.Add(zoneBg);
        var dash = new Rectangle { RadiusX = 14, RadiusY = 14, StrokeThickness = 1.6, StrokeDashArray = new DoubleCollection { 5, 4 } };
        dash.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        _dropZone.Children.Add(dash);
        var zoneText = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var arrow = new TextBlock { Text = "", FontFamily = Icons, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, RenderTransform = new TranslateTransform() };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var bob = new DoubleAnimation(-3, 3, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        ((TranslateTransform)arrow.RenderTransform).BeginAnimation(TranslateTransform.YProperty, bob);
        zoneText.Children.Add(arrow);
        var zt = Text("松手放进置物架", 12.5, FontWeights.SemiBold, 0.9);
        zt.Margin = new Thickness(0, 6, 0, 0);
        zoneText.Children.Add(zt);
        _dropZone.Children.Add(zoneText);
        filesView.Children.Add(_dropZone);
        _shelfFilesView = filesView;
        body.Children.Add(filesView);
        // clipboard
        _clipList = new StackPanel();
        _clipView = new ScrollViewer { Content = _clipList, MaxHeight = 236, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(_clipView);
        page.Children.Add(body);

        // effects layer for the swallow animation (above everything, never hit-tested)
        _fx = new Canvas { IsHitTestVisible = false };
        root.Children.Add(_fx);

        _surface.AllowDrop = true;
        _surface.DragEnter += OnShelfDragOver;
        _surface.DragOver += OnShelfDragOver;
        _surface.Drop += OnShelfDrop;

        ShelfService.Changed += OnShelfChanged;
        ClipboardService.Changed += OnClipChanged;
        ClipboardService.ScreenshotTaken += OnScreenshot;
        Closed += (_, _) => { ShelfService.Changed -= OnShelfChanged; ClipboardService.Changed -= OnClipChanged; ClipboardService.ScreenshotTaken -= OnScreenshot; };
        SetShelfTab(S.ShelfTab, false);
        return page;
    }

    private DateTime _shotToastUntil;
    /// <summary>The current toast is the screenshot one (other toasts set a different end time).</summary>
    private bool ToastIsShot => _toastUntil == _shotToastUntil && _toastUntil > DateTime.Now;

    private void OnScreenshot(string path) => Dispatcher.BeginInvoke(() =>
    {
        if (_mode == Mode.Expanded) { if (_page == ShelfPage) { _bornPaths.Add(path); RebuildShelf(); } return; }
        _bornPaths.Add(path);
        _toastGlyph.Text = "";
        _toastNote = null;
        _toastMail = null;
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.DecodePixelWidth = 120; bmp.UriSource = new Uri(path); bmp.EndInit();
            _toastArt.Source = bmp;
        }
        catch { _toastArt.Source = null; }
        _toastBarsPanel.Visibility = Visibility.Collapsed;
        StyleToastTitle(false);
        _toastTitle.Text = "截图已放进置物架";
        _toastSub.Text = "移上来拖进微信或文件夹";
        _toastUntil = _shotToastUntil = DateTime.Now.AddSeconds(4);
        Apply();
        GlowShot("converge", IslandGlow.Vivid(Aether.Theme.ThemeService.Accent));
    });

    /// <summary>Opening the island straight onto one of the newer pages: fill it now.</summary>
    private void RefreshExtraPage()
    {
        if (_page == 4) RebuildNotifications();
        else if (_page == ShelfPage) { ShelfService.Prune(); RebuildShelf(); }
        else if (_page == QuotaPage) { QuotaService.RefreshSoon(); RebuildQuota(); }
        else if (_page == MailPage) { MailService.CheckSoon(); RebuildMail(); MailService.MarkSeen(); }
    }

    private void SetShelfTab(int tab, bool rebuild = true)
    {
        S.ShelfTab = tab;
        SetPillOn(_shelfTabFiles, tab == 0);
        SetPillOn(_shelfTabClip, tab == 1);
        _shelfFilesView.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        _clipView.Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (rebuild) { RebuildShelf(); Core.SettingsStore.Touch(); }
    }

    private static void SetPillOn(Button b, bool on)
    {
        if (b.Content is not Border inner || inner.Child is not TextBlock t) return;
        inner.Background = on ? new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        t.Foreground = on ? new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x16)) : Brushes.White;
    }

    private void OnShelfChanged() => Dispatcher.BeginInvoke(() => { if (_page == ShelfPage) RebuildShelf(); });
    private void OnClipChanged() => Dispatcher.BeginInvoke(() => { if (_page == ShelfPage && _mode == Mode.Expanded) RebuildShelf(); });

    private void RebuildShelf()
    {
        ((TextBlock)((Border)_shelfTabFiles.Content).Child).Text = ShelfService.Items.Count > 0 ? $"文件 {ShelfService.Items.Count}" : "文件";
        int clips = ClipboardService.Items.Count;
        ((TextBlock)((Border)_shelfTabClip.Content).Child).Text = clips > 0 ? $"剪贴板 {clips}" : "剪贴板";
        if (S.ShelfTab == 0) RebuildFiles(); else RebuildClips();
        _layoutDirty = true;
    }

    // ------------------------------------------------------------------ files
    private void RebuildFiles()
    {
        _shelfFiles.Children.Clear();
        var wrap = new WrapPanel();
        foreach (var item in ShelfService.Items) wrap.Children.Add(FileTile(item));
        _shelfFiles.Children.Add(wrap);
        _shelfHintBox.Visibility = ShelfService.Items.Count == 0 && !_dragHot ? Visibility.Visible : Visibility.Collapsed;
        _bornPaths.Clear();
    }

    private FrameworkElement FileTile(ShelfItem item)
    {
        bool exists = item.Exists;
        var icon = new Image { Width = 36, Height = 36, Source = exists ? ShellIcons.Get(item.Path, 48) : null, Opacity = exists ? 1 : 0.35 };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var name = new TextBlock
        {
            Text = item.Name, FontSize = 10.5, Foreground = Brushes.White, FontFamily = Ui, Opacity = exists ? 0.85 : 0.4,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 28,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
        };
        var stack = new StackPanel { Width = 72 };
        stack.Children.Add(icon);
        stack.Children.Add(name);
        var face = new Border { Padding = new Thickness(4, 8, 4, 6), CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Child = stack };

        // hover actions: reveal in folder / remove
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0), Visibility = Visibility.Hidden };
        actions.Children.Add(MiniButton("", "在文件夹中显示", () => ShelfService.Reveal(item)));
        actions.Children.Add(MiniButton("", "从置物架移除（不删除文件）", () => ShelfService.Remove(item)));

        var cell = new Grid { Margin = new Thickness(0, 0, 4, 4), Background = Brushes.Transparent, ToolTip = item.Path, Cursor = Cursors.Hand, Tag = "nodrag" };
        cell.Children.Add(face);
        cell.Children.Add(actions);
        cell.MouseEnter += (_, _) => { face.Background = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)); actions.Visibility = Visibility.Visible; };
        cell.MouseLeave += (_, _) => { face.Background = Brushes.Transparent; actions.Visibility = Visibility.Hidden; };

        // click = open; drag = hand the file to another app
        Point down = default; bool pressed = false;
        cell.PreviewMouseLeftButtonDown += (_, e) => { down = e.GetPosition(cell); pressed = true; };
        cell.PreviewMouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed || !exists) return;
            var p = e.GetPosition(cell);
            if (Math.Abs(p.X - down.X) < 5 && Math.Abs(p.Y - down.Y) < 5) return;
            pressed = false;
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            GlowShot("release", IslandGlow.Vivid(Aether.Theme.ThemeService.Accent));
            try { DragDrop.DoDragDrop(cell, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
            catch (Exception ex) { Core.Log.Info("[shelf] drag " + ex.Message); }
            ShelfService.Prune(); // moved elsewhere → forget it
        };
        cell.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            if (e.OriginalSource is DependencyObject d && FindButton(d) is not null) return;
            ShelfService.Open(item);
            e.Handled = true;
        };

        if (_bornPaths.Contains(item.Path))
        {
            var st = new ScaleTransform(0.2, 0.2);
            cell.RenderTransformOrigin = new Point(0.5, 0.5);
            cell.RenderTransform = st;
            var spring = new DoubleAnimation(1, TimeSpan.FromMilliseconds(520)) { BeginTime = TimeSpan.FromMilliseconds(280), EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 5 } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, spring);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, spring);
        }
        return cell;
    }

    private static Button? FindButton(DependencyObject d)
    {
        for (var cur = d; cur is not null; cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is Button b) return b;
        return null;
    }

    private static Button MiniButton(string glyph, string tip, Action click)
    {
        var inner = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(2, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x30, 0x30, 0x34)),
            Child = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 9.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var b = new Button { Content = inner, Template = PlainTemplate(), Focusable = false, Cursor = Cursors.Hand, ToolTip = tip };
        b.Click += (_, e) => { click(); e.Handled = true; };
        b.MouseEnter += (_, _) => inner.Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x50, 0x50, 0x56));
        b.MouseLeave += (_, _) => inner.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x30, 0x30, 0x34));
        return b;
    }

    // ------------------------------------------------------------------ drag in
    private void OnShelfDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!files) return;
        _dragLastOver = DateTime.Now;
        if (_dragHot) return;
        _dragHot = true;
        _pageBeforeDrag = _page;
        if (S.ShelfTab != 0) SetShelfTab(0);
        if (_page != ShelfPage) ShowPage(ShelfPage, 1);
        SetDropZone(true);
        Apply();
    }

    private void SetDropZone(bool on)
    {
        _dropZone.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _dropZone.BeginAnimation(OpacityProperty, on ? new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) : null);
        _shelfFiles.Opacity = on ? 0.25 : 1;
        _shelfHintBox.Visibility = !on && ShelfService.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _layoutDirty = true;
    }

    /// <summary>Called from Poll: the drag left without dropping → go back to where we were.</summary>
    private void CheckDragLeft()
    {
        if (!_dragHot || (DateTime.Now - _dragLastOver).TotalMilliseconds < 450) return;
        _dragHot = false;
        SetDropZone(false);
        if (_pageBeforeDrag >= 0 && _pageBeforeDrag != _page) ShowPage(_pageBeforeDrag, -1);
        _pageBeforeDrag = -1;
    }

    private void OnShelfDrop(object sender, DragEventArgs e)
    {
        _dragHot = false;
        _pageBeforeDrag = -1;
        SetDropZone(false);
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        var at = e.GetPosition(_fx);
        var added = ShelfService.Add(paths);
        foreach (var a in added) _bornPaths.Add(a.Path);
        _hover = true; _leftSince = DateTime.Now; // keep it open to show where they went
        if (_page != ShelfPage) ShowPage(ShelfPage, 1);
        Swallow(paths.Take(4).ToList(), at);
        // once the icons are in: light runs down both sides and meets at the bottom
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(360 + Math.Min(3, paths.Length - 1) * 70) };
        delay.Tick += (_, _) => { delay.Stop(); GlowShot("converge", IslandGlow.Vivid(Aether.Theme.ThemeService.Accent)); };
        delay.Start();
        RebuildShelf();
        Apply();
    }

    /// <summary>The "吸入" effect: file icons shrink and get sucked into the island, which gulps.</summary>
    private void Swallow(List<string> paths, Point from)
    {
        // target: top-left of the files grid (where new items land)
        Point to;
        try { to = _shelfFiles.TranslatePoint(new Point(36, 22), _fx); }
        catch { to = new Point(from.X, 40); }
        if (double.IsNaN(to.X) || to.Y <= 0) to = new Point(Width / 2 - 200, 80);

        for (int i = 0; i < paths.Count; i++)
        {
            var img = new Image { Width = 48, Height = 48, Source = ShellIcons.Get(paths[i], 96) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var st = new ScaleTransform(1.25, 1.25);
            var rt = new RotateTransform(0);
            img.RenderTransformOrigin = new Point(0.5, 0.5);
            img.RenderTransform = new TransformGroup { Children = { st, rt } };
            double sx = from.X - 24 + i * 10, sy = from.Y - 24 - i * 6;
            Canvas.SetLeft(img, sx);
            Canvas.SetTop(img, sy);
            _fx.Children.Add(img);

            var begin = TimeSpan.FromMilliseconds(i * 70);
            var dur = TimeSpan.FromMilliseconds(460);
            var inEase = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.45 };
            img.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(sx, to.X - 24, dur) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            img.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(sy, to.Y - 24, dur) { BeginTime = begin, EasingFunction = inEase });
            var shrink = new DoubleAnimation(1.25, 0.15, dur) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            rt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, i % 2 == 0 ? -25 : 25, dur) { BeginTime = begin });
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160)) { BeginTime = begin + dur - TimeSpan.FromMilliseconds(120) };
            var dead = img;
            fade.Completed += (_, _) => _fx.Children.Remove(dead);
            img.BeginAnimation(OpacityProperty, fade);
        }

        // the island "gulps": a quick squash and spring back as the last icon arrives
        var gulp = new ScaleTransform(1, 1);
        _surface.RenderTransformOrigin = new Point(0.5, 0);
        _surface.RenderTransform = gulp;
        var at = TimeSpan.FromMilliseconds(380 + (paths.Count - 1) * 70);
        var sx2 = new DoubleAnimationUsingKeyFrames { BeginTime = at };
        sx2.KeyFrames.Add(new EasingDoubleKeyFrame(1.035, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        sx2.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new ElasticEase { Oscillations = 2, Springiness = 6 }));
        var sy2 = new DoubleAnimationUsingKeyFrames { BeginTime = at };
        sy2.KeyFrames.Add(new EasingDoubleKeyFrame(0.975, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        sy2.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new ElasticEase { Oscillations = 2, Springiness = 6 }));
        gulp.BeginAnimation(ScaleTransform.ScaleXProperty, sx2);
        gulp.BeginAnimation(ScaleTransform.ScaleYProperty, sy2);
    }

    // ------------------------------------------------------------------ clipboard
    private void RebuildClips()
    {
        _clipList.Children.Clear();
        if (!S.ClipboardHistory)
        {
            _clipList.Children.Add(Hint("剪贴板记录已关闭（设置 › 灵动岛）"));
            return;
        }
        var items = ClipboardService.Items.OrderByDescending(i => i.Pinned).ToList();
        foreach (var item in items) _clipList.Children.Add(ClipRow(item));
        if (items.Count == 0) _clipList.Children.Add(Hint("复制过的文字和图片会出现在这里，点一下重新复制"));
    }

    private static TextBlock Hint(string text)
    {
        var t = Text(text, 12, FontWeights.Normal, 0.45);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, 24, 0, 24);
        return t;
    }

    private FrameworkElement ClipRow(ClipItem item)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 3), Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = "nodrag" };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var face = new Border { Padding = new Thickness(10, 6, 8, 6), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)) };
        Grid.SetColumnSpan(face, 2);
        row.Children.Add(face);

        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 8, 6), VerticalAlignment = VerticalAlignment.Center };
        if (item.Pinned)
        {
            var pin = new TextBlock { Text = "", FontFamily = Icons, FontSize = 10, Foreground = Brushes.White, Opacity = 0.7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            pin.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            left.Children.Add(pin);
        }
        if (item.Text is not null)
        {
            var t = Text(ClipboardService.Preview(item.Text), 12.5, FontWeights.Normal, 0.92);
            t.MaxWidth = 360;
            left.Children.Add(t);
        }
        else if (item.ImagePath is { } p && File.Exists(p))
        {
            var bmp = new BitmapImage();
            try { bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.DecodePixelHeight = 60; bmp.UriSource = new Uri(p); bmp.EndInit(); bmp.Freeze(); } catch { }
            var img = new Image { Source = bmp, Height = 30, MaxWidth = 120, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
            left.Children.Add(new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = img });
            var dim = Text($"  图片 {item.ImageW}×{item.ImageH}", 11, FontWeights.Normal, 0.5);
            dim.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(dim);
        }
        row.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var when = Text(_copiedId == item.Id ? "已复制" : item.Time.Date == DateTime.Today ? item.Time.ToString("HH:mm") : item.Time.ToString("M/d"), 10.5, FontWeights.Normal, 0.45);
        when.VerticalAlignment = VerticalAlignment.Center;
        when.Margin = new Thickness(0, 0, 4, 0);
        right.Children.Add(when);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        actions.Children.Add(MiniButton(item.Pinned ? "" : "", item.Pinned ? "取消固定" : "固定", () => ClipboardService.TogglePin(item)));
        actions.Children.Add(MiniButton("", "删除这条", () => ClipboardService.Remove(item)));
        right.Children.Add(actions);
        Grid.SetColumn(right, 1);
        row.Children.Add(right);

        row.MouseEnter += (_, _) => { face.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)); actions.Visibility = Visibility.Visible; };
        row.MouseLeave += (_, _) => { face.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)); actions.Visibility = Visibility.Collapsed; };
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindButton(d) is not null) return;
            if (ClipboardService.Copy(item))
            {
                _copiedId = item.Id;
                when.Text = "已复制";
                when.Opacity = 0.9;
                var id = item.Id;
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
                t.Tick += (_, _) => { t.Stop(); if (_copiedId == id) { _copiedId = null; if (_page == ShelfPage) RebuildShelf(); } };
                t.Start();
            }
            e.Handled = true;
        };
        return row;
    }
}
