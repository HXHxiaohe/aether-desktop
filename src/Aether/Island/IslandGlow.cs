using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Aether.Island;

/// <summary>
/// 流光 — light that lives on the island's outline. Drawn in a layer above the surface (the
/// surface clips to its own outline, the glow must spill outside it). The outline is sampled
/// into evenly spaced points; a "parameter" t ∈ [0,1] runs from the top-centre clockwise
/// (capsule) or from the right ear round the bottom to the left ear (notch — the top edge is
/// the screen edge, so it has no light). Everything is drawn as three passes per segment:
/// a wide faint halo, a medium glow and a thin bright core.
///
/// Continuous styles (while music plays / a message is showing):
///   scan 扫描光带 · comet 彗星掠影 · dual 双子流光 · pulse 声波脉冲 · breathe 呼吸 · steady 常亮
/// One-shots: converge 收纳汇合 (top → both sides → meet at the bottom, flash),
///   release 释放 (the reverse), bloom 瞬时绽放 (whole ring flashes and widens).
/// </summary>
public sealed class IslandGlow : FrameworkElement
{
    private readonly IslandSurface _surface;

    public IslandGlow(IslandSurface surface)
    {
        _surface = surface;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    // ---------------- state driven by the island ----------------
    /// <summary>Continuous style while a scene is active, or "" for none.</summary>
    public string Style { get; set; } = "";
    /// <summary>0..1 how present the continuous light is (eased toward <see cref="Target"/>).</summary>
    public double Presence { get; private set; }
    public double Target { get; set; }
    public Color Color { get; set; } = Color.FromRgb(0x7C, 0xC4, 0xFF);
    public bool Rainbow { get; set; }
    /// <summary>Overall strength 0..1 (setting).</summary>
    public double Strength { get; set; } = 0.7;
    /// <summary>Speed multiplier (舒缓 0.7 / 均衡 1 / 利落 1.4).</summary>
    public double Speed { get; set; } = 1;
    /// <summary>Less motion: continuous styles become a still glow; one-shots stay but shorter.</summary>
    public bool Reduce { get; set; }
    /// <summary>Music level 0..1 for pulse.</summary>
    public double Audio { get; set; }

    private sealed class Shot { public string Kind = ""; public double Start, Duration; public Color Color; }
    private readonly List<Shot> _shots = new();

    private double _time;      // seconds since start, scaled by Speed
    private double _clock;     // real seconds

    public bool Busy => Presence > 0.003 || Target > 0 || _shots.Count > 0;
    public bool HasShots => _shots.Count > 0;

    public void Play(string kind, Color color)
    {
        double dur = kind switch { "converge" => 1.05, "release" => 0.9, "bloom" => 0.8, _ => 0.8 };
        if (Reduce) dur *= 0.75;
        _shots.Add(new Shot { Kind = kind, Start = _clock, Duration = dur / Math.Max(0.4, Speed), Color = color });
    }

    /// <summary>Advance by dt seconds and repaint.</summary>
    public void Tick(double dt)
    {
        Advance(dt);
        InvalidateVisual();
    }

    /// <summary>Advance time without repainting (while the island is resizing).</summary>
    public void Advance(double dt)
    {
        _clock += dt;
        _time += dt * Speed;
        double k = 1 - Math.Exp(-dt * (Target > Presence ? 5 : 3.5));
        Presence += (Target - Presence) * k;
        if (Target == 0 && Presence < 0.003) Presence = 0;
        _shots.RemoveAll(s => _clock - s.Start > s.Duration);
    }

    // ---------------- outline sampling ----------------
    private Point[] _pts = Array.Empty<Point>();
    private bool _closed;
    private (double W, double H, double R, bool N) _key;
    private const double Step = 2.5;

    private void EnsurePath()
    {
        double w = _surface.ActualWidth, h = _surface.ActualHeight;
        var key = (Math.Round(w, 1), Math.Round(h, 1), Math.Round(_surface.Radius, 1), _surface.Notch);
        if (key == _key && _pts.Length > 0) return;
        _key = key;
        if (w < 4 || h < 4) { _pts = Array.Empty<Point>(); return; }

        var flat = _surface.Outline(w, h).GetFlattenedPathGeometry(0.2, ToleranceType.Absolute);
        var raw = new List<Point>();
        foreach (var fig in flat.Figures)
        {
            raw.Add(fig.StartPoint);
            foreach (var seg in fig.Segments)
            {
                if (seg is PolyLineSegment pl) raw.AddRange(pl.Points);
                else if (seg is LineSegment ls) raw.Add(ls.Point);
            }
        }
        if (raw.Count < 3) { _pts = Array.Empty<Point>(); return; }
        // straight edges come out as just their two ends: subdivide so "top centre" is a real point
        var dense = new List<Point>(raw.Count * 4) { raw[0] };
        for (int i = 1; i < raw.Count; i++)
        {
            var a = raw[i - 1]; var b = raw[i];
            int parts = Math.Max(1, (int)((b - a).Length / 2));
            for (int k = 1; k <= parts; k++) dense.Add(a + (b - a) * (k / (double)parts));
        }
        raw = dense;

        List<Point> path;
        if (_surface.Notch)
        {
            // skip the top edge: start where it ends (right ear) and go round to the left ear
            int start = 0;
            for (int i = 0; i < raw.Count; i++) if (raw[i].Y < 0.5 && raw[i].X > w) { start = i; break; }
            path = raw.GetRange(start, raw.Count - start);
            _closed = false;
        }
        else
        {
            // start at the top centre, going right (clockwise on screen)
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < raw.Count; i++)
            {
                double d = Math.Abs(raw[i].X - w / 2) + Math.Abs(raw[i].Y) * 3;
                if (d < bd) { bd = d; best = i; }
            }
            int cnt = raw.Count;
            int dir = raw[(best + 1) % cnt].X >= raw[best].X ? 1 : -1; // go right along the top
            path = new List<Point>(cnt + 1);
            for (int i = 0; i < cnt; i++) path.Add(raw[((best + dir * i) % cnt + cnt) % cnt]);
            path.Add(path[0]);
            _closed = true;
        }
        _pts = Resample(path, Step);
    }

    private static Point[] Resample(List<Point> p, double step)
    {
        double total = 0;
        for (int i = 1; i < p.Count; i++) total += (p[i] - p[i - 1]).Length;
        int n = Math.Max(8, (int)(total / step));
        var res = new Point[n + 1];
        int seg = 1; double acc = 0, segLen = (p[1] - p[0]).Length;
        for (int k = 0; k <= n; k++)
        {
            double want = total * k / n;
            while (seg < p.Count - 1 && acc + segLen < want) { acc += segLen; seg++; segLen = (p[seg] - p[seg - 1]).Length; }
            double f = segLen > 0 ? Math.Clamp((want - acc) / segLen, 0, 1) : 0;
            res[k] = p[seg - 1] + (p[seg] - p[seg - 1]) * f;
        }
        return res;
    }

    // ---------------- drawing ----------------
    private static readonly double[] PassWidth = { 11, 5, 1.7 };
    private static readonly double[] PassAlpha = { 0.10, 0.28, 0.95 };

    private double[] _amp = Array.Empty<double>();

    protected override void OnRender(DrawingContext dc)
    {
        if (!Busy) return;
        EnsurePath();
        int n = _pts.Length;
        if (n < 4) return;
        if (_amp.Length != n) _amp = new double[n];
        Array.Clear(_amp);
        _base = 0;

        double strength = Math.Clamp(Strength, 0.1, 1);
        double widthBoost = 1;
        bool any = false;

        // ---- continuous style
        if (Presence > 0.003 && Style.Length > 0)
        {
            any = true;
            double P = Presence;
            string style = Reduce && Style is "scan" or "comet" or "dual" ? "steady" : Style;
            switch (style)
            {
                case "scan":
                    Base(0.32 * P);
                    Band(Frac(_time / 3.6), 0.13, 0.13, 1.0 * P);
                    break;
                case "comet":
                    Base(0.16 * P);
                    Band(Frac(_time / 2.4), 0.03, 0.34, 1.1 * P);
                    break;
                case "dual":
                    Base(0.22 * P);
                    Band(Frac(_time / 4.2), 0.10, 0.10, 0.9 * P);
                    Band(Frac(_time / 4.2 + 0.5), 0.10, 0.10, 0.9 * P);
                    break;
                case "pulse":
                {
                    double a = Math.Clamp(Audio, 0, 1);
                    double idle = 0.5 + 0.5 * Math.Sin(_time * 2 * Math.PI / 3.0);
                    double lvl = a > 0.02 ? a : idle * 0.35;
                    Base((0.25 + 0.75 * lvl) * P);
                    widthBoost = 1 + lvl * 0.7;
                    break;
                }
                case "breathe":
                    Base((0.22 + 0.45 * (0.5 + 0.5 * Math.Sin(_time * 2 * Math.PI / 3.4))) * P);
                    break;
                default: // steady
                    Base(0.55 * P);
                    break;
            }
        }

        // ---- one-shots (each may carry its own colour; the last one wins the colour)
        Color color = Color;
        foreach (var s in _shots)
        {
            double p = Math.Clamp((_clock - s.Start) / s.Duration, 0, 1);
            any = true;
            color = s.Color;
            switch (s.Kind)
            {
                case "converge":
                {
                    // two heads from the top (t=0 and t=1) racing to the bottom (t=0.5)
                    double run = Math.Min(1, p / 0.72);
                    double e = 1 - Math.Pow(1 - run, 2.2);
                    double fadeOut = p < 0.72 ? 1 : 1 - (p - 0.72) / 0.28;
                    double head = 0.5 * e;
                    Band(head, 0.025, 0.16, 1.15 * fadeOut);
                    Band(1 - head, 0.16, 0.025, 1.15 * fadeOut);
                    if (p > 0.62)
                    {
                        // they meet: a burst at the bottom centre, ring flashes
                        double q = (p - 0.62) / 0.38;
                        double burst = Math.Sin(Math.Min(1, q * 1.6) * Math.PI);
                        Band(0.5, 0.06 + q * 0.12, 0.06 + q * 0.12, 1.6 * burst);
                        Base(0.35 * burst);
                    }
                    break;
                }
                case "release":
                {
                    // reverse: a burst at the bottom, two heads run back up to the top and vanish
                    double e = 1 - Math.Pow(1 - p, 2);
                    double fade = p < 0.75 ? 1 : 1 - (p - 0.75) / 0.25;
                    if (p < 0.3) Band(0.5, 0.08, 0.08, 1.4 * Math.Sin(p / 0.3 * Math.PI));
                    double head = 0.5 - 0.5 * e;
                    Band(head, 0.16, 0.025, 1.1 * fade);
                    Band(1 - head, 0.025, 0.16, 1.1 * fade);
                    break;
                }
                case "bloom":
                {
                    double f = p < 0.18 ? p / 0.18 : 1 - (p - 0.18) / 0.82;
                    Base(1.0 * f);
                    widthBoost = Math.Max(widthBoost, 1 + p * 1.4);
                    break;
                }
            }
        }
        if (!any) return;

        // ---- paint: three passes, alpha per segment quantised so pens can be reused
        var origin = _surface.TranslatePoint(new Point(0, 0), this);
        dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
        var ring = Ring();
        for (int pass = 0; pass < 3; pass++)
        {
            double w = PassWidth[pass] * (pass < 2 ? widthBoost : 1);
            var pens = PensFor(pass, w, color);
            // uniform part: one geometry per pass
            double ba = _base * strength * PassAlpha[pass];
            if (ba >= 0.012)
            {
                int bl = Math.Min(Levels - 1, (int)(Math.Min(1, ba) * (Levels - 1) + 0.5));
                if (bl > 0) dc.DrawGeometry(null, Rainbow ? RainbowRingPen(w, ba) : pens[bl], ring);
            }
            // moving bands: only the lit segments
            for (int i = 0; i < n - 1; i++)
            {
                double a = (_amp[i] + _amp[i + 1]) * 0.5 * strength * PassAlpha[pass];
                if (a < 0.012) continue;
                int level = Math.Min(Levels - 1, (int)(Math.Min(1, a) * (Levels - 1) + 0.5));
                if (level <= 0) continue;
                var pen = Rainbow ? RainbowPen(pass, w, i / (double)n, a) : pens[level];
                dc.DrawLine(pen, _pts[i], _pts[i + 1]);
            }
        }
        dc.Pop();
    }

    private static double Frac(double x) => x - Math.Floor(x);

    /// <summary>Uniform light on the whole outline: drawn once as a geometry, not per segment.</summary>
    private double _base;
    private void Base(double a) { if (a > 0) _base += a; }

    private StreamGeometry? _ring;
    private Point[]? _ringFor;
    private Geometry Ring()
    {
        if (_ring is not null && ReferenceEquals(_ringFor, _pts)) return _ring;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(_pts[0], false, _closed);
            ctx.PolyLineTo(_pts, true, true);
        }
        g.Freeze();
        _ring = g; _ringFor = _pts;
        return g;
    }

    /// <summary>Animations that move need a smooth frame rate; still glows don't.</summary>
    public bool NeedsFastFrames => _shots.Count > 0 || (Presence > 0.003 && Style is "scan" or "comet" or "dual" or "pulse" && !Reduce);

    /// <summary>A soft band centred at t. ahead/behind = reach (in t) in each direction.</summary>
    private void Band(double t, double ahead, double behind, double amp)
    {
        if (amp <= 0) return;
        int n = _amp.Length;
        for (int i = 0; i < n; i++)
        {
            double u = i / (double)(n - 1);
            double d = u - t;
            if (_closed) { if (d > 0.5) d -= 1; else if (d < -0.5) d += 1; }
            double reach = d >= 0 ? ahead : behind;
            if (reach <= 0) continue;
            double x = Math.Abs(d) / reach;
            if (x >= 1) continue;
            // smooth bell; tails (long reach) fall off more gently
            double v = Math.Pow(1 - x * x, reach > 0.1 ? 1.4 : 2.2);
            _amp[i] += amp * v;
        }
    }

    // ---------------- pens ----------------
    private const int Levels = 28;
    private readonly Pen[][] _pens = new Pen[3][];
    private readonly (Color C, double W)[] _penKey = new (Color, double)[3];

    private Pen[] PensFor(int pass, double width, Color c)
    {
        if (_pens[pass] is { } cached && _penKey[pass].C == c && Math.Abs(_penKey[pass].W - width) < 0.05) return cached;
        var arr = new Pen[Levels];
        for (int l = 0; l < Levels; l++)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(255 * l / (double)(Levels - 1)), c.R, c.G, c.B));
            b.Freeze();
            var p = new Pen(b, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            p.Freeze();
            arr[l] = p;
        }
        _pens[pass] = arr;
        _penKey[pass] = (c, width);
        return arr;
    }

    private Pen RainbowPen(int pass, double width, double u, double alpha)
    {
        double hue = Frac(u + _time * 0.08) * 360;
        var c = Hsv(hue, 0.75, 1);
        var b = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Min(1, alpha)), c.R, c.G, c.B));
        return new Pen(b, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
    }

    /// <summary>Rainbow ring: a gradient that slowly turns around the island.</summary>
    private Pen RainbowRingPen(double width, double alpha)
    {
        var b = new LinearGradientBrush { MappingMode = BrushMappingMode.RelativeToBoundingBox };
        double a = _time * 0.6;
        b.StartPoint = new Point(0.5 + 0.5 * Math.Cos(a), 0.5 + 0.5 * Math.Sin(a));
        b.EndPoint = new Point(0.5 - 0.5 * Math.Cos(a), 0.5 - 0.5 * Math.Sin(a));
        for (int i = 0; i <= 5; i++)
        {
            var c = Hsv(i * 72, 0.7, 1);
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * Math.Min(1, alpha)), c.R, c.G, c.B), i / 5.0));
        }
        return new Pen(b, width) { LineJoin = PenLineJoin.Round };
    }

    public static Color Hsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>Make any colour glow-worthy: full brightness, decent saturation.</summary>
    public static Color Vivid(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 210 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        double s = max == 0 ? 0 : d / max;
        return Hsv(h, Math.Clamp(Math.Max(s, 0.55), 0, 0.9), 1);
    }
}
