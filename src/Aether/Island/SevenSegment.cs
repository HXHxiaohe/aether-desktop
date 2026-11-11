using System;
using System.Windows;
using System.Windows.Media;

namespace Aether.Island;

/// <summary>Tiny seven-segment ("数码管") clock text: digits and ':' only, with a soft glow.</summary>
public sealed class SevenSegment : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SevenSegment), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(SevenSegment), new FrameworkPropertyMetadata(Color.FromRgb(0xC8, 0xFF, 0x5A), FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    public double DigitHeight { get; set; } = 15;

    // segments a..g as bit flags
    private static readonly int[] Map = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };

    private double DigitW => DigitHeight * 0.52;
    private double Gap => DigitHeight * 0.22;

    protected override Size MeasureOverride(Size available)
    {
        double w = 0;
        foreach (var c in Text) w += c == ':' ? DigitHeight * 0.3 : DigitW + Gap;
        return new Size(Math.Max(0, w - Gap * 0.5), DigitHeight + 2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var on = new SolidColorBrush(Color);
        var off = new SolidColorBrush(Color.FromArgb(0x1E, Color.R, Color.G, Color.B));
        on.Freeze(); off.Freeze();
        double x = 0, h = DigitHeight, w = DigitW, t = Math.Max(1.6, h * 0.13);
        foreach (var c in Text)
        {
            if (c == ':')
            {
                double cx = x + h * 0.12;
                dc.DrawEllipse(on, null, new Point(cx, h * 0.32), t / 2, t / 2);
                dc.DrawEllipse(on, null, new Point(cx, h * 0.72), t / 2, t / 2);
                x += h * 0.3;
                continue;
            }
            if (c < '0' || c > '9') { x += w + Gap; continue; }
            int bits = Map[c - '0'];
            double slant = h * 0.08; // slight italic like real displays
            void Seg(int bit, double x1, double y1, double x2, double y2)
            {
                var b = (bits & (1 << bit)) != 0 ? on : off;
                double sx1 = x1 + slant * (1 - y1 / h), sx2 = x2 + slant * (1 - y2 / h);
                var pen = new Pen(b, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(pen, new Point(x + sx1, 1 + y1), new Point(x + sx2, 1 + y2));
            }
            double m = t * 0.9, mid = h / 2;
            Seg(0, m, 0, w - m, 0);            // a top
            Seg(1, w, m, w, mid - m);          // b upper right
            Seg(2, w, mid + m, w, h - m);      // c lower right
            Seg(3, m, h, w - m, h);            // d bottom
            Seg(4, 0, mid + m, 0, h - m);      // e lower left
            Seg(5, 0, m, 0, mid - m);          // f upper left
            Seg(6, m, mid, w - m, mid);        // g middle
            x += w + Gap;
        }
    }
}
