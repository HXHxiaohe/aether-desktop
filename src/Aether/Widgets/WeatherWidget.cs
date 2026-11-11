using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Aether.Core;
using Aether.Shell;
using static Aether.Widgets.WidgetUi;

namespace Aether.Widgets;

/// <summary>
/// Weather card from Open-Meteo (open-meteo.com — free, no API key). Only the city name
/// (once, to find coordinates) and the coordinates are sent. Refreshes every 30 minutes.
/// </summary>
public sealed class WeatherWidget : WidgetWindow
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(30) };
    private Forecast? _data;
    private string? _error;

    private sealed record Day(DateTime Date, double Max, double Min, int Code);
    private sealed record Forecast(string Place, double Temp, double Feels, int Humidity, int Code, bool IsDay, Day[] Days, DateTime At);

    static WeatherWidget() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("AetherDesktop/1.0 (personal desktop widget)");

    public WeatherWidget(WidgetConfig cfg) : base(cfg)
    {
        Title = "Aether Weather";
        Config.City ??= "墨尔本";
        _refresh.Tick += async (_, _) => await LoadAsync();
        _refresh.Start();
        Loaded += async (_, _) => await LoadAsync();
    }

    protected override void OnClosedCleanup() => _refresh.Stop();

    protected override bool WantsCard => true;

    private async Task LoadAsync()
    {
        try
        {
            if (Config.Lat is null || Config.Lon is null)
            {
                var geo = await GetJson($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(Config.City!)}&count=1&language=zh&format=json");
                if (!geo.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0)
                { _error = $"找不到城市“{Config.City}”"; Refresh(); return; }
                var r = res[0];
                Config.Lat = r.GetProperty("latitude").GetDouble();
                Config.Lon = r.GetProperty("longitude").GetDouble();
                Config.City = r.GetProperty("name").GetString() ?? Config.City;
                SettingsStore.Touch();
            }
            var ci = CultureInfo.InvariantCulture;
            using var doc = await GetJson($"https://api.open-meteo.com/v1/forecast?latitude={Config.Lat!.Value.ToString(ci)}&longitude={Config.Lon!.Value.ToString(ci)}" +
                "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,is_day" +
                "&daily=temperature_2m_max,temperature_2m_min,weather_code&forecast_days=4&timezone=auto");
            var c = doc.RootElement.GetProperty("current");
            var d = doc.RootElement.GetProperty("daily");
            var dates = d.GetProperty("time").EnumerateArray().Select(x => DateTime.Parse(x.GetString()!, ci)).ToArray();
            var max = d.GetProperty("temperature_2m_max").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var min = d.GetProperty("temperature_2m_min").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var codes = d.GetProperty("weather_code").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            _data = new Forecast(Config.City!,
                c.GetProperty("temperature_2m").GetDouble(), c.GetProperty("apparent_temperature").GetDouble(),
                (int)c.GetProperty("relative_humidity_2m").GetDouble(), c.GetProperty("weather_code").GetInt32(),
                c.GetProperty("is_day").GetInt32() == 1,
                dates.Select((dt, i) => new Day(dt, max[i], min[i], codes[i])).ToArray(), DateTime.Now);
            _error = null;
        }
        catch (Exception ex)
        {
            Log.Error("weather", ex);
            _error = "天气暂时获取不到（网络或代理）";
        }
        Refresh();
    }

    private static async Task<JsonDocument> GetJson(string url)
    {
        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    protected override FrameworkElement BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 16), Width = 252 };
        root.Children.Add(Header("", Config.City ?? "天气",
            IconButton("", 22, 9, async () => await LoadAsync(), "刷新")));

        if (_data is null)
        {
            root.Children.Add(Text(_error ?? "正在获取天气…", 12.5, FontWeights.Normal, 0.6));
            return root;
        }

        var now = new Grid();
        now.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        now.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = WeatherGlyph(Icon(_data.Code, _data.IsDay), 38);
        icon.Margin = new Thickness(0, 0, 14, 0);
        now.Children.Add(icon);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Text($"{_data.Temp:0}°", 34, FontWeights.Light, 1, Display));
        info.Children.Add(Text($"{Describe(_data.Code)} · 体感 {_data.Feels:0}° · 湿度 {_data.Humidity}%", 12, FontWeights.Normal, 0.7));
        Grid.SetColumn(info, 1);
        now.Children.Add(info);
        root.Children.Add(now);

        var days = new UniformGrid { Columns = 4, Margin = new Thickness(0, 14, 0, 0) };
        var zh = CultureInfo.GetCultureInfo("zh-CN");
        foreach (var day in _data.Days.Take(4))
        {
            var col = new StackPanel();
            var name = Text(day.Date.Date == DateTime.Today ? "今天" : day.Date.ToString("ddd", zh), 11, FontWeights.SemiBold, 0.55);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            var g = WeatherGlyph(Icon(day.Code, true), 17);
            g.HorizontalAlignment = HorizontalAlignment.Center;
            g.Margin = new Thickness(0, 5, 0, 5);
            var t = Text($"{day.Max:0}° / {day.Min:0}°", 11.5, FontWeights.Normal, 0.85);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            col.Children.Add(name);
            col.Children.Add(g);
            col.Children.Add(t);
            days.Children.Add(col);
        }
        root.Children.Add(days);
        return root;
    }

    protected override void AddMenuItems(ContextMenu menu)
    {
        menu.Items.Add(Item("刷新天气", async () => await LoadAsync()));
        var cities = new MenuItem { Header = "切换城市" };
        foreach (var c in new[] { "墨尔本", "悉尼", "北京", "上海", "广州", "深圳", "杭州", "成都", "香港", "东京" })
        {
            var city = c;
            cities.Items.Add(new MenuItem { Header = c, IsCheckable = true, IsChecked = Config.City == c });
            ((MenuItem)cities.Items[^1]).Click += async (_, _) =>
            {
                Config.City = city; Config.Lat = null; Config.Lon = null; _data = null;
                SettingsStore.Touch(); Refresh(); await LoadAsync();
            };
        }
        menu.Items.Add(cities);
    }

    // WMO weather codes → weather symbol (Segoe UI Emoji, drawn monochrome by WPF) + description
    private static string Icon(int code, bool day) => code switch
    {
        0 => day ? "☀" : "🌙",
        1 or 2 => day ? "⛅" : "☁",
        3 => "☁",
        45 or 48 => "🌫",
        51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "🌧",
        71 or 73 or 75 or 77 or 85 or 86 => "🌨",
        95 or 96 or 99 => "⛈",
        _ => "☁",
    };

    private static TextBlock WeatherGlyph(string symbol, double size) => new()
    {
        Text = symbol, FontSize = size, Foreground = WidgetUi.InkBrush,
        FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static string Describe(int code) => code switch
    {
        0 => "晴", 1 => "大致晴朗", 2 => "多云", 3 => "阴",
        45 or 48 => "雾", 51 or 53 or 55 => "毛毛雨", 56 or 57 => "冻毛毛雨",
        61 => "小雨", 63 => "中雨", 65 => "大雨", 66 or 67 => "冻雨",
        71 => "小雪", 73 => "中雪", 75 => "大雪", 77 => "雪粒",
        80 or 81 => "阵雨", 82 => "强阵雨", 85 or 86 => "阵雪",
        95 => "雷阵雨", 96 or 99 => "雷阵雨伴冰雹",
        _ => "—",
    };
}
