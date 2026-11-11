using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Aether.Core;
using Aether.Shell;
using static Aether.Widgets.WidgetUi;

namespace Aether.Widgets;

/// <summary>Month calendar card: today highlighted, browse months, weeks start on Monday.</summary>
public sealed class CalendarWidget : WidgetWindow
{
    private static readonly CultureInfo Zh = CultureInfo.GetCultureInfo("zh-CN");
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private readonly DispatcherTimer _midnight = new(DispatcherPriority.Background);

    public CalendarWidget(WidgetConfig cfg) : base(cfg)
    {
        Title = "Aether Calendar";
        // Re-render once just after midnight so "today" moves on.
        _midnight.Tick += (_, _) => { _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); Refresh(); Arm(); };
        Arm();
    }

    private void Arm()
    {
        _midnight.Stop();
        _midnight.Interval = DateTime.Today.AddDays(1).AddSeconds(5) - DateTime.Now;
        _midnight.Start();
    }

    protected override void OnClosedCleanup() => _midnight.Stop();

    protected override bool WantsCard => true;

    protected override FrameworkElement BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 16), Width = 252 };

        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        nav.Children.Add(IconButton("", 24, 10, () => { _month = _month.AddMonths(-1); Refresh(); }, "上个月"));
        var todayBtn = IconButton("", 24, 10, () => { _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); Refresh(); }, "回到今天");
        todayBtn.Margin = new Thickness(4, 0, 4, 0);
        nav.Children.Add(todayBtn);
        nav.Children.Add(IconButton("", 24, 10, () => { _month = _month.AddMonths(1); Refresh(); }, "下个月"));

        var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        var title = Text(_month.ToString("yyyy年 M月", Zh), 16, FontWeights.SemiBold, 1, Display);
        title.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(title);
        nav.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(nav);
        root.Children.Add(head);

        var grid = new UniformGrid { Columns = 7 };
        foreach (var d in new[] { "一", "二", "三", "四", "五", "六", "日" })
        {
            var t = Text(d, 11, FontWeights.SemiBold, 0.45);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Margin = new Thickness(0, 0, 0, 4);
            grid.Children.Add(t);
        }

        int offset = ((int)_month.DayOfWeek + 6) % 7; // Monday first
        var start = _month.AddDays(-offset);
        for (int i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            if (i >= 35 && day.Month != _month.Month) break; // drop an all-next-month last row
            bool inMonth = day.Month == _month.Month;
            bool today = day == DateTime.Today;
            var cell = new Grid { Height = 30 };
            if (today)
            {
                var dot = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14) };
                dot.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                cell.Children.Add(dot);
            }
            var num = Text(day.Day.ToString(), 13, today ? FontWeights.Bold : FontWeights.Normal, inMonth ? (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0.7 : 0.95) : 0.25);
            num.HorizontalAlignment = HorizontalAlignment.Center;
            num.VerticalAlignment = VerticalAlignment.Center;
            if (today) { num.Foreground = new SolidColorBrush(Color.FromRgb(0x12, 0x13, 0x18)); num.Effect = null; }
            cell.Children.Add(num);
            grid.Children.Add(cell);
        }
        root.Children.Add(grid);

        var footer = Text(DateTime.Today.ToString("M月d日 dddd", Zh) + $" · 第 {ISOWeek.GetWeekOfYear(DateTime.Today)} 周", 11.5, FontWeights.Normal, 0.55);
        footer.Margin = new Thickness(2, 8, 0, 0);
        root.Children.Add(footer);
        return root;
    }
}
