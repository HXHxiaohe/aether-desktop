using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Aether.Island;

/// <summary>
/// 静默渐隐: when nothing has happened for a while (no music, messages, timers, hover), the
/// island slowly fades to a faint outline; any activity or the pointer coming near brings it back.
/// </summary>
public sealed partial class IslandWindow
{
    private DateTime _lastActivity = DateTime.Now;
    private bool _faded;

    /// <summary>From Poll (5×/s): decide whether the island should be faded.</summary>
    private void IdleFadeTick(double cursorX, double cursorY)
    {
        var now = DateTime.Now;
        bool calm = _mode is Mode.Compact or Mode.Idle
                    && !Playing && !TimerService.Active && !_hover && !_pressed && !_dragHot
                    && NotificationService.Unseen == 0;
        // the pointer near the island counts as activity (so it lights up before you reach it)
        double half = Math.Max(_surface.ActualWidth / 2 + 80, 160);
        bool near = Math.Abs(cursorX - CenterX) < half && cursorY < Top + _surface.ActualHeight + 70;
        if (!calm || near) _lastActivity = now;

        bool fade = S.IdleFade && (now - _lastActivity).TotalSeconds >= Math.Clamp(S.IdleFadeSeconds, 10, 600);
        if (fade == _faded) return;
        _faded = fade;
        if (Content is not UIElement root) return;
        // slow to fade, quick to come back
        var a = new DoubleAnimation(fade ? Math.Clamp(S.IdleFadeOpacity / 100.0, 0.05, 0.9) : 1,
            TimeSpan.FromMilliseconds(fade ? 1600 : 220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        root.BeginAnimation(OpacityProperty, a);
    }

    /// <summary>Something happened (toast, OSD, expand…): wake up right away.</summary>
    private void MarkActivity()
    {
        _lastActivity = DateTime.Now;
        if (_faded && Content is UIElement root)
        {
            _faded = false;
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        }
    }
}
