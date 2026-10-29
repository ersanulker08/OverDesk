using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Overdesk.App.Controls;
using Overdesk.App.Services;
using Overdesk.App.Theme;
using Overdesk.Core.Works;
using Shapes = Avalonia.Controls.Shapes;

namespace Overdesk.App;

/// <summary>
/// Edits one work: a title, rows (text, to-dos, true/false variables, numbers, fields...) and a settings drawer
/// for look (color, icon, fonts, size, width). Every change is saved by <see cref="WorkService"/>. Used full-size in a
/// tab and compact in the tools panel.
/// </summary>
internal sealed class WorkEditorView : UserControl
{
    private static readonly (BlockKind Kind, string Label)[] AddKinds =
    {
        (BlockKind.Text, "Text"), (BlockKind.Heading, "Heading"), (BlockKind.Todo, "To-do"), (BlockKind.Toggle, "True / false"),
        (BlockKind.Number, "Number"), (BlockKind.Field, "Field"), (BlockKind.Bullet, "Bullet"), (BlockKind.Quote, "Quote"),
        (BlockKind.Divider, "Divider"),
    };

    private readonly WorkService _svc;
    private readonly Work _work;
    private readonly bool _compact;
    private readonly Dictionary<string, TextBox> _editors = new();
    private readonly StackPanel _rows = new() { Spacing = 1 };
    private readonly StackPanel _meta = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private readonly TextBox _titleBox = new() { Classes = { "wtext" }, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Untitled" };
    private readonly TextBlock _saved = new() { FontSize = 11.5, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _haze = new() { IsHitTestVisible = false };
    private readonly Border _tileHost = new();
    private readonly Border _headerHost = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Border _page = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Border _drawer = new() { IsVisible = false };
    private readonly Button _settingsButton;
    private readonly Grid _root = new();
    private FontFamily _bodyFont = FontFamily.Default;
    private double _size = 15;
    private bool _building;
    private bool _confirmDelete;

    public WorkEditorView(WorkService svc, Work work, bool compact)
    {
        _svc = svc;
        _work = work;
        _compact = compact;

        _root.RowDefinitions = new RowDefinitions("Auto,*");
        if (!compact) _root.ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _haze.SetValue(Grid.RowSpanProperty, 2);
        _root.Children.Add(_haze);

        // ---- header
        _settingsButton = IconButton("IconSliders", "Look and settings", ToggleDrawer);
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(compact ? "Auto,Auto,*,Auto,Auto,Auto,Auto" : "Auto,*,Auto,Auto,Auto"),
            Margin = compact ? new Thickness(10, 10, 8, 4) : new Thickness(36, 28, 36, 6),
        };
        var col = 0;
        if (compact)
        {
            var back = IconButton("IconChevronLeft", "All works", () => BackRequested?.Invoke());
            Grid.SetColumn(back, col++);
            header.Children.Add(back);
        }
        _tileHost.Margin = new Thickness(compact ? 4 : 0, 0, 12, 0);
        _tileHost.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_tileHost, col++);
        header.Children.Add(_tileHost);

        _titleBox.FontWeight = FontWeight.SemiBold;
        _titleBox.FontSize = compact ? 24 : 40;
        _titleBox.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_titleBox, col++);
        header.Children.Add(_titleBox);

        Grid.SetColumn(_saved, col++);
        _saved.Margin = new Thickness(8, 0, 6, 0);
        header.Children.Add(_saved);

        Grid.SetColumn(_settingsButton, col++);
        header.Children.Add(_settingsButton);
        if (compact)
        {
            var tab = IconButton("IconExternal", "Open as a tab", () => OpenAsTabRequested?.Invoke());
            Grid.SetColumn(tab, col++);
            header.Children.Add(tab);
        }
        var more = IconButton("IconMore", "More", null);
        more.Click += (_, _) => ShowMoreMenu(more);
        Grid.SetColumn(more, col);
        header.Children.Add(more);
        _headerHost.Child = header;
        Grid.SetRow(_headerHost, 0);
        _root.Children.Add(_headerHost);

        // ---- body
        var bodyStack = new StackPanel { Margin = compact ? new Thickness(14, 4, 14, 40) : new Thickness(36, 4, 36, 90) };
        bodyStack.Children.Add(_meta);
        _meta.Margin = new Thickness(4, 0, 0, compact ? 8 : 18);
        bodyStack.Children.Add(_rows);
        bodyStack.Children.Add(BuildAddBar());
        _page.Child = bodyStack;
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _page,
        };
        Grid.SetRow(scroller, 1);
        _root.Children.Add(scroller);

        // ---- settings drawer: a side panel in a tab, a sheet over the page in the tools panel
        _drawer.Background = Res.Brush("Bg2Brush");
        _drawer.BorderBrush = Res.Brush("StrokeBrush");
        if (compact)
        {
            _drawer.ZIndex = 5;
            Grid.SetRow(_drawer, 1);
        }
        else
        {
            _drawer.Width = 320;
            _drawer.BorderThickness = new Thickness(1, 0, 0, 0);
            Grid.SetColumn(_drawer, 1);
            Grid.SetRowSpan(_drawer, 2);
        }
        _root.Children.Add(_drawer);

        _titleBox.TextChanged += (_, _) =>
        {
            if (_building) return;
            _work.Title = _titleBox.Text ?? "";
            Edited(listChanged: true);
        };
        _titleBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (_work.Blocks.Count > 0) FocusBlock(_work.Blocks[0].Id, atEnd: false);
        };

        Content = _root;
        ApplyLook();
        Reload();
    }

    public event Action? BackRequested;
    public event Action? OpenAsTabRequested;
    public event Action? Deleted;

    public Work Work => _work;

    // ------------------------------------------------------------------ lifecycle

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _svc.Library.Changed += OnLibraryChanged;
        _svc.Saved += OnSaved;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _svc.Library.Changed -= OnLibraryChanged;
        _svc.Saved -= OnSaved;
        _svc.Flush();
    }

    private void OnSaved() => Dispatcher.UIThread.Post(() => _saved.Text = "Saved");

    private void OnLibraryChanged(Work? work, object? sender)
    {
        if (ReferenceEquals(sender, this)) return;
        if (work is null)
        {
            if (_svc.Library.Find(_work.Id) is null) Dispatcher.UIThread.Post(() => Deleted?.Invoke());
            return;
        }
        if (work == _work) Dispatcher.UIThread.Post(() => { ApplyLook(); Reload(); });
    }

    // ------------------------------------------------------------------ look

    private void ApplyLook()
    {
        var theme = AccentThemes.Find(_work.Color);
        WorkStyle.ApplyAccent(this, theme);
        _haze.Background = WorkStyle.Haze(theme);
        _tileHost.Child = WorkStyle.IconTile(_work, _compact ? 34 : 52);
        _bodyFont = _svc.BodyFont(_work);
        _size = _svc.FontSize(_work);
        _titleBox.FontFamily = _svc.TitleFont(_work);
        _page.MaxWidth = _compact ? double.PositiveInfinity : Math.Clamp(_work.PageWidth, 380, 1400);
        _headerHost.MaxWidth = _page.MaxWidth;
        _headerHost.HorizontalAlignment = _page.HorizontalAlignment = HorizontalAlignment.Stretch; // Stretch + MaxWidth centers the column
    }

    /// <summary>Rebuild everything from the model (first show, appearance change, edit made elsewhere).</summary>
    private void Reload()
    {
        _building = true;
        if (_titleBox.Text != _work.Title) _titleBox.Text = _work.Title;
        _building = false;
        RebuildRows(null);
        BuildDrawer();
    }

    private void Edited(bool listChanged = false)
    {
        _svc.Library.Touch(_work, this, listChanged);
        _saved.Text = "Saving…";
        UpdateMeta();
    }

    private void UpdateMeta()
    {
        _meta.Children.Clear();
        var template = WorkTemplates.Find(_work.Template);
        _meta.Children.Add(Chip(template.Name));
        if (_work.TodoTotal > 0)
        {
            var done = _work.TodoDone;
            var bar = new Border { Width = _compact ? 90 : 140, Height = 6, CornerRadius = new CornerRadius(3), Background = Res.Brush("Bg4Brush"), VerticalAlignment = VerticalAlignment.Center };
            var fill = new Border
            {
                CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left,
                Width = (_compact ? 90 : 140) * done / _work.TodoTotal, Background = Res.Brush("AccentBrush"),
            };
            bar.Child = fill;
            fill.Bind(Border.BackgroundProperty, this.GetResourceObservable("AccentGradient"));
            _meta.Children.Add(bar);
            _meta.Children.Add(new TextBlock { Text = $"{done} / {_work.TodoTotal} done", FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });
        }
        var vars = _work.Blocks.Count(b => b.Kind is BlockKind.Toggle or BlockKind.Number or BlockKind.Field);
        if (vars > 0)
            _meta.Children.Add(new TextBlock { Text = $"{vars} variable{(vars == 1 ? "" : "s")}", FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });
    }

    private static Border Chip(string text) => new()
    {
        Classes = { "chip" }, VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text },
    };

    // ------------------------------------------------------------------ rows

    private void RebuildRows(string? focusId, bool atEnd = true)
    {
        _rows.Children.Clear();
        _editors.Clear();
        foreach (var block in _work.Blocks.ToList()) _rows.Children.Add(BuildRow(block));
        UpdateMeta();
        if (focusId is not null) FocusBlock(focusId, atEnd);
    }

    private void FocusBlock(string id, bool atEnd)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_editors.TryGetValue(id, out var box)) return;
            box.Focus();
            box.CaretIndex = atEnd ? (box.Text?.Length ?? 0) : 0;
        }, DispatcherPriority.Background);
    }

    private Control BuildRow(WorkBlock block)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*"), Background = Brushes.Transparent };
        var menu = IconButton("IconMore", "Row options", null);
        menu.Width = 24;
        menu.Height = 24;
        menu.Opacity = 0;
        menu.VerticalAlignment = VerticalAlignment.Top;
        menu.Margin = new Thickness(0, 3, 0, 0);
        menu.Click += (_, _) => ShowRowMenu(block, menu);
        grid.Children.Add(menu);
        grid.PointerEntered += (_, _) => menu.Opacity = 1;
        grid.PointerExited += (_, _) => menu.Opacity = 0;

        var content = block.Kind switch
        {
            BlockKind.Heading => BuildHeading(block),
            BlockKind.Text => BuildText(block),
            BlockKind.Todo => BuildTodo(block),
            BlockKind.Toggle => BuildToggle(block),
            BlockKind.Field => BuildField(block),
            BlockKind.Number => BuildNumber(block),
            BlockKind.Bullet => BuildBullet(block),
            BlockKind.Quote => BuildQuote(block),
            _ => BuildDivider(),
        };
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private TextBox Edit(WorkBlock block, string text, Action<string> set, bool multiline, double? size = null, FontFamily? font = null,
        FontWeight weight = FontWeight.Normal, string? placeholder = null, bool register = true)
    {
        var box = new TextBox
        {
            Classes = { "wtext" }, Text = text, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap,
            FontFamily = font ?? _bodyFont, FontSize = size ?? _size, FontWeight = weight, PlaceholderText = placeholder,
        };
        if (multiline) box.LineHeight = (size ?? _size) * Math.Clamp(_work.LineSpacing, 1.0, 2.6);
        box.TextChanged += (_, _) =>
        {
            if (_building) return;
            set(box.Text ?? "");
            Edited();
        };
        box.KeyDown += (_, e) => OnRowKey(block, box, e, multiline);
        if (register) _editors[block.Id] = box;
        return box;
    }

    private void OnRowKey(WorkBlock block, TextBox box, KeyEventArgs e, bool multiline)
    {
        var index = _work.Blocks.IndexOf(block);
        if (index < 0) return;
        if (e.Key == Key.Enter && !multiline && e.KeyModifiers == KeyModifiers.None && block.Kind is not (BlockKind.Field or BlockKind.Toggle or BlockKind.Number))
        {
            e.Handled = true;
            var next = new WorkBlock { Kind = block.Kind is BlockKind.Todo or BlockKind.Bullet ? block.Kind : BlockKind.Text };
            _work.Blocks.Insert(index + 1, next);
            Edited();
            RebuildRows(next.Id, atEnd: false);
        }
        else if (e.Key == Key.Back && string.IsNullOrEmpty(box.Text) && box.CaretIndex == 0 && _work.Blocks.Count > 1
                 && block.Kind is BlockKind.Todo or BlockKind.Bullet or BlockKind.Text or BlockKind.Quote or BlockKind.Heading)
        {
            e.Handled = true;
            _work.Blocks.RemoveAt(index);
            Edited();
            RebuildRows(_work.Blocks[Math.Max(index - 1, 0)].Id);
        }
    }

    private Control BuildHeading(WorkBlock block)
    {
        var size = _size * block.Level switch { 1 => 2.0, 2 => 1.5, _ => 1.2 };
        var box = Edit(block, block.Text, t => block.Text = t, false, size, _svc.TitleFont(_work), FontWeight.SemiBold, "Heading");
        box.Margin = new Thickness(0, block.Level == 1 ? 14 : 8, 0, 2);
        return box;
    }

    private Control BuildText(WorkBlock block)
        => Edit(block, block.Text, t => block.Text = t, true, placeholder: "Write something…");

    private Control BuildBullet(WorkBlock block)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*") };
        grid.Children.Add(new Border
        {
            Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Res.Brush("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(5, _size * 0.9, 0, 0),
        });
        var box = Edit(block, block.Text, t => block.Text = t, false, placeholder: "List item");
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return grid;
    }

    private Control BuildQuote(WorkBlock block)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 4) };
        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(4, 2, 8, 2) };
        bar.Bind(Border.BackgroundProperty, this.GetResourceObservable("AccentGradientDiagonal"));
        grid.Children.Add(bar);
        var box = Edit(block, block.Text, t => block.Text = t, true, font: _svc.TitleFont(_work), placeholder: "Quote");
        box.FontStyle = FontStyle.Italic;
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return grid;
    }

    private Control BuildTodo(WorkBlock block)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*") };
        var check = new CheckBox { IsChecked = block.Checked, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, _size * 0.25, 0, 0), MinWidth = 0 };
        grid.Children.Add(check);
        var box = Edit(block, block.Text, t => block.Text = t, false, placeholder: "To-do");
        void Style() => box.Opacity = block.Checked ? 0.42 : 1;
        Style();
        check.IsCheckedChanged += (_, _) =>
        {
            block.Checked = check.IsChecked == true;
            Style();
            Edited();
        };
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return grid;
    }

    private Control BuildToggle(WorkBlock block)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2) };
        var label = Edit(block, block.Text, t => block.Text = t, false, placeholder: "Variable name");
        label.FontFamily = _bodyFont;
        grid.Children.Add(label);
        var toggle = new ToggleSwitch { IsChecked = block.Checked, OnContent = "true", OffContent = "false", MinWidth = 0, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        toggle.IsCheckedChanged += (_, _) =>
        {
            block.Checked = toggle.IsChecked == true;
            Edited();
        };
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return grid;
    }

    private Control BuildField(WorkBlock block)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*"), Margin = new Thickness(0, 2) };
        var label = Edit(block, block.Text, t => block.Text = t, false, weight: FontWeight.SemiBold, placeholder: "Name");
        label.Classes.Add("dim");
        grid.Children.Add(label);
        var value = Edit(block, block.Value, t => block.Value = t, false, placeholder: "Value", register: false);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private Control BuildNumber(WorkBlock block)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2) };
        var label = Edit(block, block.Text, t => block.Text = t, false, placeholder: "Variable name");
        grid.Children.Add(label);
        var number = new NumericUpDown
        {
            Value = (decimal)block.Number, Minimum = -1_000_000_000, Maximum = 1_000_000_000, FormatString = "0.##",
            Increment = 1, Width = 140, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            FontFamily = _bodyFont,
        };
        number.ValueChanged += (_, e) =>
        {
            if (_building) return;
            block.Number = (double)(e.NewValue ?? 0);
            Edited();
        };
        Grid.SetColumn(number, 1);
        grid.Children.Add(number);
        return grid;
    }

    private static Control BuildDivider() => new Border
    {
        Height = 1, Margin = new Thickness(4, 14), Background = Res.Brush("StrokeStrongBrush"), VerticalAlignment = VerticalAlignment.Center,
    };

    // ------------------------------------------------------------------ add bar and row menu

    private Control BuildAddBar()
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 22, 0, 0) };
        wrap.Children.Add(new TextBlock { Text = "ADD", Classes = { "eyebrow" }, FontSize = 9.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 10, 0) });
        foreach (var (kind, label) in AddKinds)
        {
            var button = new Button { Classes = { "addblock" }, Content = label, Margin = new Thickness(0, 0, 6, 6) };
            var k = kind;
            button.Click += (_, _) =>
            {
                var block = NewBlock(k);
                _work.Blocks.Add(block);
                Edited();
                RebuildRows(block.Id, atEnd: false);
            };
            wrap.Children.Add(button);
        }
        return wrap;
    }

    private static WorkBlock NewBlock(BlockKind kind) => new() { Kind = kind, Level = kind == BlockKind.Heading ? 2 : 1 };

    private void ShowRowMenu(WorkBlock block, Control anchor)
    {
        var index = _work.Blocks.IndexOf(block);
        var items = new List<Control>();
        var up = Entry("Move up", "IconUp", () => Move(block, -1));
        up.IsEnabled = index > 0;
        var down = Entry("Move down", "IconChevronDown", () => Move(block, 1));
        down.IsEnabled = index < _work.Blocks.Count - 1;
        items.Add(up);
        items.Add(down);

        var turn = new MenuItem { Header = "Turn into" };
        foreach (var (kind, label) in AddKinds.Where(k => k.Kind != block.Kind))
        {
            var k = kind;
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => Convert(block, k);
            turn.Items.Add(item);
        }
        items.Add(turn);

        if (block.Kind == BlockKind.Heading)
        {
            var size = new MenuItem { Header = "Heading size" };
            foreach (var (level, name) in new[] { (1, "Large"), (2, "Medium"), (3, "Small") })
            {
                var l = level;
                var item = new MenuItem { Header = name };
                item.Click += (_, _) =>
                {
                    block.Level = l;
                    Edited();
                    RebuildRows(block.Id);
                };
                size.Items.Add(item);
            }
            items.Add(size);
        }

        items.Add(new Separator());
        items.Add(Entry("Duplicate", "IconCopy", () =>
        {
            var copy = block.Clone();
            _work.Blocks.Insert(index + 1, copy);
            Edited();
            RebuildRows(copy.Id);
        }));
        var delete = Entry("Delete row", "IconTrash", () =>
        {
            _work.Blocks.Remove(block);
            Edited();
            RebuildRows(null);
        });
        delete.Classes.Add("danger");
        items.Add(delete);
        new ContextMenu { ItemsSource = items }.Open(anchor);
    }

    private void Move(WorkBlock block, int delta)
    {
        var index = _work.Blocks.IndexOf(block);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _work.Blocks.Count) return;
        _work.Blocks.RemoveAt(index);
        _work.Blocks.Insert(target, block);
        Edited();
        RebuildRows(block.Id);
    }

    private void Convert(WorkBlock block, BlockKind kind)
    {
        // Keep the text when moving between text-like rows; a Field keeps its label as text.
        block.Kind = kind;
        if (kind == BlockKind.Heading && block.Level is < 1 or > 3) block.Level = 2;
        Edited();
        RebuildRows(block.Id);
    }

    private static MenuItem Entry(string header, string icon, Action onClick)
    {
        var item = new MenuItem { Header = header, Icon = new Shapes.Path { Classes = { "icon" }, Data = Res.Icon(icon) } };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static Button IconButton(string icon, string tip, Action? onClick)
    {
        var button = new Button
        {
            Classes = { "icon" }, Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center,
            Content = new Shapes.Path { Classes = { "icon" }, Data = Res.Icon(icon) },
        };
        ToolTip.SetTip(button, tip);
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    private void ShowMoreMenu(Control anchor)
    {
        var items = new List<Control>
        {
            Entry(_work.Pinned ? "Unpin" : "Pin to the top", "IconPin", () =>
            {
                _work.Pinned = !_work.Pinned;
                Edited(listChanged: true);
                BuildDrawer();
            }),
            Entry("Copy as Markdown", "IconCopy", CopyMarkdown),
            Entry("Duplicate", "IconCopy", () =>
            {
                var copy = _svc.Library.Duplicate(_work);
                _svc.OpenInTab(copy);
            }),
            new Separator(),
            Entry("Delete…", "IconTrash", () =>
            {
                if (!_drawer.IsVisible) ToggleDrawer();
                _confirmDelete = true;
                BuildDrawer();
            }),
        };
        ((MenuItem)items[^1]).Classes.Add("danger");
        new ContextMenu { ItemsSource = items }.Open(anchor);
    }

    private async void CopyMarkdown()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(_work.ToMarkdown());
        _saved.Text = "Copied as Markdown";
    }

    // ------------------------------------------------------------------ settings drawer

    public void ShowDrawer()
    {
        if (!_drawer.IsVisible) ToggleDrawer();
    }

    private void ToggleDrawer()
    {
        _drawer.IsVisible = !_drawer.IsVisible;
        _settingsButton.Classes.Set("selected", _drawer.IsVisible);
        if (_drawer.IsVisible) BuildDrawer();
    }

    private void BuildDrawer()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(18, 16, 18, 24) };
        panel.Children.Add(Eyebrow("COLOR", first: true));
        var swatches = new WrapPanel();
        foreach (var theme in AccentThemes.All)
        {
            var t = theme;
            var swatch = new Button
            {
                Classes = { "swatch" }, Margin = new Thickness(0, 0, 8, 8), Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
                Content = new Border { CornerRadius = new CornerRadius(15), Background = AccentThemes.Gradient(theme.Primary, theme.Secondary, horizontal: false), Margin = new Thickness(3) },
            };
            swatch.Classes.Set("selected", string.Equals(_work.Color, theme.Name, StringComparison.OrdinalIgnoreCase));
            ToolTip.SetTip(swatch, theme.Name);
            swatch.Click += (_, _) =>
            {
                _work.Color = t.Name;
                Edited(listChanged: true);
                ApplyLook();
                Reload();
            };
            swatches.Children.Add(swatch);
        }
        panel.Children.Add(swatches);

        panel.Children.Add(Eyebrow("ICON"));
        var icons = new WrapPanel();
        foreach (var key in WorkStyle.Icons)
        {
            var k = key;
            var button = new Button
            {
                Classes = { "icon" }, Width = 34, Height = 34, Margin = new Thickness(0, 0, 4, 4),
                Content = new Shapes.Path { Classes = { "icon" }, Data = WorkStyle.Icon(key) },
            };
            button.Classes.Set("selected", _work.Icon == key);
            button.Click += (_, _) =>
            {
                _work.Icon = k;
                Edited(listChanged: true);
                ApplyLook();
                BuildDrawer();
            };
            icons.Children.Add(button);
        }
        panel.Children.Add(icons);

        panel.Children.Add(Eyebrow("FONTS"));
        panel.Children.Add(Label("Title font"));
        panel.Children.Add(FontPicker(_work.TitleFont, _svc.Settings.WorkTitleFont, name => _work.TitleFont = name));
        panel.Children.Add(Label("Writing font"));
        panel.Children.Add(FontPicker(_work.BodyFont, _svc.Settings.WorkBodyFont, name => _work.BodyFont = name));

        panel.Children.Add(Eyebrow("TEXT"));
        panel.Children.Add(SliderRow("Size", 11, 32, _svc.FontSize(_work), v => _work.FontSize = Math.Round(v), "0", () => _work.FontSize = 0));
        panel.Children.Add(SliderRow("Line spacing", 1.0, 2.4, _work.LineSpacing, v => _work.LineSpacing = Math.Round(v, 2), "0.0#", null));
        if (!_compact)
            panel.Children.Add(SliderRow("Page width", 420, 1200, _work.PageWidth, v => _work.PageWidth = Math.Round(v), "0", null));

        panel.Children.Add(Eyebrow("THIS WORK"));
        var pin = new ToggleSwitch { Content = "Pin to the top", IsChecked = _work.Pinned };
        pin.IsCheckedChanged += (_, _) =>
        {
            _work.Pinned = pin.IsChecked == true;
            Edited(listChanged: true);
        };
        panel.Children.Add(pin);
        var copy = new Button { Content = "Copy as Markdown", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        copy.Click += (_, _) => CopyMarkdown();
        panel.Children.Add(copy);

        var delete = new Button
        {
            Content = _confirmDelete ? "Click again to delete forever" : "Delete this work",
            Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch,
            Foreground = Res.Brush("DangerBrush"),
        };
        delete.Click += (_, _) =>
        {
            if (!_confirmDelete)
            {
                _confirmDelete = true;
                BuildDrawer();
                return;
            }
            _svc.Library.Delete(_work);
            Deleted?.Invoke();
        };
        panel.Children.Add(delete);

        var template = WorkTemplates.Find(_work.Template);
        panel.Children.Add(new TextBlock
        {
            Text = $"Made from “{template.Name}”\nCreated {_work.Created:d MMM yyyy}, edited {WorkService.Ago(_work.Updated)}",
            Classes = { "faint" }, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        _drawer.Child = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static TextBlock Eyebrow(string text, bool first = false)
        => new() { Text = text, Classes = { "eyebrow" }, FontSize = 9.5, Margin = new Thickness(0, first ? 0 : 14, 0, 2) };

    private static TextBlock Label(string text) => new() { Text = text, Classes = { "muted" }, FontSize = 12 };

    private FontPickerButton FontPicker(string? current, string defaultName, Action<string?> set)
    {
        var picker = new FontPickerButton { DefaultLabel = "Default", DefaultFontName = defaultName, Selected = current };
        picker.Chosen += name =>
        {
            set(name);
            Edited(listChanged: true);
            ApplyLook();
            RebuildRows(null);
        };
        return picker;
    }

    private Control SliderRow(string label, double min, double max, double value, Action<double> set, string format, Action? reset)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        grid.Children.Add(Label(label));
        var number = new TextBlock { FontSize = 12, Classes = { "muted" }, Text = value.ToString(format, System.Globalization.CultureInfo.InvariantCulture) };
        Grid.SetColumn(number, 1);
        grid.Children.Add(number);
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max) };
        Grid.SetRow(slider, 1);
        Grid.SetColumnSpan(slider, 2);
        grid.Children.Add(slider);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _building) return;
            set(slider.Value);
            number.Text = slider.Value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            Edited();
            ApplyLook();
            RebuildRows(null);
        };
        return grid;
    }
}
