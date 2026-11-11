using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Aether.Core;
using Aether.Shell;
using static Aether.Widgets.WidgetUi;

namespace Aether.Widgets;

/// <summary>
/// A note card: free text or a to-do checklist. Several can be open at once; each keeps its
/// own note in Data\notes.json. Text is editable in place; export to .txt / .md.
/// </summary>
public sealed class NotesWidget : WidgetWindow
{
    private readonly Note _note;

    public NotesWidget(WidgetConfig cfg) : base(cfg)
    {
        Title = "Aether Note";
        _note = NotesStore.Get(cfg.NoteId);
        if (cfg.NoteId != _note.Id) { cfg.NoteId = _note.Id; SettingsStore.Touch(); }
        Deactivated += (_, _) => NotesStore.SaveNow();
    }

    protected override bool WantsCard => true;
    protected override bool AcceptsKeyboard => true;

    private static TextBox Box(string text, double size, bool multiline, FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, FontFamily = Ui, FontWeight = weight ?? FontWeights.Normal,
        Foreground = WidgetUi.InkBrush, CaretBrush = WidgetUi.InkBrush, Background = Brushes.Transparent,
        BorderThickness = new Thickness(0), Padding = new Thickness(0),
        AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x8C, 0x9E, 0xFF)),
        VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
    };

    protected override FrameworkElement BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(16, 12, 14, 14), Width = 270 };

        // header: icon, editable title, mode switch
        var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Glyph(_note.Mode == "todo" ? "" : "", 12.5);
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        head.Children.Add(icon);
        var title = Box(_note.Title, 13, false, FontWeights.SemiBold);
        title.Margin = new Thickness(8, 0, 6, 0);
        title.Opacity = 0.9;
        title.TextChanged += (_, _) => { _note.Title = title.Text; Changed(); };
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        var mode = IconButton(_note.Mode == "todo" ? "" : "", 22, 10, () =>
        {
            _note.Mode = _note.Mode == "todo" ? "text" : "todo";
            Changed();
            Refresh();
        }, _note.Mode == "todo" ? "切换成便签" : "切换成待办");
        Grid.SetColumn(mode, 2);
        head.Children.Add(mode);
        root.Children.Add(head);

        if (_note.Mode == "todo") root.Children.Add(BuildTodo());
        else
        {
            var text = Box(_note.Text, 13, true);
            text.MinHeight = 110;
            text.MaxHeight = 380;
            System.Windows.Documents.Block.SetLineHeight(text, 20);
            text.TextChanged += (_, _) => { _note.Text = text.Text; Changed(); };
            if (_note.Text.Length == 0)
            {
                var grid = new Grid();
                var hint = Text("想到什么写什么…", 13, FontWeights.Normal, 0.35);
                hint.IsHitTestVisible = false;
                text.TextChanged += (_, _) => hint.Visibility = text.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                grid.Children.Add(text);
                grid.Children.Add(hint);
                root.Children.Add(grid);
            }
            else root.Children.Add(text);
        }
        return root;
    }

    private FrameworkElement BuildTodo()
    {
        var list = new StackPanel();
        int done = 0;
        foreach (var item in _note.Items)
        {
            var it = item;
            if (it.Done) done++;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var check = new Button
            {
                Template = Plain(), Focusable = false, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                Content = new Border
                {
                    Width = 16, Height = 16, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1.4),
                    BorderBrush = new SolidColorBrush(WidgetUi.Ink(0x99)),
                    Child = it.Done ? new TextBlock { Text = "", FontFamily = Icons, FontSize = 10, Foreground = WidgetUi.InkBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null,
                },
            };
            if (it.Done) ((Border)check.Content).SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            check.Click += (_, _) => { it.Done = !it.Done; Changed(); Refresh(); };
            row.Children.Add(check);
            var text = Box(it.Text, 13, false);
            text.Margin = new Thickness(10, 0, 4, 0);
            text.Opacity = it.Done ? 0.45 : 0.95;
            if (it.Done) text.TextDecorations = TextDecorations.Strikethrough;
            text.TextChanged += (_, _) => { it.Text = text.Text; Changed(); };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var del = IconButton("", 20, 8, () => { _note.Items.Remove(it); Changed(); Refresh(); }, "删除");
            del.Opacity = 0;
            row.MouseEnter += (_, _) => del.Opacity = 1;
            row.MouseLeave += (_, _) => del.Opacity = 0;
            Grid.SetColumn(del, 2);
            row.Children.Add(del);
            list.Children.Add(row);
        }

        var add = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        add.ColumnDefinitions.Add(new ColumnDefinition());
        add.Children.Add(Glyph("", 12, 0.5));
        var input = Box("", 13, false);
        input.Margin = new Thickness(12, 0, 0, 0);
        var hint = Text("添加待办，回车确认", 13, FontWeights.Normal, 0.35);
        hint.Margin = new Thickness(12, 0, 0, 0);
        hint.IsHitTestVisible = false;
        input.TextChanged += (_, _) => hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || input.Text.Trim().Length == 0) return;
            _note.Items.Add(new TodoItem { Text = input.Text.Trim() });
            Changed();
            Refresh();
            Dispatcher.BeginInvoke(() => FocusAddBox(), System.Windows.Threading.DispatcherPriority.Input);
        };
        Grid.SetColumn(input, 1);
        Grid.SetColumn(hint, 1);
        add.Children.Add(input);
        add.Children.Add(hint);
        _addBox = input;
        list.Children.Add(add);

        if (_note.Items.Count > 0)
        {
            var summary = Text($"已完成 {done} / {_note.Items.Count}", 11, FontWeights.Normal, 0.45);
            summary.Margin = new Thickness(0, 8, 0, 0);
            list.Children.Add(summary);
        }
        return list;
    }

    private TextBox? _addBox;

    private void FocusAddBox()
    {
        if (_addBox is null) return;
        Activate();
        _addBox.Focus();
        Keyboard.Focus(_addBox);
    }

    private void Changed()
    {
        _note.Updated = DateTime.Now;
        NotesStore.Touch();
    }

    protected override void AddMenuItems(ContextMenu menu)
    {
        menu.Items.Add(Item("新建便签", () => WidgetHost.Add("notes")));
        menu.Items.Add(Item(_note.Mode == "todo" ? "切换成便签" : "切换成待办", () => { _note.Mode = _note.Mode == "todo" ? "text" : "todo"; Changed(); Refresh(); }));
        menu.Items.Add(Item("导出…", Export));
        menu.Items.Add(Item("删除此便签", () =>
        {
            var ok = MessageBox.Show($"删除“{_note.Title}”？内容会一起删除。", "Aether", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK) return;
            NotesStore.Delete(_note);
            WidgetHost.Remove(Config.Id);
        }));
    }

    private void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出便签",
            FileName = string.Join("_", _note.Title.Split(Path.GetInvalidFileNameChars())),
            Filter = "Markdown|*.md|文本文件|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog() != true) return;
        bool md = dlg.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(dlg.FileName, NotesStore.Export(_note, md), new System.Text.UTF8Encoding(true));
    }
}
