using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Aether.Apps;
using Aether.Core;
using Aether.Shell;
using static Aether.Widgets.WidgetUi;

namespace Aether.Widgets;

/// <summary>Quick access card: folders, files and apps one click away. Drop things on it to add.</summary>
public sealed class QuickWidget : WidgetWindow
{
    public QuickWidget(WidgetConfig cfg) : base(cfg)
    {
        Title = "Aether Quick";
        Config.Items ??= new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files) Add(f);
        };
    }

    protected override bool WantsCard => true;

    private void Add(string path)
    {
        if (Config.Items!.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return;
        Config.Items!.Add(path);
        SettingsStore.Touch();
        Refresh();
    }

    private static string NameOf(string path)
    {
        if (Directory.Exists(path))
        {
            var special = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Environment.GetFolderPath(Environment.SpecialFolder.Desktop)] = "桌面",
                [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")] = "下载",
                [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)] = "文档",
                [Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)] = "图片",
                [Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)] = "音乐",
                [Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)] = "视频",
            };
            if (special.TryGetValue(path.TrimEnd('\\'), out var n)) return n;
            var name = new DirectoryInfo(path).Name;
            return string.IsNullOrEmpty(name) ? path : name;
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    protected override FrameworkElement BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 12), Width = 290 };
        root.Children.Add(Header("", "快速访问", IconButton("", 22, 9, AddFolder, "添加文件夹")));
        var grid = new UniformGrid { Columns = 4 };
        foreach (var item in Config.Items!)
        {
            var path = item;
            var p = new StackPanel { Width = 64 };
            var img = new Image { Source = ShellIcons.Get(path, 96), Width = 34, Height = 34, Margin = new Thickness(0, 4, 0, 4) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            p.Children.Add(img);
            var t = Text(NameOf(path), 11.5, FontWeights.Normal, 0.85);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.MaxWidth = 62;
            p.Children.Add(t);
            var b = new Button { Template = Plain(), Content = new Border { Padding = new Thickness(0, 4, 0, 6), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Child = p }, Focusable = false, Cursor = Cursors.Hand, ToolTip = path, Margin = new Thickness(0, 0, 0, 4) };
            var bg = (Border)b.Content;
            b.MouseEnter += (_, _) => bg.Background = new SolidColorBrush(WidgetUi.Ink(0x1C));
            b.MouseLeave += (_, _) => bg.Background = Brushes.Transparent;
            b.Click += (_, _) => AppCatalog.Launch(path);
            var menu = new ContextMenu();
            menu.Items.Add(Item("打开", () => AppCatalog.Launch(path)));
            menu.Items.Add(Item("从快速访问移除", () => { Config.Items!.Remove(path); SettingsStore.Touch(); Refresh(); }));
            b.ContextMenu = menu;
            grid.Children.Add(b);
        }
        root.Children.Add(grid);
        if (Config.Items!.Count == 0) root.Children.Add(Text("把文件夹或程序拖到这里", 11.5, FontWeights.Normal, 0.45));
        return root;
    }

    private void AddFolder()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要放进快速访问的文件夹", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) Add(dlg.SelectedPath);
    }

    private void AddProgram()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择程序或文件", Filter = "所有文件|*.*" };
        if (dlg.ShowDialog() == true) Add(dlg.FileName);
    }

    protected override void AddMenuItems(ContextMenu menu)
    {
        menu.Items.Add(Item("添加文件夹…", AddFolder));
        menu.Items.Add(Item("添加程序或文件…", AddProgram));
    }
}
