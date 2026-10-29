using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Overdesk.App.Services;
using Overdesk.App.Theme;
using Overdesk.Core.Works;
using Shapes = Avalonia.Controls.Shapes;

namespace Overdesk.App;

/// <summary>The Works tool in the right-hand panel: a list, a template picker and a compact editor, one at a time.</summary>
internal sealed class WorksPanelView : UserControl
{
    private readonly WorkService _svc;
    private readonly Border _host = new();
    private readonly TextBox _search = new() { Classes = { "search" }, PlaceholderText = "Search…", Margin = new Thickness(8, 0) };
    private readonly StackPanel _list = new() { Spacing = 2 };
    private Work? _editing;
    private bool _gallery;

    public WorksPanelView(WorkService svc)
    {
        _svc = svc;
        Content = _host;
        _search.TextChanged += (_, _) => FillList();
        svc.Library.Changed += (work, _) =>
        {
            if (work is null && _editing is null && !_gallery) Dispatcher.UIThread.Post(Show);
            else if (work is not null && _editing is null && !_gallery) Dispatcher.UIThread.Post(FillList);
        };
        svc.Library.Changed += (work, _) =>
        {
            // The open work was deleted elsewhere.
            if (work is null && _editing is not null && svc.Library.Find(_editing.Id) is null)
                Dispatcher.UIThread.Post(() =>
                {
                    _editing = null;
                    Show();
                });
        };
        Show();
    }

    /// <summary>Jump straight into a work (used by "Open in the side panel").</summary>
    public void OpenWork(Work work)
    {
        _editing = work;
        _gallery = false;
        Show();
    }

    private void Show()
    {
        if (_editing is { } work)
        {
            var editor = new WorkEditorView(_svc, work, compact: true);
            editor.BackRequested += () =>
            {
                _editing = null;
                Show();
            };
            editor.OpenAsTabRequested += () => _svc.OpenInTab(work);
            editor.Deleted += () =>
            {
                _editing = null;
                Show();
            };
            _host.Child = editor;
        }
        else if (_gallery) _host.Child = BuildGallery();
        else _host.Child = BuildList();
    }

    private Control BuildList()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(8, 0, 8, 8) };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(6, 4, 0, 10) };
        head.Children.Add(new TextBlock
        {
            Text = "Works", FontFamily = WorkFonts.Resolve(_svc.Settings.WorkTitleFont), FontSize = 26, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var add = new Button { Classes = { "accent" }, Padding = new Thickness(12, 6), Content = "New" };
        add.Click += (_, _) =>
        {
            _gallery = true;
            Show();
        };
        Grid.SetColumn(add, 1);
        head.Children.Add(add);
        grid.Children.Add(head);

        var box = new Border { Classes = { "searchbox" }, Height = 38, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 8) };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(12, 0, 4, 0) };
        row.Children.Add(new Shapes.Path { Classes = { "icon" }, Data = Res.Icon("IconSearch"), Height = 14 });
        if (_search.Parent is Panel old) old.Children.Remove(_search);
        Grid.SetColumn(_search, 1);
        row.Children.Add(_search);
        box.Child = row;
        Grid.SetRow(box, 1);
        grid.Children.Add(box);

        var scroller = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 2);
        grid.Children.Add(scroller);
        FillList();
        return grid;
    }

    private void FillList()
    {
        _list.Children.Clear();
        var works = _svc.Library.Works.Where(w => WorkCards.Matches(w, _search.Text)).ToList();
        foreach (var work in works)
            _list.Children.Add(WorkCards.WorkRow(_svc, work, w =>
            {
                _editing = w;
                Show();
            }, (w, anchor) => WorkCards.ShowMenu(_svc, w, anchor, includePanel: false)));
        if (works.Count == 0)
            _list.Children.Add(new TextBlock
            {
                Text = _svc.Library.Works.Count == 0 ? "No works yet.\nPress New to start from a template." : "Nothing matches.",
                Classes = { "faint" }, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 36, 0, 0),
            });
    }

    private Control BuildGallery()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(8, 0, 8, 8) };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 4, 0, 8) };
        var back = new Button { Classes = { "icon" }, Width = 32, Height = 32, Content = new Shapes.Path { Classes = { "icon" }, Data = Res.Icon("IconChevronLeft") } };
        back.Click += (_, _) =>
        {
            _gallery = false;
            Show();
        };
        head.Children.Add(back);
        var title = new TextBlock
        {
            Text = "Choose a template", FontFamily = WorkFonts.Resolve(_svc.Settings.WorkTitleFont), FontSize = 22, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        };
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        grid.Children.Add(head);

        var stack = new StackPanel { Spacing = 2 };
        foreach (var category in new[] { WorkTemplates.Games, WorkTemplates.Writing, WorkTemplates.Lists, WorkTemplates.Life })
        {
            stack.Children.Add(new TextBlock { Text = category.ToUpperInvariant(), Classes = { "eyebrow" }, FontSize = 9.5, Margin = new Thickness(8, 12, 0, 4) });
            foreach (var template in WorkTemplates.All.Where(t => t.Category == category))
            {
                var t = template;
                var sample = t.Create();
                var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                rowGrid.Children.Add(WorkStyle.IconTile(sample, 34));
                var text = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = t.Name, FontFamily = WorkFonts.Resolve(t.TitleFont ?? "Playfair Display"), FontSize = 16, FontWeight = FontWeight.SemiBold });
                text.Children.Add(new TextBlock { Text = t.Description, Classes = { "faint" }, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(text, 1);
                rowGrid.Children.Add(text);
                var button = new Button { Classes = { "wrow" }, Content = rowGrid };
                button.Click += (_, _) =>
                {
                    _editing = _svc.Library.Create(t);
                    _gallery = false;
                    Show();
                };
                stack.Children.Add(button);
            }
        }
        var scroller = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 1);
        grid.Children.Add(scroller);
        return grid;
    }
}
