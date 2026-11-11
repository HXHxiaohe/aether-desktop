using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using Aether.Theme;

namespace Aether.Island;

/// <summary>流光: which light shows when, in which colour. The drawing lives in <see cref="IslandGlow"/>.</summary>
public sealed partial class IslandWindow
{
    private IslandGlow _glow = null!;
    private DispatcherTimer? _glowTimer;
    private readonly Stopwatch _glowClock = new();
    private DateTime _msgToastUntil, _glowPreviewUntil;
    private Color _msgToastColor;

    private static readonly Color WeChatGreen = Color.FromRgb(0x2B, 0xE0, 0x7E);
    private static readonly Color QqBlue = Color.FromRgb(0x3D, 0xB9, 0xFF);
    private static readonly Color MailAmber = Color.FromRgb(0xFF, 0xC4, 0x5C);

    private void BuildGlow(System.Windows.Controls.Grid root)
    {
        _glow = new IslandGlow(_surface);
        root.Children.Add(_glow);
        // Debug: AETHER_GLOW_DEMO=scan|comet|…|converge|release|bloom keeps showing that effect.
        if (Environment.GetEnvironmentVariable("AETHER_GLOW_DEMO") is { Length: > 0 } demo)
        {
            Loaded += (_, _) =>
            {
                if (demo is "converge" or "release" or "bloom")
                {
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
                    t.Tick += (_, _) => GlowShot(demo, IslandGlow.Vivid(ThemeService.Accent));
                    t.Start();
                }
                else { S.GlowStyle = demo; _glowPreviewUntil = DateTime.MaxValue; UpdateGlowScene(); }
            };
        }
    }

    /// <summary>Colour a message from this app should glow in (auto mode).</summary>
    private static Color AppGlowColor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("微信") || n.Contains("wechat") || n.Contains("weixin")) return WeChatGreen;
        if (n.Contains("qq") || n.Contains("tim")) return QqBlue;
        return IslandGlow.Vivid(ThemeService.Accent);
    }

    /// <summary>The colour setting applied to a scene's natural colour.</summary>
    private Color GlowColor(Color natural)
    {
        var c = S.GlowColor;
        if (c == "theme") return IslandGlow.Vivid(ThemeService.Accent);
        if (c.StartsWith('#') && ColorConverter.ConvertFromString(c) is Color fixedColor) return fixedColor;
        return natural;
    }

    /// <summary>A message toast is showing: glow in the app's colour while it's up.</summary>
    private void MarkMessageToast(string appName)
    {
        _msgToastUntil = _toastUntil;
        _msgToastColor = AppGlowColor(appName);
        if (S.GlowEnabled && S.GlowNotify) GlowShot("bloom", _msgToastColor);
    }

    private void GlowShot(string kind, Color natural)
    {
        if (!S.GlowEnabled || _glow is null) return;
        if (kind is "converge" or "release" && !S.GlowShelf) return;
        _glow.Rainbow = S.GlowColor == "rainbow";
        _glow.Play(kind, GlowColor(natural));
        StartGlowTimer();
    }

    /// <summary>Settings › 光效 › 预览: show the chosen style for a few seconds.</summary>
    public void PreviewGlow(string? oneShot = null)
    {
        if (oneShot is not null) { GlowShot(oneShot, IslandGlow.Vivid(ThemeService.Accent)); return; }
        _glowPreviewUntil = DateTime.Now.AddSeconds(6);
        UpdateGlowScene();
    }

    /// <summary>Decide the continuous light for the current mode (called from Apply).</summary>
    private void UpdateGlowScene()
    {
        if (_glow is null) return;
        _glow.Strength = Math.Clamp(S.GlowStrength, 10, 100) / 100.0;
        _glow.Speed = S.GlowSpeed switch { "calm" => 0.7, "snappy" => 1.4, _ => 1.0 };
        _glow.Reduce = S.ReduceMotion;
        _glow.Rainbow = S.GlowColor == "rainbow";

        var now = DateTime.Now;
        bool visible = _mode is Mode.Compact or Mode.Expanded or Mode.Toast or Mode.Osd or Mode.Alert;
        string style = "";
        Color natural = IslandGlow.Vivid(ThemeService.Accent);
        if (!S.GlowEnabled || !visible) style = "";
        else if (now < _glowPreviewUntil) style = S.GlowStyle;
        else if (_mode == Mode.Toast && _toastUntil == _msgToastUntil && S.GlowNotify)
        {
            style = "breathe";
            natural = _msgToastColor;
        }
        else if (S.GlowMusic && Playing && _mode is Mode.Compact or Mode.Expanded or Mode.Toast)
        {
            style = S.GlowStyle;
            if (_coverColor is { } cc) natural = IslandGlow.Vivid(cc);
        }
        if (style.Length > 0)
        {
            _glow.Style = style;
            _glow.Color = GlowColor(natural);
        }
        _glow.Target = style.Length > 0 ? 1 : 0;
        if (_glow.Target > 0 || _glow.Busy) StartGlowTimer();
    }

    private bool _glowResizing;

    /// <summary>
    /// While the island grows/shrinks: a bitmap cache would be re-rasterised every frame and the
    /// outline re-sampled every frame — that made hovering stutter. Hide the light, drop the
    /// cache, let the resize run on its own; <see cref="GlowResume"/> brings both back.
    /// </summary>
    private void GlowPauseForResize()
    {
        if (_glow is null) return;
        _surface.CacheMode = null;
        if (!_glow.Busy) return;
        // the light rides along: repaint on every WPF frame, stretching the last outline
        _glow.Follow = true;
        if (!_glowResizing) System.Windows.Media.CompositionTarget.Rendering += OnGlowResizeFrame;
        _glowResizing = true;
    }

    private void OnGlowResizeFrame(object? sender, EventArgs e) => _glow.InvalidateVisual();

    private void GlowResume()
    {
        if (_glow is null || !_glowResizing) return;
        _glowResizing = false;
        System.Windows.Media.CompositionTarget.Rendering -= OnGlowResizeFrame;
        _glow.Follow = false; // exact outline again
        _glow.InvalidateVisual();
        ApplyGlowCache();
    }

    /// <summary>
    /// Cache the island body as a bitmap while the light animates — only when collapsed: the
    /// expanded island is big, and rasterising its cache costs a visible frame right after it opens.
    /// </summary>
    private void ApplyGlowCache()
    {
        bool want = _glowTimer is { IsEnabled: true } && !_glowResizing && _mode != Mode.Expanded
                    && Environment.GetEnvironmentVariable("AETHER_GLOW_NOCACHE") != "1";
        if (want && _surface.CacheMode is null) _surface.CacheMode = new BitmapCache();
        else if (!want && _surface.CacheMode is not null) _surface.CacheMode = null;
    }

    private void StartGlowTimer()
    {
        if (_glowTimer is null)
        {
            _glowTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _glowTimer.Tick += (_, _) => GlowFrame();
        }
        if (!_glowTimer.IsEnabled)
        {
            _glowClock.Restart(); _glowTimer.Start();
            // the light repaints every frame; keep the island body as a cached bitmap meanwhile
            ApplyGlowCache();
        }
    }

    private void GlowFrame()
    {
        double dt = Math.Clamp(_glowClock.Elapsed.TotalSeconds, 0, 0.1);
        _glowClock.Restart();
        if (_glow.Style == "pulse" && _glow.Target > 0)
        {
            // music level from the spectrum the bars already use (low/mid bands)
            double sum = 0; int k = Math.Min(14, _levels.Length);
            for (int i = 2; i < k; i++) sum += _levels[i];
            double lvl = Math.Clamp(sum / Math.Max(1, k - 2) * 1.6, 0, 1);
            _glow.Audio += (lvl - _glow.Audio) * 0.45;
            UpdateSpectrumNeed();
        }
        if (_glow.Style.Length > 0 && _glow.Target > 0 && DateTime.Now >= _glowPreviewUntil && !(_mode == Mode.Toast && _toastUntil == _msgToastUntil) && !(Playing && S.GlowMusic))
            UpdateGlowScene(); // the reason for the light went away (preview over, music stopped)
        if (_glowResizing) { _glow.Advance(dt); return; } // time moves; painting follows the resize frames
        _glow.Tick(dt);
        if (!_glow.Busy) { _glowTimer!.Stop(); _surface.CacheMode = null; return; }
        // smooth for moving light, slower for still glows (each frame repaints the island window)
        var want = TimeSpan.FromMilliseconds(_glow.NeedsFastFrames ? (_glow.HasShots ? 33 : 50) : 100);
        if (_glowTimer!.Interval != want) _glowTimer.Interval = want;
    }
}
