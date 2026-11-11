using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Aether.Core;
using Aether.Interop;
using Aether.Theme;

namespace Aether.Shell;

/// <summary>
/// Base desktop widget: borderless, see-through, sits just above the wallpaper,
/// survives Win+D, never steals focus, free drag (unless the layout is locked).
/// Subclasses provide <see cref="BuildContent"/> and react to <see cref="Refresh"/>.
/// </summary>
public abstract class WidgetWindow : Window
{
    protected readonly WidgetConfig Config;
    private readonly Border _card;
    private IntPtr _hwnd;
    private bool _dragging;

    protected WidgetWindow(WidgetConfig config)
    {
        Config = config;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = WindowEffects.Layered;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Title = "Aether Widget";
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

        _card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
        };
        Content = _card;

        MouseLeftButtonDown += OnMouseDown;
        LocationChanged += (_, _) => { if (_dragging) SavePosition(); };
        ContextMenuOpening += (_, _) => ContextMenu = BuildMenu();
        ContextMenu = new ContextMenu();
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) => { ThemeService.Changed -= OnThemeChanged; OnClosedCleanup(); };
    }

    public string WidgetId => Config.Id;

    public static readonly double[] ScaleSteps = { 0.75, 0.9, 1.0, 1.15, 1.3, 1.5 };

    /// <summary>True when this widget draws a glass card behind its content.</summary>
    protected abstract bool WantsCard { get; }

    /// <summary>
    /// Card background + size options handled here for every widget. The clock has its own
    /// (style-specific) versions and turns these off.
    /// </summary>
    protected virtual bool HasCommonOptions => true;

    private bool UseCard => HasCommonOptions ? WantsCard && (Config.Card ?? true) : WantsCard;
    private double Scale => HasCommonOptions ? Math.Clamp(Config.Scale ?? 1.0, 0.6, 2.0) : 1.0;

    /// <summary>Widgets with text input (notes) may take keyboard focus when clicked.</summary>
    protected virtual bool AcceptsKeyboard => false;

    /// <summary>Called when the widget closes (stop timers, unsubscribe).</summary>
    protected virtual void OnClosedCleanup() { }

    protected abstract FrameworkElement BuildContent();

    protected virtual void AddMenuItems(ContextMenu menu) { }

    /// <summary>Rebuild visuals after an option change.</summary>
    public void Refresh()
    { Aether.Core.Diag.Hit("Widget.Refresh");
        var content = BuildContent();
        if (Scale != 1.0) content.LayoutTransform = new ScaleTransform(Scale, Scale);
        _card.Child = content;
        ApplyCard();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | (AcceptsKeyboard ? 0 : Native.WS_EX_NOACTIVATE), Native.WS_EX_APPWINDOW);
        if (WindowEffects.DebugWidgetsOnTop) Topmost = true;
        else WindowEffects.PinToDesktop(_hwnd);
        if (!WindowEffects.Layered) WindowEffects.MakeTransparent(this);
        WindowEffects.SetBorderColor(_hwnd, Native.DWMWA_COLOR_NONE);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Refresh();
        PlaceInitially();
    }

    private void ApplyCard()
    { Aether.Core.Diag.Hit("Widget.ApplyCard");
        bool card = UseCard;
        if (card)
        {
            _card.SetResourceReference(Border.BackgroundProperty, "GlassBrush");
            _card.SetResourceReference(Border.BorderBrushProperty, "GlassBorderBrush");
            _card.BorderThickness = new Thickness(1);
        }
        else
        {
            // A nearly invisible fill keeps the whole widget draggable.
            _card.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            _card.BorderBrush = null;
            _card.BorderThickness = new Thickness(0);
        }
        if (_hwnd != IntPtr.Zero)
        {
            bool blur = card && SettingsStore.Current.Theme.Blur;
            if (_blurOn && !blur)
            {
                // Can't un-blur a window cleanly: replace it with a fresh one.
                Dispatcher.BeginInvoke(() => WidgetHost.Recreate(Config.Id));
                return;
            }
            WindowEffects.SetCorners(_hwnd, card ? Native.DWMWCP_ROUND : Native.DWMWCP_DONOTROUND);
            if (blur) WindowEffects.SetBlur(_hwnd, true);
            _blurOn = blur;
        }
    }

    private bool _blurOn;

    private void OnThemeChanged() => Dispatcher.BeginInvoke(ApplyCard);

    private void PlaceInitially()
    {
        var area = SystemParameters.WorkArea;
        double left = Config.Left ?? area.Left + 48;
        double top = Config.Top ?? area.Top + 64;
        UpdateLayout();
        double w = Math.Max(ActualWidth, 40), h = Math.Max(ActualHeight, 40);
        // Keep at least part of the widget on screen if the resolution changed.
        left = Math.Clamp(left, SystemParameters.VirtualScreenLeft - w + 40, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40);
        top = Math.Clamp(top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40);
        Left = left;
        Top = top;
    }

    private void SavePosition()
    {
        Config.Left = Math.Round(Left);
        Config.Top = Math.Round(Top);
        SettingsStore.Touch();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (SettingsStore.Current.LayoutLocked || e.ButtonState != MouseButtonState.Pressed) return;
        if (e.OriginalSource is DependencyObject d && IsInteractive(d)) return;
        _dragging = true;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally
        {
            _dragging = false;
            SavePosition();
        }
    }

    private static bool IsInteractive(DependencyObject d)
    {
        for (var cur = d; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is System.Windows.Controls.Primitives.ButtonBase or TextBox or Slider or System.Windows.Controls.Primitives.Thumb) return true;
            if (cur is FrameworkElement { Tag: "nodrag" }) return true;
            if (cur is WidgetWindow) return false;
        }
        return false;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        AddMenuItems(menu);
        if (HasCommonOptions)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            var cardItem = new MenuItem { Header = "玻璃卡片背景", IsCheckable = true, IsChecked = Config.Card ?? true };
            cardItem.Click += (_, _) => { Config.Card = cardItem.IsChecked; SettingsStore.Touch(); Refresh(); };
            menu.Items.Add(cardItem);
            var size = new MenuItem { Header = "大小" };
            foreach (var s in ScaleSteps)
            {
                var v = s;
                var item = new MenuItem { Header = $"{s:P0}", IsCheckable = true, IsChecked = Math.Abs((Config.Scale ?? 1.0) - s) < 0.01 };
                item.Click += (_, _) => { Config.Scale = v; SettingsStore.Touch(); Refresh(); };
                size.Items.Add(item);
            }
            menu.Items.Add(size);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        var lockItem = new MenuItem { Header = "锁定所有组件位置", IsCheckable = true, IsChecked = SettingsStore.Current.LayoutLocked };
        lockItem.Click += (_, _) => { SettingsStore.Current.LayoutLocked = lockItem.IsChecked; SettingsStore.Touch(); };
        menu.Items.Add(lockItem);

        var hide = new MenuItem { Header = "隐藏此组件" };
        hide.Click += (_, _) => { Config.Visible = false; SettingsStore.Touch(); WidgetHost.Sync(); };
        menu.Items.Add(hide);

        var settings = new MenuItem { Header = "打开设置…" };
        settings.Click += (_, _) => App.ShowSettings();
        menu.Items.Add(settings);
        return menu;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_MOUSEACTIVATE:
                if (AcceptsKeyboard) break; // let it take focus so you can type
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            case Native.WM_WINDOWPOSCHANGING:
                if (WindowEffects.DebugWidgetsOnTop) break;
                // Always stay at the bottom of the z-order (just above the wallpaper).
                var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
                if ((pos.flags & Native.SWP_NOZORDER) == 0)
                {
                    pos.hwndInsertAfter = Native.HWND_BOTTOM;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
                }
                break;
        }
        return IntPtr.Zero;
    }
}
