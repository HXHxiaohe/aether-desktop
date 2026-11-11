using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Aether.Island;

/// <summary>邮件 page + verification-code toast.</summary>
public sealed partial class IslandWindow
{
    private const int MailPage = 7;
    private StackPanel _mailList = null!;
    private TextBlock _mailStatus = null!;
    private MailItem? _toastMail;
    private DispatcherTimer? _codeTimer;

    private FrameworkElement BuildMailPage()
    {
        var page = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(Text("邮件", 11, FontWeights.SemiBold, 0.45));
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(Pill("收信", false, () => _ = MailService.CheckAsync()));
        right.Children.Add(Pill("邮箱设置", false, () => { _hover = false; Apply(); App.ShowSettings("mail"); }));
        head.Children.Add(right);
        page.Children.Add(head);
        _mailStatus = Text("", 10.5, FontWeights.Normal, 0.45);
        _mailStatus.TextWrapping = TextWrapping.Wrap;
        _mailStatus.Margin = new Thickness(0, 0, 0, 6);
        page.Children.Add(_mailStatus);
        _mailList = new StackPanel();
        page.Children.Add(new ScrollViewer { Content = _mailList, MaxHeight = 236, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        MailService.Changed += OnMailChanged;
        MailService.CodeArrived += OnMailCode;
        Closed += (_, _) => { MailService.Changed -= OnMailChanged; MailService.CodeArrived -= OnMailCode; };
        return page;
    }

    private void OnMailChanged() => Dispatcher.BeginInvoke(() => { if (_page == MailPage && _mode == Mode.Expanded) { RebuildMail(); MailService.MarkSeen(); } });

    private void OnMailCode(MailItem m) => Dispatcher.BeginInvoke(() =>
    {
        if (!S.MailCodeToast) return;
        _toastGlyph.Text = "";
        _toastArt.Source = null;
        _toastNote = null;
        _toastMail = m;
        _toastBarsPanel.Visibility = Visibility.Collapsed;
        StyleToastTitle(true);
        _toastTitle.Text = m.Code;
        _toastSub.Text = $"验证码 · {(m.From.Length > 0 ? m.From : MailProviders.Name(m.Provider))} · 移上来复制";
        _toastUntil = DateTime.Now.AddSeconds(Math.Clamp(S.MailCodeSeconds, 8, 15));
        Apply();
        ScheduleCodeHide(m);
    });

    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Segoe UI");

    /// <summary>Codes show big, monospaced and in the accent colour; everything else uses the normal title.</summary>
    private void StyleToastTitle(bool code)
    {
        if (code)
        {
            _toastTitle.FontSize = 24;
            _toastTitle.FontFamily = CodeFont;
            _toastTitle.FontWeight = FontWeights.Bold;
            _toastTitle.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        }
        else if (_toastTitle.FontFamily == CodeFont)
        {
            _toastTitle.FontSize = 14.5;
            _toastTitle.FontFamily = Ui;
            _toastTitle.FontWeight = FontWeights.SemiBold;
            _toastTitle.Foreground = Brushes.White;
            _toastMail = null;
        }
    }

    /// <summary>Re-render when a code's visible time runs out, so it gets masked on screen.</summary>
    private void ScheduleCodeHide(MailItem m)
    {
        var wait = m.CodeUntil - DateTime.Now;
        if (wait <= TimeSpan.Zero) return;
        _codeTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _codeTimer.Stop();
        _codeTimer.Interval = wait + TimeSpan.FromMilliseconds(200);
        _codeTimer.Tick -= OnCodeTimer;
        _codeTimer.Tick += OnCodeTimer;
        _codeTimer.Start();
    }

    private void OnCodeTimer(object? s, EventArgs e)
    {
        _codeTimer!.Stop();
        if (_page == MailPage) RebuildMail();
        var next = MailService.Items.Where(i => i.CodeVisible).OrderBy(i => i.CodeUntil).FirstOrDefault();
        if (next is not null) ScheduleCodeHide(next);
    }

    private void RebuildMail()
    {
        _mailList.Children.Clear();
        if (!S.Mail) { _mailStatus.Text = ""; _mailList.Children.Add(Hint("邮件已关闭（设置 › 灵动岛）")); _layoutDirty = true; return; }
        if (MailService.Accounts.Count == 0)
        {
            _mailStatus.Text = "";
            _mailList.Children.Add(Hint("还没有连接邮箱。点右上角「邮箱设置」添加 QQ / 163 / Gmail 等"));
            _layoutDirty = true;
            return;
        }
        var errors = MailService.Accounts.Where(a => a.Enabled).Select(a => (a, s: MailService.StatusOf(a))).Where(x => x.s.Length > 0).ToList();
        _mailStatus.Text = string.Join("\n", errors.Select(x => $"{x.a.Email}：{x.s}"));
        _mailStatus.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var m in MailService.Items.Take(30)) _mailList.Children.Add(MailRow(m));
        if (MailService.Items.Count == 0) _mailList.Children.Add(Hint("最近 7 天没有新邮件"));
        _layoutDirty = true;
    }

    private FrameworkElement MailRow(MailItem m)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 3), Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = "nodrag", ToolTip = "打开网页邮箱" };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var face = new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)) };
        Grid.SetColumnSpan(face, 2);
        row.Children.Add(face);

        var info = new StackPanel { Margin = new Thickness(10, 6, 8, 6) };
        var top = new Grid();
        top.Children.Add(Text(m.From.Length > 0 ? m.From : "(无发件人)", 12, FontWeights.SemiBold, 0.95));
        var when = Text(Ago(m.Date.LocalDateTime), 10, FontWeights.Normal, 0.4);
        when.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(when);
        info.Children.Add(top);
        info.Children.Add(Text(m.Subject.Length > 0 ? m.Subject : "(无主题)", 11.5, FontWeights.Normal, 0.65));
        row.Children.Add(info);

        if (m.Code is not null)
        {
            Button chip;
            if (m.CodeVisible)
            {
                chip = Pill(m.Code, true, () => { });
                chip.ToolTip = "复制验证码";
                chip.Click += (_, e) =>
                {
                    try { Clipboard.SetText(m.Code); } catch { }
                    if (chip.Content is Border { Child: TextBlock t }) t.Text = "已复制";
                    e.Handled = true;
                };
            }
            else
            {
                chip = Pill("验证码已隐藏", false, () => { });
                chip.ToolTip = $"超过 {S.MailCodeSeconds} 秒自动隐藏";
                chip.IsHitTestVisible = false;
                chip.Opacity = 0.6;
            }
            chip.VerticalAlignment = VerticalAlignment.Center;
            chip.Margin = new Thickness(0, 6, 4, 0);
            Grid.SetColumn(chip, 1);
            row.Children.Add(chip);
        }

        row.MouseEnter += (_, _) => face.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        row.MouseLeave += (_, _) => face.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindButton(d) is not null) return;
            MailService.OpenWeb(m);
            _hover = false; Apply();
            e.Handled = true;
        };
        return row;
    }
}
