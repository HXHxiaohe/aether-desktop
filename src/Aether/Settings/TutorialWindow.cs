using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Aether.Core;
using Aether.Shell;

namespace Aether.Settings;

/// <summary>
/// 使用教程: a short slideshow of what Aether does, shown once after install/update and
/// from Settings › 关于 › 使用教程. Each slide can carry a "try it" button.
/// </summary>
public sealed class TutorialWindow : Window
{
    private sealed record Slide(string Glyph, string? Image, string Title, string Body, string[] Points, string? Try = null, Action? TryAction = null);

    private static Slide[] Slides => new[]
    {
        new Slide("", "glow-converge.png", "欢迎使用 Aether Desktop",
            "顶部的灵动岛、底部的 Dock、桌面上的小卡片，配色跟着壁纸走。花一分钟看看都能做什么。",
            new[] { "很轻：平时几乎不占 CPU", "打游戏、看全屏视频时自动全部让开", "随时可以在 设置 › 关于 › 使用教程 重新打开这里" }),
        new Slide("", "island-page1.png", "灵动岛",
            "屏幕顶部正中的小岛。收起时显示时间、歌词、新消息和电脑状态；鼠标移上去就展开。",
            new[] { "展开后左右滑动或滚轮切换页面：音乐、系统、控制、计时、通知、置物架、AI 额度、邮件",
                    "按住小岛左右拖，可以换位置",
                    "形态可选「刘海」或「悬浮胶囊」，皮肤可随专辑封面变色" }),
        new Slide("", null, "消息与验证码",
            "微信、QQ 来消息时岛上会提示是哪个软件，点一下就切过去。其他软件的通知会显示内容。",
            new[] { "通知里的验证码会被认出来，点一下就复制",
                    "连上邮箱后，验证码邮件会在岛上大字弹出（QQ / 163 / Gmail 等，用授权码）",
                    "Windows 自己的系统提示不会来打扰你" },
            "设置邮箱", () => App.ShowSettings("mail")),
        new Slide("", null, "置物架：临时放东西",
            "把文件或文件夹拖到灵动岛上，它会把东西「吸」进去；要用的时候再从岛上拖出来，直接拖进微信、邮件或文件夹。",
            new[] { "用 Win+Shift+S 截图，截图会自动放进置物架，拖进聊天就能发",
                    "按住窗口标题栏拖到灵动岛上松手，窗口会被吸进去收纳；在「窗口」标签点一下放回原处",
                    "「剪贴板」标签里有最近复制过的 30 条文字和图片，点一下重新复制" }),
        new Slide("", "glow-scan.png", "流光",
            "灵动岛轮廓上流动的光：放歌时跟着专辑颜色流动，来消息时按应用颜色亮起，收纳文件时两道光沿两侧汇合。",
            new[] { "6 种样式：扫描光带、彗星掠影、双子流光、声波脉冲、呼吸、常亮",
                    "颜色、亮度、速度都能调，也可以关掉",
                    "设置 › 灵动岛 › 流光与动效" },
            "预览流光", () => App.PreviewGlow()),
        new Slide("", "dock.png", "Dock 与应用抽屉",
            "屏幕底部的 Dock 可以代替任务栏：固定常用软件，显示正在运行的窗口。最左边的按钮打开应用抽屉。",
            new[] { "拖动图标换位置；从抽屉把应用拖到 Dock 就能固定",
                    "抽屉自动按类别分组，搜索支持拼音首字母（输入 wx 找到微信）",
                    "图标大小、外框大小、透明度都能调，也可以完全去掉外框" },
            "打开应用抽屉", () => Aether.Dock.DrawerHost.Toggle()),
        new Slide("", null, "桌面卡片与配色",
            "时钟、日历、天气、音乐、便签待办、音量混音、快速访问……都可以放在桌面上随意拖动。",
            new[] { "设置 › 桌面组件 里选择要显示哪些",
                    "设置 › 外观与颜色：从桌面取色、自动跟随壁纸，或固定颜色",
                    "布局满意了可以锁定，免得误拖" },
            "从桌面取色", () => _ = Theme.ThemeService.PickNowAsync()),
        new Slide("", null, "几个小贴士",
            "就这些！下面几条以后可能用得上。",
            new[] { "任务栏找不回来：开始菜单 › Aether Desktop › 「Aether 恢复任务栏（应急）」",
                    "更新：设置 › 关于 › 更新（会自动检查，设置和数据都保留）",
                    "卸载：Windows 设置 › 应用，或 设置 › 关于 › 卸载（会先恢复任务栏）" }),
    };

    private readonly Slide[] _slides = Slides;
    private int _index;
    private readonly Grid _stage = new();
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _back, _next, _skip;

    private static TutorialWindow? _open;

    /// <summary>Show (or bring forward) the tutorial.</summary>
    public static void ShowTutorial()
    {
        if (_open is not null) { _open.Activate(); return; }
        _open = new TutorialWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
        if (!SettingsStore.Current.TutorialSeen) { SettingsStore.Current.TutorialSeen = true; SettingsStore.Touch(); }
    }

    private TutorialWindow()
    {
        Title = "Aether 使用教程";
        Width = 720; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        Foreground = Brushes.White;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        var shell = new Border
        {
            CornerRadius = new CornerRadius(18), Margin = new Thickness(12),
            Background = new LinearGradientBrush(Color.FromRgb(0x1C, 0x1F, 0x2D), Color.FromRgb(0x13, 0x15, 0x1F), 90),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45 },
        };
        var root = new Grid { Margin = new Thickness(28, 22, 28, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.Child = root;
        Content = shell;

        // header: brand + close
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = "AETHER · 使用教程", FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.5 });
        var close = new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(8, 4, 8, 4) };
        if (TryFindResource("GhostButton") is Style ghost) close.Style = ghost;
        close.Click += (_, _) => Close();
        head.Children.Add(close);
        head.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        root.Children.Add(head);

        Grid.SetRow(_stage, 1);
        _stage.Margin = new Thickness(0, 10, 0, 10);
        root.Children.Add(_stage);

        // footer: skip · dots · back / next
        var foot = new Grid();
        Grid.SetRow(foot, 2);
        _skip = Btn("跳过", false, Close);
        _skip.HorizontalAlignment = HorizontalAlignment.Left;
        foot.Children.Add(_skip);
        foot.Children.Add(_dots);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _back = Btn("上一步", false, () => Go(_index - 1));
        _next = Btn("下一步", true, () => { if (_index == _slides.Length - 1) Close(); else Go(_index + 1); });
        nav.Children.Add(_back);
        nav.Children.Add(_next);
        foot.Children.Add(nav);
        root.Children.Add(foot);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Right or Key.Enter or Key.Space) { if (_index < _slides.Length - 1) Go(_index + 1); else Close(); e.Handled = true; }
            else if (e.Key == Key.Left) { Go(_index - 1); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        SourceInitialized += (_, _) => WindowEffects.SetDarkMode(new WindowInteropHelper(this).Handle, true);
        Go(0, animate: false);
    }

    private Button Btn(string text, bool accent, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 7, 16, 7) };
        if (TryFindResource(accent ? "AccentButton" : "GhostButton") is Style st) b.Style = st;
        b.Click += (_, _) => click();
        return b;
    }

    private void Go(int index, bool animate = true)
    {
        if (index < 0 || index >= _slides.Length) return;
        int dir = index >= _index ? 1 : -1;
        _index = index;
        var view = Build(_slides[index]);
        _stage.Children.Clear();
        _stage.Children.Add(view);
        if (animate)
        {
            var tt = new TranslateTransform(36 * dir, 0);
            view.RenderTransform = tt;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
            view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        }

        _dots.Children.Clear();
        for (int i = 0; i < _slides.Length; i++)
        {
            int k = i;
            var d = new Border
            {
                Width = i == index ? 18 : 7, Height = 7, CornerRadius = new CornerRadius(3.5), Margin = new Thickness(3, 0, 3, 0),
                Background = new SolidColorBrush(Color.FromArgb((byte)(i == index ? 0xEE : 0x44), 0xFF, 0xFF, 0xFF)), Cursor = Cursors.Hand,
            };
            d.MouseLeftButtonUp += (_, _) => Go(k);
            _dots.Children.Add(d);
        }
        _back.Visibility = index == 0 ? Visibility.Hidden : Visibility.Visible;
        _next.Content = index == _slides.Length - 1 ? "开始使用" : "下一步";
        _skip.Visibility = index == _slides.Length - 1 ? Visibility.Hidden : Visibility.Visible;
    }

    private static FrameworkElement Build(Slide s)
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(190) });
        panel.RowDefinitions.Add(new RowDefinition());

        // picture: a screenshot, or a large glyph on a soft accent glow
        var pic = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0D, 0x14)) };
        if (s.Image is not null && LoadImage(s.Image) is { } img)
            pic.Child = new Image { Source = img, Stretch = Stretch.Uniform, Margin = new Thickness(12) };
        else
        {
            var glow = new Grid();
            var halo = new System.Windows.Shapes.Ellipse { Width = 150, Height = 150, Opacity = 0.35 };
            var accent = Theme.ThemeService.Accent;
            halo.Fill = new RadialGradientBrush(Color.FromArgb(0xFF, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B));
            glow.Children.Add(halo);
            glow.Children.Add(new TextBlock
            {
                Text = s.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 64,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White,
            });
            pic.Child = glow;
        }
        panel.Children.Add(pic);

        var text = new StackPanel { Margin = new Thickness(4, 18, 4, 0) };
        Grid.SetRow(text, 1);
        text.Children.Add(new TextBlock { Text = s.Title, FontSize = 22, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = s.Body, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Opacity = 0.78, Margin = new Thickness(0, 8, 0, 10), LineHeight = 21 });
        foreach (var p in s.Points)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new TextBlock { Text = "•", FontSize = 13, Opacity = 0.9 };
            dot.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            row.Children.Add(dot);
            var t = new TextBlock { Text = p, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
            Grid.SetColumn(t, 1);
            row.Children.Add(t);
            text.Children.Add(row);
        }
        if (s.Try is not null && s.TryAction is not null)
        {
            var b = new Button { Content = s.Try + "  →", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
            if (Application.Current?.TryFindResource("GhostButton") is Style st) b.Style = st;
            b.Click += (_, _) => s.TryAction();
            text.Children.Add(b);
        }
        panel.Children.Add(text);
        return panel;
    }

    private static BitmapImage? LoadImage(string name)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri($"pack://application:,,,/Assets/Tutorial/{name}");
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) { Log.Info("[tutorial] image " + ex.Message); return null; }
    }
}
