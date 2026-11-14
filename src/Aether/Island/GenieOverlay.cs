using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aether.Interop;
using Aether.Shell;

namespace Aether.Island;

/// <summary>
/// The "genie" effect for 窗口收纳: a snapshot of the window, cut into thin horizontal strips,
/// funnels into a point at the island (or, reversed, pours back out of it). Strips nearest the
/// island narrow and travel first, the far edge follows — the window looks sucked through a straw.
/// Drawn in a temporary click-through layered window covering just the area the effect needs.
/// </summary>
public sealed class GenieOverlay : Window
{
    private const int Strips = 128; // 64 left visible steps along the funnel's sides

    private readonly BitmapSource _img;
    private readonly Rect _from;        // window bounds, screen DIPs
    private readonly Point _to;         // island mouth, screen DIPs
    private readonly bool _reverse;
    private readonly double _ms;
    private readonly CroppedBitmap[] _slices = new CroppedBitmap[Strips];
    private readonly Stopwatch _clock = new();
    private readonly TaskCompletionSource _done = new();
    private readonly Surface _surface;
    private double _p;

    /// <summary>Play the effect; completes when it's over (the overlay closes itself).</summary>
    public static Task Play(BitmapSource img, Rect from, Point to, bool reverse, double ms = 620)
    {
        var w = new GenieOverlay(img, from, to, reverse, ms);
        w.Show();
        return w._done.Task;
    }

    private GenieOverlay(BitmapSource img, Rect from, Point to, bool reverse, double ms)
    {
        _img = img; _from = from; _to = to; _reverse = reverse; _ms = ms;
        Title = "Aether Genie";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;

        // cover the window and the path up to the island, with a little room
        var area = Rect.Union(from, new Rect(to.X - 40, to.Y - 4, 80, 8));
        area.Inflate(12, 12);
        Left = area.Left; Top = area.Top; Width = area.Width; Height = area.Height;

        int ph = img.PixelHeight, pw = img.PixelWidth;
        for (int i = 0; i < Strips; i++)
        {
            int y0 = (int)Math.Floor(ph * i / (double)Strips), y1 = (int)Math.Floor(ph * (i + 1) / (double)Strips);
            var c = new CroppedBitmap(img, new Int32Rect(0, y0, pw, Math.Max(1, y1 - y0)));
            c.Freeze();
            _slices[i] = c;
        }
        _surface = new Surface(this);
        Content = _surface;
        _p = reverse ? 1 : 0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        // click-through, never activates, not in Alt+Tab
        WindowEffects.AddExStyle(h, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | 0x20 /* WS_EX_TRANSPARENT */, Native.WS_EX_APPWINDOW);
        _clock.Start();
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double t = Math.Min(1, _clock.Elapsed.TotalMilliseconds / _ms);
        _p = _reverse ? 1 - t : t;
        _surface.InvalidateVisual();
        if (t >= 1)
        {
            CompositionTarget.Rendering -= OnFrame;
            // tell the caller first (it may show the real window under us), then go a moment later
            _done.TrySetResult();
            var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            later.Tick += (_, _) => { later.Stop(); Close(); };
            later.Start();
        }
    }

    private static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }

    private sealed class Surface : FrameworkElement
    {
        private readonly GenieOverlay _o;
        public Surface(GenieOverlay o) { _o = o; IsHitTestVisible = false; }

        protected override void OnRender(DrawingContext dc)
        {
            var o = _o;
            double p = o._p;
            if (p >= 0.999) return; // fully inside the island
            // local coordinates
            double ox = o.Left, oy = o.Top;
            double fx = o._from.X - ox, fy = o._from.Y - oy, fw = o._from.Width, fh = o._from.Height;
            double tx = o._to.X - ox, ty = o._to.Y - oy;
            double neck = 26; // width at the island mouth

            // per-edge (v = 0 top … 1 bottom) state
            double Y(double v)
            {
                double k = Smooth(p * 1.65 - v * 0.65);           // near edge leaves first
                return fy + v * fh + (ty - (fy + v * fh)) * k;
            }
            (double cx, double w) X(double v)
            {
                double k = Smooth(p * 2.1 - v * 1.1);             // narrowing runs ahead of the travel
                double cx = (fx + fw / 2) + (tx - (fx + fw / 2)) * k;
                double w = fw + (neck - fw) * k;
                return (cx, Math.Max(2, w));
            }

            double fade = p > 0.82 ? 1 - (p - 0.82) / 0.18 : 1;
            if (fade < 1) dc.PushOpacity(fade);
            double prevY = Y(0);
            for (int i = 0; i < Strips; i++)
            {
                double v0 = i / (double)Strips, v1 = (i + 1) / (double)Strips;
                double y0 = prevY, y1 = Y(v1);
                prevY = y1;
                if (y1 - y0 < 0.15) continue; // collapsed into the neck
                var (cx, w) = X((v0 + v1) / 2);
                dc.DrawImage(o._slices[i], new Rect(cx - w / 2, y0, w, y1 - y0 + 0.6)); // +0.6: no seams
            }
            if (fade < 1) dc.Pop();
        }
    }
}
