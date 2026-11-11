using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aether.Core;

namespace Aether.Settings;

/// <summary>流光与动效 section (built in code): style, colour, strength, speed, scenes, preview.</summary>
public partial class SettingsWindow
{
    private static readonly (string Id, string Name, string Hint)[] GlowStyles =
    {
        ("scan", "扫描光带", "一段光沿着轮廓匀速巡游"),
        ("comet", "彗星掠影", "亮头长尾，掠过时拖出光痕"),
        ("dual", "双子流光", "两段光对向而行"),
        ("pulse", "声波脉冲", "跟着音乐的节奏明暗涨落"),
        ("breathe", "呼吸", "整圈光慢慢明暗起伏"),
        ("steady", "常亮", "静止的柔光描边"),
    };

    private static readonly (string Id, string Name)[] GlowColors =
    {
        ("auto", "自动"), ("theme", "主题色"), ("rainbow", "彩虹"),
        ("#7CC4FF", ""), ("#2BE07E", ""), ("#FF6FB5", ""), ("#FFC45C", ""), ("#B48CFF", ""),
    };

    private void LoadGlow() => BuildGlow();

    private IslandSettings G => SettingsStore.Current.Island;

    /// <summary>The island re-reads glow settings on its own; no full refresh (that would re-layout it).</summary>
    private static void GlowChanged() => SettingsStore.Touch();

    private void BuildGlow()
    {
        var box = GlowBox;
        box.Children.Clear();
        var sub = (Brush)FindResource("SubTextBrush");
        var faint = (Brush)FindResource("FaintTextBrush");

        Grid Row(string label, string? hint, UIElement right)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 10) };
            var left = new StackPanel { Margin = new Thickness(0, 0, 200, 0) };
            left.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("RowLabel") });
            if (hint is not null) left.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("RowHint"), TextWrapping = TextWrapping.Wrap });
            g.Children.Add(left);
            if (right is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Right; fe.VerticalAlignment = VerticalAlignment.Center; }
            g.Children.Add(right);
            box.Children.Add(g);
            return g;
        }
        CheckBox Switch(bool on, Action<bool> set)
        {
            var c = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = on };
            c.Click += (_, _) => { set(c.IsChecked == true); GlowChanged(); BuildGlow(); };
            return c;
        }
        void Label(string text) => box.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = faint, Margin = new Thickness(0, 4, 0, 6) });

        Row("灵动岛流光", "沿灵动岛轮廓流动的光：放歌时持续流动，来消息时按应用颜色亮起，收纳文件时两道光汇合", Switch(G.GlowEnabled, v => G.GlowEnabled = v));
        if (!G.GlowEnabled) return;

        // ---- style
        Label("放歌时的光效");
        var styles = new WrapPanel();
        foreach (var (id, name, hint) in GlowStyles)
        {
            var sid = id;
            var b = Chip(name, G.GlowStyle == id, () => { G.GlowStyle = sid; GlowChanged(); BuildGlow(); App.PreviewGlow(); });
            b.ToolTip = hint;
            b.Margin = new Thickness(0, 0, 6, 6);
            styles.Children.Add(b);
        }
        box.Children.Add(styles);

        // ---- colour
        Label("颜色（自动 = 跟专辑封面 / 微信绿 / QQ 蓝 / 邮件琥珀）");
        var colors = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (id, name) in GlowColors)
        {
            var cid = id;
            bool on = string.Equals(G.GlowColor, id, StringComparison.OrdinalIgnoreCase);
            Button b;
            if (name.Length > 0) b = Chip(name, on, () => { G.GlowColor = cid; GlowChanged(); BuildGlow(); App.PreviewGlow(); });
            else
            {
                var col = (Color)ColorConverter.ConvertFromString(id);
                b = new Button
                {
                    Width = 26, Height = 26, Margin = new Thickness(6, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
                    Template = SwatchTemplate(col, on), ToolTip = id,
                };
                b.Click += (_, _) => { G.GlowColor = cid; GlowChanged(); BuildGlow(); App.PreviewGlow(); };
            }
            b.Margin = new Thickness(0, 0, 6, 6);
            colors.Children.Add(b);
        }
        box.Children.Add(colors);

        // ---- strength
        var slider = new Slider { Minimum = 20, Maximum = 100, Value = G.GlowStrength, Width = 200 };
        slider.ValueChanged += (_, _) => { G.GlowStrength = (int)Math.Round(slider.Value); GlowChanged(); };
        slider.PreviewMouseLeftButtonUp += (_, _) => App.PreviewGlow();
        Row("亮度", null, slider);

        // ---- speed
        var speeds = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, name) in new[] { ("calm", "舒缓"), ("balanced", "均衡"), ("snappy", "利落") })
        {
            var sid = id;
            speeds.Children.Add(Chip(name, G.GlowSpeed == id, () => { G.GlowSpeed = sid; GlowChanged(); BuildGlow(); App.PreviewGlow(); }));
        }
        Row("动画速度", null, speeds);

        // ---- scenes
        Row("放歌时流动", null, Switch(G.GlowMusic, v => G.GlowMusic = v));
        Row("消息亮起", "微信 / QQ / 通知 / 验证码弹出时，按应用颜色闪一下并保持柔光", Switch(G.GlowNotify, v => G.GlowNotify = v));
        Row("收纳与释放", "文件或截图放进置物架时两道光汇合；拖出时反向散开", Switch(G.GlowShelf, v => G.GlowShelf = v));
        Row("减少动态效果", "流动的光改为静止柔光，动画更短", Switch(G.ReduceMotion, v => G.ReduceMotion = v));

        // ---- preview
        var prev = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        prev.Children.Add(PreviewButton("预览流光", () => App.PreviewGlow()));
        prev.Children.Add(PreviewButton("预览收纳", () => App.PreviewGlow("converge")));
        prev.Children.Add(PreviewButton("预览释放", () => App.PreviewGlow("release")));
        prev.Children.Add(PreviewButton("预览消息", () => App.PreviewGlow("bloom")));
        box.Children.Add(prev);
        box.Children.Add(new TextBlock { Text = "预览会在屏幕顶部的灵动岛上播放", FontSize = 11.5, Foreground = sub, Margin = new Thickness(0, 6, 0, 0) });
    }

    private Button PreviewButton(string text, Action click)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("GhostButton"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5) };
        b.Click += (_, _) => click();
        return b;
    }

    private static ControlTemplate SwatchTemplate(Color c, bool on)
    {
        var t = new ControlTemplate(typeof(Button));
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        f.SetValue(Border.BackgroundProperty, new SolidColorBrush(c));
        f.SetValue(Border.BorderThicknessProperty, new Thickness(on ? 2.5 : 1));
        f.SetValue(Border.BorderBrushProperty, on ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
        t.VisualTree = f;
        return t;
    }
}
