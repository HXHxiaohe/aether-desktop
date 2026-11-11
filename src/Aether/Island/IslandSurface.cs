using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Aether.Island;

/// <summary>
/// The island's body. Notch: flush with the top of the screen, rounded bottom corners and
/// concave "ears" that flare into the top edge. Capsule: a plain rounded pill. The child is
/// clipped to the same outline. Width/Height are the body size; the ears sit outside it.
/// </summary>
public sealed class IslandSurface : Decorator
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(IslandSurface), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OverlayProperty = DependencyProperty.Register(
        nameof(Overlay), typeof(Brush), typeof(IslandSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(IslandSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(IslandSurface), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty NotchProperty = DependencyProperty.Register(
        nameof(Notch), typeof(bool), typeof(IslandSurface), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    /// <summary>Optional second layer (e.g. a glow) drawn over the fill, inside the outline.</summary>
    public Brush? Overlay { get => (Brush?)GetValue(OverlayProperty); set => SetValue(OverlayProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary>Bottom corner radius (capsule: all corners).</summary>
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public bool Notch { get => (bool)GetValue(NotchProperty); set => SetValue(NotchProperty, value); }

    /// <summary>Size of the flared ears on each side (notch only).</summary>
    public double Ear => Notch ? Math.Min(12, Math.Max(3, ActualHeight * 0.4)) : 0;

    protected override Size ArrangeOverride(Size size)
    {
        Child?.Arrange(new Rect(size));
        Clip = Outline(size.Width, size.Height);
        return size;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var geo = Outline(ActualWidth, ActualHeight);
        dc.DrawGeometry(Fill, null, geo);
        if (Overlay is not null) dc.DrawGeometry(Overlay, null, geo);
        if (Stroke is not null && !Notch) dc.DrawGeometry(null, new Pen(Stroke, 1), Inset(geo));
    }

    private static Geometry Inset(Geometry g) => g; // 1px stroke inside the clip is fine at our sizes

    /// <summary>
    /// Outline in local coordinates. For the notch the ears extend left/right of the body,
    /// so the geometry spans [-ear, width+ear]; the window leaves room for them.
    /// </summary>
    public Geometry Outline(double w, double h)
    {
        w = Math.Max(w, 1);
        h = Math.Max(h, 1);
        double r = Math.Min(Radius, Math.Min(w / 2, h / (Notch ? 1 : 2)));
        if (!Notch)
        {
            var rr = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
            rr.Freeze();
            return rr;
        }

        double e = Ear;
        var fig = new PathFigure { StartPoint = new Point(-e, 0), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(new Point(w + e, 0), true));
        // right ear: concave arc from the top edge into the body's right side
        fig.Segments.Add(new ArcSegment(new Point(w, e), new Size(e, e), 0, false, SweepDirection.Counterclockwise, true));
        fig.Segments.Add(new LineSegment(new Point(w, h - r), true));
        fig.Segments.Add(new ArcSegment(new Point(w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(new Point(r, h), true));
        fig.Segments.Add(new ArcSegment(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(new Point(0, e), true));
        // left ear
        fig.Segments.Add(new ArcSegment(new Point(-e, 0), new Size(e, e), 0, false, SweepDirection.Counterclockwise, true));
        var geo = new PathGeometry(new[] { fig });
        geo.Freeze();
        return geo;
    }
}
