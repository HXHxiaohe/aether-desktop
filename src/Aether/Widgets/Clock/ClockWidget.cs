using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Aether.Core;
using Aether.Shell;

namespace Aether.Widgets.Clock;

/// <summary>Desktop clock with several looks. Ticks once a minute unless seconds are visible.</summary>
public sealed class ClockWidget : WidgetWindow
{
    public static readonly string[] StyleNames =
    {
        "极简", "卡片", "竖排", "海报", "表盘", "英文", "进度环", "翻页", "签名",
    };

    private static readonly CultureInfo Zh = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");
    private static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    private static readonly FontFamily Serif = new("Georgia, Times New Roman, SimSun");

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    private Action<DateTime>? _update;

    public ClockWidget(WidgetConfig cfg) : base(cfg)
    {
        Title = "Aether Clock";
        _timer.Tick += (_, _) => Tick();
        Closed += (_, _) => _timer.Stop();
    }

    private ClockOptions O => Config.Clock!;

    protected override bool WantsCard => O.Card || O.Style == 1;
    protected override bool HasCommonOptions => false; // card + size live in ClockOptions

    private bool NeedsSeconds => O.ShowSeconds;

    protected override FrameworkElement BuildContent()
    {
        var now = DateTime.Now;
        FrameworkElement root = O.Style switch
        {
            1 => BuildCard(),
            2 => BuildVertical(),
            3 => BuildPoster(),
            4 => BuildAnalog(),
            5 => BuildEnglish(),
            6 => BuildRing(),
            7 => BuildFlip(),
            8 => BuildSignature(),
            _ => BuildMinimal(),
        };
        if (root.Margin == default)
            root.Margin = WantsCard ? new Thickness(22, 16, 22, 18) : new Thickness(10, 6, 16, 10); // room for glow / italics
        root.LayoutTransform = new ScaleTransform(O.Scale, O.Scale);
        _update?.Invoke(now);
        Schedule();
        return root;
    }

    private void Tick()
    {
        _update?.Invoke(DateTime.Now);
        Schedule();
    }

    /// <summary>Fire exactly on the next second / minute boundary.</summary>
    private void Schedule()
    {
        var now = DateTime.Now;
        var next = NeedsSeconds
            ? now.AddSeconds(1).AddMilliseconds(-now.Millisecond)
            : now.AddMinutes(1).AddSeconds(-now.Second).AddMilliseconds(-now.Millisecond);
        _timer.Stop();
        _timer.Interval = (next - now) + TimeSpan.FromMilliseconds(15);
        _timer.Start();
    }

    // ---------- formatting ----------
    private string Time(DateTime t)
    {
        var s = O.Hour24 ? t.ToString("HH:mm") : t.ToString("h:mm");
        return NeedsSeconds ? s + t.ToString(":ss") : s;
    }

    private string AmPm(DateTime t) => O.Hour24 ? "" : (t.Hour < 12 ? "上午" : "下午");
    private static string DateZh(DateTime t) => t.ToString("M月d日  dddd", Zh);
    private static string Weekday(DateTime t) => t.ToString("dddd", Zh);

    private static TextBlock T(double size, FontWeight weight, double opacity = 1, FontFamily? font = null)
    {
        return new TextBlock
        {
            FontSize = size,
            FontWeight = weight,
            FontFamily = font ?? Display,
            Foreground = Brushes.White,
            Opacity = opacity,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Opacity = 0.35, Color = Colors.Black },
        };
    }

    private static void Accent(Shape s) => s.SetResourceReference(Shape.FillProperty, "AccentBrush");
    private static void AccentText(TextBlock t) => t.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

    // ---------- styles ----------
    private FrameworkElement BuildMinimal()
    {
        var time = T(76, FontWeights.Light);
        var ampm = T(18, FontWeights.Normal, 0.8);
        ampm.VerticalAlignment = VerticalAlignment.Bottom;
        ampm.Margin = new Thickness(8, 0, 0, 16);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(time);
        row.Children.Add(ampm);

        var date = T(19, FontWeights.Normal, 0.9, Text);
        date.Margin = new Thickness(4, -4, 0, 0);
        var bar = new Rectangle { Width = 44, Height = 3, RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(5, 14, 0, 0) };
        Accent(bar);

        var panel = new StackPanel();
        panel.Children.Add(row);
        if (O.ShowDate) { panel.Children.Add(date); panel.Children.Add(bar); }
        _update = t => { time.Text = Time(t); ampm.Text = AmPm(t); date.Text = DateZh(t); };
        return panel;
    }

    private FrameworkElement BuildCard()
    {
        var time = T(54, FontWeights.SemiBold);
        var ampm = T(15, FontWeights.Normal, 0.75);
        ampm.VerticalAlignment = VerticalAlignment.Bottom;
        ampm.Margin = new Thickness(6, 0, 0, 12);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(time);
        row.Children.Add(ampm);

        var week = T(15, FontWeights.SemiBold, 1, Text);
        AccentText(week);
        var date = T(15, FontWeights.Normal, 0.85, Text);
        date.Margin = new Thickness(10, 0, 0, 0);
        var info = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 2, 0, 0) };
        info.Children.Add(week);
        info.Children.Add(date);

        var panel = new StackPanel { MinWidth = 210 };
        panel.Children.Add(row);
        if (O.ShowDate) panel.Children.Add(info);
        _update = t =>
        {
            time.Text = Time(t); ampm.Text = AmPm(t);
            week.Text = Weekday(t); date.Text = t.ToString("yyyy年M月d日", Zh);
        };
        return panel;
    }

    private FrameworkElement BuildVertical()
    {
        var hh = T(92, FontWeights.Thin);
        var mm = T(92, FontWeights.Thin);
        AccentText(mm);
        hh.Margin = new Thickness(0, 0, 0, -30);
        var col = new StackPanel();
        col.Children.Add(hh);
        col.Children.Add(mm);

        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        var week = T(20, FontWeights.SemiBold, 1, Text);
        var date = T(15, FontWeights.Normal, 0.8, Text);
        var sec = T(15, FontWeights.Normal, 0.6, Text);
        side.Children.Add(week);
        side.Children.Add(date);
        side.Children.Add(sec);

        var grid = new StackPanel { Orientation = Orientation.Horizontal };
        grid.Children.Add(col);
        if (O.ShowDate || NeedsSeconds) grid.Children.Add(side);
        week.Visibility = date.Visibility = O.ShowDate ? Visibility.Visible : Visibility.Collapsed;
        sec.Visibility = NeedsSeconds ? Visibility.Visible : Visibility.Collapsed;
        _update = t =>
        {
            int h = O.Hour24 ? t.Hour : (t.Hour % 12 == 0 ? 12 : t.Hour % 12);
            hh.Text = h.ToString("00"); mm.Text = t.ToString("mm");
            week.Text = Weekday(t); date.Text = t.ToString("M月d日", Zh) + (O.Hour24 ? "" : "  " + AmPm(t));
            sec.Text = t.ToString("ss") + " 秒";
        };
        return grid;
    }

    private FrameworkElement BuildPoster()
    {
        var day = T(118, FontWeights.Normal, 1, Serif);
        day.Margin = new Thickness(0, -14, 0, -14);
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        var week = T(24, FontWeights.SemiBold, 1, Text);
        var month = T(15, FontWeights.Normal, 0.75, Text);
        var line = new Rectangle { Width = 120, Height = 1, Fill = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 10, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
        var time = T(28, FontWeights.Light);
        AccentText(time);
        right.Children.Add(week);
        right.Children.Add(month);
        right.Children.Add(line);
        right.Children.Add(time);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(day);
        row.Children.Add(right);
        _update = t =>
        {
            day.Text = t.Day.ToString("00"); week.Text = Weekday(t);
            month.Text = t.ToString("yyyy · M月", Zh);
            time.Text = (O.Hour24 ? "" : AmPm(t) + " ") + Time(t);
        };
        return row;
    }

    private FrameworkElement BuildAnalog()
    {
        const double size = 176, c = size / 2;
        var canvas = new Canvas { Width = size, Height = size };
        var face = new Ellipse { Width = size, Height = size, StrokeThickness = 1.2, Stroke = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)) };
        face.SetResourceReference(Shape.FillProperty, "GlassBrush");
        canvas.Children.Add(face);

        for (int i = 0; i < 60; i++)
        {
            bool major = i % 5 == 0;
            double len = major ? 12 : 5, a = i * Math.PI / 30;
            var tick = new Line
            {
                X1 = c + Math.Sin(a) * (c - 9), Y1 = c - Math.Cos(a) * (c - 9),
                X2 = c + Math.Sin(a) * (c - 9 - len), Y2 = c - Math.Cos(a) * (c - 9 - len),
                Stroke = new SolidColorBrush(Color.FromArgb(major ? (byte)0xEE : (byte)0x55, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = major ? 2.2 : 1,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            canvas.Children.Add(tick);
        }

        Line Hand(double length, double thick, Brush? brush, bool accent = false)
        {
            var l = new Line { X1 = c, Y1 = c + length * 0.14, X2 = c, Y2 = c - length, StrokeThickness = thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = new RotateTransform(0, c, c) };
            if (accent) l.SetResourceReference(Shape.StrokeProperty, "AccentBrush"); else l.Stroke = brush;
            canvas.Children.Add(l);
            return l;
        }
        var hour = Hand(c * 0.48, 5, Brushes.White);
        var min = Hand(c * 0.72, 3.2, Brushes.White);
        var sec = Hand(c * 0.80, 1.4, null, true);
        sec.Visibility = NeedsSeconds ? Visibility.Visible : Visibility.Collapsed;
        var hub = new Ellipse { Width = 9, Height = 9 };
        Accent(hub);
        Canvas.SetLeft(hub, c - 4.5); Canvas.SetTop(hub, c - 4.5);
        canvas.Children.Add(hub);
        canvas.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.3 };

        var date = T(15, FontWeights.Normal, 0.9, Text);
        date.HorizontalAlignment = HorizontalAlignment.Center;
        date.Margin = new Thickness(0, 10, 0, 0);
        var panel = new StackPanel();
        panel.Children.Add(canvas);
        if (O.ShowDate) panel.Children.Add(date);
        _update = t =>
        {
            ((RotateTransform)hour.RenderTransform).Angle = (t.Hour % 12 + t.Minute / 60.0) * 30;
            ((RotateTransform)min.RenderTransform).Angle = (t.Minute + t.Second / 60.0) * 6;
            ((RotateTransform)sec.RenderTransform).Angle = t.Second * 6;
            date.Text = DateZh(t);
        };
        return panel;
    }

    private FrameworkElement BuildEnglish()
    {
        var week = T(46, FontWeights.Normal, 1, Serif);
        week.FontStyle = FontStyles.Italic;
        var date = T(16, FontWeights.Normal, 0.85, Text);
        date.Margin = new Thickness(3, 0, 0, 0);
        var time = T(22, FontWeights.SemiBold, 1, Text);
        AccentText(time);
        time.Margin = new Thickness(3, 8, 0, 0);
        var panel = new StackPanel();
        panel.Children.Add(week);
        if (O.ShowDate) panel.Children.Add(date);
        panel.Children.Add(time);
        _update = t =>
        {
            week.Text = t.ToString("dddd", En);
            date.Text = t.ToString("d MMMM yyyy", En).ToUpperInvariant();
            time.Text = O.Hour24 ? Time(t) : Time(t) + (t.Hour < 12 ? " AM" : " PM");
        };
        return panel;
    }

    private FrameworkElement BuildRing()
    {
        const double size = 168, stroke = 7, r = (size - stroke) / 2, c = size / 2;
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), StrokeThickness = stroke });
        var arc = new Path { StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        arc.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        grid.Children.Add(arc);

        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var time = T(NeedsSeconds ? 30 : 38, FontWeights.Light);
        time.HorizontalAlignment = HorizontalAlignment.Center;
        var pct = T(12, FontWeights.Normal, 0.7, Text);
        pct.HorizontalAlignment = HorizontalAlignment.Center;
        center.Children.Add(time);
        center.Children.Add(pct);
        grid.Children.Add(center);

        var date = T(15, FontWeights.Normal, 0.9, Text);
        date.HorizontalAlignment = HorizontalAlignment.Center;
        date.Margin = new Thickness(0, 10, 0, 0);
        var panel = new StackPanel();
        panel.Children.Add(grid);
        if (O.ShowDate) panel.Children.Add(date);

        _update = t =>
        {
            double p = t.TimeOfDay.TotalMinutes / 1440.0;
            double a = Math.Max(p, 0.002) * 2 * Math.PI;
            var start = new Point(c, c - r);
            var end = new Point(c + Math.Sin(a) * r, c - Math.Cos(a) * r);
            var fig = new PathFigure { StartPoint = start, IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true));
            arc.Data = new PathGeometry(new[] { fig });
            time.Text = Time(t);
            pct.Text = $"今天已过 {p:P0}";
            date.Text = DateZh(t);
        };
        return panel;
    }

    private FrameworkElement BuildFlip()
    {
        (Border tile, TextBlock text) Tile()
        {
            var text = T(72, FontWeights.SemiBold);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, -6, 0, 0);
            var g = new Grid();
            g.Children.Add(text);
            g.Children.Add(new Rectangle { Height = 1.5, Fill = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), VerticalAlignment = VerticalAlignment.Center });
            var b = new Border { Width = 108, Height = 116, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = g };
            b.SetResourceReference(Border.BackgroundProperty, "GlassBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "GlassBorderBrush");
            return (b, text);
        }
        var (hb, ht) = Tile();
        var (mb, mt) = Tile();
        mb.Margin = new Thickness(10, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(hb);
        row.Children.Add(mb);

        var date = T(15, FontWeights.Normal, 0.9, Text);
        date.Margin = new Thickness(4, 10, 0, 0);
        var panel = new StackPanel();
        panel.Children.Add(row);
        if (O.ShowDate || NeedsSeconds || !O.Hour24) panel.Children.Add(date);
        _update = t =>
        {
            int h = O.Hour24 ? t.Hour : (t.Hour % 12 == 0 ? 12 : t.Hour % 12);
            ht.Text = h.ToString("00"); mt.Text = t.ToString("mm");
            var parts = new System.Collections.Generic.List<string>();
            if (O.ShowDate) parts.Add(DateZh(t));
            if (!O.Hour24) parts.Add(AmPm(t));
            if (NeedsSeconds) parts.Add(t.ToString("ss") + " 秒");
            date.Text = string.Join("   ", parts);
        };
        return panel;
    }

    /// <summary>Large translucent handwritten weekday behind a light time, date underneath.</summary>
    private FrameworkElement BuildSignature()
    {
        var script = new FontFamily("Segoe Script, Ink Free, Gabriola");
        var week = new TextBlock
        {
            FontFamily = script, FontSize = 74, Foreground = Brushes.White, Opacity = 0.32,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -8, 0, 0),
        };
        var time = T(54, FontWeights.Light);
        time.HorizontalAlignment = HorizontalAlignment.Center;
        time.VerticalAlignment = VerticalAlignment.Center;
        time.Margin = new Thickness(0, 4, 0, 0);

        var stage = new Grid { MinWidth = 300 };
        stage.Children.Add(week);
        stage.Children.Add(time);

        var date = T(16, FontWeights.Normal, 0.9, Text);
        date.HorizontalAlignment = HorizontalAlignment.Center;
        date.Margin = new Thickness(0, -4, 0, 0);

        var panel = new StackPanel();
        panel.Children.Add(stage);
        if (O.ShowDate) panel.Children.Add(date);
        _update = t =>
        {
            week.Text = t.ToString("dddd", En);
            time.Text = O.Hour24 ? Time(t) : Time(t) + (t.Hour < 12 ? " AM" : " PM");
            date.Text = t.ToString("d MMMM", En);
        };
        return panel;
    }

    // ---------- context menu ----------
    protected override void AddMenuItems(ContextMenu menu)
    {
        var style = new MenuItem { Header = "时钟样式" };
        for (int i = 0; i < StyleNames.Length; i++)
        {
            int idx = i;
            var item = new MenuItem { Header = StyleNames[i], IsCheckable = true, IsChecked = O.Style == i };
            item.Click += (_, _) => Change(o => o.Style = idx);
            style.Items.Add(item);
        }
        menu.Items.Add(style);

        var size = new MenuItem { Header = "大小" };
        foreach (var s in new[] { 0.75, 0.9, 1.0, 1.25, 1.5, 2.0 })
        {
            var item = new MenuItem { Header = $"{s:P0}", IsCheckable = true, IsChecked = Math.Abs(O.Scale - s) < 0.01 };
            item.Click += (_, _) => Change(o => o.Scale = s);
            size.Items.Add(item);
        }
        menu.Items.Add(size);

        menu.Items.Add(Toggle("24 小时制", O.Hour24, (o, v) => o.Hour24 = v));
        menu.Items.Add(Toggle("显示秒", O.ShowSeconds, (o, v) => o.ShowSeconds = v));
        menu.Items.Add(Toggle("显示日期", O.ShowDate, (o, v) => o.ShowDate = v));
        menu.Items.Add(Toggle("玻璃卡片背景", O.Card, (o, v) => o.Card = v));
    }

    private MenuItem Toggle(string header, bool value, Action<ClockOptions, bool> set)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = value };
        item.Click += (_, _) => Change(o => set(o, item.IsChecked));
        return item;
    }

    private void Change(Action<ClockOptions> edit)
    {
        edit(O);
        SettingsStore.Touch();
        Refresh();
    }
}
