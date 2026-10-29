using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Overdesk.App.Controls;
using Overdesk.App.Theme;

namespace Overdesk.App;

/// <summary>One entry in the command palette.</summary>
public sealed record PaletteItem(
    string Title, string Category, Action Run,
    string? IconKey = null, string? ProgramPath = null, string? Shortcut = null, string? Detail = null);

/// <summary>
/// Keyboard-first launcher (Cmd/Ctrl+P): fuzzy search over everything OverDesk can do, its tabs,
/// installed programs, themes, local dev servers and clipboard history.
/// </summary>
public partial class CommandPaletteView : UserControl
{
    private const int MaxResults = 40;

    private IReadOnlyList<PaletteItem> _items = Array.Empty<PaletteItem>();

    /// <summary>Items made from what is typed ("Ask AI: …", "? docs", "> command"), shown above the matches.</summary>
    internal Func<string, IEnumerable<PaletteItem>>? Dynamic { get; set; }
    private readonly List<(Button Row, PaletteItem Item)> _rows = new();
    private int _highlight;

    public CommandPaletteView()
    {
        InitializeComponent();
        Backdrop.PointerPressed += (_, _) => Hide();
        SearchBox.TextChanged += (_, _) => Filter();
        SearchBox.KeyDown += OnKey;
    }

    public void Show(IReadOnlyList<PaletteItem> items)
    {
        _items = items;
        SearchBox.Text = "";
        IsVisible = true;
        Filter();
        Backdrop.Opacity = 0;
        Dialog.Opacity = 0;
        Animator.Run(this, 0.22, t =>
        {
            var e = Ease.OutCubic(t);
            Backdrop.Opacity = e;
            Dialog.Opacity = e;
            Dialog.RenderTransform = new TranslateTransform(0, -10 * (1 - e));
        });
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Background);
    }

    public void Hide(Action? done = null)
    {
        if (!IsVisible) return;
        Animator.Run(this, 0.14, t =>
        {
            Backdrop.Opacity = 1 - t;
            Dialog.Opacity = 1 - t;
        }, () =>
        {
            done?.Invoke();
            IsVisible = false;
        });
    }

    private void Filter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var matches = query.Length == 0
            ? _items.Where(i => i.Category is "Tab" or "Action").Take(MaxResults).ToList()
            : _items
                .Select(i => (Item: i, Score: Score(query, i.Title) * 2 + Score(query, i.Category + " " + i.Detail)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Item.Title.Length)
                .Take(MaxResults)
                .Select(x => x.Item)
                .ToList();
        if (query.Length > 0 && Dynamic?.Invoke(query) is { } typed)
        {
            var extra = typed.ToList();
            // A prefix (? or >) means the user wants exactly that: put it first; otherwise suggest it after the matches.
            if (query[0] is '?' or '>') matches.InsertRange(0, extra);
            else matches.AddRange(extra);
        }

        Results.Children.Clear();
        _rows.Clear();
        foreach (var item in matches)
        {
            var row = Row(item);
            _rows.Add((row, item));
            Results.Children.Add(row);
        }
        NoResults.IsVisible = matches.Count == 0;
        Highlight(0);
    }

    /// <summary>0 = no match; higher = better. Prefix beats word start beats substring beats scattered letters.</summary>
    private static int Score(string query, string text)
    {
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 100;
        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index > 0 && !char.IsLetterOrDigit(text[index - 1])) return 80;
        if (index >= 0) return 60;
        var qi = 0;
        foreach (var ch in text)
            if (qi < query.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[qi])) qi++;
        return qi == query.Length ? 20 : 0;
    }

    private Button Row(PaletteItem item)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        Control icon = item.ProgramPath is { } path
            ? new AppIcon { ProgramPath = path, Width = 22, Height = 22 }
            : new Avalonia.Controls.Shapes.Path { Classes = { "icon" }, Data = Res.Icon(item.IconKey ?? "IconSparkle"), Height = 16 };
        grid.Children.Add(new Border { Width = 28, Child = icon, VerticalAlignment = VerticalAlignment.Center });

        var text = new StackPanel { Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = item.Title, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.Detail is { Length: > 0 } detail)
            text.Children.Add(new TextBlock { Text = detail, FontSize = 11, Classes = { "faint" }, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var category = new TextBlock { Text = item.Category.ToUpperInvariant(), Classes = { "eyebrow" }, FontSize = 9.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(category, 2);
        grid.Children.Add(category);
        if (item.Shortcut is { } shortcut)
        {
            var chip = new Border { Classes = { "chip" }, Child = new TextBlock { Text = shortcut }, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chip, 3);
            grid.Children.Add(chip);
        }

        var button = new Button { Classes = { "result" }, Content = grid, Padding = new Thickness(10, 8) };
        button.Click += (_, _) => Run(item);
        return button;
    }

    private void Highlight(int index)
    {
        _highlight = _rows.Count == 0 ? 0 : Math.Clamp(index, 0, _rows.Count - 1);
        for (var i = 0; i < _rows.Count; i++) _rows[i].Row.Classes.Set("highlight", i == _highlight);
        if (_rows.Count > 0) _rows[_highlight].Row.BringIntoView();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Highlight(_highlight + 1); e.Handled = true; break;
            case Key.Up: Highlight(_highlight - 1); e.Handled = true; break;
            case Key.Enter when _rows.Count > 0: Run(_rows[_highlight].Item); e.Handled = true; break;
            case Key.Escape: Hide(); e.Handled = true; break;
        }
    }

    private void Run(PaletteItem item) => Hide(item.Run);
}
