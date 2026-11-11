using System;
using System.Drawing;
using System.Windows.Forms;
using Aether.Core;
using Aether.Theme;

namespace Aether.Shell;

/// <summary>Temporary tray entry. The dynamic island will take this role in a later step.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _lock;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Microsoft YaHei UI", 9f) };
        menu.Items.Add("打开设置", null, (_, _) => App.ShowSettings());
        menu.Items.Add("从桌面取色", null, async (_, _) => await ThemeService.PickNowAsync());
        _lock = new ToolStripMenuItem("锁定组件位置") { CheckOnClick = true };
        _lock.Click += (_, _) => { SettingsStore.Current.LayoutLocked = _lock.Checked; SettingsStore.Touch(); };
        menu.Items.Add(_lock);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 Aether", null, (_, _) => App.Quit());
        menu.Opening += (_, _) => _lock.Checked = SettingsStore.Current.LayoutLocked;

        Icon? icon = null;
        try { icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        _icon = new NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Text = "Aether Desktop",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) App.ShowSettings(); };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
