using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Overdesk.App.Services;
using Overdesk.App.Theme;
using Overdesk.Core.Works;
using Shapes = Avalonia.Controls.Shapes;

namespace Overdesk.App;

/// <summary>The "Works" tab of the home page: every work as a card, a button to start a new one from a template.</summary>
internal sealed class WorksHubView : UserControl
{
    private readonly StackPanel _column = new() { MaxWidth = 920, Margin = new Thickness(32, 40, 32, 60) };
    private readonly TextBox _search = new() { Classes = { "search" }, PlaceholderText = "Search your works…", Margin = new Thickness(12, 0, 8, 0) };
    private readonly WrapPanel _cards = new() { Margin = new Thickness(4, 0, 0, 0) };
    private readonly TextBlock _noMatch = new() { Text = "Nothing matches your search.", Classes = { "faint" }, Margin = new Thickness(8, 8), IsVisible = false };
    private WorkService? _svc;
    private bool _gallery;

    public WorksHubView()
    {
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _column,
        };
        _search.TextChanged += (_, _) =>
        {
            if (!_gallery) RefreshCards();
        };
    }

    public void Initialize(WorkService svc)
    {
        _svc = svc;
        svc.Library.Changed += (work, _) =>
        {
            // Edits made while this page is hidden are shown when it comes back; list changes refresh at once.
            if (work is null && IsVisible) Avalonia.Threading.Dispatcher.UIThread.Post(() => Rebuild(keepSearch: true));
        };
        Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && _svc is not null)
        {
            _gallery = false;
            Rebuild();
        }
    }

    private void Rebuild(bool keepSearch = false)
    {
        if (_svc is null) return;
        var svc = _svc;
        if (!keepSearch) _search.Text = "";
        _column.Children.Clear();

        if (_gallery)
        {
            BuildGallery(svc);
            return;
        }

        var total = svc.Library.Works.Count;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(4, 0, 4, 22) };
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = "WORKS", Classes = { "eyebrow" }, Foreground = Res.Brush("AccentBrush") });
        titles.Children.Add(new TextBlock
        {
            Text = "Your works", FontFamily = WorkFonts.Resolve(svc.Settings.WorkTitleFont), FontSize = 40, FontWeight = FontWeight.SemiBold,
        });
        titles.Children.Add(new TextBlock
        {
            Text = total == 0 ? "Notes, lists, stories, game variables… start with a template." : $"{total} work{(total == 1 ? "" : "s")} · everything saves as you type",
            Classes = { "muted" }, FontSize = 13,
        });
        head.Children.Add(titles);
        var newButton = new Button { Classes = { "accent" }, Padding = new Thickness(16, 9), VerticalAlignment = VerticalAlignment.Bottom, Content = "New work" };
        newButton.Click += (_, _) => ShowGallery();
        Grid.SetColumn(newButton, 1);
        head.Children.Add(newButton);
        _column.Children.Add(head);

        if (total > 3)
        {
            var box = new Border { Classes = { "searchbox" }, Height = 44, Margin = new Thickness(4, 0, 4, 20) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(16, 0, 8, 0) };
            row.Children.Add(new Shapes.Path { Classes = { "icon" }, Data = Res.Icon("IconSearch"), Height = 16 });
            Grid.SetColumn(_search, 1);
            if (_search.Parent is Panel old) old.Children.Remove(_search);
            row.Children.Add(_search);
            box.Child = row;
            _column.Children.Add(box);
        }

        _column.Children.Add(_cards);
        _column.Children.Add(_noMatch);
        RefreshCards();
    }

    private void RefreshCards()
    {
        if (_svc is null) return;
        var svc = _svc;
        _cards.Children.Clear();
        var works = svc.Library.Works.Where(w => WorkCards.Matches(w, _search.Text)).ToList();
        if (string.IsNullOrWhiteSpace(_search.Text)) _cards.Children.Add(WorkCards.NewCard(ShowGallery));
        foreach (var work in works)
            _cards.Children.Add(WorkCards.WorkCard(svc, work, svc.OpenInTab, (w, anchor) => WorkCards.ShowMenu(svc, w, anchor)));
        _noMatch.IsVisible = works.Count == 0 && svc.Library.Works.Count > 0;
    }

    internal void ShowGallery()
    {
        _gallery = true;
        Rebuild();
    }

    private void BuildGallery(WorkService svc)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 4, 22) };
        var back = new Button { Classes = { "icon" }, Width = 38, Height = 38, VerticalAlignment = VerticalAlignment.Center, Content = new Shapes.Path { Classes = { "icon" }, Data = Res.Icon("IconChevronLeft") } };
        back.Click += (_, _) =>
        {
            _gallery = false;
            Rebuild();
        };
        head.Children.Add(back);
        var titles = new StackPanel { Margin = new Thickness(8, 0, 0, 0), Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = "NEW WORK", Classes = { "eyebrow" }, Foreground = Res.Brush("AccentBrush") });
        titles.Children.Add(new TextBlock { Text = "Choose a template", FontFamily = WorkFonts.Resolve(svc.Settings.WorkTitleFont), FontSize = 34, FontWeight = FontWeight.SemiBold });
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        _column.Children.Add(head);

        foreach (var category in new[] { WorkTemplates.Games, WorkTemplates.Writing, WorkTemplates.Lists, WorkTemplates.Life })
        {
            _column.Children.Add(new TextBlock { Text = category.ToUpperInvariant(), Classes = { "eyebrow" }, Margin = new Thickness(6, 14, 0, 10) });
            var wrap = new WrapPanel();
            foreach (var template in WorkTemplates.All.Where(t => t.Category == category))
                wrap.Children.Add(WorkCards.TemplateCard(template, t =>
                {
                    var work = svc.Library.Create(t);
                    _gallery = false;
                    svc.OpenInTab(work);
                }));
            _column.Children.Add(wrap);
        }
    }
}
