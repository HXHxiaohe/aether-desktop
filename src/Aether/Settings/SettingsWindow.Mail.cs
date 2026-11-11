using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aether.Core;
using Aether.Island;

namespace Aether.Settings;

/// <summary>邮件与验证码: account list + "add mailbox" form (built in code).</summary>
public partial class SettingsWindow
{
    private string _newProvider = "qq";

    private void LoadMail()
    {
        BuildHideChoices();
        RebuildMailAccounts();
    }

    private void BuildHideChoices()
    {
        MailHideChoices.Children.Clear();
        foreach (var sec in new[] { 30, 60, 300 })
        {
            int s = sec;
            var b = Chip(s == 300 ? "5 分钟" : $"{s} 秒", SettingsStore.Current.Island.MailCodeSeconds == s, () =>
            {
                SettingsStore.Current.Island.MailCodeSeconds = s;
                SettingsStore.Touch();
                BuildHideChoices();
            });
            MailHideChoices.Children.Add(b);
        }
    }

    private Button Chip(string text, bool on, Action click)
    {
        var b = new Button
        {
            Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5),
            Style = (Style)FindResource(on ? "AccentButton" : "GhostButton"),
        };
        b.Click += (_, _) => click();
        return b;
    }

    private void RebuildMailAccounts()
    {
        var box = MailAccountsBox;
        box.Children.Clear();
        var faint = (Brush)FindResource("FaintTextBrush");
        var sub = (Brush)FindResource("SubTextBrush");

        foreach (var a in MailService.Accounts.ToList())
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = a.Email, FontSize = 14 });
            var status = MailService.StatusOf(a);
            info.Children.Add(new TextBlock { Text = MailProviders.Name(a.Provider) + (status.Length > 0 ? " · " + status : a.Enabled ? " · 已连接" : " · 已暂停"), FontSize = 12, Foreground = faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 120, 0) });
            row.Children.Add(info);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            btns.Children.Add(Chip(a.Enabled ? "暂停" : "启用", false, () => { MailService.SetEnabled(a, !a.Enabled); RebuildMailAccounts(); }));
            btns.Children.Add(Chip("删除", false, () =>
            {
                if (MessageBox.Show(this, $"删除邮箱 {a.Email}？（只是不再收信，邮箱本身不受影响）", "Aether", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
                { MailService.Remove(a); RebuildMailAccounts(); }
            }));
            row.Children.Add(btns);
            box.Children.Add(row);
        }

        // ---- add form ----
        box.Children.Add(new TextBlock { Text = MailService.Accounts.Count == 0 ? "添加邮箱" : "再添加一个邮箱", FontSize = 14, Margin = new Thickness(0, 6, 0, 8) });
        var providers = new WrapPanel();
        foreach (var (id, name) in MailProviders.All)
        {
            var pid = id;
            var c = Chip(name, _newProvider == id, () => { _newProvider = pid; RebuildMailAccounts(); });
            c.Margin = new Thickness(0, 0, 6, 6);
            providers.Children.Add(c);
        }
        box.Children.Add(providers);
        box.Children.Add(new TextBlock { Text = MailProviders.Help(_newProvider), FontSize = 12, Foreground = sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });

        TextBox Field(string hint)
        {
            box.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = faint, Margin = new Thickness(0, 0, 0, 4) });
            var t = new TextBox { Style = (Style)FindResource("HexBox"), FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), Margin = new Thickness(0, 0, 0, 10) };
            box.Children.Add(t);
            return t;
        }

        var email = Field("邮箱地址");
        TextBox? host = null, user = null;
        if (_newProvider == "custom")
        {
            host = Field("IMAP 服务器（如 imap.example.com，993 端口 SSL）");
            user = Field("登录名（留空则用邮箱地址）");
        }
        box.Children.Add(new TextBlock { Text = _newProvider is "gmail" or "icloud" ? "应用专用密码" : "授权码（不是邮箱登录密码）", FontSize = 12, Foreground = faint, Margin = new Thickness(0, 0, 0, 4) });
        var pass = new PasswordBox { Style = (Style)FindResource("PassBox"), Margin = new Thickness(0, 0, 0, 10) };
        box.Children.Add(pass);

        var result = new TextBlock { FontSize = 12, Foreground = sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        var save = new Button { Content = "测试并保存", Style = (Style)FindResource("AccentButton"), HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += async (_, _) =>
        {
            var addr = email.Text.Trim();
            if (!addr.Contains('@')) { result.Text = "请填写完整的邮箱地址"; return; }
            if (pass.Password.Length == 0) { result.Text = "请填写授权码"; return; }
            if (host is not null && host.Text.Trim().Length == 0) { result.Text = "请填写 IMAP 服务器"; return; }
            var account = new MailAccount
            {
                Provider = _newProvider, Email = addr,
                Host = host?.Text.Trim() ?? "", User = user?.Text.Trim() ?? "",
            };
            save.IsEnabled = false;
            result.Text = "正在连接…";
            var error = await MailService.TestAsync(account, pass.Password);
            save.IsEnabled = true;
            if (error is not null) { result.Text = error; return; }
            account.Secret = MailService.Protect(pass.Password);
            pass.Clear();
            MailService.Save(account);
            RebuildMailAccounts();
        };
        box.Children.Add(result);
        box.Children.Add(save);
    }
}
