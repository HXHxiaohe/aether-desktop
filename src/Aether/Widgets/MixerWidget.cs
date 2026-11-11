using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Aether.Apps;
using Aether.Core;
using Aether.Island;
using Aether.Shell;
using static Aether.Widgets.WidgetUi;

namespace Aether.Widgets;

/// <summary>Volume mixer card: master volume plus one slider per app that has audio open.</summary>
public sealed class MixerWidget : WidgetWindow
{
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
    private List<AudioService.MixerApp> _apps = new();
    private bool _dragging;

    public MixerWidget(WidgetConfig cfg) : base(cfg)
    {
        Title = "Aether Mixer";
        _apps = AudioService.MixerApps();
        // Only rebuild when the set of apps changes (not while you drag a slider).
        _poll.Tick += (_, _) =>
        {
            if (_dragging) return;
            var now = AudioService.MixerApps();
            if (!now.Select(a => a.ExePath).SequenceEqual(_apps.Select(a => a.ExePath), StringComparer.OrdinalIgnoreCase))
            {
                _apps = now;
                Refresh();
            }
        };
        _poll.Start();
        AudioService.VolumeChanged += OnMaster;
        GameMode.Changed += OnGameMode;
    }

    private void OnGameMode(bool on) { if (on) _poll.Stop(); else { _poll.Start(); _apps = AudioService.MixerApps(); Refresh(); } }

    protected override void OnClosedCleanup()
    {
        _poll.Stop();
        AudioService.VolumeChanged -= OnMaster;
        GameMode.Changed -= OnGameMode;
    }

    protected override bool WantsCard => true;

    private Slider? _master;
    private TextBlock? _masterText;

    private void OnMaster(float v, bool muted)
    {
        if (_master is null || _dragging) return;
        _master.Value = v;
        if (_masterText is not null) _masterText.Text = muted ? "静音" : $"{v * 100:0}";
    }

    protected override FrameworkElement BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14), Width = 290 };
        root.Children.Add(Header("", "音量混音"));
        root.Children.Add(Row(null, "主音量", AudioService.Volume, AudioService.Muted,
            v => AudioService.SetVolume(v), () => AudioService.ToggleMute(), out _master, out _masterText));
        if (_apps.Count > 0)
            root.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(WidgetUi.Ink(0x22)), Margin = new Thickness(0, 8, 0, 8) });
        foreach (var app in _apps.Take(8))
        {
            var a = app;
            root.Children.Add(Row(ShellIcons.Get(a.ExePath, 48), a.Name, a.Volume, a.Muted,
                v => AudioService.SetAppVolume(a.Pids, v), () => { AudioService.SetAppVolume(a.Pids, a.Volume, !a.Muted); _apps = AudioService.MixerApps(); Refresh(); },
                out _, out _));
        }
        if (_apps.Count == 0)
        {
            var t = Text("还没有程序打开声音", 11.5, FontWeights.Normal, 0.45);
            t.Margin = new Thickness(0, 8, 0, 0);
            root.Children.Add(t);
        }
        return root;
    }

    private FrameworkElement Row(ImageSource? icon, string name, float value, bool muted, Action<float> set, Action toggleMute, out Slider slider, out TextBlock valueText)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        var iconBtn = new Button { Template = Plain(), Focusable = false, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "静音 / 取消静音", VerticalAlignment = VerticalAlignment.Center };
        iconBtn.Content = icon is null
            ? Glyph(muted ? "" : "", 14)
            : new Image { Source = icon, Width = 18, Height = 18, Opacity = muted ? 0.35 : 1 };
        iconBtn.Click += (_, _) => toggleMute();
        row.Children.Add(iconBtn);

        var label = Text(name, 12, FontWeights.Normal, muted ? 0.4 : 0.85);
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        var s = new Slider { Minimum = 0, Maximum = 1, Value = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
        var vt = Text(muted ? "静音" : $"{value * 100:0}", 11.5, FontWeights.SemiBold, 0.75);
        vt.HorizontalAlignment = HorizontalAlignment.Right;
        vt.VerticalAlignment = VerticalAlignment.Center;
        s.ValueChanged += (_, e) => { set((float)e.NewValue); vt.Text = $"{e.NewValue * 100:0}"; };
        s.PreviewMouseLeftButtonDown += (_, _) => _dragging = true;
        s.PreviewMouseLeftButtonUp += (_, _) => _dragging = false;
        Grid.SetColumn(s, 2);
        row.Children.Add(s);
        Grid.SetColumn(vt, 3);
        row.Children.Add(vt);
        slider = s;
        valueText = vt;
        return row;
    }
}
