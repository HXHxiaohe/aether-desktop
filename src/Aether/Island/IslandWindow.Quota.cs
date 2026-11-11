using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Aether.Island;

/// <summary>AI 额度 page: one card per provider, a bar per limit window with its reset time.</summary>
public sealed partial class IslandWindow
{
    private const int QuotaPage = 6;
    private StackPanel _quotaList = null!;
    private TextBlock _quotaStamp = null!;

    private FrameworkElement BuildQuotaPage()
    {
        var page = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(Text("AI 额度", 11, FontWeights.SemiBold, 0.45));
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _quotaStamp = Text("", 10.5, FontWeights.Normal, 0.4);
        _quotaStamp.VerticalAlignment = VerticalAlignment.Center;
        _quotaStamp.Margin = new Thickness(0, 0, 8, 6);
        right.Children.Add(_quotaStamp);
        right.Children.Add(Pill("刷新", false, () => _ = QuotaService.RefreshAsync(true)));
        head.Children.Add(right);
        page.Children.Add(head);
        _quotaList = new StackPanel();
        page.Children.Add(_quotaList);

        QuotaService.Changed += OnQuotaChanged;
        Closed += (_, _) => QuotaService.Changed -= OnQuotaChanged;
        return page;
    }

    private void OnQuotaChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_page == QuotaPage) RebuildQuota();
        if (S.QuotaInCompact) UpdateCompact();
    });

    private void RebuildQuota()
    {
        _quotaList.Children.Clear();
        if (!S.Quota)
        {
            _quotaList.Children.Add(Hint("AI 额度已关闭（设置 › 灵动岛）"));
            _layoutDirty = true;
            return;
        }
        var updated = QuotaService.Providers.Where(p => p.Updated > DateTime.MinValue).Select(p => p.Updated).DefaultIfEmpty().Max();
        _quotaStamp.Text = updated > DateTime.MinValue ? $"更新于 {updated:HH:mm}" : "";

        var grid = new UniformGrid2(QuotaService.Providers.Count);
        foreach (var p in QuotaService.Providers) grid.Add(ProviderCard(p));
        _quotaList.Children.Add(grid.Panel);
        _layoutDirty = true;
    }

    private FrameworkElement ProviderCard(QuotaProvider p)
    {
        var card = new StackPanel();
        var title = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Text(p.Name, 13.5, FontWeights.SemiBold));
        if (p.Plan.Length > 0)
        {
            var plan = new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                Child = Text(p.Plan, 10, FontWeights.SemiBold, 0.8),
            };
            name.Children.Add(plan);
        }
        title.Children.Add(name);
        if (p.Meters.Count > 0)
        {
            var ring = Ring(p.Highest, 38);
            ring.HorizontalAlignment = HorizontalAlignment.Right;
            ring.Margin = new Thickness(0, -2, 0, -4);
            title.Children.Add(ring);
        }
        card.Children.Add(title);

        if (p.Meters.Count == 0)
        {
            var msg = p.State == "loading" ? "读取中…" : p.Message.Length > 0 ? p.Message : "暂时读取不到";
            var t = Text(msg, 11.5, FontWeights.Normal, 0.5);
            t.TextWrapping = TextWrapping.Wrap;
            card.Children.Add(t);
        }
        foreach (var m in p.Meters.Take(4))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            row.Children.Add(Text(m.Label, 11, FontWeights.Normal, 0.7));
            var pct = Text($"{m.Percent:0}%", 11, FontWeights.SemiBold, 0.95);
            pct.HorizontalAlignment = HorizontalAlignment.Right;
            row.Children.Add(pct);
            card.Children.Add(row);

            var bar = new Grid { Height = 5, Margin = new Thickness(0, 0, 0, 2) };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) });
            var fill = new Border { CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left };
            if (m.Percent >= 90) fill.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x4F));
            else if (m.Percent >= 70) fill.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40));
            else fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            bar.Children.Add(fill);
            bar.SizeChanged += (_, e) => fill.Width = Math.Max(5, e.NewSize.Width * m.Percent / 100);
            card.Children.Add(bar);
            if (m.ResetsAt is { } r)
            {
                var reset = Text(m.Percent < 0.5 ? "还没开始用" : ResetText(r), 10, FontWeights.Normal, 0.4);
                reset.Margin = new Thickness(0, 0, 0, 6);
                card.Children.Add(reset);
            }
        }
        if (p.Meters.Count > 0 && p.Message.Length > 0)
        {
            var warn = Text(p.Message, 10, FontWeights.Normal, 0.45);
            card.Children.Add(warn);
        }
        return new Border
        {
            Margin = new Thickness(3), Padding = new Thickness(12, 9, 12, 8), CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), Child = card,
        };
    }

    private static void SetUsageBrush(double pct, FrameworkElement owner, DependencyProperty dp)
    {
        if (pct >= 90) owner.SetValue(dp, new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x4F)));
        else if (pct >= 70) owner.SetValue(dp, new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)));
        else owner.SetResourceReference(dp, "AccentBrush");
    }

    /// <summary>Progress ring with the percentage in the middle (animated sweep).</summary>
    private static FrameworkElement Ring(double pct, double size)
    {
        double stroke = 3.5, r = (size - stroke) / 2, c = size / 2;
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), StrokeThickness = stroke });
        var arc = new System.Windows.Shapes.Path { StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        SetUsageBrush(pct, arc, Shape.StrokeProperty);
        g.Children.Add(arc);
        var label = Text($"{pct:0}", 11, FontWeights.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(label);

        void Draw(double v)
        {
            v = Math.Clamp(v, 0.001, 99.99);
            double a = v / 100 * 2 * Math.PI;
            var start = new Point(c, c - r);
            var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
            var fig = new PathFigure { StartPoint = start, IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, v > 50, SweepDirection.Clockwise, true));
            arc.Data = new PathGeometry(new[] { fig });
        }
        // sweep in once when the card appears
        var t0 = DateTime.Now;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double k = Math.Min(1, (DateTime.Now - t0).TotalMilliseconds / 600);
            double e = 1 - Math.Pow(1 - k, 3);
            Draw(pct * e);
            if (k >= 1) timer.Stop();
        };
        Draw(0);
        g.Loaded += (_, _) => timer.Start();
        g.Unloaded += (_, _) => timer.Stop();
        return g;
    }

    private static string ResetText(DateTimeOffset r)
    {
        var left = r - DateTimeOffset.Now;
        if (left.TotalMinutes < 1) return "即将重置";
        if (left.TotalHours < 1) return $"{(int)left.TotalMinutes} 分钟后重置";
        if (left.TotalHours < 24) return $"{(int)left.TotalHours} 小时 {left.Minutes} 分后重置";
        return $"{r.LocalDateTime:M月d日 HH:mm} 重置";
    }

    /// <summary>Highest usage across providers, for the collapsed island ("AI 63%").</summary>
    private static string? QuotaCompactText()
    {
        var ok = QuotaService.Providers.Where(p => p.Meters.Count > 0).ToList();
        if (ok.Count == 0) return null;
        var top = ok.OrderByDescending(p => p.Highest).First();
        return $"{top.Name} {top.Highest:0}%";
    }

    /// <summary>Equal-width columns that stretch to the page width.</summary>
    private sealed class UniformGrid2
    {
        public Grid Panel { get; } = new() { Margin = new Thickness(-3, 0, -3, 0) };
        private int _n;
        public UniformGrid2(int columns) { for (int i = 0; i < Math.Max(1, columns); i++) Panel.ColumnDefinitions.Add(new ColumnDefinition()); }
        public void Add(FrameworkElement e) { Grid.SetColumn(e, _n++); e.VerticalAlignment = VerticalAlignment.Top; Panel.Children.Add(e); }
    }
}
