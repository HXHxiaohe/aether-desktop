using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Aether.Core;
using Aether.Interop;
using Aether.Shell;
using Aether.Theme;
using Aether.Widgets.Clock;

namespace Aether.Settings;

public partial class SettingsWindow : Window
{
    private static readonly string[] Presets =
    {
        "#8C9EFF", "#A78BFA", "#F0A6CA", "#FF9E80", "#FFD166", "#7BE0AD", "#5ED1E8", "#9AA8C7",
    };

    private bool _ready;

    public SettingsWindow()
    {
        InitializeComponent();
        BuildSwatches();
        BuildClockStyles();
        LoadValues();
        _ready = true;
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeService.Changed -= OnThemeChanged;
        VersionText.Text = $"版本 {typeof(App).Assembly.GetName().Version?.ToString(3)}  ·  .NET {Environment.Version}";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // No system menu => DWM stops painting its own caption buttons over ours.
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, new IntPtr(style & ~0x00080000L));
        WindowEffects.SetDarkMode(hwnd, true);
        WindowEffects.MakeTransparent(this);
        WindowEffects.SetCorners(hwnd, Native.DWMWCP_ROUND);
        WindowEffects.SetBackdrop(hwnd, Native.DWMSBT_TABBEDWINDOW);
    }

    // ---------- load ----------
    private void LoadValues()
    {
        var t = SettingsStore.Current.Theme;
        ModeManual.IsChecked = t.Mode == ColorMode.Manual;
        ModeAuto.IsChecked = t.Mode == ColorMode.Auto;
        ModeFixed.IsChecked = t.Mode == ColorMode.Fixed;
        GlassSlider.Value = t.GlassOpacity;
        BlurSwitch.IsChecked = t.Blur;
        ToneLight.IsChecked = t.GlassTone != "dark";
        ToneDark.IsChecked = t.GlassTone == "dark";
        HexBox.Text = t.FixedColor;
        InkLight.IsChecked = t.WidgetText != "dark";
        InkDark.IsChecked = t.WidgetText == "dark";
        ShadowSoft.IsChecked = t.TextShadow == "soft";
        ShadowStrong.IsChecked = t.TextShadow == "strong";
        ShadowOff.IsChecked = t.TextShadow == "off";
        UpdateModeUi();

        var clock = SettingsStore.Current.Widget("clock");
        ClockVisible.IsChecked = clock.Visible;
        ClockScale.Value = clock.Clock!.Scale;
        Clock24.IsChecked = clock.Clock.Hour24;
        ClockSeconds.IsChecked = clock.Clock.ShowSeconds;
        ClockDate.IsChecked = clock.Clock.ShowDate;
        ClockCard.IsChecked = clock.Clock.Card;
        foreach (RadioButton rb in ClockStyles.Children)
            rb.IsChecked = (int)rb.Tag == clock.Clock.Style;
        LockSwitch.IsChecked = SettingsStore.Current.LayoutLocked;
        LoadDock();
        LoadIsland();
        BuildWidgetToggles();
        LoadAutostart();
        LoadUpdate();
        LoadGlow();
    }

    private void UpdateModeUi()
    {
        var t = SettingsStore.Current.Theme;
        ModeHint.Text = t.Mode switch
        {
            ColorMode.Auto => "换壁纸时自动重新取色。Wallpaper Engine 通过它的配置文件得知切换，不会持续截屏，几乎不占性能。",
            ColorMode.Fixed => "颜色固定不变，不再跟随桌面。",
            _ => "换壁纸后点一下「从桌面取色」。平时完全不运行，零性能开销。",
        };
        FixedPanel.Visibility = t.Mode == ColorMode.Fixed ? Visibility.Visible : Visibility.Collapsed;
        PickButton.Visibility = t.Mode == ColorMode.Fixed ? Visibility.Collapsed : Visibility.Visible;
        ColorValue.Text = ThemeService.ToHex(ThemeService.Accent);
        ColorSource.Text = t.Mode == ColorMode.Fixed
            ? "固定颜色"
            : string.IsNullOrEmpty(t.PickedSource) ? "尚未取色" : "来源：" + t.PickedSource;
    }

    private void OnThemeChanged() => Dispatcher.BeginInvoke(UpdateModeUi);

    // ---------- navigation ----------
    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (PageLook is null) return; // fired during InitializeComponent
        PageLook.Visibility = NavLook.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageWidgets.Visibility = NavWidgets.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageIsland.Visibility = NavIsland.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageDock.Visibility = NavDock.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---------- appearance ----------
    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var t = SettingsStore.Current.Theme;
        t.Mode = ModeAuto.IsChecked == true ? ColorMode.Auto : ModeFixed.IsChecked == true ? ColorMode.Fixed : ColorMode.Manual;
        SettingsStore.Touch();
        ThemeService.Apply();
        ThemeService.UpdateAutoMode();
        UpdateModeUi();
    }

    private async void OnPick(object sender, RoutedEventArgs e)
    {
        PickButton.IsEnabled = false;
        try
        {
            var r = await ThemeService.PickNowAsync();
            if (r is null) ColorSource.Text = "没取到颜色，请稍后再试";
        }
        finally
        {
            PickButton.IsEnabled = true;
            UpdateModeUi();
        }
    }

    private void BuildSwatches()
    {
        foreach (var hex in Presets)
        {
            var color = ThemeService.Parse(hex)!.Value;
            var b = new Button
            {
                Width = 34, Height = 34, Margin = new Thickness(0, 0, 10, 10), Cursor = Cursors.Hand, Focusable = false,
                ToolTip = hex,
                Template = SwatchTemplate(color),
            };
            b.Click += (_, _) => SetFixed(hex);
            Swatches.Children.Add(b);
        }
    }

    private static ControlTemplate SwatchTemplate(Color c)
    {
        var t = new ControlTemplate(typeof(Button));
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
        f.SetValue(Border.BackgroundProperty, new SolidColorBrush(c));
        f.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)));
        f.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        t.VisualTree = f;
        return t;
    }

    private void SetFixed(string hex)
    {
        var c = ThemeService.Parse(hex.Trim());
        if (c is null) { HexBox.Text = SettingsStore.Current.Theme.FixedColor; return; }
        var t = SettingsStore.Current.Theme;
        t.FixedColor = ThemeService.ToHex(c.Value);
        HexBox.Text = t.FixedColor;
        SettingsStore.Touch();
        ThemeService.Apply();
    }

    private void OnHexApply(object sender, RoutedEventArgs e) => SetFixed(HexBox.Text);

    private void OnHexKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SetFixed(HexBox.Text);
    }

    private void OnGlassChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        SettingsStore.Current.Theme.GlassOpacity = (int)Math.Round(e.NewValue);
        SettingsStore.Touch();
        ThemeService.Apply();
    }

    private void OnToneChanged(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.Theme.GlassTone = ToneDark.IsChecked == true ? "dark" : "light";
        SettingsStore.Touch();
        ThemeService.Apply();
        App.ApplyDock();
    }

    private void OnBlurChanged(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.Theme.Blur = BlurSwitch.IsChecked == true;
        SettingsStore.Touch();
        ThemeService.Apply();
    }

    // ---------- widgets ----------
    private void BuildClockStyles()
    {
        for (int i = 0; i < ClockWidget.StyleNames.Length; i++)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Chip"),
                Content = ClockWidget.StyleNames[i],
                Tag = i,
                GroupName = "clockstyle",
                MinWidth = 76,
            };
            rb.Checked += (s, _) =>
            {
                if (!_ready) return;
                EditClock(o => o.Style = (int)((RadioButton)s!).Tag);
            };
            ClockStyles.Children.Add(rb);
        }
    }

    private void EditClock(Action<ClockOptions> edit)
    {
        var cfg = SettingsStore.Current.Widget("clock");
        edit(cfg.Clock!);
        SettingsStore.Touch();
        WidgetHost.Refresh(cfg.Id);
    }

    /// <summary>One switch per desktop card (+ "新建" for notes, which can have several).</summary>
    private void BuildWidgetToggles()
    {
        WidgetToggles.Children.Clear();
        bool first = true;
        foreach (var (kind, name, multi) in WidgetHost.Kinds)
        {
            if (kind == "clock") continue; // has its own section below
            if (!first) WidgetToggles.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("PanelBorderBrush") });
            first = false;
            var row = new Grid { Margin = new Thickness(0, 12, 0, 12) };
            var label = new TextBlock { Text = name, Style = (Style)FindResource("RowLabel") };
            row.Children.Add(label);
            if (multi)
            {
                int count = SettingsStore.Current.Widgets.Count(w => w.Kind == kind && w.Visible);
                label.Text = count > 0 ? $"{name}（{count} 个）" : name;
                var add = new Button { Style = (Style)FindResource("GhostButton"), Content = "新建", HorizontalAlignment = HorizontalAlignment.Right };
                var k = kind;
                add.Click += (_, _) => { WidgetHost.Add(k); BuildWidgetToggles(); };
                row.Children.Add(add);
            }
            else
            {
                var sw = new CheckBox { Style = (Style)FindResource("Switch"), HorizontalAlignment = HorizontalAlignment.Right, IsChecked = WidgetHost.IsShown(kind) };
                var k = kind;
                sw.Click += (_, _) => WidgetHost.SetShown(k, sw.IsChecked == true);
                row.Children.Add(sw);
            }
            WidgetToggles.Children.Add(row);

            // Common options: glass card on/off + size (applies to every card of this kind).
            var cfg = WidgetHost.First(kind);
            var opts = new Grid { Margin = new Thickness(0, -4, 0, 12) };
            opts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            opts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            opts.ColumnDefinitions.Add(new ColumnDefinition());
            var kk = kind;
            var cardChip = new CheckBox { Style = (Style)FindResource("ToggleChip"), Content = "玻璃卡片背景", IsChecked = cfg?.Card ?? true, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            cardChip.Click += (_, _) => WidgetHost.Edit(kk, c => c.Card = cardChip.IsChecked == true);
            opts.Children.Add(cardChip);
            var sizeLabel = new TextBlock { Text = "大小", Foreground = (Brush)FindResource("SubTextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            Grid.SetColumn(sizeLabel, 1);
            opts.Children.Add(sizeLabel);
            var size = new Slider { Minimum = 0.7, Maximum = 1.6, Value = cfg?.Scale ?? 1.0, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, HorizontalAlignment = HorizontalAlignment.Left, Width = 220 };
            // Apply when the drag ends (rebuilding cards on every tick of the slider is wasteful).
            size.PreviewMouseLeftButtonUp += (_, _) => WidgetHost.Edit(kk, c => c.Scale = Math.Round(size.Value, 2));
            size.KeyUp += (_, _) => WidgetHost.Edit(kk, c => c.Scale = Math.Round(size.Value, 2));
            Grid.SetColumn(size, 2);
            opts.Children.Add(size);
            opts.IsEnabled = cfg is not null;
            opts.Opacity = cfg is null ? 0.4 : 1;
            WidgetToggles.Children.Add(opts);
        }
    }

    private void OnClockVisible(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.Widget("clock").Visible = ClockVisible.IsChecked == true;
        SettingsStore.Touch();
        WidgetHost.Sync();
    }

    private void OnClockScale(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        EditClock(o => o.Scale = Math.Round(e.NewValue, 2));
    }

    private void OnClockOption(object sender, RoutedEventArgs e)
    {
        EditClock(o =>
        {
            o.Hour24 = Clock24.IsChecked == true;
            o.ShowSeconds = ClockSeconds.IsChecked == true;
            o.ShowDate = ClockDate.IsChecked == true;
            o.Card = ClockCard.IsChecked == true;
        });
    }

    private void OnLock(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.LayoutLocked = LockSwitch.IsChecked == true;
        SettingsStore.Touch();
    }

    private void OnResetPositions(object sender, RoutedEventArgs e) => WidgetHost.ResetPositions();

    // ---------- dock ----------
    public void SelectPage(string page)
    {
        var nav = page switch
        {
            "widgets" => NavWidgets,
            "island" or "mail" or "glow" => NavIsland,
            "dock" => NavDock,
            "about" => NavAbout,
            _ => NavLook,
        };
        nav.IsChecked = true;
        if (page == "mail") Dispatcher.BeginInvoke(() => MailSection.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
        if (page == "glow") Dispatcher.BeginInvoke(() => GlowBox.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void LoadDock()
    {
        var d = SettingsStore.Current.Dock;
        DockEnabled.IsChecked = d.Enabled;
        DockHideTaskbar.IsChecked = d.HideTaskbar;
        HideSmart.IsChecked = d.HideMode == DockHideMode.Smart;
        HideAlways.IsChecked = d.HideMode == DockHideMode.Always;
        HideNever.IsChecked = d.HideMode == DockHideMode.Never;
        DockIconSize.Value = d.IconSize;
        DockOpacity.Value = d.Opacity;
        DockFramePad.Value = d.FramePadding;
        DockFrame.IsChecked = d.ShowFrame;
        DockSystemButtons.IsChecked = d.ShowSystemButtons;
        DrawerOpacity.Value = d.DrawerOpacity;
        DrawerBlur.IsChecked = d.DrawerBlur;
        DockBlur.IsChecked = d.Blur;
        DockMagnify.IsChecked = d.Magnify;
        DockLabels.IsChecked = d.ShowLabels;
        DockRunning.IsChecked = d.ShowRunning;
        UpdateDockHints();
    }

    private void UpdateDockHints()
    {
        HideHint.Text = SettingsStore.Current.Dock.HideMode switch
        {
            DockHideMode.Always => "平时藏起来，鼠标移到屏幕底边中间时出现。",
            DockHideMode.Never => "始终显示（全屏游戏/视频时仍会自动消失）。",
            _ => "桌面上一直显示；窗口挡到它时藏起来，鼠标碰到底边再出现。",
        };
        var apps = Aether.Apps.AppCatalog.Apps;
        DrawerInfo.Text = $"共 {apps.Count} 个应用，{SettingsStore.Current.Groups.Count} 个分组，{apps.Count(a => a.DockOrder is not null)} 个固定在 Dock";
    }

    private void OnDockOption(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var d = SettingsStore.Current.Dock;
        d.Enabled = DockEnabled.IsChecked == true;
        d.HideTaskbar = DockHideTaskbar.IsChecked == true;
        d.HideMode = HideAlways.IsChecked == true ? DockHideMode.Always : HideNever.IsChecked == true ? DockHideMode.Never : DockHideMode.Smart;
        d.Blur = DockBlur.IsChecked == true;
        d.ShowFrame = DockFrame.IsChecked == true;
        d.ShowSystemButtons = DockSystemButtons.IsChecked == true;
        d.DrawerBlur = DrawerBlur.IsChecked == true;
        d.Magnify = DockMagnify.IsChecked == true;
        d.ShowLabels = DockLabels.IsChecked == true;
        d.ShowRunning = DockRunning.IsChecked == true;
        SettingsStore.Touch();
        App.ApplyDock();
        UpdateDockHints();
    }

    private void OnDockSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        var d = SettingsStore.Current.Dock;
        d.IconSize = (int)Math.Round(DockIconSize.Value);
        d.Opacity = (int)Math.Round(DockOpacity.Value);
        d.FramePadding = (int)Math.Round(DockFramePad.Value);
        d.DrawerOpacity = (int)Math.Round(DrawerOpacity.Value);
        SettingsStore.Touch();
        if (sender != DrawerOpacity) App.ApplyDock();
    }

    private void OnOpenDrawer(object sender, RoutedEventArgs e) => Aether.Dock.DrawerHost.Toggle();

    // ---------- island ----------
    private void LoadIsland()
    {
        var s = SettingsStore.Current.Island;
        IslandEnabled.IsChecked = s.Enabled;
        IslandThinLine.IsChecked = s.ThinLineWhenMaximized;
        IslandSpectrum.IsChecked = s.ShowSpectrum;
        IslandSources.IsChecked = s.ShowSources;
        IslandToast.IsChecked = s.TrackToast;
        IslandVolume.IsChecked = s.VolumeOsd;
        IslandBrightness.IsChecked = s.BrightnessOsd;
        IslandOnlineLyrics.IsChecked = s.OnlineLyrics;
        IslandPower.IsChecked = s.PowerToasts;
        IslandNotify.IsChecked = s.Notifications;
        IslandNotifyBody.IsChecked = s.NotificationContent;
        ShapeNotch.IsChecked = s.Shape != "capsule";
        ShapeCapsule.IsChecked = s.Shape == "capsule";
        SkinBlack.IsChecked = s.Skin != "cover";
        SkinCover.IsChecked = s.Skin == "cover";
        IslandTime.IsChecked = s.ShowTime;
        TimeSimple.IsChecked = s.TimeStyle == 0;
        TimeSeg.IsChecked = s.TimeStyle == 1;
        TimeSerif.IsChecked = s.TimeStyle == 2;
        IslandLyrics.IsChecked = s.ShowLyrics;
        IslandUnread.IsChecked = s.ShowUnread;
        IslandStats.IsChecked = s.ShowStats;
        StatCpu.IsChecked = s.StatCpu;
        StatMem.IsChecked = s.StatMemory;
        StatNet.IsChecked = s.StatNetwork;
        StatBat.IsChecked = s.StatBattery;
        IslandClipboard.IsChecked = s.ClipboardHistory;
        IslandClipboardImages.IsChecked = s.ClipboardImages;
        IslandScreenshotShelf.IsChecked = s.ScreenshotShelf;
        IslandQuota.IsChecked = s.Quota;
        IslandQuotaCompact.IsChecked = s.QuotaInCompact;
        IslandMail.IsChecked = s.Mail;
        IslandMailCode.IsChecked = s.MailCodeToast;
        LoadMail();
    }

    private void OnIslandOption(object sender, RoutedEventArgs e)
    {
        var s = SettingsStore.Current.Island;
        s.Enabled = IslandEnabled.IsChecked == true;
        s.ThinLineWhenMaximized = IslandThinLine.IsChecked == true;
        s.ShowSpectrum = IslandSpectrum.IsChecked == true;
        s.ShowSources = IslandSources.IsChecked == true;
        s.TrackToast = IslandToast.IsChecked == true;
        s.VolumeOsd = IslandVolume.IsChecked == true;
        s.BrightnessOsd = IslandBrightness.IsChecked == true;
        s.OnlineLyrics = IslandOnlineLyrics.IsChecked == true;
        s.PowerToasts = IslandPower.IsChecked == true;
        s.Notifications = IslandNotify.IsChecked == true;
        s.NotificationContent = IslandNotifyBody.IsChecked == true;
        s.Shape = ShapeCapsule.IsChecked == true ? "capsule" : "notch";
        s.Skin = SkinCover.IsChecked == true ? "cover" : "black";
        s.ShowTime = IslandTime.IsChecked == true;
        s.TimeStyle = TimeSeg.IsChecked == true ? 1 : TimeSerif.IsChecked == true ? 2 : 0;
        s.ShowLyrics = IslandLyrics.IsChecked == true;
        s.ShowUnread = IslandUnread.IsChecked == true;
        s.ShowStats = IslandStats.IsChecked == true;
        s.StatCpu = StatCpu.IsChecked == true;
        s.StatMemory = StatMem.IsChecked == true;
        s.StatNetwork = StatNet.IsChecked == true;
        s.StatBattery = StatBat.IsChecked == true;
        s.ClipboardHistory = IslandClipboard.IsChecked == true;
        s.ClipboardImages = IslandClipboardImages.IsChecked == true;
        s.ScreenshotShelf = IslandScreenshotShelf.IsChecked == true;
        bool quotaCompactWasOff = !s.QuotaInCompact;
        s.Quota = IslandQuota.IsChecked == true;
        s.QuotaInCompact = IslandQuotaCompact.IsChecked == true;
        if (s.Quota && s.QuotaInCompact && quotaCompactWasOff) _ = Aether.Island.QuotaService.RefreshAsync(false);
        s.Mail = IslandMail.IsChecked == true;
        s.MailCodeToast = IslandMailCode.IsChecked == true;
        SettingsStore.Touch();
        App.ApplyIsland();
    }

    private void OnIslandCenter(object sender, RoutedEventArgs e) => App.ResetIslandPosition();

    // ---------- about ----------
    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        Paths.Ensure();
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.DataDir}\"") { UseShellExecute = true });
    }

    private void OnQuit(object sender, RoutedEventArgs e) => App.Quit();

    private void OnTutorial(object sender, RoutedEventArgs e) => TutorialWindow.ShowTutorial();

    private void OnWidgetInk(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsStore.Current.Theme.WidgetText = InkDark.IsChecked == true ? "dark" : "light";
        SettingsStore.Touch();
        foreach (var w in SettingsStore.Current.Widgets.ToList()) WidgetHost.Refresh(w.Id);
        App.ApplyDock(); // the Dock's built-in buttons follow the same colour
    }

    private void OnTextShadow(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsStore.Current.Theme.TextShadow = ShadowStrong.IsChecked == true ? "strong" : ShadowOff.IsChecked == true ? "off" : "soft";
        SettingsStore.Touch();
        foreach (var w in SettingsStore.Current.Widgets.ToList()) WidgetHost.Refresh(w.Id);
    }

    // ---------- install / update ----------
    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (!Setup.RunningInstalled)
        {
            MessageBox.Show(this, "这个 Aether 不是安装版（直接从文件夹运行的），删掉它所在的文件夹就行。", "Aether");
            return;
        }
        var wipe = new CheckBox { Content = "同时删除我的设置、便签和置物架记录", Foreground = Brushes.White, Margin = new Thickness(0, 14, 0, 0) };
        if (Setup.Ask("卸载 Aether Desktop", "会恢复 Windows 任务栏，删除开机自启、开始菜单快捷方式和程序文件。", wipe, "卸载", "取消") != 1) return;
        Setup.Uninstall(wipe.IsChecked == true);
        App.Quit();
    }

    private void LoadUpdate()
    {
        UpdateAuto.IsChecked = SettingsStore.Current.AutoCheckUpdates;
        Updater.Changed += OnUpdaterChanged;
        Closed += (_, _) => Updater.Changed -= OnUpdaterChanged;
        OnUpdaterChanged();
    }

    private void OnUpdaterChanged() => Dispatcher.BeginInvoke(() =>
    {
        UpdateStatus.Text = !Updater.Configured ? "这个版本没有设置更新地址（自己编译的版本）"
            : !Setup.RunningInstalled ? "直接运行的版本不会自动更新，先安装"
            : Updater.Status.Length > 0 ? Updater.Status : $"当前版本 {Updater.Current}";
        bool has = Updater.Available is not null;
        UpdateInstall.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        UpdateInstall.IsEnabled = !Updater.Busy && Setup.RunningInstalled;
        UpdateCheck.IsEnabled = !Updater.Busy && Updater.Configured;
        UpdateNotes.Visibility = has && Updater.Available!.Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (has) UpdateNotes.Text = Updater.Available!.Notes.Length > 400 ? Updater.Available.Notes[..400] + "…" : Updater.Available.Notes;
    });

    private void OnCheckUpdate(object sender, RoutedEventArgs e) => _ = Updater.CheckAsync();
    private void OnInstallUpdate(object sender, RoutedEventArgs e) => _ = Updater.InstallAsync();

    private void OnUpdateAuto(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.AutoCheckUpdates = UpdateAuto.IsChecked == true;
        SettingsStore.Touch();
    }

    private static readonly string[] AutostartHints =
    {
        "不随 Windows 启动",
        "高：登录后由计划任务立即启动，不受启动项被延迟或拦截影响（推荐）",
        "普通：Windows 标准启动项，可在任务管理器 › 启动应用 里看到",
        "低：登录后等 60 秒再启动，给其它程序让路",
    };

    private void LoadAutostart(string? error = null)
    {
        var (mode, target) = Autostart.Read();
        AutoHigh.IsChecked = mode == AutostartMode.High;
        AutoNormal.IsChecked = mode == AutostartMode.Normal;
        AutoLow.IsChecked = mode == AutostartMode.Low;
        AutoOff.IsChecked = mode == AutostartMode.Off;
        AutostartHint.Text = error is not null ? "设置失败：" + error
            : mode != AutostartMode.Off && !Autostart.PointsHere(target) ? "现在的自启指向另一个位置的 Aether；重新点一下档位即可改成当前这个"
            : AutostartHints[(int)mode];
    }

    private void OnAutostart(object sender, RoutedEventArgs e)
    {
        var mode = sender == AutoHigh ? AutostartMode.High : sender == AutoNormal ? AutostartMode.Normal
                 : sender == AutoLow ? AutostartMode.Low : AutostartMode.Off;
        LoadAutostart(Autostart.Set(mode));
    }
}
