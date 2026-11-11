using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Aether.Dock;

/// <summary>One icon in the Dock: magnify on hover, press bounce, running indicator.</summary>
public sealed class DockItemView : Grid
{
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Rectangle _dot;
    private readonly double _size;

    public string Label { get; set; } = "";
    public Action? Click;
    public Action? MiddleClick;
    public Func<ContextMenu?>? Menu;

    public DockItemView(double size, ImageSource? icon, string? glyph = null)
    {
        _size = size;
        Width = size + 12;
        Height = size + 16;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;

        FrameworkElement visual;
        if (icon is not null)
        {
            visual = new Image { Source = icon, Width = size, Height = size };
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        }
        else
        {
            // built-in buttons follow Settings › 卡片文字颜色 (light / dark), like the desktop cards
            _glyph = new TextBlock
            {
                Text = glyph ?? "?", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = size * 0.42, Foreground = Aether.Widgets.WidgetUi.InkBrush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var tile = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.26),
                Background = new SolidColorBrush(Aether.Widgets.WidgetUi.Ink(0x26)),
                BorderBrush = new SolidColorBrush(Aether.Widgets.WidgetUi.Ink(0x30)), BorderThickness = new Thickness(1),
                Child = _glyph,
            };
            visual = tile;
        }
        visual.VerticalAlignment = VerticalAlignment.Top;
        visual.HorizontalAlignment = HorizontalAlignment.Center;
        visual.Margin = new Thickness(0, 5, 0, 0);
        visual.RenderTransformOrigin = new Point(0.5, 1);
        visual.RenderTransform = _scale;
        Children.Add(visual);

        _dot = new Rectangle
        {
            Width = 4, Height = 4, RadiusX = 2, RadiusY = 2,
            VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 3), Visibility = Visibility.Hidden,
        };
        _dot.SetResourceReference(Shape.FillProperty, "AccentBrush");
        Children.Add(_dot);
    }

    public bool Magnify { get; set; } = true;

    private TextBlock? _glyph;
    public void SetGlyph(string g) { if (_glyph is not null) _glyph.Text = g; }

    /// <summary>0 = not running, 1 = running, 2 = running and in front.</summary>
    public void SetRunning(int state)
    {
        _dot.Visibility = state == 0 ? Visibility.Hidden : Visibility.Visible;
        _dot.Width = state == 2 ? 14 : 4;
        _dot.Opacity = state == 2 ? 1 : 0.75;
    }

    private void AnimateScale(double to, double ms)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (Magnify) AnimateScale(1.16, 140);
        DockWindow.ShowLabel(this, Label);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        AnimateScale(1.0, 180);
        DockWindow.HideLabel(this);
    }

    // ---- drag to reorder (pinned items only) ----
    /// <summary>Called with the horizontal distance from where the press started (DIPs).</summary>
    public Action<double>? DragMove;
    public Action<double>? DragEnd;
    public Action? DragStart;
    private Point? _downAt;
    private bool _dragging;
    public bool IsDragging => _dragging;

    private double PointerX(MouseEventArgs e) => Window.GetWindow(this) is { } w ? e.GetPosition(w).X : e.GetPosition(this).X;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        AnimateScale(0.9, 80);
        if (DragMove is not null) { _downAt = new Point(PointerX(e), 0); CaptureMouse(); }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_downAt is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
        double dx = PointerX(e) - d.X;
        if (!_dragging && Math.Abs(dx) < 6) return;
        if (!_dragging)
        {
            _dragging = true;
            DockWindow.HideLabel(this);
            AnimateScale(1.12, 120);
            Panel.SetZIndex(this, 10);
            DragStart?.Invoke();
        }
        DragMove?.Invoke(dx);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) FinishDrag(e);
        _downAt = null;
    }

    private void FinishDrag(MouseEventArgs e)
    {
        double dx = _downAt is { } d ? PointerX(e) - d.X : 0;
        _dragging = false;
        _downAt = null;
        Panel.SetZIndex(this, 0);
        AnimateScale(1.0, 160);
        DragEnd?.Invoke(dx);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging) { FinishDrag(e); ReleaseMouseCapture(); e.Handled = true; return; }
        _downAt = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        AnimateScale(Magnify && IsMouseOver ? 1.16 : 1.0, 160);
        if (IsMouseOver) Click?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle) { MiddleClick?.Invoke(); e.Handled = true; }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var menu = Menu?.Invoke();
        if (menu is not null)
        {
            DockWindow.HideLabel(this);
            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
        }
        e.Handled = true;
    }
}
