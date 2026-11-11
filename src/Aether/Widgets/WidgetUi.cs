using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Aether.Widgets;

/// <summary>Shared look for the desktop cards: fonts, header row, small buttons.</summary>
internal static class WidgetUi
{
    public static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // ---- text colour: light (white, for dark wallpapers) or dark (near-black, for light ones) ----
    /// <summary>Settings › 外观与颜色 › 卡片文字: "dark" = dark text.</summary>
    public static bool DarkText => Aether.Core.SettingsStore.Current.Theme.WidgetText == "dark";
    /// <summary>The "ink" colour cards draw text, icons and lines in (alpha kept).</summary>
    public static Color Ink(byte alpha = 0xFF) => DarkText ? Color.FromArgb(alpha, 0x16, 0x18, 0x20) : Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF);
    public static Brush InkBrush => DarkText ? DarkInk : LightInk;
    /// <summary>Text on an ink-filled chip (the opposite of ink).</summary>
    public static Color Paper => DarkText ? Color.FromRgb(0xF4, 0xF5, 0xF8) : Color.FromRgb(0x12, 0x13, 0x18);
    /// <summary>Halo colour behind text: dark behind light text, light behind dark text.</summary>
    public static Color Halo => DarkText ? Colors.White : Colors.Black;
    private static readonly Brush LightInk = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush DarkInk = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x20)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public static TextBlock Text(string t, double size, FontWeight? weight = null, double opacity = 1, FontFamily? font = null) => new()
    {
        Text = t, FontSize = size, FontWeight = weight ?? FontWeights.Normal, Opacity = opacity,
        Foreground = WidgetUi.InkBrush, FontFamily = font ?? Ui, TextTrimming = TextTrimming.CharacterEllipsis,
        Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.25, Color = Halo },
    };

    public static TextBlock Glyph(string g, double size, double opacity = 1) => new()
    {
        Text = g, FontFamily = Icons, FontSize = size, Foreground = WidgetUi.InkBrush, Opacity = opacity,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>"GLYPH  标题" header used at the top of every card.</summary>
    public static FrameworkElement Header(string glyph, string title, FrameworkElement? right = null)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Glyph(glyph, 12.5, 0.9);
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        left.Children.Add(icon);
        var t = Text(title, 12, FontWeights.SemiBold, 0.75);
        t.Margin = new Thickness(8, 0, 0, 0);
        left.Children.Add(t);
        g.Children.Add(left);
        if (right is not null)
        {
            right.HorizontalAlignment = HorizontalAlignment.Right;
            g.Children.Add(right);
        }
        return g;
    }

    public static ControlTemplate Plain()
    {
        var t = new ControlTemplate(typeof(Button));
        t.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        return t;
    }

    public static Button IconButton(string glyph, double size, double glyphSize, Action click, string tip, bool filled = false)
    {
        var inner = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush(filled ? WidgetUi.Ink(0xEE) : WidgetUi.Ink(0x1C)),
            Child = new TextBlock
            {
                Text = glyph, FontFamily = Icons, FontSize = glyphSize,
                Foreground = filled ? new SolidColorBrush(WidgetUi.Paper) : WidgetUi.InkBrush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var b = new Button { Content = inner, Template = Plain(), Focusable = false, Cursor = Cursors.Hand, ToolTip = tip };
        b.Click += (_, _) => click();
        b.MouseEnter += (_, _) => inner.Opacity = 0.8;
        b.MouseLeave += (_, _) => inner.Opacity = 1;
        return b;
    }

    public static MenuItem Item(string header, Action act)
    {
        var i = new MenuItem { Header = header };
        i.Click += (_, _) => act();
        return i;
    }
}
