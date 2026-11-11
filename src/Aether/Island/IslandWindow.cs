using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Aether.Apps;
using Aether.Core;
using Aether.Interop;
using Aether.Shell;
using Aether.Theme;

namespace Aether.Island;

/// <summary>
/// Dynamic island at the top of the screen (notch or capsule). Layered window, so the empty
/// area around the island never blocks clicks underneath. States: hidden (fullscreen), thin
/// line (maximized window), collapsed, OSD, toast, expanded (pages: music / system / controls).
/// </summary>
public sealed partial class IslandWindow : Window
{
    private enum Mode { Hidden, Line, Idle, Compact, Osd, Toast, Alert, Expanded }

    private static IslandSettings S => SettingsStore.Current.Island;
    private bool IsNotch => S.Shape != "capsule";

    private static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    private static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly CultureInfo Zh = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly string? DebugMode = Environment.GetEnvironmentVariable("AETHER_ISLAND_MODE");
    private static readonly string? DebugPage = Environment.GetEnvironmentVariable("AETHER_ISLAND_PAGE");

    private const double ExpandedW = 500;

    private readonly IslandSurface _surface;
    private readonly Grid _layers;
    private readonly DispatcherTimer _poll, _frame;
    private IntPtr _hwnd;

    private Mode _mode = Mode.Idle;
    private bool _hover, _lineHover, _pressed, _dragging, _fullscreen, _maximized;
    private DateTime _leftSince = DateTime.MinValue, _osdUntil, _toastUntil, _spectrumLastNeeded;
    private int _tick, _page;
    private string _lastMinute = "", _lastLyric = "";
    private Point _downAt;
    private double _downOffset;
    private Color? _coverColor;
    private bool _layoutDirty = true;

    // ---- compact ----
    private readonly Grid _compact;
    private readonly Border _cCoverBox;
    private readonly Image _cCover;
    private readonly TextBlock _cStats, _cText, _cTimeText;
    private readonly SevenSegment _cTimeSeg;
    private readonly StackPanel _cUnread;
    private readonly FrameworkElement _cBarsPanel;
    private readonly Rectangle[] _cBars;

    // ---- osd / toast ----
    private readonly Grid _osd, _toast;
    private readonly TextBlock _osdGlyph, _osdText, _toastTitle, _toastSub;
    private readonly Border _osdFill, _toastArtBox;
    private readonly Image _toastArt;
    private readonly FrameworkElement _toastBarsPanel;
    private readonly Rectangle[] _toastBars;

    // ---- expanded ----
    private readonly Grid _expanded, _pages;
    private readonly FrameworkElement[] _pageViews;
    private readonly Button[] _tabs;
    private readonly StackPanel _dots, _hUnread;
    private readonly TextBlock _hClock;
    private readonly Button _arrowL, _arrowR;
    // music page
    private readonly FrameworkElement _mBody, _mEmpty;
    private readonly Image _mCover;
    private readonly TextBlock _mTitle, _mSub, _mLyric, _mPos, _mDur, _mPlay, _mApp;
    private readonly Slider _mProgress;
    private readonly Rectangle[] _mBars;
    // system page
    private readonly TextBlock _sCpu, _sMem, _sMemSub, _sNet, _sNetSub, _sBat, _sBatSub;
    private readonly Polyline _sSpark;
    private readonly FrameworkElement _sBatTile;
    // control page
    private readonly Slider _vol, _bri;
    private readonly TextBlock _volText, _briText, _volGlyph;
    private readonly FrameworkElement _briRow;
    private readonly StackPanel _sources;
    private bool _syncing;
    private readonly Border _wifiTile, _btTile;
    private readonly TextBlock _wifiSub, _btSub;
    // timer page
    private readonly TextBlock _tTime, _tStartGlyph, _spinH, _spinM;
    private readonly Border _tBarFill;
    private readonly StackPanel _alarmList;
    private readonly Button _dailyBtn;
    private int _newHour = 7, _newMinute = 30;
    private bool _newDaily;
    // alert (timer / alarm ringing) + toast glyph + compact timer
    private readonly Grid _alert;
    private readonly TextBlock _alertText, _toastGlyph, _cTimer;
    private DateTime _alertUntil;
    private readonly Action<bool> _onGameMode;
    // notifications
    private readonly StackPanel _noteList;
    private readonly TextBlock _cNotify;
    private NotificationItem? _toastNote;
    private readonly Border _lineBar;

    public IslandWindow()
    {
        Title = "Aether Island";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 600; Height = 330; Top = 0;
        UseLayoutRounding = true;
        FontFamily = Ui;
        Foreground = Brushes.White;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        _surface = new IslandSurface
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Width = 160, Height = 32, Radius = 14, Cursor = Cursors.Hand,
        };
        _layers = new Grid();
        _surface.Child = _layers;
        var root = new Grid();
        root.Children.Add(_surface);
        BuildGlow(root);
        // Thin-line mode over maximized windows: a light "home indicator" bar (the black island
        // shrunk to 5 px vanished against dark tab bars). Dark rim keeps it visible on light ones.
        _lineBar = new Border
        {
            Width = 120, Height = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0x00, 0x00)), BorderThickness = new Thickness(0.8),
            Opacity = 0, IsHitTestVisible = false,
        };
        root.Children.Add(_lineBar);
        Content = root;

        // ================= compact =================
        _compact = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _cCover = new Image { Stretch = Stretch.UniformToFill };
        _cCoverBox = Rounded(_cCover, 20, 6);
        _cCoverBox.Margin = new Thickness(0, 0, 8, 0);
        cLeft.Children.Add(_cCoverBox);
        _cTimer = Text("", 12.5, FontWeights.SemiBold);
        _cTimer.FontFamily = Display;
        _cTimer.Margin = new Thickness(0, 0, 10, 0);
        _cTimer.VerticalAlignment = VerticalAlignment.Center;
        _cTimer.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        cLeft.Children.Add(_cTimer);
        _cStats = Text("", 11.5, FontWeights.SemiBold, 0.85);
        _cStats.FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI");
        _cStats.Margin = new Thickness(0, 0, 10, 0);
        _cStats.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Documents.Typography.SetNumeralAlignment(_cStats, FontNumeralAlignment.Tabular);
        cLeft.Children.Add(_cStats);
        _compact.Children.Add(cLeft);

        var cCenter = new Grid { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _cText = Text("", 12.5, FontWeights.SemiBold);
        _cText.MaxWidth = 260;
        cCenter.Children.Add(_cText);
        _cTimeText = Text("", 13.5, FontWeights.SemiBold);
        _cTimeText.FontFamily = Display;
        cCenter.Children.Add(_cTimeText);
        _cTimeSeg = new SevenSegment { DigitHeight = 13, VerticalAlignment = VerticalAlignment.Center };
        cCenter.Children.Add(_cTimeSeg);
        Grid.SetColumn(cCenter, 1);
        _compact.Children.Add(cCenter);

        var cRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _cBarsPanel = Bars(5, 3, 2.5, 16, out _cBars);
        _cBarsPanel.Margin = new Thickness(8, 0, 0, 0);
        cRight.Children.Add(_cBarsPanel);
        _cUnread = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        cRight.Children.Add(_cUnread);
        _cNotify = Text("", 11.5, FontWeights.SemiBold);
        _cNotify.Margin = new Thickness(8, 0, 0, 0);
        _cNotify.VerticalAlignment = VerticalAlignment.Center;
        _cNotify.Visibility = Visibility.Collapsed;
        cRight.Children.Add(_cNotify);
        Grid.SetColumn(cRight, 2);
        _compact.Children.Add(cRight);
        _layers.Children.Add(_compact);

        // ================= OSD =================
        _osd = new Grid { Margin = new Thickness(18, 0, 18, 0) };
        _osd.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _osd.ColumnDefinitions.Add(new ColumnDefinition());
        _osd.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        _osdGlyph = Glyph("", 15);
        _osd.Children.Add(_osdGlyph);
        var track = new Grid { Height = 5, Margin = new Thickness(12, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) });
        _osdFill = new Border { CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left };
        _osdFill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        track.Children.Add(_osdFill);
        Grid.SetColumn(track, 1);
        _osd.Children.Add(track);
        _osdText = Text("", 12.5, FontWeights.SemiBold);
        _osdText.HorizontalAlignment = HorizontalAlignment.Right;
        _osdText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_osdText, 2);
        _osd.Children.Add(_osdText);
        _layers.Children.Add(_osd);

        // ================= toast (new song / app wants attention) =================
        _toast = new Grid { Margin = new Thickness(14, 10, 18, 10) };
        _toast.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _toast.ColumnDefinitions.Add(new ColumnDefinition());
        _toast.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _toastArt = new Image { Stretch = Stretch.UniformToFill };
        _toastArtBox = Rounded(_toastArt, 50, 12);
        _toastArtBox.Child = null;
        var artGrid = new Grid();
        artGrid.Children.Add(_toastArt);
        _toastGlyph = Glyph("", 24);
        artGrid.Children.Add(_toastGlyph);
        _toastArtBox.Child = artGrid;
        _toast.Children.Add(_toastArtBox);
        var tInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
        _toastTitle = Text("", 14.5, FontWeights.SemiBold);
        _toastSub = Text("", 12, FontWeights.Normal, 0.65);
        tInfo.Children.Add(_toastTitle);
        tInfo.Children.Add(_toastSub);
        Grid.SetColumn(tInfo, 1);
        _toast.Children.Add(tInfo);
        _toastBarsPanel = Bars(7, 3, 2.5, 24, out _toastBars);
        Grid.SetColumn(_toastBarsPanel, 2);
        _toast.Children.Add(_toastBarsPanel);
        _layers.Children.Add(_toast);

        // ================= alert: timer finished / alarm ringing =================
        _alert = new Grid { Margin = new Thickness(16, 0, 14, 0) };
        _alert.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _alert.ColumnDefinitions.Add(new ColumnDefinition());
        _alert.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var bell = Glyph("", 20);
        bell.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        _alert.Children.Add(bell);
        _alertText = Text("", 14, FontWeights.SemiBold);
        _alertText.Margin = new Thickness(12, 0, 8, 0);
        _alertText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_alertText, 1);
        _alert.Children.Add(_alertText);
        var alertBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        alertBtns.Children.Add(Pill("再等 5 分钟", false, () => { TimerService.Snooze(); _alertUntil = DateTime.MinValue; Apply(); }));
        alertBtns.Children.Add(Pill("知道了", true, () => { TimerService.StopSound(); _alertUntil = DateTime.MinValue; Apply(); }));
        Grid.SetColumn(alertBtns, 2);
        _alert.Children.Add(alertBtns);
        _layers.Children.Add(_alert);

        // ================= expanded =================
        _expanded = new Grid { Margin = new Thickness(18, 10, 18, 8), VerticalAlignment = VerticalAlignment.Top };
        _expanded.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _expanded.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _expanded.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // header: tabs | clock | unread + settings
        var header = new Grid { Height = 28 };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _tabs = new[] { Tab("", "音乐", 0), Tab("", "系统", 1), Tab("", "控制", 2), Tab("", "计时", 3), Tab("", "通知", 4), Tab("", "置物架", 5), Tab("", "AI 额度", 6), Tab("", "邮件", 7) };
        foreach (var t in _tabs) tabs.Children.Add(t);
        header.Children.Add(tabs);
        _hClock = Text("", 12, FontWeights.SemiBold, 0.75);
        _hClock.HorizontalAlignment = HorizontalAlignment.Right;
        _hClock.VerticalAlignment = VerticalAlignment.Center; _hClock.Margin = new Thickness(0, 0, 8, 0);
        
        var hRight = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _hUnread = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
        hRight.Children.Add(_hUnread);
        hRight.Children.Add(_hClock);
        hRight.Children.Add(IconButton("", 26, 12, () => App.ShowSettings("island"), "灵动岛设置"));
        header.Children.Add(hRight);
        _expanded.Children.Add(header);

        _pages = new Grid { Margin = new Thickness(0, 10, 0, 0), ClipToBounds = false };
        Grid.SetRow(_pages, 1);
        _expanded.Children.Add(_pages);

        _dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(_dots, 2);
        _expanded.Children.Add(_dots);

        // --- music page ---
        var music = new Grid();
        var mBody = new StackPanel();
        _mBody = mBody;
        var mTop = new Grid();
        mTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mTop.ColumnDefinitions.Add(new ColumnDefinition());
        mTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _mCover = new Image { Stretch = Stretch.UniformToFill };
        mTop.Children.Add(Rounded(_mCover, 82, 16));
        var mInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 10, 0) };
        _mTitle = Text("", 17, FontWeights.SemiBold);
        _mTitle.FontFamily = Display;
        _mSub = Text("", 12.5, FontWeights.Normal, 0.6);
        _mLyric = Text("", 13, FontWeights.Normal, 1);
        _mLyric.Margin = new Thickness(0, 6, 0, 0);
        _mApp = Text("", 10.5, FontWeights.Normal, 0.4);
        _mApp.Margin = new Thickness(0, 4, 0, 0);
        mInfo.Children.Add(_mTitle);
        mInfo.Children.Add(_mSub);
        mInfo.Children.Add(_mLyric);
        mInfo.Children.Add(_mApp);
        Grid.SetColumn(mInfo, 1);
        mTop.Children.Add(mInfo);
        var mBars = Bars(6, 3.5, 3, 22, out _mBars);
        mBars.VerticalAlignment = VerticalAlignment.Top;
        mBars.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetColumn(mBars, 2);
        mTop.Children.Add(mBars);
        mBody.Children.Add(mTop);

        _mProgress = new Slider { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 12, 0, 0) };
        _mProgress.PreviewMouseLeftButtonUp += (_, _) => Seek();
        mBody.Children.Add(_mProgress);
        var times = new Grid { Margin = new Thickness(0, -2, 0, 0) };
        _mPos = Text("", 10.5, FontWeights.Normal, 0.5);
        _mDur = Text("", 10.5, FontWeights.Normal, 0.5);
        _mDur.HorizontalAlignment = HorizontalAlignment.Right;
        times.Children.Add(_mPos);
        times.Children.Add(_mDur);
        mBody.Children.Add(times);
        var ctl = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        ctl.Children.Add(IconButton("", 40, 17, MediaService.Previous, "上一首", round: false));
        var play = IconButton("", 46, 24, MediaService.PlayPause, "播放/暂停", round: false);
        _mPlay = (TextBlock)((Border)play.Content).Child;
        play.Margin = new Thickness(18, 0, 18, 0);
        ctl.Children.Add(play);
        ctl.Children.Add(IconButton("", 40, 17, MediaService.Next, "下一首", round: false));
        mBody.Children.Add(ctl);
        music.Children.Add(mBody);
        var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 14, 0, 14) };
        empty.Children.Add(new TextBlock { Text = "", FontFamily = Icons, FontSize = 26, Opacity = 0.35, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        var et = Text("没有正在播放的音乐", 13, FontWeights.Normal, 0.55);
        et.HorizontalAlignment = HorizontalAlignment.Center;
        et.Margin = new Thickness(0, 8, 0, 0);
        empty.Children.Add(et);
        _mEmpty = empty;
        music.Children.Add(empty);

        // --- system page ---
        var sys = new UniformGrid { Columns = 2, Rows = 2 };
        _sCpu = Big();
        _sSpark = new Polyline { StrokeThickness = 1.6, Height = 22, Stretch = Stretch.None, Margin = new Thickness(0, 4, 0, 0) };
        _sSpark.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        sys.Children.Add(Tile("CPU", _sCpu, _sSpark));
        _sMem = Big();
        _sMemSub = Text("", 11, FontWeights.Normal, 0.55);
        sys.Children.Add(Tile("内存", _sMem, _sMemSub));
        _sNet = Text("", 15, FontWeights.SemiBold);
        _sNetSub = Text("", 11, FontWeights.Normal, 0.55);
        sys.Children.Add(Tile("网络", _sNet, _sNetSub));
        _sBat = Big();
        _sBatSub = Text("", 11, FontWeights.Normal, 0.55);
        _sBatTile = Tile("电池", _sBat, _sBatSub);
        sys.Children.Add(_sBatTile);

        // --- controls page ---
        var ctlPage = new StackPanel();
        var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(-3, 0, -3, 10) };
        _wifiTile = ToggleTile("", "WLAN", out _wifiSub, RadioService.ToggleWifi);
        _btTile = ToggleTile("", "蓝牙", out _btSub, RadioService.ToggleBluetooth);
        var trayTile = ToggleTile("", "托盘图标", out var traySub, () => { _hover = false; _lineHover = false; Apply(); Taskbar.PeekTray(); });
        traySub.Text = "临时显示任务栏";
        tiles.Children.Add(_wifiTile);
        tiles.Children.Add(_btTile);
        tiles.Children.Add(trayTile);
        ctlPage.Children.Add(tiles);
        var volRow = SliderRow("", out _vol, out _volText, out _volGlyph);
        _vol.ValueChanged += (_, e) => { if (!_syncing) { AudioService.SetVolume((float)e.NewValue); _volText.Text = $"{e.NewValue:P0}"; } };
        ((Button)volRow.Children[0]).Click += (_, _) => AudioService.ToggleMute();
        ctlPage.Children.Add(volRow);
        var briRow = SliderRow("", out _bri, out _briText, out _);
        _bri.Maximum = 100;
        _bri.ValueChanged += (_, e) => { if (!_syncing) { BrightnessService.Set((int)e.NewValue); _briText.Text = $"{(int)e.NewValue}%"; } };
        briRow.Margin = new Thickness(0, 8, 0, 0);
        _briRow = briRow;
        ctlPage.Children.Add(briRow);
        var srcTitle = Text("正在发声", 11, FontWeights.SemiBold, 0.45);
        srcTitle.Margin = new Thickness(0, 14, 0, 6);
        ctlPage.Children.Add(srcTitle);
        _sources = new StackPanel { Orientation = Orientation.Horizontal, MinHeight = 22 };
        ctlPage.Children.Add(_sources);

        // --- timer page ---
        var timerPage = new StackPanel();
        var tTop = new Grid();
        tTop.ColumnDefinitions.Add(new ColumnDefinition());
        tTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tLeft = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _tTime = new TextBlock { FontSize = 38, FontWeight = FontWeights.Light, Foreground = Brushes.White, FontFamily = Display };
        tLeft.Children.Add(_tTime);
        var tBar = new Grid { Height = 4, Margin = new Thickness(0, 4, 18, 0) };
        tBar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) });
        _tBarFill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _tBarFill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        tBar.Children.Add(_tBarFill);
        tLeft.Children.Add(tBar);
        tTop.Children.Add(tLeft);
        var tCtl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tCtl.Children.Add(IconButton("", 34, 13, TimerService.Reset, "重置"));
        var startBtn = IconButton("", 44, 18, TimerService.StartOrPause, "开始/暂停");
        _tStartGlyph = (TextBlock)((Border)startBtn.Content).Child;
        startBtn.Margin = new Thickness(10, 0, 10, 0);
        tCtl.Children.Add(startBtn);
        tCtl.Children.Add(Pill("+1 分", false, TimerService.AddMinute));
        Grid.SetColumn(tCtl, 1);
        tTop.Children.Add(tCtl);
        timerPage.Children.Add(tTop);
        var presets = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var m in new[] { 1, 5, 10, 15, 25, 45, 60 })
        {
            int mm = m;
            presets.Children.Add(Pill($"{m} 分", false, () => TimerService.SetMinutes(mm)));
        }
        timerPage.Children.Add(presets);

        var alarmHead = new Grid { Margin = new Thickness(0, 12, 0, 6) };
        alarmHead.Children.Add(Text("闹钟", 11, FontWeights.SemiBold, 0.45));
        var adder = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _spinH = Spinner(() => _newHour, v => _newHour = (v + 24) % 24, 1);
        _spinM = Spinner(() => _newMinute, v => _newMinute = (v + 60) % 60, 5);
        adder.Children.Add(_spinH);
        adder.Children.Add(new TextBlock { Text = ":", Foreground = Brushes.White, FontSize = 15, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center });
        adder.Children.Add(_spinM);
        _dailyBtn = Pill("仅一次", false, () => { _newDaily = !_newDaily; UpdateDailyButton(); });
        _dailyBtn.Margin = new Thickness(8, 0, 4, 0);
        adder.Children.Add(_dailyBtn);
        adder.Children.Add(Pill("添加", true, AddAlarm));
        alarmHead.Children.Add(adder);
        timerPage.Children.Add(alarmHead);
        _alarmList = new StackPanel();
        timerPage.Children.Add(_alarmList);

        // --- notifications page ---
        var notePage = new StackPanel();
        var noteHead = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        noteHead.Children.Add(Text("最近的通知", 11, FontWeights.SemiBold, 0.45));
        var clear = Pill("清空", false, () => { NotificationService.ClearList(); RebuildNotifications(); });
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        noteHead.Children.Add(clear);
        notePage.Children.Add(noteHead);
        _noteList = new StackPanel();
        notePage.Children.Add(new ScrollViewer { Content = _noteList, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        _pageViews = new FrameworkElement[] { music, sys, ctlPage, timerPage, notePage, BuildShelfPage(root), BuildQuotaPage(), BuildMailPage() };
        foreach (var p in _pageViews) { p.Visibility = Visibility.Collapsed; _pages.Children.Add(p); }
        for (int i = 0; i < _pageViews.Length; i++)
            _dots.Children.Add(new Ellipse { Width = 5, Height = 5, Margin = new Thickness(3, 0, 3, 0), Fill = Brushes.White });

        _arrowL = IconButton("", 24, 10, () => Page(-1), "上一页");
        _arrowR = IconButton("", 24, 10, () => Page(+1), "下一页");
        foreach (var a in new[] { _arrowL, _arrowR })
        {
            a.VerticalAlignment = VerticalAlignment.Center;
            a.Opacity = 0;
            Grid.SetRowSpan(a, 3);
            _expanded.Children.Add(a);
        }
        _arrowL.HorizontalAlignment = HorizontalAlignment.Left;
        _arrowL.Margin = new Thickness(-22, 20, 0, 0);   // sit in the side padding, clear of content
        _arrowR.HorizontalAlignment = HorizontalAlignment.Right;
        _arrowR.Margin = new Thickness(0, 20, -22, 0);
        _layers.Children.Add(_expanded);

        foreach (var l in Layers) { l.Opacity = 0; l.Visibility = Visibility.Collapsed; }

        // ================= timers (before anything can call Apply) =================
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _poll.Tick += (_, _) => Poll();
        // Hidden during fullscreen games anyway: poll once a second until the game leaves.
        void OnGameMode(bool on) => _poll.Interval = TimeSpan.FromMilliseconds(on ? 1000 : 200);
        _onGameMode = OnGameMode;
        _frame = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(66) }; // ~15 fps bars
        _frame.Tick += (_, _) => Frame();

        _page = DebugPage is { } dp && int.TryParse(dp, out var pi) ? pi : Math.Clamp(S.LastPage, 0, _pageViews.Length - 1);
        ShowPage(_page, 0, true);

        // ================= interaction =================
        _surface.MouseEnter += (_, _) =>
        {
            if (_mode == Mode.Toast && ToastIsShot) { SetShelfTab(0); ShowPage(ShelfPage, 1); }
            else if (_mode == Mode.Toast && _toastNote is not null) ShowPage(4, 1);
            else if (_mode == Mode.Toast && _toastMail is not null) ShowPage(MailPage, 1);
            _hover = true; FadeArrows(1);
        };
        _surface.MouseLeave += (_, _) => { _hover = false; _leftSince = DateTime.Now; FadeArrows(0); };
        _surface.MouseMove += OnSurfaceMove;
        _surface.PreviewMouseLeftButtonDown += OnDown;
        _surface.PreviewMouseMove += OnDragMove;
        _surface.PreviewMouseLeftButtonUp += OnUp;
        _surface.MouseWheel += (_, e) => { if (_mode == Mode.Expanded) { Page(e.Delta < 0 ? 1 : -1); e.Handled = true; } };

        MediaService.Changed += OnMedia;
        MediaService.TrackChanged += OnTrack;
        LyricsService.Changed += OnLyrics;
        AudioService.VolumeChanged += OnVolume;
        AudioService.SourcesChanged += OnSources;
        BrightnessService.Changed += OnBrightness;
        AttentionService.Changed += OnAttention;
        SystemStats.Updated += OnStats;
        ThemeService.Changed += OnTheme;
        TimerService.Changed += OnTimer;
        TimerService.Ring += OnRing;
        PowerWatch.Event += OnPower;
        RadioService.Changed += OnRadio;
        GameMode.Changed += _onGameMode;
        NotificationService.Arrived += OnNotification;
        NotificationService.Changed += OnNotificationsChanged;
        Closed += (_, _) =>
        {
            NotificationService.Arrived -= OnNotification;
            NotificationService.Changed -= OnNotificationsChanged;
            GameMode.Changed -= _onGameMode;
            TimerService.Changed -= OnTimer;
            TimerService.Ring -= OnRing;
            PowerWatch.Event -= OnPower;
            RadioService.Changed -= OnRadio;
            _poll.Stop();
            _frame.Stop();
            MediaService.Changed -= OnMedia;
            MediaService.TrackChanged -= OnTrack;
            LyricsService.Changed -= OnLyrics;
            AudioService.VolumeChanged -= OnVolume;
            AudioService.SourcesChanged -= OnSources;
            BrightnessService.Changed -= OnBrightness;
            AttentionService.Changed -= OnAttention;
            SystemStats.Updated -= OnStats;
            ThemeService.Changed -= OnTheme;
            SystemStats.Need(this, false);
            AudioService.WantSpectrum(this, false);
        };
    }

    private IEnumerable<FrameworkElement> Layers => new FrameworkElement[] { _compact, _osd, _toast, _alert, _expanded };

    // =====================================================================
    // step 4: timer / alarms / power / radios
    // =====================================================================
    private static Button Pill(string text, bool primary, Action click)
    {
        var inner = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 4, 11, 5), Margin = new Thickness(0, 0, 6, 6),
            Background = primary ? new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Child = new TextBlock
            {
                Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = Ui,
                Foreground = primary ? new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x16)) : Brushes.White,
            },
        };
        var b = new Button { Content = inner, Cursor = Cursors.Hand, Focusable = false, Template = PlainTemplate() };
        b.Click += (_, _) => click();
        b.MouseEnter += (_, _) => inner.Opacity = 0.85;
        b.MouseLeave += (_, _) => inner.Opacity = 1;
        return b;
    }

    private static Border ToggleTile(string glyph, string title, out TextBlock sub, Action click)
    {
        var p = new StackPanel();
        var icon = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 15, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 5) };
        p.Children.Add(icon);
        p.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, FontFamily = Ui });
        sub = new TextBlock { FontSize = 10.5, Opacity = 0.6, Foreground = Brushes.White, FontFamily = Ui, TextTrimming = TextTrimming.CharacterEllipsis };
        p.Children.Add(sub);
        var tile = new Border
        {
            Margin = new Thickness(3), Padding = new Thickness(12, 9, 10, 9), CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), Child = p,
        };
        var btn = new Button { Content = tile, Template = PlainTemplate(), Focusable = false };
        btn.Click += (_, _) => click();
        var wrapper = new Border { Child = btn };
        return wrapper;
    }

    private static void SetTileOn(Border wrapper, bool on)
    {
        if (((Button)wrapper.Child).Content is not Border tile) return;
        if (on) tile.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        else tile.Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>A two-digit number you change with the mouse wheel or by clicking (left +, right −).</summary>
    private static TextBlock Spinner(Func<int> get, Action<int> set, int step)
    {
        var t = new TextBlock
        {
            Text = get().ToString("00"), FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = Display, Foreground = Brushes.White,
            Cursor = Cursors.SizeNS, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
            ToolTip = "滚轮调整；左键 +，右键 −", Padding = new Thickness(3, 0, 3, 0),
        };
        void Change(int d) { set(get() + d); t.Text = get().ToString("00"); }
        t.MouseWheel += (_, e) => { Change(e.Delta > 0 ? 1 : -1); e.Handled = true; };
        t.MouseLeftButtonUp += (_, e) => { Change(step); e.Handled = true; };
        t.MouseRightButtonUp += (_, e) => { Change(-step); e.Handled = true; };
        return t;
    }

    private void UpdateDailyButton()
    {
        if (((Border)_dailyBtn.Content).Child is TextBlock tb) tb.Text = _newDaily ? "每天" : "仅一次";
    }

    private void AddAlarm()
    {
        var list = S.Alarms;
        if (!list.Any(a => a.Hour == _newHour && a.Minute == _newMinute))
            list.Add(new AlarmItem { Hour = _newHour, Minute = _newMinute, Daily = _newDaily, Enabled = true });
        list.Sort((a, b) => (a.Hour * 60 + a.Minute).CompareTo(b.Hour * 60 + b.Minute));
        SettingsStore.Touch();
        RebuildAlarms();
    }

    private void RebuildAlarms()
    {
        _alarmList.Children.Clear();
        foreach (var a in S.Alarms)
        {
            var item = a;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = a.Enabled ? 1 : 0.4 };
            label.Children.Add(new TextBlock { Text = $"{a.Hour:00}:{a.Minute:00}", FontSize = 16, FontFamily = Display, Foreground = Brushes.White });
            label.Children.Add(new TextBlock { Text = a.Daily ? "  每天" : "  仅一次", FontSize = 11, Opacity = 0.55, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(label);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(Pill(a.Enabled ? "已开" : "已关", a.Enabled, () => { item.Enabled = !item.Enabled; SettingsStore.Touch(); RebuildAlarms(); }));
            btns.Children.Add(IconButton("", 24, 10, () => { S.Alarms.Remove(item); SettingsStore.Touch(); RebuildAlarms(); }, "删除"));
            row.Children.Add(btns);
            _alarmList.Children.Add(row);
        }
        if (S.Alarms.Count == 0) _alarmList.Children.Add(Text("还没有闹钟", 11.5, FontWeights.Normal, 0.4));
        _layoutDirty = true;
    }

    private void OnTimer()
    {
        _tTime.Text = TimerService.Fmt(TimerService.Remaining);
        _tBarFill.Width = Math.Max(0, (ExpandedW - 36 - 160) * TimerService.Progress);
        _tStartGlyph.Text = TimerService.Running ? "" : "";
        _cTimer.Text = " " + TimerService.Fmt(TimerService.Remaining);
        bool showTimer = TimerService.Active;
        if (_cTimer.Visibility != (showTimer ? Visibility.Visible : Visibility.Collapsed))
        {
            _cTimer.Visibility = showTimer ? Visibility.Visible : Visibility.Collapsed;
            UpdateCompact();
        }
        else if (showTimer && _mode == Mode.Compact && TimerService.Remaining.Seconds == 59) _layoutDirty = true; // width may change at minute rollover
    }

    // ---------- notifications ----------
    private void OnNotification(NotificationItem n)
    {
        if (!S.Notifications || _mode == Mode.Expanded) return;
        _toastGlyph.Text = "";
        _toastArt.Source = n.Icon;
        _toastBarsPanel.Visibility = Visibility.Collapsed;
        if (S.NotificationContent && OtpExtractor.Extract(n.Title, n.Body) is { } code)
        {
            StyleToastTitle(true);
            _toastTitle.Text = code;
            _toastSub.Text = $"验证码 · {n.AppName} · 移上来复制";
        }
        else if (S.NotificationContent)
        {
            StyleToastTitle(false);
            _toastTitle.Text = n.Title.Length > 0 ? n.Title : n.AppName;
            _toastSub.Text = n.Body.Length > 0 ? n.Body : n.AppName;
        }
        else
        {
            // Like WeChat/QQ on a phone's lock screen: only who has a message, never what it says.
            int count = NotificationService.Items.TakeWhile(x => x.AppId == n.AppId).Count();
            StyleToastTitle(false);
            _toastTitle.Text = n.AppName;
            _toastSub.Text = count > 1 ? $"{count} 条新消息" : "新消息";
        }
        _toastNote = n;
        _toastUntil = DateTime.Now.AddSeconds(5);
        MarkMessageToast(n.AppName);
        Apply();
    }

    private void OnNotificationsChanged()
    {
        int unseen = S.Notifications ? NotificationService.Unseen : 0;
        _cNotify.Text = unseen > 0 ? $" {unseen}" : "";
        _cNotify.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI");
        _cNotify.Visibility = unseen > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_page == 4) RebuildNotifications();
        UpdateCompact();
    }

    private void RebuildNotifications()
    {
        _noteList.Children.Clear();
        if (!S.NotificationContent)
        {
            // Content hidden: one row per app — icon, name, how many, when the latest came.
            foreach (var g in NotificationService.Items.GroupBy(x => x.AppId).Take(20))
            {
                var latest = g.First();
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand, Background = Brushes.Transparent, Tag = "nodrag" };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new Image { Source = latest.Icon, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
                var info = new StackPanel();
                info.Children.Add(Text(latest.AppName, 12.5, FontWeights.SemiBold));
                int c = g.Count();
                info.Children.Add(Text($"{(c > 1 ? $"{c} 条新消息" : "新消息")} · {Ago(latest.Time)}", 10.5, FontWeights.Normal, 0.45));
                Grid.SetColumn(info, 1);
                row.Children.Add(info);
                row.MouseLeftButtonUp += (_, e) => { NotificationService.Open(latest); _hover = false; Apply(); e.Handled = true; };
                _noteList.Children.Add(row);
            }
            if (_noteList.Children.Count == 0) _noteList.Children.Add(Text("还没有新通知", 11.5, FontWeights.Normal, 0.4));
            _layoutDirty = true;
            return;
        }
        foreach (var n in NotificationService.Items.Take(20)) _noteList.Children.Add(NoteRow(n));
        if (_noteList.Children.Count == 0) _noteList.Children.Add(Text("还没有新通知", 11.5, FontWeights.Normal, 0.4));
        _layoutDirty = true;
    }

    /// <summary>One notification as a card: icon, app · time, title, body; code pill + 复制 on the right; click opens the app.</summary>
    private FrameworkElement NoteRow(NotificationItem n)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand, Background = Brushes.Transparent, Tag = "nodrag", ToolTip = $"打开 {n.AppName}" };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var face = new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)) };
        Grid.SetColumnSpan(face, 3);
        row.Children.Add(face);
        row.Children.Add(new Image { Source = n.Icon, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 9, 0, 0) });

        var info = new StackPanel { Margin = new Thickness(0, 6, 8, 6) };
        info.Children.Add(Text($"{n.AppName} · {Ago(n.Time)}", 10, FontWeights.Normal, 0.45));
        if (n.Title.Length > 0) info.Children.Add(Text(n.Title, 12.5, FontWeights.SemiBold));
        if (n.Body.Length > 0)
        {
            var b = Text(n.Body, 11.5, FontWeights.Normal, 0.72);
            b.TextWrapping = TextWrapping.Wrap;
            b.TextTrimming = TextTrimming.CharacterEllipsis;
            b.MaxHeight = 32;
            info.Children.Add(b);
        }
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Button CopyPill(string label, bool primary, string what, string tip)
        {
            var chip = Pill(label, primary, () => { });
            chip.ToolTip = tip;
            chip.Margin = new Thickness(4, 0, 0, 0);
            chip.Click += (_, e) =>
            {
                try { Clipboard.SetText(what); } catch { }
                if (chip.Content is Border { Child: TextBlock t }) t.Text = "已复制";
                e.Handled = true;
            };
            return chip;
        }
        var code = OtpExtractor.Extract(n.Title, n.Body);
        if (code is not null) btns.Children.Add(CopyPill(code, true, code, "复制验证码"));
        var all = string.Join("\n", new[] { n.Title, n.Body }.Where(s => s.Length > 0));
        var copy = CopyPill("复制", false, all, "复制通知内容");
        copy.Opacity = 0;
        btns.Children.Add(copy);
        Grid.SetColumn(btns, 2);
        row.Children.Add(btns);

        row.MouseEnter += (_, _) => { face.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)); copy.Opacity = 1; };
        row.MouseLeave += (_, _) => { face.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)); copy.Opacity = 0; };
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindButton(d) is not null) return;
            NotificationService.Open(n); _hover = false; Apply(); e.Handled = true;
        };
        return row;
    }

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? "刚刚" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} 分钟前" : t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("M月d日 HH:mm");
    }

    private void OnRing(string text)
    {
        _alertText.Text = text;
        _alertUntil = DateTime.Now.AddSeconds(60);
        Apply();
    }

    private void OnPower(PowerWatch.Kind kind, int pct)
    {
        if (!S.PowerToasts) return;
        _toastNote = null;
        _toastArt.Source = null;
        _toastBarsPanel.Visibility = Visibility.Collapsed;
        StyleToastTitle(false);
        (_toastGlyph.Text, _toastTitle.Text, _toastSub.Text) = kind switch
        {
            PowerWatch.Kind.Plugged => ("", "已接通电源", $"电量 {pct}% · 正在充电"),
            PowerWatch.Kind.Unplugged => ("", "已断开电源", $"电量 {pct}%"),
            PowerWatch.Kind.Low => ("", "电量较低", $"剩余 {pct}%，记得充电"),
            _ => ("", "电量严重不足", $"剩余 {pct}%，请尽快接通电源"),
        };
        _toastGlyph.Foreground = kind is PowerWatch.Kind.Low or PowerWatch.Kind.Critical
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x5B)) : (Brush)FindResource("AccentBrush");
        _toastUntil = DateTime.Now.AddSeconds(3);
        Apply();
    }

    private void OnRadio()
    {
        SetTileOn(_wifiTile, RadioService.WifiOn);
        SetTileOn(_btTile, RadioService.BluetoothOn);
        _wifiTile.Opacity = RadioService.HasWifi ? 1 : 0.4;
        _btTile.Opacity = RadioService.HasBluetooth ? 1 : 0.4;
        var link = RadioService.WifiOn ? RadioService.CurrentWifi() : null;
        _wifiSub.Text = !RadioService.HasWifi ? "不可用" : !RadioService.WifiOn ? "已关闭" : link is null ? "未连接" : $"{link.Ssid} · {link.SignalPercent}%";
        _btSub.Text = !RadioService.HasBluetooth ? "不可用" : RadioService.BluetoothOn ? "已打开" : "已关闭";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, Native.WS_EX_APPWINDOW);
        HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(Native.MA_NOACTIVATE); }
            return IntPtr.Zero;
        });
        ApplySkin();
        OnMedia();
        OnSources();
        OnAttention(null);
        _cTimer.Visibility = Visibility.Collapsed;
        OnTimer();
        RebuildAlarms();
        OnRadio();
        UpdateCompact();
        _poll.Start();
        Apply(true);
    }

    // =====================================================================
    // small builders
    // =====================================================================
    private static TextBlock Text(string t, double size, FontWeight w, double opacity = 1) => new()
    {
        Text = t, FontSize = size, FontWeight = w, Opacity = opacity, Foreground = Brushes.White,
        TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = Ui,
    };

    private static TextBlock Big() => new() { FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, FontFamily = Display };

    private static TextBlock Glyph(string g, double size) => new()
    {
        Text = g, FontFamily = Icons, FontSize = size, Foreground = Brushes.White,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static Border Rounded(Image img, double size, double radius)
    {
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return new Border
        {
            Width = size, Height = size,
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            Child = img,
            Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius),
        };
    }

    private static StackPanel Bars(int count, double width, double gap, double height, out Rectangle[] bars)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Height = height, VerticalAlignment = VerticalAlignment.Center };
        bars = new Rectangle[count];
        for (int i = 0; i < count; i++)
        {
            var r = new Rectangle
            {
                Width = width, Height = 3, RadiusX = width / 2, RadiusY = width / 2,
                Margin = new Thickness(i == 0 ? 0 : gap, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            r.SetResourceReference(Shape.FillProperty, "AccentBrush");
            bars[i] = r;
            panel.Children.Add(r);
        }
        return panel;
    }

    private static ControlTemplate PlainTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        t.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        return t;
    }

    private static Button IconButton(string glyph, double size, double glyphSize, Action click, string tip, bool round = true)
    {
        var inner = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = round ? new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
            Child = new TextBlock
            {
                Text = glyph, FontFamily = Icons, FontSize = glyphSize, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var b = new Button { Content = inner, Cursor = Cursors.Hand, Focusable = false, Template = PlainTemplate(), ToolTip = tip };
        b.Click += (_, _) => click();
        b.MouseEnter += (_, _) => inner.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        b.MouseLeave += (_, _) => inner.Background = round ? new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
        return b;
    }

    private Button Tab(string glyph, string tip, int index)
    {
        var b = IconButton(glyph, 26, 12.5, () => ShowPage(index, index > _page ? 1 : -1), tip, round: false);
        b.Margin = new Thickness(0, 0, 2, 0);
        return b;
    }

    private static Border Tile(string title, FrameworkElement value, FrameworkElement sub)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = title, FontSize = 11, Opacity = 0.5, Foreground = Brushes.White, FontFamily = Ui });
        p.Children.Add(value);
        p.Children.Add(sub);
        return new Border
        {
            Margin = new Thickness(3), Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), Child = p,
        };
    }

    private static Grid SliderRow(string glyph, out Slider slider, out TextBlock value, out TextBlock glyphText)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        var btn = new Button { Template = PlainTemplate(), Focusable = false, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left };
        glyphText = Glyph(glyph, 15);
        glyphText.Width = 26;
        btn.Content = glyphText;
        row.Children.Add(btn);
        slider = new Slider { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        value = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.8, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
    }

    // =====================================================================
    // skin
    // =====================================================================
    private void OnTheme() => Dispatcher.BeginInvoke(ApplySkin);

    public void ApplySkin()
    { Aether.Core.Diag.Hit("Island.ApplySkin");
        _surface.Notch = IsNotch;
        bool cover = S.Skin == "cover" && _coverColor is not null && HasMedia;
        if (cover)
        {
            var c = _coverColor!.Value;
            _surface.Fill = Frozen(new LinearGradientBrush(
                ThemeService.Mix(Color.FromRgb(0x08, 0x08, 0x0C), c, 0.34),
                ThemeService.Mix(Color.FromRgb(0x05, 0x05, 0x07), c, 0.14), 90));
            UpdateGlow(0.25);
        }
        else
        {
            _surface.Fill = IsNotch ? Brushes.Black : Frozen(new SolidColorBrush(Color.FromArgb(0xF4, 0x0B, 0x0C, 0x11)));
            _surface.Overlay = null;
        }
        _surface.Stroke = IsNotch ? null : new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
        _shape = default; // force re-layout with the new shape
        _layoutDirty = true;
        if (IsLoaded) Apply(true);
    }

    private void UpdateGlow(double x)
    {
        if (_coverColor is not { } c || S.Skin != "cover") return;
        var g = new RadialGradientBrush(Color.FromArgb(0x70, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B))
        {
            Center = new Point(x, 0.1), GradientOrigin = new Point(x, 0.1), RadiusX = 0.55, RadiusY = 1.1,
        };
        g.Freeze();
        _surface.Overlay = g;
    }

    private void OnSurfaceMove(object sender, MouseEventArgs e)
    {
        if (_mode == Mode.Expanded && S.Skin == "cover" && _surface.ActualWidth > 0)
            UpdateGlow(Math.Clamp(e.GetPosition(_surface).X / _surface.ActualWidth, 0, 1));
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // =====================================================================
    // data → visuals
    // =====================================================================
    private static bool HasMedia => MediaService.Current is { Title.Length: > 0 };
    private static bool Playing => MediaService.Current is { Playing: true, Title.Length: > 0 };

    private void OnMedia() => Dispatcher.BeginInvoke(() =>
    {
        var m = MediaService.Current;
        var art = m?.Cover ?? (m is null ? null : MediaAppIcon(m.SourceApp));
        _cCover.Source = _mCover.Source = art;
        _layoutDirty = true;
        _mBody.Visibility = HasMedia ? Visibility.Visible : Visibility.Collapsed;
        _mEmpty.Visibility = HasMedia ? Visibility.Collapsed : Visibility.Visible;
        if (m is not null && HasMedia)
        {
            _mTitle.Text = m.Title;
            _mSub.Text = string.Join(" · ", new[] { m.Artist, m.Album }.Where(s => !string.IsNullOrWhiteSpace(s)));
            _mApp.Text = MediaAppName(m.SourceApp);
            _mPlay.Text = m.Playing ? "" : "";
        }
        var newColor = m?.Cover is BitmapSource bs ? WallpaperColor.FromImage(bs) : null;
        if (newColor != _coverColor) { _coverColor = newColor; ApplySkin(); }
        LyricsService.OnTrack(HasMedia ? m : null);
        UpdateProgress();
        UpdateCompact();
        Apply();
    });

    private void OnTrack() => Dispatcher.BeginInvoke(() =>
    {
        if (!S.TrackToast || _mode == Mode.Expanded || MediaService.Current is not { } m) return;
        _toastGlyph.Text = "";
        _toastNote = null;
        _toastArt.Source = _mCover.Source;
        _toastArtBox.Visibility = Visibility.Visible;
        StyleToastTitle(false);
        _toastTitle.Text = m.Title;
        _toastSub.Text = m.Artist;
        _toastBarsPanel.Visibility = Visibility.Visible;
        _toastUntil = DateTime.Now.AddSeconds(3.2);
        Apply();
    });

    private void OnLyrics() => Dispatcher.BeginInvoke(() => { _lastLyric = "\u0000"; UpdateLyric(); UpdateCompact(); });

    private void OnAttention(Attention? fresh) => Dispatcher.BeginInvoke(() =>
    {
        var items = S.ShowUnread ? AttentionService.Current : Array.Empty<Attention>();
        _cUnread.Children.Clear();
        _hUnread.Children.Clear();
        foreach (var a in items.Take(4))
        {
            _cUnread.Children.Add(UnreadIcon(a, 17));
            _hUnread.Children.Add(UnreadIcon(a, 18));
        }
        if (fresh is not null && S.ShowUnread && _mode != Mode.Expanded)
        {
            _toastGlyph.Text = "";
            _toastNote = null;
            _toastArt.Source = ShellIcons.Get(fresh.ExePath, 96);
            StyleToastTitle(false);
            _toastTitle.Text = fresh.Name;
            _toastSub.Text = "有新消息";
            _toastBarsPanel.Visibility = Visibility.Collapsed;
            _toastUntil = DateTime.Now.AddSeconds(3);
            MarkMessageToast(fresh.Name);
        }
        UpdateCompact();
        Apply();
    });

    private static FrameworkElement UnreadIcon(Attention a, double size)
    {
        var g = new Grid { Width = size + 4, Height = size + 2, Margin = new Thickness(2, 0, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = $"{a.Name}：有新消息" };
        g.Children.Add(new Image { Source = ShellIcons.Get(a.ExePath, 48), Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom });
        g.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)), Stroke = Brushes.Black, StrokeThickness = 1.2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top });
        g.MouseLeftButtonUp += (_, e) => { AttentionService.Open(a); e.Handled = true; };
        return g;
    }

    private void OnVolume(float v, bool muted)
    {
        SyncSliders();
        if (!S.VolumeOsd || _mode == Mode.Expanded) return;
        _osdGlyph.Text = muted || v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";
        ShowOsd(muted ? 0 : v, muted ? "静音" : $"{v * 100:0}");
    }

    private void OnBrightness(int level)
    {
        SyncSliders();
        if (!S.BrightnessOsd || _mode == Mode.Expanded) return;
        _osdGlyph.Text = "";
        ShowOsd(level / 100.0, $"{level}");
    }

    private void ShowOsd(double fraction, string text)
    {
        _osdText.Text = text;
        _osdFill.Width = Math.Max(5, 210 * Math.Clamp(fraction, 0, 1));
        _osdUntil = DateTime.Now.AddSeconds(1.6);
        Apply();
    }

    private void OnSources()
    {
        _sources.Children.Clear();
        foreach (var src in AudioService.Sources.Take(5))
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            chip.Children.Add(new Image { Source = ShellIcons.Get(src.ExePath, 48), Width = 18, Height = 18 });
            var t = Text(src.Name, 11.5, FontWeights.Normal, 0.75);
            t.MaxWidth = 100;
            t.Margin = new Thickness(6, 0, 0, 0);
            t.VerticalAlignment = VerticalAlignment.Center;
            chip.Children.Add(t);
            _sources.Children.Add(chip);
        }
        if (_sources.Children.Count == 0) _sources.Children.Add(Text("没有程序在发声", 11.5, FontWeights.Normal, 0.4));
        _layoutDirty = true;
        UpdateCompact();
        Apply();
    }

    private void OnStats()
    {
        UpdateCompact();
        if (_mode != Mode.Expanded || _page != 1) return;
        _sCpu.Text = $"{SystemStats.Cpu:0}%";
        var pts = new PointCollection();
        var h = SystemStats.CpuHistory;
        for (int i = 0; i < h.Length; i++) pts.Add(new Point(i * 4.2, 22 - h[i] / 100 * 22));
        _sSpark.Points = pts;
        _sMem.Text = $"{SystemStats.Memory:0}%";
        _sMemSub.Text = $"{SystemStats.MemoryUsedGb:0.0} / {SystemStats.MemoryTotalGb:0} GB";
        _sNet.Text = $"↓ {SystemStats.Speed(SystemStats.DownBps)}/s";
        _sNetSub.Text = $"↑ {SystemStats.Speed(SystemStats.UpBps)}/s";
        _sBatTile.Visibility = SystemStats.Battery is null ? Visibility.Hidden : Visibility.Visible;
        _sBat.Text = SystemStats.Battery is { } b ? $"{b}%" : "";
        _sBatSub.Text = SystemStats.Charging ? "正在充电" : "使用电池";
    }

    private void SyncSliders()
    {
        _syncing = true;
        _vol.Value = AudioService.Volume;
        _volText.Text = AudioService.Muted ? "静音" : $"{AudioService.Volume:P0}";
        _volGlyph.Text = AudioService.Muted ? "" : "";
        _briRow.Visibility = BrightnessService.Available ? Visibility.Visible : Visibility.Collapsed;
        _bri.Value = BrightnessService.Level;
        _briText.Text = $"{BrightnessService.Level}%";
        _syncing = false;
    }

    private void UpdateProgress()
    {
        var m = MediaService.Current;
        if (m is null || m.Duration <= TimeSpan.Zero)
        {
            _mProgress.Visibility = Visibility.Hidden;
            _mPos.Text = _mDur.Text = "";
            return;
        }
        _mProgress.Visibility = Visibility.Visible;
        if (_mProgress.IsMouseCaptureWithin) return;
        var pos = m.PositionNow;
        _mProgress.Value = pos.TotalSeconds / m.Duration.TotalSeconds;
        _mPos.Text = Fmt(pos);
        _mDur.Text = "-" + Fmt(m.Duration - pos);
    }

    private void Seek()
    {
        var m = MediaService.Current;
        if (m is null || m.Duration <= TimeSpan.Zero || !m.CanSeek) return;
        MediaService.Seek(TimeSpan.FromSeconds(_mProgress.Value * m.Duration.TotalSeconds));
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private static string? CurrentLyric() =>
        Playing && S.ShowLyrics && MediaService.Current is { PositionKnown: true } m ? LyricsService.LineAt(m.PositionNow) : null;

    private void UpdateLyric()
    {
        var line = CurrentLyric() ?? "";
        if (line == _lastLyric) return;
        _lastLyric = line;
        _layoutDirty = true;
        _mLyric.Text = line;
        _mLyric.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_mode == Mode.Compact) UpdateCompact();
    }

    /// <summary>Decide what the collapsed island shows (time / lyric or title / stats / unread).</summary>
    private void UpdateCompact()
    { Aether.Core.Diag.Hit("Island.UpdateCompact");
        bool playing = Playing;
        _cCoverBox.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        _cBarsPanel.Visibility = playing && S.ShowSpectrum ? Visibility.Visible : Visibility.Collapsed;
        _cUnread.Visibility = S.ShowUnread && _cUnread.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool stats = S.ShowStats && !playing;
        bool sysPage = _mode == Mode.Expanded && _page == 1;
        SystemStats.WantNetwork = sysPage || (stats && S.StatNetwork);
        // Hidden during fullscreen games: no point sampling then.
        SystemStats.Need(this, !GameMode.Active && (stats || sysPage));
        string? quota = S.Quota && S.QuotaInCompact && !playing ? QuotaCompactText() : null;
        _cStats.Visibility = stats || quota is not null ? Visibility.Visible : Visibility.Collapsed;
        if (!stats && quota is not null) _cStats.Text = quota;
        if (stats)
        {
            // Figure-space padding + tabular digits: the text keeps the same width as numbers
            // change, so the island doesn't resize (and re-animate) every second.
            static string Pad(string s, int n) => s.PadLeft(n, ' ');
            var parts = new List<string>();
            if (S.StatCpu) parts.Add($"CPU {Pad($"{SystemStats.Cpu:0}", 3)}%");
            if (S.StatMemory) parts.Add($"内存 {Pad($"{SystemStats.Memory:0}", 2)}%");
            if (S.StatNetwork) parts.Add($"↓{Pad(SystemStats.Speed(SystemStats.DownBps), 5)}");
            if (S.StatBattery && SystemStats.Battery is { } b) parts.Add((SystemStats.Charging ? "⚡" : "") + $"{Pad(b.ToString(), 3)}%");
            if (quota is not null) parts.Add(quota);
            _cStats.Text = string.Join("  ", parts);
        }

        if (playing)
        {
            var lyric = CurrentLyric();
            _cText.Text = string.IsNullOrEmpty(lyric) ? MediaService.Current!.Title : lyric;
            _cText.Visibility = Visibility.Visible;
            _cTimeText.Visibility = _cTimeSeg.Visibility = Visibility.Collapsed;
        }
        else
        {
            _cText.Visibility = Visibility.Collapsed;
            var now = DateTime.Now.ToString("HH:mm");
            bool seg = S.TimeStyle == 1;
            _cTimeSeg.Visibility = S.ShowTime && seg ? Visibility.Visible : Visibility.Collapsed;
            _cTimeText.Visibility = S.ShowTime && !seg ? Visibility.Visible : Visibility.Collapsed;
            _cTimeSeg.Text = now;
            _cTimeSeg.Color = ThemeService.Accent;
            _cTimeText.Text = now;
            if (S.TimeStyle == 2)
            {
                _cTimeText.FontFamily = new FontFamily("Georgia");
                _cTimeText.FontStyle = FontStyles.Italic;
                _cTimeText.FontSize = 15;
                _cTimeText.FontWeight = FontWeights.Normal;
                _cTimeText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            }
            else
            {
                _cTimeText.FontFamily = Display;
                _cTimeText.FontStyle = FontStyles.Normal;
                _cTimeText.FontSize = 13.5;
                _cTimeText.FontWeight = FontWeights.SemiBold;
                _cTimeText.Foreground = Brushes.White;
            }
        }
        // Stats text is padded to a fixed width, so a new reading alone needn't re-measure
        // the island; only re-layout when what's shown (or its length) changed.
        var shape = $"{playing}|{_cCoverBox.Visibility}|{_cBarsPanel.Visibility}|{_cUnread.Visibility}|{_cUnread.Children.Count}|{S.TimeStyle}|{S.Shape}|{_mode}|{_cStats.Visibility}|{_cStats.Text.Length}|{_cText.Text}|{_cTimeText.Visibility}|{_cTimeSeg.Visibility}|{_cTimeText.Text}|{_cNotify.Visibility}|{_cNotify.Text}|{_cTimer.Visibility}";
        if (shape == _compactShape) return;
        _compactShape = shape;
        _layoutDirty = true;
        if (_mode is Mode.Compact or Mode.Idle) Apply();
    }
    private string _compactShape = "";

    private static readonly Dictionary<string, ImageSource?> AppIconCache = new(StringComparer.OrdinalIgnoreCase);

    private static ImageSource? MediaAppIcon(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return null;
        if (AppIconCache.TryGetValue(appId, out var hit)) return hit;
        ImageSource? img = null;
        if (appId.Contains('!')) img = ShellIcons.Get("shell:AppsFolder\\" + appId, 96);
        if (img is null)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(appId);
            try
            {
                var p = Process.GetProcessesByName(name).FirstOrDefault();
                if (p?.MainModule?.FileName is { } exe) img = ShellIcons.Get(exe, 96);
            }
            catch { }
        }
        AppIconCache[appId] = img;
        return img;
    }

    private static string MediaAppName(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return "";
        var id = appId.Contains('!') ? appId[(appId.IndexOf('!') + 1)..] : System.IO.Path.GetFileNameWithoutExtension(appId);
        return id.ToLowerInvariant() switch
        {
            "chrome" => "Google Chrome",
            "msedge" => "Microsoft Edge",
            "cloudmusic" => "网易云音乐",
            "qqmusic" => "QQ 音乐",
            "spotify" => "Spotify",
            _ => id,
        };
    }

    // =====================================================================
    // pages
    // =====================================================================
    private void Page(int delta) => ShowPage((_page + delta + _pageViews.Length) % _pageViews.Length, delta);

    private void ShowPage(int index, int direction, bool instant = false)
    {
        var old = _pageViews[_page];
        _page = index;
        S.LastPage = index;
        for (int i = 0; i < _pageViews.Length; i++)
        {
            ((Ellipse)_dots.Children[i]).Opacity = i == index ? 0.95 : 0.3;
            ((Ellipse)_dots.Children[i]).Width = i == index ? 12 : 5;
            var tabFace = (Border)_tabs[i].Content;
            tabFace.Opacity = i == index ? 1 : 0.45;
            if (tabFace.Child is TextBlock tg)
            {
                if (i == index) tg.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                else tg.Foreground = Brushes.White;
            }
        }
        var view = _pageViews[index];
        if (old != view && !instant)
        {
            var o = old;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
            fade.Completed += (_, _) => { if (_pageViews[_page] != o) o.Visibility = Visibility.Collapsed; };
            o.BeginAnimation(OpacityProperty, fade);
        }
        else if (old != view) old.Visibility = Visibility.Collapsed;

        view.Visibility = Visibility.Visible;
        var tt = new TranslateTransform(instant ? 0 : 18 * Math.Sign(direction == 0 ? 1 : direction), 0);
        view.RenderTransform = tt;
        view.BeginAnimation(OpacityProperty, new DoubleAnimation(instant ? 1 : 0, 1, TimeSpan.FromMilliseconds(instant ? 0 : 260)));
        if (!instant) tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } });

        _layoutDirty = true;
        if (instant) return; // constructor: the window isn't up yet

        if (index == 2) { SyncSliders(); OnSources(); OnRadio(); }
        if (index == 3) { OnTimer(); RebuildAlarms(); }
        if (index == 4) { RebuildNotifications(); NotificationService.MarkSeen(); }
        if (index == ShelfPage) RebuildShelf();
        if (index == QuotaPage) { QuotaService.RefreshSoon(); RebuildQuota(); }
        if (index == MailPage) { MailService.CheckSoon(); RebuildMail(); MailService.MarkSeen(); }
        if (index == 1) { SystemStats.Need(this, true); OnStats(); }
        else UpdateCompact();
        if (_mode == Mode.Expanded) Resize(Mode.Expanded, false);
        UpdateSpectrumNeed();
        SettingsStore.Touch();
    }

    private void FadeArrows(double to)
    {
        foreach (var a in new[] { _arrowL, _arrowR }) a.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)));
    }

    // =====================================================================
    // mode + layout
    // =====================================================================
    private Mode Decide()
    {
        if (DebugMode is { Length: > 0 } && Enum.TryParse<Mode>(DebugMode, true, out var forced)) return forced;
        var now = DateTime.Now;
        if (now < _alertUntil && !_fullscreen) return Mode.Alert; // ringing: needs its buttons
        if (_fullscreen && !_hover) return Mode.Hidden;
        if (_hover || _lineHover || _pressed || _dragHot) return Mode.Expanded;
        if (now < _osdUntil) return Mode.Osd;
        if (now < _toastUntil) return Mode.Toast;
        if (_maximized && S.ThinLineWhenMaximized) return Mode.Line;
        bool richer = Playing || TimerService.Active || (S.ShowUnread && AttentionService.Current.Count > 0) || S.ShowStats || S.ShowTime || NotificationService.Unseen > 0
            || (S.Quota && S.QuotaInCompact && QuotaCompactText() is not null);
        return richer ? Mode.Compact : Mode.Idle;
    }

    private (double W, double H, double R) ShapeOf(Mode m)
    {
        bool notch = IsNotch;
        return m switch
        {
            Mode.Hidden => (110, 4, 2),
            Mode.Line => notch ? (130, 5, 2.5) : (110, 5, 2.5),
            Mode.Idle => notch ? (150, 30, 13) : (118, 30, 15),
            Mode.Compact => (CompactWidth(), 32, notch ? 14 : 16),
            Mode.Osd => (320, notch ? 38 : 40, notch ? 18 : 20),
            Mode.Toast => (400, 72, notch ? 26 : 24),
            Mode.Alert => (440, 50, notch ? 22 : 25),
            _ => (ExpandedW, ExpandedHeight(), notch ? 30 : 28),
        };
    }

    private double CompactWidth()
    {
        _compact.Visibility = Visibility.Visible;
        _compact.Measure(new Size(double.PositiveInfinity, 32));
        // Snap to 12 px steps so small text changes (a lyric, a digit) don't trigger a resize.
        double w = Math.Ceiling((_compact.DesiredSize.Width + 4) / 12) * 12;
        return Math.Clamp(w, IsNotch ? 150 : 120, 460);
    }

    private double ExpandedHeight()
    {
        _expanded.Visibility = Visibility.Visible;
        _expanded.Measure(new Size(ExpandedW - 2, double.PositiveInfinity));
        return Math.Ceiling(_expanded.DesiredSize.Height) + 4;
    }

    private void Apply(bool instant = false)
    { Aether.Core.Diag.Hit("Island.Apply");
        var next = Decide();
        if (next == _mode && !instant)
        {
            // Only re-measure when content changed — measuring every tick kept the CPU busy.
            if (_layoutDirty && next is Mode.Compact or Mode.Expanded) { _layoutDirty = false; Resize(next, false); }
            UpdateGlowScene(); // cheap: only sets targets; the timer runs while something glows
            return;
        }
        _mode = next;
        if (next == Mode.Expanded) { SyncSliders(); UpdateProgress(); UpdateLyric(); _hClock.Text = DateTime.Now.ToString("M月d日 ddd  HH:mm", Zh); if (_page == 1) OnStats(); if (_page == 2) OnRadio(); RefreshExtraPage(); }
        _layoutDirty = false;
        Resize(next, instant);

        var layer = LayerFor(next);
        foreach (var l in Layers)
        {
            if (l == layer)
            {
                l.Visibility = Visibility.Visible;
                // Spring-in: tiny scale + fade, slightly delayed so the outline leads.
                var st = new ScaleTransform(0.96, 0.96);
                l.RenderTransformOrigin = new Point(0.5, 0);
                l.RenderTransform = st;
                var delay = TimeSpan.FromMilliseconds(instant ? 0 : 90);
                l.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(instant ? 0 : 220)) { BeginTime = delay });
                var spring = new DoubleAnimation(1, TimeSpan.FromMilliseconds(instant ? 0 : 480)) { BeginTime = delay, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 } };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, spring);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, spring);
            }
            else if (l.Visibility == Visibility.Visible)
            {
                var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(instant ? 0 : 100));
                var target = l;
                a.Completed += (_, _) => { if (LayerFor(_mode) != target) target.Visibility = Visibility.Collapsed; };
                l.BeginAnimation(OpacityProperty, a);
            }
        }
        bool line = next == Mode.Line;
        _surface.BeginAnimation(OpacityProperty, new DoubleAnimation(next == Mode.Hidden || line ? 0 : 1, TimeSpan.FromMilliseconds(180)));
        _surface.IsHitTestVisible = next != Mode.Hidden && !line;
        _lineBar.BeginAnimation(OpacityProperty, new DoubleAnimation(line ? 1 : 0, TimeSpan.FromMilliseconds(line ? 260 : 120)) { BeginTime = TimeSpan.FromMilliseconds(line ? 120 : 0) });
        UpdateGlowScene();
        if (next != Mode.Expanded) SystemStats.Need(this, S.ShowStats && !Playing && !GameMode.Active);
        UpdateSpectrumNeed();
    }

    private FrameworkElement? LayerFor(Mode m) => m switch
    {
        Mode.Compact => _compact,
        Mode.Osd => _osd,
        Mode.Toast => _toast,
        Mode.Alert => _alert,
        Mode.Expanded => _expanded,
        _ => null,
    };

    private (double W, double H, double R) _shape;
    private DispatcherTimer? _fitTimer;

    private void Resize(Mode m, bool instant)
    { Aether.Core.Diag.Hit("Island.Resize");
        var shape = ShapeOf(m);
        if (shape == _shape && !instant) return;
        bool growing = shape.W > _shape.W || shape.H > _shape.H;
        _shape = shape;
        var (w, h, r) = shape;
        double top = IsNotch ? 0 : (m is Mode.Line or Mode.Hidden ? 2 : 6);

        var dur = TimeSpan.FromMilliseconds(instant ? 0 : growing ? 460 : 320);
        IEasingFunction ease = growing
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
            : new CubicEase { EasingMode = EasingMode.EaseOut };

        if (growing || instant) FitWindow(Math.Max(w, _surface.ActualWidth), Math.Max(h, _surface.ActualHeight) + top);
        _fitTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _fitTimer.Stop();
        _fitTimer.Interval = dur + TimeSpan.FromMilliseconds(60);
        _fitTimer.Tick -= OnFitTick;
        _fitTimer.Tick += OnFitTick;
        _fitTimer.Start();

        Animate(_surface, WidthProperty, w, dur, ease);
        Animate(_surface, HeightProperty, h, dur, ease);
        Animate(_surface, IslandSurface.RadiusProperty, r, dur, ease);
        _surface.Margin = new Thickness(0, top, 0, 0);
    }

    private void OnFitTick(object? sender, EventArgs e)
    {
        _fitTimer!.Stop();
        FitWindow(_shape.W, _shape.H + _surface.Margin.Top);
    }

    /// <summary>Size the layered window around the island (+ears, + spring overshoot room), centered.</summary>
    private void FitWindow(double w, double bottom)
    { Aether.Core.Diag.Hit("Island.FitWindow");
        double ww = Math.Ceiling(w * 1.08 + 40), hh = Math.Ceiling(Math.Max(bottom, 8) * 1.1 + 12);
        if (Math.Abs(Width - ww) < 1 && Math.Abs(Height - hh) < 1) return;
        Width = ww;
        Height = hh;
        PlaceWindow();
    }

    private static void Animate(UIElement el, DependencyProperty dp, double to, TimeSpan dur, IEasingFunction ease)
    {
        if (dur == TimeSpan.Zero) { el.BeginAnimation(dp, null); el.SetValue(dp, to); return; }
        el.BeginAnimation(dp, new DoubleAnimation(to, dur) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
    }

    private double CenterX => SystemParameters.PrimaryScreenWidth / 2 + S.OffsetX;

    private void PlaceWindow()
    {
        double sw = SystemParameters.PrimaryScreenWidth;
        double maxOff = sw / 2 - 160;
        S.OffsetX = Math.Clamp(S.OffsetX, -maxOff, maxOff);
        Left = Math.Round(CenterX - Width / 2);
        Top = 0;
    }

    /// <summary>Settings changed: re-read everything.</summary>
    public void RefreshAll()
    {
        ApplySkin();
        OnAttention(null);
        _lastLyric = "\u0000";
        UpdateCompact();
        Apply(true);
    }

    public void ResetPosition()
    {
        S.OffsetX = 0;
        SettingsStore.Touch();
        PlaceWindow();
    }

    // =====================================================================
    // timers
    // =====================================================================
    private void Poll()
    {
        _tick++;
        CheckDragLeft();
        bool fs = TaskWindows.ForegroundIsFullscreen();
        if (fs != _fullscreen) AudioService.SetPaused(fs);
        _fullscreen = fs;
        _maximized = !fs && ForegroundMaximized();

        Native.GetCursorPos(out var cur);
        var dpi = VisualTreeHelper.GetDpi(this);
        double cx = cur.X / dpi.DpiScaleX, cy = cur.Y / dpi.DpiScaleY;
        bool nearTop = cy <= 4 && Math.Abs(cx - CenterX) < 120;
        if (_mode is Mode.Line or Mode.Hidden && nearTop && !fs) _lineHover = true;
        else if (_lineHover && !_hover && !(cy < 300 && Math.Abs(cx - CenterX) < ExpandedW / 2 + 30)) _lineHover = false;

        var minute = DateTime.Now.ToString("HH:mm");
        if (minute != _lastMinute)
        {
            _lastMinute = minute;
            if (_mode is Mode.Compact or Mode.Idle) UpdateCompact();
            _hClock.Text = DateTime.Now.ToString("M月d日 ddd  HH:mm", Zh);
        }
        if (Playing && (_mode == Mode.Compact || (_mode == Mode.Expanded && _page == 0))) UpdateLyric();

        if (!_hover && _mode == Mode.Expanded && !_pressed && !_lineHover && (DateTime.Now - _leftSince).TotalMilliseconds < 380) return;
        if (_mode == Mode.Expanded && _page == 0 && _tick % 4 == 0) UpdateProgress();
        Apply();
    }

    private bool ForegroundMaximized()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return false;
        var cls = Native.ClassOf(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!Native.IsZoomed(fg)) return false;
        var mon = Native.MonitorBounds(fg);
        var prim = Native.PrimaryMonitor();
        return mon.Left == prim.Left && mon.Top == prim.Top;
    }

    private bool SpectrumVisible =>
        S.ShowSpectrum && !_fullscreen && Playing &&
        (_mode == Mode.Compact || (_mode == Mode.Toast && _toastBarsPanel.Visibility == Visibility.Visible) || (_mode == Mode.Expanded && _page == 0));

    private void UpdateSpectrumNeed()
    {
        if (!SpectrumVisible) return;
        _spectrumLastNeeded = DateTime.Now;
        AudioService.WantSpectrum(this, true);
        if (!_frame.IsEnabled) _frame.Start();
    }

    private readonly float[] _levels = new float[32];

    private void Frame()
    { Aether.Core.Diag.Hit("Island.Frame");
        if (SpectrumVisible) _spectrumLastNeeded = DateTime.Now;
        else if ((DateTime.Now - _spectrumLastNeeded).TotalSeconds > 2)
        {
            _frame.Stop();
            AudioService.WantSpectrum(this, false);
            return;
        }
        if (!AudioService.SpectrumRunning) AudioService.Spectrum.Decay();
        AudioService.Spectrum.Read(_levels);
        if (_mode == Mode.Compact) Paint(_cBars, 16, 3, 3);
        else if (_mode == Mode.Toast) Paint(_toastBars, 24, 3, 3);
        else if (_mode == Mode.Expanded) Paint(_mBars, 22, 3, 4);
    }

    private void Paint(Rectangle[] bars, double maxH, double minH, int skip)
    {
        int n = bars.Length, usable = _levels.Length - skip * 2;
        for (int i = 0; i < n; i++)
        {
            int a = skip + i * usable / n, b = skip + (i + 1) * usable / n;
            float v = 0;
            for (int k = a; k < Math.Max(b, a + 1); k++) v = Math.Max(v, _levels[k]);
            bars[i].Height = Math.Round(minH + (maxH - minH) * v);
        }
    }

    // =====================================================================
    // drag sideways
    // =====================================================================
    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsInteractive(d)) return;
        _pressed = true;
        _dragging = false;
        _downAt = ScreenDip(e);
        _downOffset = S.OffsetX;
        _surface.CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        double dx = ScreenDip(e).X - _downAt.X;
        if (!_dragging && Math.Abs(dx) > 6) _dragging = true;
        if (_dragging)
        {
            S.OffsetX = _downOffset + dx;
            PlaceWindow();
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        _surface.ReleaseMouseCapture();
        if (_dragging) SettingsStore.Touch();
        _dragging = false;
        Apply();
    }

    private Point ScreenDip(MouseEventArgs e)
    {
        var p = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
    }

    private static bool IsInteractive(DependencyObject d)
    {
        for (var cur = d; cur is not null; cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
        {
            if (cur is ButtonBase or Slider or Thumb or ScrollBar) return true;
            if (cur is FrameworkElement { Tag: "nodrag" }) return true;
            if (cur is Grid g && g.Cursor == Cursors.Hand && g.ToolTip is string) return true; // unread icon
            if (cur is IslandWindow) return false;
        }
        return false;
    }
}
