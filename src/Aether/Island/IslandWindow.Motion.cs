using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Aether.Island;

/// <summary>
/// Small motions: 消息轻弹 (the island gives a little squash-and-spring on a new message, repeated
/// messages from one app merge), the music page's 封面光环 (a band of cover colours turning round
/// the cover, breathing with the music) and 切歌翻片 (the cover flips over to the new one).
/// </summary>
public sealed partial class IslandWindow
{
    // ------------------------------------------------------------------ 消息轻弹
    private string _burstKey = "";
    private int _burstCount;
    private DateTime _burstUntil;

    /// <summary>How many messages from this app in the current burst (1 = a fresh one).</summary>
    private int MessageBurst(string key)
    {
        var now = DateTime.Now;
        if (key == _burstKey && now < _burstUntil) _burstCount++;
        else { _burstKey = key; _burstCount = 1; }
        _burstUntil = now.AddSeconds(6);
        return _burstCount;
    }

    /// <summary>A quick squash (wider, flatter) that springs back. strength 0..1.</summary>
    private void Bounce(double strength)
    {
        if (S.ReduceMotion) return;
        if (_surface.RenderTransform is not ScaleTransform st || st.IsFrozen)
        {
            st = new ScaleTransform(1, 1);
            _surface.RenderTransformOrigin = new Point(0.5, 0);
            _surface.RenderTransform = st;
        }
        DoubleAnimationUsingKeyFrames Key(double peak)
        {
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1 + (peak - 1) * strength, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(85)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(560)), new ElasticEase { Oscillations = 2, Springiness = 5 }));
            return a;
        }
        st.BeginAnimation(ScaleTransform.ScaleXProperty, Key(1.045));
        st.BeginAnimation(ScaleTransform.ScaleYProperty, Key(0.93));
    }

    // ------------------------------------------------------------------ 封面光环
    private Border _ringOuter = null!, _ringInner = null!;
    private LinearGradientBrush _ringBrush = null!;
    private RotateTransform _ringTurn = null!;
    private double _ringLevel;

    /// <summary>Wraps the music page cover: a soft wide ring and a crisp thin one share a turning gradient.</summary>
    private FrameworkElement BuildCoverRing(Border cover)
    {
        _ringTurn = new RotateTransform(0, 0.5, 0.5);
        _ringBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), RelativeTransform = _ringTurn };
        UpdateCoverRingColors();
        double size = cover.Width;
        _ringOuter = new Border
        {
            Width = size + 14, Height = size + 14, CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(5),
            BorderBrush = _ringBrush, Opacity = 0, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        _ringInner = new Border
        {
            Width = size + 6, Height = size + 6, CornerRadius = new CornerRadius(19), BorderThickness = new Thickness(1.6),
            BorderBrush = _ringBrush, Opacity = 0, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var g = new Grid { Width = size + 14, Height = size + 14, Margin = new Thickness(-7) };
        g.Children.Add(_ringOuter);
        g.Children.Add(_ringInner);
        cover.HorizontalAlignment = HorizontalAlignment.Center;
        cover.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(cover);
        return g;
    }

    /// <summary>Three neighbouring hues of the cover colour (or the theme accent).</summary>
    private void UpdateCoverRingColors()
    {
        if (_ringBrush is null) return;
        var baseColor = IslandGlow.Vivid(_coverColor ?? Aether.Theme.ThemeService.Accent);
        var (h, s) = HueOf(baseColor);
        _ringBrush.GradientStops.Clear();
        _ringBrush.GradientStops.Add(new GradientStop(IslandGlow.Hsv(h - 45, s, 1), 0));
        _ringBrush.GradientStops.Add(new GradientStop(baseColor, 0.5));
        _ringBrush.GradientStops.Add(new GradientStop(IslandGlow.Hsv(h + 45, s, 1), 1));
    }

    private static (double H, double S) HueOf(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 210 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h, Math.Clamp(max == 0 ? 0 : d / max, 0.5, 0.85));
    }

    /// <summary>Runs with the spectrum frame (~15 fps, only while bars are visible): turn and breathe.</summary>
    private void CoverRingFrame()
    {
        if (_ringOuter is null) return;
        bool on = S.CoverRing && Playing && _mode == Mode.Expanded && _page == 0 && !S.ReduceMotion;
        if (!on)
        {
            if (_ringOuter.Opacity > 0) { _ringOuter.Opacity = 0; _ringInner.Opacity = 0; }
            return;
        }
        double sum = 0; int k = Math.Min(14, _levels.Length);
        for (int i = 2; i < k; i++) sum += _levels[i];
        double lvl = Math.Clamp(sum / Math.Max(1, k - 2) * 1.8, 0, 1);
        _ringLevel += (lvl - _ringLevel) * 0.4;
        _ringTurn.Angle = (_ringTurn.Angle + 3 + _ringLevel * 6) % 360; // faster when it's loud
        _ringOuter.Opacity = 0.18 + 0.42 * _ringLevel;
        _ringInner.Opacity = 0.55 + 0.45 * _ringLevel;
    }

    // ------------------------------------------------------------------ 切歌翻片
    private string? _coverTrackKey;
    private ImageSource? _flipPending;
    private bool _flipping;

    /// <summary>Set the cover; on a track change flip the old one away and the new one in.</summary>
    private void SetCoverArt(MediaSnapshot? m, ImageSource? art)
    {
        string? key = m?.TrackKey;
        bool changed = key is not null && _coverTrackKey is not null && key != _coverTrackKey;
        _coverTrackKey = key ?? _coverTrackKey;
        if (_flipping) { _flipPending = art; return; } // the cover often arrives just after the title
        if (!changed || !S.CoverFlip || S.ReduceMotion || _mCover.Source is null || art is null)
        {
            _cCover.Source = _mCover.Source = art;
            return;
        }
        _flipping = true;
        _flipPending = art;
        var boxes = new FrameworkElement[] { _mCoverBox, _cCoverBox };
        var away = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        away.Completed += (_, _) =>
        {
            _cCover.Source = _mCover.Source = _flipPending;
            var back = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
            back.Completed += (_, _) => { _flipping = false; if (_flipPending != _mCover.Source) _cCover.Source = _mCover.Source = _flipPending; };
            foreach (var b in boxes) Flip(b).BeginAnimation(ScaleTransform.ScaleXProperty, back);
        };
        foreach (var b in boxes) Flip(b).BeginAnimation(ScaleTransform.ScaleXProperty, away);
    }

    private static ScaleTransform Flip(FrameworkElement e)
    {
        if (e.RenderTransform is ScaleTransform st && !st.IsFrozen) return st;
        st = new ScaleTransform(1, 1);
        e.RenderTransformOrigin = new Point(0.5, 0.5);
        e.RenderTransform = st;
        return st;
    }
}
