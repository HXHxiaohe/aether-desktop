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
    /// <summary>Spectrum bands (0..1, low → high) for the 频谱环 style; the island's own array.</summary>
    public float[]? Bands { get; set; }

    /// <summary>扫描光带: how many bands, evenly spaced (1–3).</summary>
    public int BandCount { get; set; } = 1;

    /// <summary>Styles that move (reduce motion turns these into a still glow; they need smooth frames).</summary>
    public static bool IsMoving(string style) => style is "scan" or "comet" or "dual" or "pulse" or "prism" or "aurora" or "warp" or "arc" or "bloomloop" or "embers" or "heartbeat" or "vortex" or "spectrum"
        or "particles" or "sheen" or "ripple" or "gravity" or "drip" or "liquid";
    /// <summary>Slow, soft styles look the same at 15 fps; saves a quarter of the repaints.</summary>
    public static bool IsSlow(string style) => style is "aurora" or "ripple" or "particles" or "gravity" or "sheen" or "steady" or "embers" or "liquid";
    /// <summary>Styles whose light leaves the outline and needs room around the island.</summary>
    public static bool Spills(string style) => style is "particles" or "ripple" or "aurora" or "drip" or "liquid";
    /// <summary>Extra room (DIP) the window should leave around the island right now.</summary>
    public double Outset => Presence > 0.003 && Spills(Style) && !Reduce ? 22 : 0;
    /// <summary>Styles drawn in rainbow colours along the outline regardless of the colour setting.</summary>
    public static bool IsRainbowStyle(string style) => style is "prism"; // (aurora paints its own palette)
    /// <summary>Styles that react to the music.</summary>
    public static bool WantsAudio(string style) => style is "pulse" or "spectrum";

    private sealed class Shot { public string Kind = ""; public double Start, Duration; public Color Color; }
    private readonly List<Shot> _shots = new();

    private double _time;      // seconds since start, scaled by Speed
    private double _clock;     // real seconds

    public bool Busy => Presence > 0.003 || Target > 0 || _shots.Count > 0 || _parts.Count > 0 || _drops.Count > 0;
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
        StepParticles(dt);
        StepDrops(dt);
    }

    // ---------------- 粒子溢出 ----------------
    private struct Particle { public Point P; public Vector V; public double Age, Life, Size; }
    private readonly List<Particle> _parts = new();
    private readonly Random _rng = new();
    private double _spawn;

    private void StepParticles(double dt)
    {
        bool on = Style == "particles" && Presence > 0.05 && !Reduce && _pts.Length > 8 && _nrm.Length == _pts.Length;
        if (on)
        {
            _spawn += dt * 16 * Speed;
            while (_spawn >= 1)
            {
                _spawn -= 1;
                int i = _rng.Next(_pts.Length);
                var n = _nrm[i];
                var tangent = new Vector(-n.Y, n.X) * (_rng.NextDouble() - 0.5) * 6;
                _parts.Add(new Particle
                {
                    P = _pts[i], V = n * (9 + _rng.NextDouble() * 14) + tangent,
                    Life = 0.9 + _rng.NextDouble() * 0.8, Size = 0.9 + _rng.NextDouble() * 1.3,
                });
            }
        }
        for (int k = _parts.Count - 1; k >= 0; k--)
        {
            var p = _parts[k];
            p.Age += dt;
            if (p.Age >= p.Life) { _parts.RemoveAt(k); continue; }
            p.P += p.V * dt;
            p.V *= Math.Pow(0.35, dt); // drift slows down
            _parts[k] = p;
        }
        if (_parts.Count > 120) _parts.RemoveRange(0, _parts.Count - 120);
    }

    public bool HasParticles => _parts.Count > 0;

    // ---------------- 滴落 ----------------
    private sealed class DripDrop { public double X, Y0, R, Age, Hang; public Color Color; }
    private readonly List<DripDrop> _drops = new();
    private double _dripNext;

    private void StepDrops(double dt)
    {
        bool on = Style == "drip" && Presence > 0.05 && !Reduce && _pts.Length > 8;
        if (on)
        {
            _dripNext -= dt * Speed;
            if (_dripNext <= 0)
            {
                _dripNext = 0.7 + _rng.NextDouble() * 1.1;
                // somewhere along the bottom edge, away from the rounded ends
                double w = _surface.ActualWidth, h = _surface.ActualHeight;
                double x = w * (0.22 + 0.56 * _rng.NextDouble());
                _drops.Add(new DripDrop { X = x, Y0 = h, R = 2.2 + _rng.NextDouble() * 1.6, Hang = 0.55 + _rng.NextDouble() * 0.5, Color = Color });
            }
        }
        foreach (var d in _drops) d.Age += dt;
        _drops.RemoveAll(d => d.Age > d.Hang + 0.6);
    }

    // ---------------- outline sampling ----------------
    private Point[] _pts = Array.Empty<Point>();
    private Vector[] _nrm = Array.Empty<Vector>();
    private bool _closed;
    private (double W, double H, double R, bool N) _key;
    private const double Step = 2.5;

    /// <summary>
    /// While the island is growing/shrinking: don't re-sample the outline every frame, stretch
    /// the last one to the current size instead (the light is soft — nobody sees the corners).
    /// </summary>
    public bool Follow { get; set; }

    private void EnsurePath()
    {
        double w = _surface.ActualWidth, h = _surface.ActualHeight;
        if (Follow && _pts.Length > 0) return;
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
        // outward unit normals (pointing away from the island's middle)
        _nrm = new Vector[_pts.Length];
        var mid = new Point(w / 2, _surface.Notch ? h * 0.35 : h / 2);
        for (int i = 0; i < _pts.Length; i++)
        {
            var a = _pts[Math.Max(0, i - 1)]; var b = _pts[Math.Min(_pts.Length - 1, i + 1)];
            var t = b - a;
            var n = t.Length > 0 ? new Vector(t.Y, -t.X) / t.Length : new Vector(0, 1);
            if (Vector.Multiply(n, _pts[i] - mid) < 0) n = -n;
            _nrm[i] = n;
        }
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
            string style = Reduce && IsMoving(Style) ? "steady" : Style;
            switch (style)
            {
                // ---- 霓虹
                case "prism": // 棱镜折射: three short bands, coloured by position (forced rainbow)
                    Base(0.16 * P);
                    for (int k = 0; k < 3; k++) Band(Frac(_time / 5.0 + k / 3.0), 0.06, 0.09, 0.95 * P);
                    break;
                case "aurora": // 极光: a faint rim; the wavy curtain itself is drawn in DrawSpill
                    Base(0.1 * P);
                    break;
                case "gravity": // 流淌: drawn entirely in DrawSpill (width varies along the outline)
                case "drip":    // 滴落: same heavier-below rim, plus drops (drawn in DrawSpill)
                case "liquid":  // 垂坠: the island's lower edge sags like liquid (drawn in DrawSpill)
                    break;
                // ---- 醒目
                case "warp": // 曲率加速: speeds up, the tail stretches, flash at the finish
                {
                    double c = Frac(_time / 2.6);
                    double pos = c * c * c;
                    Band(Frac(pos + 0.05), 0.02, 0.05 + 0.35 * c * c, (0.6 + 0.6 * c) * P);
                    Base((0.1 + (c > 0.9 ? 0.6 * (c - 0.9) / 0.1 : 0)) * P);
                    break;
                }
                case "arc": // 电弧跳跃: a few short sparks jumping about, ~11 times a second
                {
                    long slot = (long)Math.Floor(_time * 11);
                    var rnd = new Random(unchecked((int)(slot * 7919)));
                    Base((0.1 + 0.08 * rnd.NextDouble()) * P);
                    int sparks = 2 + rnd.Next(2);
                    for (int k = 0; k < sparks; k++) Band(rnd.NextDouble(), 0.008 + 0.012 * rnd.NextDouble(), 0.008 + 0.012 * rnd.NextDouble(), (0.9 + 0.7 * rnd.NextDouble()) * P);
                    break;
                }
                case "bloomloop": // 瞬时绽放: the whole ring flashes and widens every couple of seconds
                {
                    double c = Frac(_time / 2.4);
                    double f = c < 0.12 ? c / 0.12 : Math.Pow(1 - (c - 0.12) / 0.88, 2);
                    Base((0.14 + 0.75 * f) * P);
                    widthBoost = 1 + c * 1.1;
                    break;
                }
                // ---- 自然
                case "embers": // 余烬升腾: a dim warm glow with sparks that glow up and die down
                    Base(0.12 * P);
                    for (int k = 0; k < 7; k++)
                    {
                        double phase = _time / 1.8 + k * 0.137;
                        double life = Frac(phase);
                        var r = new Random(unchecked((int)Math.Floor(phase) * 104729 + k * 31));
                        Band(r.NextDouble(), 0.014, 0.02, Math.Sin(life * Math.PI) * 0.75 * P);
                    }
                    break;
                case "heartbeat": // 心跳: lub-dub
                {
                    double c = Frac(_time / 1.15);
                    double beat = Math.Exp(-Math.Pow(c / 0.05, 2)) + Math.Exp(-Math.Pow(1 - c, 2) / 0.0025) + 0.7 * Math.Exp(-Math.Pow((c - 0.22) / 0.05, 2));
                    Base((0.14 + 0.75 * Math.Min(1, beat)) * P);
                    widthBoost = 1 + 0.5 * Math.Min(1, beat);
                    break;
                }
                case "vortex": // 涡流: three tailed bands spinning fast
                    Base(0.1 * P);
                    for (int k = 0; k < 3; k++) Band(Frac(_time / 1.6 + k / 3.0), 0.02, 0.14, 0.85 * P);
                    break;
                // ---- 频谱
                case "spectrum": // 频谱环: the music's bands laid along the outline, mirrored from the top
                {
                    var bands = Bands;
                    Base(0.1 * P);
                    if (bands is { Length: > 8 })
                    {
                        int nb = Math.Min(bands.Length - 2, 22);
                        for (int i = 0; i < _amp.Length; i++)
                        {
                            double u = i / (double)(_amp.Length - 1);
                            double d = Math.Min(u, 1 - u) * 2;               // 0 at the top, 1 at the bottom
                            double f = d * (nb - 1);
                            int b0 = (int)f; double t = f - b0;
                            double lvl = bands[2 + b0] * (1 - t) + bands[2 + Math.Min(nb - 1, b0 + 1)] * t;
                            _amp[i] += Math.Clamp(lvl * 1.6, 0, 1.3) * P;
                        }
                    }
                    else Base(0.25 * (0.5 + 0.5 * Math.Sin(_time * 2.1)) * P);
                    break;
                }
                case "scan": // 1–3 evenly spaced bands (2 = the old 双子流光, 3 = 涡流-like)
                {
                    int nb = Math.Clamp(BandCount, 1, 3);
                    double len = nb == 1 ? 0.13 : nb == 2 ? 0.10 : 0.075;
                    Base((0.32 - 0.05 * (nb - 1)) * P);
                    for (int k = 0; k < nb; k++) Band(Frac(_time / (3.6 - 0.5 * (nb - 1)) + k / (double)nb), len, len * (nb == 3 ? 1.6 : 1), (1.0 - 0.08 * (nb - 1)) * P);
                    break;
                }
                // ---- new: the light leaves the outline
                case "particles": // 粒子溢出: a soft rim; sparks drift off it (drawn after the passes)
                    Base(0.2 * P);
                    break;
                case "sheen": // 玻璃反光: a slanted shine sweeps across the island (drawn after the passes)
                    Base(0.26 * P);
                    break;
                case "ripple": // 水波扩散: rings leave the island and fade (drawn after the passes)
                    Base(0.22 * P);
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
                default: // steady (now with the old 呼吸's slow swell built in)
                    Base((0.48 + 0.1 * Math.Sin(_time * 2 * Math.PI / 4.2)) * P);
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
        if (!any && _parts.Count == 0) return;

        // ---- paint: three passes, alpha per segment quantised so pens can be reused
        var origin = _surface.TranslatePoint(new Point(0, 0), this);
        dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
        bool stretched = Follow && _key.W > 0 && _key.H > 0;
        if (stretched) dc.PushTransform(new ScaleTransform(_surface.ActualWidth / _key.W, _surface.ActualHeight / _key.H));
        var ring = Ring();
        // one-shots keep the scene colour; neon styles paint in rainbow along the outline
        bool rainbow = Rainbow || (_shots.Count == 0 && Presence > 0.003 && IsRainbowStyle(Style));
        for (int pass = 0; pass < 3; pass++)
        {
            double w = PassWidth[pass] * (pass < 2 ? widthBoost : 1);
            var pens = PensFor(pass, w, color);
            // uniform part: one geometry per pass
            double ba = _base * strength * PassAlpha[pass];
            if (ba >= 0.012)
            {
                int bl = Math.Min(Levels - 1, (int)(Math.Min(1, ba) * (Levels - 1) + 0.5));
                if (bl > 0) dc.DrawGeometry(null, rainbow ? RainbowRingPen(w, ba) : pens[bl], ring);
            }
            // moving bands: only the lit segments
            for (int i = 0; i < n - 1; i++)
            {
                double a = (_amp[i] + _amp[i + 1]) * 0.5 * strength * PassAlpha[pass];
                if (a < 0.012) continue;
                int level = Math.Min(Levels - 1, (int)(Math.Min(1, a) * (Levels - 1) + 0.5));
                if (level <= 0) continue;
                var pen = rainbow ? RainbowPen(pass, w, i / (double)n, a) : pens[level];
                dc.DrawLine(pen, _pts[i], _pts[i + 1]);
            }
        }
        DrawSpill(dc, color, strength, rainbow);
        if (stretched) dc.Pop();
        dc.Pop();
    }

    /// <summary>The effects that leave the outline: sparks, a shine across the body, rings.</summary>
    private void DrawSpill(DrawingContext dc, Color color, double strength, bool rainbow)
    {
        double P = Presence;
        // 粒子溢出 — drawn even after the style ends, so the last sparks fade out naturally
        foreach (var p in _parts)
        {
            double t = p.Age / p.Life;
            double a = (t < 0.15 ? t / 0.15 : 1 - (t - 0.15) / 0.85) * strength;
            var c = rainbow ? Hsv(Frac(p.P.X / 300 + _time * 0.08) * 360, 0.7, 1) : color;
            double r = p.Size * (1 - 0.45 * t);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(a * 0.22, 0, 1)), c.R, c.G, c.B)), null, p.P, r * 3.4, r * 3.4);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(a * 0.95, 0, 1)), c.R, c.G, c.B)), null, p.P, r, r);
        }
        if (P <= 0.003 || Reduce) return;

        if (Style == "aurora" && _nrm.Length == _pts.Length)
        {
            // 极光: a wavy curtain floating just outside the outline. Two veils drift round in
            // opposite directions; where they are, the ribbon lifts off, widens and brightens, and
            // ripples along its length; colours run green → teal → violet.
            int n = _pts.Length;
            var q = new Point[n];
            var env = new double[n];
            double c1 = Frac(_time / 11.0), c2 = Frac(0.45 - _time / 15.0);
            for (int i = 0; i < n; i++)
            {
                double u = i / (double)(n - 1);
                double e = Veil(u, c1, 0.3) + 0.85 * Veil(u, c2, 0.24);
                env[i] = Math.Min(1, e);
                double wave = Math.Sin(2 * Math.PI * (u * 3.2 - _time / 3.2)) * 3.2 + Math.Sin(2 * Math.PI * (u * 7.0 + _time / 5.0)) * 1.2;
                double lift = 2 + 9 * env[i] + wave * env[i];
                q[i] = _pts[i] + _nrm[i] * lift;
            }
            double[] widths = { 15, 7, 2 }, alphas = { 0.13, 0.3, 0.8 };
            Color Hue(int i)
            {
                double u = i / (double)(n - 1);
                return Hsv(135 + 140 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * (u * 1.3 + _time * 0.04))), 0.65, 1);
            }
            for (int pass = 0; pass < 3; pass++)
            {
                int ps = pass;
                DrawRibbon(dc, q, _nrm, 0, n - 1, 6,
                    i => widths[ps] * (0.55 + 0.45 * env[i]),
                    i => env[i] * alphas[ps] * strength * P,
                    Hue);
            }
        }

        if (Style == "gravity")
        {
            // 流淌: light hugging the outline, thin at the top and heavier towards the bottom, with
            // drops that leave the top centre, run down both sides speeding up (gravity) and pool
            // at the bottom, which brightens as they arrive.
            int n = _pts.Length;
            double h = Math.Max(1, _surface.ActualHeight);
            var amp = new double[n];
            double pool = 0;
            for (int k = 0; k < 3; k++)
            {
                double s = Frac(_time / 2.4 + k / 3.0);
                double d = 0.5 * s * s;                         // distance from the top: accelerates
                double stretch = 0.02 + 0.07 * s;               // faster → longer streak behind it
                for (int i = 0; i < n; i++)
                {
                    double u = i / (double)(n - 1);
                    // right side runs 0 → 0.5, left side 1 → 0.5; the tail trails back up
                    double dr = u - d, dl = (1 - u) - d;
                    double vr = dr <= 0 ? Math.Exp(-Math.Pow(dr / stretch, 2)) : Math.Exp(-Math.Pow(dr / 0.015, 2));
                    double vl = dl <= 0 ? Math.Exp(-Math.Pow(dl / stretch, 2)) : Math.Exp(-Math.Pow(dl / 0.015, 2));
                    amp[i] += 0.8 * (vr + vl);
                }
                if (s > 0.85) pool += (s - 0.85) / 0.15;          // a drop is arriving
            }
            double[] baseW = { 4, 2.2, 0.8 }, addW = { 14, 6, 1.6 }, alphas = { 0.12, 0.3, 0.85 };
            if (_nrm.Length == n)
            {
                double Weight(int i) { double v = Math.Clamp(_pts[i].Y / h, 0, 1); return v * v; } // 0 top … 1 bottom
                double Bottom(int i) => Math.Exp(-Math.Pow((i / (double)(n - 1) - 0.5) / 0.12, 2));
                for (int pass = 0; pass < 3; pass++)
                {
                    int ps = pass;
                    DrawRibbon(dc, _pts, _nrm, 0, n - 1, 4,
                        i => baseW[ps] + addW[ps] * Weight(i) + (ps < 2 ? 3 * pool * Bottom(i) : 0),
                        i => (0.18 + 0.5 * Weight(i) + amp[i] + 0.6 * pool * Bottom(i)) * alphas[ps] * strength * P,
                        _ => color);
                }
            }
        }

        if (Style == "liquid" && !Follow)
        {
            // 垂坠: the island hangs like a drop of liquid — the top stays straight, the lower edge
            // sags (most in the middle) with slow waves running along it. The sagging part is filled
            // with the island's own colour, and a thin glow follows the new outline.
            int n = _pts.Length;
            double w = Math.Max(1, _surface.ActualWidth), h = Math.Max(1, _surface.ActualHeight);
            double amp = Math.Clamp(h * 0.3, 6, 20); // compact ~10 px, expanded up to 20
            var q = new Point[n];
            for (int i = 0; i < n; i++)
            {
                double v = Math.Clamp(_pts[i].Y / h, 0, 1);
                double lower = v > 0.45 ? Math.Pow((v - 0.45) / 0.55, 2) : 0;
                double x = Math.Clamp(_pts[i].X / w, 0, 1);
                double belly = 0.55 + 0.45 * Math.Sin(Math.PI * x);                 // heavier in the middle
                double wave = 0.62 + 0.24 * Math.Sin(2 * Math.PI * (x * 1.6 + _time * 0.12))
                                   + 0.14 * Math.Sin(2 * Math.PI * (x * 3.3 - _time * 0.21));
                q[i] = new Point(_pts[i].X, _pts[i].Y + lower * belly * wave * amp * P);
            }
            // the sag itself, in the island's colour (between the real outline and the sagging one)
            var skirt = new StreamGeometry();
            using (var ctx = skirt.Open())
            {
                ctx.BeginFigure(q[0], true, true);
                ctx.PolyLineTo(q, true, false);
                var back = new Point[n];
                for (int i = 0; i < n; i++) { var p0 = _pts[n - 1 - i]; back[i] = p0.Y > h * 0.5 ? new Point(p0.X, p0.Y - 2) : p0; } // overlap 2 px: no seam
                ctx.PolyLineTo(back, true, false);
            }
            skirt.Freeze();
            // a gradient fill would restart over the skirt's own bounds; use its bottom colour instead
            Brush skirtFill = _surface.Fill is GradientBrush gb && gb.GradientStops.Count > 0
                ? new SolidColorBrush(gb.GradientStops[gb.GradientStops.Count - 1].Color) : _surface.Fill;
            dc.DrawGeometry(skirtFill, null, skirt);
            // light inside the drop: a soft glow-coloured wash, brightest where it hangs lowest,
            // breathing with the waves (clipped to the sagging part by drawing on the same geometry)
            double sagMid = 0;
            for (int i = 0; i < n; i++) sagMid = Math.Max(sagMid, q[i].Y - _pts[i].Y);
            if (sagMid > 1)
            {
                var light = new RadialGradientBrush
                {
                    MappingMode = BrushMappingMode.Absolute,
                    Center = new Point(w / 2, h + sagMid * 0.35), GradientOrigin = new Point(w / 2, h + sagMid * 0.55),
                    RadiusX = w * 0.42, RadiusY = Math.Max(8, sagMid * 1.4),
                };
                double glowA = Math.Clamp((0.42 + 0.1 * Math.Sin(_time * 1.1)) * strength * P, 0, 1);
                light.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * glowA), color.R, color.G, color.B), 0));
                light.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * glowA * 0.45), color.R, color.G, color.B), 0.55));
                light.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
                dc.DrawGeometry(light, null, skirt);
            }
            // the glow along the new outline
            var line = new StreamGeometry();
            using (var ctx = line.Open()) { ctx.BeginFigure(q[0], false, _closed); ctx.PolyLineTo(q, true, true); }
            line.Freeze();
            double shimmer = 0.85 + 0.15 * Math.Sin(_time * 1.3);
            for (int pass = 0; pass < 3; pass++)
            {
                double a = (pass == 0 ? 0.12 : pass == 1 ? 0.3 : 0.9) * 0.75 * shimmer * strength * P;
                int lvl = Math.Min(Levels - 1, (int)(Math.Min(1, a) * (Levels - 1) + 0.5));
                if (lvl <= 0) continue;
                var pens = PensFor(pass, PassWidth[pass] * (pass == 2 ? 1.1 : 0.8), color);
                dc.DrawGeometry(null, rainbow ? RainbowRingPen(PassWidth[pass], a) : pens[lvl], line);
            }
        }

        if (Style == "drip" && _nrm.Length == _pts.Length)
        {
            // 滴落: the light is heavier towards the bottom; now and then a drop swells under the
            // island, lets go and falls, fading out.
            int n = _pts.Length;
            double h = Math.Max(1, _surface.ActualHeight);
            double Weight(int i) { double v = Math.Clamp(_pts[i].Y / h, 0, 1); return v * v; }
            double[] baseW = { 4, 2.2, 0.8 }, addW = { 10, 4, 1.2 }, alphas = { 0.12, 0.3, 0.85 };
            for (int pass = 0; pass < 3; pass++)
            {
                int ps = pass;
                DrawRibbon(dc, _pts, _nrm, 0, n - 1, 4,
                    i => baseW[ps] + addW[ps] * Weight(i),
                    i => (0.2 + 0.6 * Weight(i)) * alphas[ps] * strength * P,
                    _ => color);
            }
            foreach (var d in _drops)
            {
                double a, rx, ry, y;
                if (d.Age < d.Hang)
                {
                    double k = d.Age / d.Hang;                    // swelling, still attached
                    a = 0.9; rx = d.R * (0.6 + 0.4 * k); ry = d.R * (0.6 + 0.9 * k); y = d.Y0 + ry * 0.6;
                }
                else
                {
                    double tf = d.Age - d.Hang;                   // falling: y = ½gt²
                    double fall = 0.5 * 260 * tf * tf;
                    a = Math.Max(0, 1 - fall / 34);
                    rx = d.R * 0.8; ry = d.R * 1.6; y = d.Y0 + d.R + fall;
                }
                if (a <= 0) continue;
                var c = d.Color;
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(a * 0.25 * strength * P, 0, 1)), c.R, c.G, c.B)), null, new Point(d.X, y), rx * 2.6, ry * 2.2);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(a * 0.95 * strength * P, 0, 1)), c.R, c.G, c.B)), null, new Point(d.X, y), rx, ry);
            }
        }

        if (Style == "sheen" && !Follow)
        {
            // 玻璃反光: a slanted band sweeps across, then a pause
            double w = _surface.ActualWidth, h = _surface.ActualHeight;
            double c = Frac(_time / 4.6);
            if (c < 0.55)
            {
                double x = -0.35 * w + (c / 0.55) * 1.7 * w;
                double band = Math.Max(26, w * 0.16);
                var g = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                byte peak = (byte)(255 * Math.Clamp(0.5 * strength * P, 0, 1)); // 0.30 was hard to see
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(peak, 255, 255, 255), 0.45));
                g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(peak / 2), color.R, color.G, color.B), 0.6));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
                dc.PushClip(_surface.Outline(w, h));
                dc.PushTransform(new RotateTransform(22, x + band / 2, h / 2));
                dc.DrawRectangle(g, null, new Rect(x, -h, band, h * 3));
                dc.Pop();
                dc.Pop();
            }
        }
        else if (Style == "ripple")
        {
            // 水波扩散: two rings at a time grow outward from the outline and fade
            double w = Math.Max(1, _surface.ActualWidth), h = Math.Max(1, _surface.ActualHeight);
            var mid = new Point(w / 2, _surface.Notch ? 0 : h / 2);
            var ring = Ring();
            for (int k = 0; k < 2; k++)
            {
                double a = Frac(_time / 1.8 + k * 0.5);
                double grow = a * 20;
                var tf = new ScaleTransform(1 + 2 * grow / w, 1 + (_surface.Notch ? grow / h : 2 * grow / h), mid.X, mid.Y);
                double alpha = Math.Pow(1 - a, 1.6) * strength * P;
                var c = rainbow ? Hsv(Frac(_time * 0.1 + k * 0.5) * 360, 0.7, 1) : color;
                dc.PushTransform(tf);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(alpha * 0.22, 0, 1)), c.R, c.G, c.B)), 6 * (1 - a * 0.4)), ring);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(alpha * 0.85, 0, 1)), c.R, c.G, c.B)), 1.6 * (1 - a * 0.5)), ring);
                dc.Pop();
            }
        }
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
    public bool NeedsFastFrames => _shots.Count > 0 || (Presence > 0.003 && IsMoving(Style) && !Reduce);

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

    /// <summary>
    /// A variable-width stroke drawn as filled ribbon pieces (no overlapping round caps — those
    /// repainted the same pixels 5–7 times and cost ~3× the CPU). Each chunk of a few samples is
    /// one quad strip with the chunk's average alpha/colour.
    /// </summary>
    private static void DrawRibbon(DrawingContext dc, Point[] pts, Vector[] nrm, int from, int to, int chunk,
                                   Func<int, double> width, Func<int, double> alpha, Func<int, Color> color)
    {
        for (int s = from; s < to; s += chunk)
        {
            int e = Math.Min(to, s + chunk);
            double a = 0; for (int i = s; i <= e; i++) a += alpha(i); a /= e - s + 1;
            if (a < 0.015) continue;
            var c = color((s + e) / 2);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(pts[s] + nrm[s] * (width(s) / 2), true, true);
                for (int i = s + 1; i <= e; i++) ctx.LineTo(pts[i] + nrm[i] * (width(i) / 2), false, false);
                for (int i = e; i >= s; i--) ctx.LineTo(pts[i] - nrm[i] * (width(i) / 2), false, false);
            }
            g.Freeze();
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Min(1, a)), c.R, c.G, c.B)), null, g);
        }
    }

    /// <summary>Soft bump around c (circular for closed outlines), width w.</summary>
    private double Veil(double u, double c, double w)
    {
        double d = u - c;
        if (_closed) { if (d > 0.5) d -= 1; else if (d < -0.5) d += 1; }
        double x = d / w;
        return Math.Exp(-x * x * 2.2);
    }

    // aurora pens, cached by (pass, hue bucket, alpha level, width bucket)
    private readonly Dictionary<int, Pen> _auroraPens = new();
    private Pen AuroraPen(int pass, double width, double hue, double alpha)
    {
        int hb = (int)(hue / 6), al = (int)(Math.Min(1, alpha) * 24), wb = (int)Math.Round(width);
        int key = ((pass * 64 + hb) * 32 + al) * 32 + wb;
        if (_auroraPens.TryGetValue(key, out var pen)) return pen;
        var c = Hsv(hb * 6, 0.65, 1);
        var b = new SolidColorBrush(Color.FromArgb((byte)(255 * al / 24.0), c.R, c.G, c.B));
        b.Freeze();
        pen = new Pen(b, Math.Max(1, wb)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        if (_auroraPens.Count > 4000) _auroraPens.Clear();
        _auroraPens[key] = pen;
        return pen;
    }

    // pens of one colour with varying width/alpha (流淌), cached by quantised width and alpha
    private readonly Dictionary<int, Pen> _varPens = new();
    private Color _varColor;
    private Pen CachedPen(Color c, int pass, double width, double alpha)
    {
        if (c != _varColor) { _varPens.Clear(); _varColor = c; }
        int wb = (int)Math.Round(width * 2), al = (int)(Math.Min(1, alpha) * 24);
        int key = (pass * 64 + wb) * 32 + al;
        if (_varPens.TryGetValue(key, out var pen)) return pen;
        var b = new SolidColorBrush(Color.FromArgb((byte)(255 * al / 24.0), c.R, c.G, c.B));
        b.Freeze();
        pen = new Pen(b, Math.Max(0.5, wb / 2.0)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        _varPens[key] = pen;
        return pen;
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
