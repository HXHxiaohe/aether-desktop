using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Aether.Core;
using Aether.Island;
using Aether.Shell;
using static Aether.Widgets.WidgetUi;

namespace Aether.Widgets;

/// <summary>Now-playing card: cover, title/artist, progress, controls, optional spectrum.</summary>
public sealed class MusicWidget : WidgetWindow
{
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _frame = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(66) };
    private readonly float[] _levels = new float[32];
    private Image? _cover;
    private TextBlock? _title, _artist, _pos, _dur, _play;
    private Border? _fill;
    private Rectangle[]? _bars;
    private double _barWidth;

    public MusicWidget(WidgetConfig cfg) : base(cfg)
    {
        Title = "Aether Music";
        MediaService.Changed += OnMedia;
        _tick.Tick += (_, _) => UpdateProgress();
        _frame.Tick += (_, _) => PaintBars();
        GameMode.Changed += OnGameMode;
    }

    private void OnGameMode(bool on)
    {
        if (on) { _tick.Stop(); _frame.Stop(); AudioService.WantSpectrum(this, false); }
        else Refresh();
    }

    protected override void OnClosedCleanup()
    {
        MediaService.Changed -= OnMedia;
        GameMode.Changed -= OnGameMode;
        _tick.Stop();
        _frame.Stop();
        AudioService.WantSpectrum(this, false);
    }

    protected override bool WantsCard => true;
    private bool SpectrumOn => Config.Spectrum == true;

    private void OnMedia() => Dispatcher.BeginInvoke(() => { if (!GameMode.Active) Refresh(); });

    protected override FrameworkElement BuildContent()
    {
        var m = MediaService.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14), Width = 300 };
        if (m is null || m.Title.Length == 0)
        {
            root.Children.Add(Header("", "音乐"));
            root.Children.Add(Text("没有正在播放的音乐", 12.5, FontWeights.Normal, 0.55));
            _tick.Stop();
            _frame.Stop();
            AudioService.WantSpectrum(this, false);
            return root;
        }

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        _cover = new Image { Stretch = Stretch.UniformToFill, Source = m.Cover };
        RenderOptions.SetBitmapScalingMode(_cover, BitmapScalingMode.HighQuality);
        var coverBox = new Border
        {
            Width = 64, Height = 64, Child = _cover,
            Background = new SolidColorBrush(WidgetUi.Ink(0x30)),
            Clip = new RectangleGeometry(new Rect(0, 0, 64, 64), 12, 12),
        };
        if (m.Cover is null) coverBox.Child = new TextBlock { Text = "", FontFamily = Icons, FontSize = 24, Foreground = WidgetUi.InkBrush, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(coverBox);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        _title = Text(m.Title, 15, FontWeights.SemiBold, 1, Display);
        _artist = Text(string.IsNullOrEmpty(m.Artist) ? m.Album : m.Artist, 12, FontWeights.Normal, 0.65);
        info.Children.Add(_title);
        info.Children.Add(_artist);
        if (SpectrumOn)
        {
            var bars = new StackPanel { Orientation = Orientation.Horizontal, Height = 18, Margin = new Thickness(0, 6, 0, 0) };
            _bars = new Rectangle[16];
            for (int i = 0; i < _bars.Length; i++)
            {
                var r = new Rectangle { Width = 3, Height = 2, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Bottom };
                r.SetResourceReference(Shape.FillProperty, "AccentBrush");
                _bars[i] = r;
                bars.Children.Add(r);
            }
            info.Children.Add(bars);
        }
        else _bars = null;
        Grid.SetColumn(info, 1);
        top.Children.Add(info);
        root.Children.Add(top);

        if (m.Duration > TimeSpan.Zero)
        {
            var track = new Grid { Height = 4, Margin = new Thickness(0, 12, 0, 4) };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(WidgetUi.Ink(0x30)) });
            _fill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
            _fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            track.Children.Add(_fill);
            _barWidth = 300 - 32;
            root.Children.Add(track);
            var times = new Grid();
            _pos = Text("", 10.5, FontWeights.Normal, 0.5);
            _dur = Text("", 10.5, FontWeights.Normal, 0.5);
            _dur.HorizontalAlignment = HorizontalAlignment.Right;
            times.Children.Add(_pos);
            times.Children.Add(_dur);
            root.Children.Add(times);
        }
        else { _fill = null; _pos = _dur = null; }

        var ctl = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        ctl.Children.Add(IconButton("", 34, 13, MediaService.Previous, "上一首"));
        var play = IconButton(m.Playing ? "" : "", 40, 16, MediaService.PlayPause, "播放/暂停", filled: true);
        _play = (TextBlock)((Border)play.Content).Child;
        play.Margin = new Thickness(14, 0, 14, 0);
        ctl.Children.Add(play);
        ctl.Children.Add(IconButton("", 34, 13, MediaService.Next, "下一首"));
        root.Children.Add(ctl);

        UpdateProgress();
        if (m.Playing) _tick.Start(); else _tick.Stop();
        bool spec = m.Playing && SpectrumOn;
        AudioService.WantSpectrum(this, spec);
        if (spec) _frame.Start(); else _frame.Stop();
        return root;
    }

    private void UpdateProgress()
    {
        var m = MediaService.Current;
        if (m is null || _fill is null || m.Duration <= TimeSpan.Zero) return;
        var p = m.PositionNow;
        _fill.Width = Math.Max(4, _barWidth * Math.Clamp(p.TotalSeconds / m.Duration.TotalSeconds, 0, 1));
        if (_pos is not null) _pos.Text = Fmt(p);
        if (_dur is not null) _dur.Text = Fmt(m.Duration);
    }

    private void PaintBars()
    {
        if (_bars is null) return;
        AudioService.Spectrum.Read(_levels);
        for (int i = 0; i < _bars.Length; i++)
        {
            int a = 3 + i * 26 / _bars.Length, b = 3 + (i + 1) * 26 / _bars.Length;
            float v = 0;
            for (int k = a; k < Math.Max(b, a + 1); k++) v = Math.Max(v, _levels[k]);
            _bars[i].Height = Math.Round(2 + 16 * v);
        }
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    protected override void AddMenuItems(ContextMenu menu)
    {
        var spec = new MenuItem { Header = "显示频谱", IsCheckable = true, IsChecked = SpectrumOn };
        spec.Click += (_, _) => { Config.Spectrum = spec.IsChecked; SettingsStore.Touch(); Refresh(); };
        menu.Items.Add(spec);
    }
}
