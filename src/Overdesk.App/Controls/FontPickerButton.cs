using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Overdesk.App.Services;
using Overdesk.App.Theme;

namespace Overdesk.App.Controls;

/// <summary>A button showing the chosen font in that very font; clicking opens the whole catalog, each name drawn in its own font.</summary>
internal sealed class FontPickerButton : Button
{
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis };
    private string? _selected;
    private Flyout? _flyout;

    public FontPickerButton()
    {
        Classes.Add("modelpicker");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(_label);
        var chevron = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon("IconChevronDown"), Height = 9 };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);
        Content = grid;
        Click += (_, _) => Open();
    }

    /// <summary>Show a "use the default" entry that reports null.</summary>
    public string? DefaultLabel { get; set; }

    /// <summary>Text shown when <see cref="Selected"/> is null (a default is in effect).</summary>
    public string? DefaultFontName { get; set; }

    public string? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            var name = value ?? DefaultFontName;
            _label.Text = value is null && DefaultLabel is not null ? $"{DefaultLabel} · {name}" : name;
            _label.FontFamily = WorkFonts.Resolve(name);
        }
    }

    /// <summary>The user picked a font (null = back to the default).</summary>
    public event Action<string?>? Chosen;

    private void Open()
    {
        var list = new StackPanel { Spacing = 2, Margin = new Thickness(2) };
        if (DefaultLabel is not null)
            list.Children.Add(Row(null, DefaultLabel, FontFamily.Default));
        foreach (var category in WorkFonts.Categories)
        {
            list.Children.Add(new TextBlock { Text = category.ToUpperInvariant(), Classes = { "eyebrow" }, FontSize = 9.5, Margin = new Thickness(10, 10, 0, 4) });
            foreach (var font in WorkFonts.All.Where(f => f.Category == category))
                list.Children.Add(Row(font.Name, font.Name, font.Font));
        }
        _flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new ScrollViewer { Width = 300, MaxHeight = 380, Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };
        _flyout.ShowAt(this);
    }

    private Button Row(string? value, string text, FontFamily family)
    {
        var selected = value == _selected;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock { Text = text, FontFamily = family, FontSize = 17, VerticalAlignment = VerticalAlignment.Center });
        if (selected)
        {
            var check = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "accent", "small" }, Data = Res.Icon("IconCheck") };
            Grid.SetColumn(check, 1);
            row.Children.Add(check);
        }
        var button = new Button { Classes = { "result" }, Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) =>
        {
            Selected = value;
            _flyout?.Hide();
            Chosen?.Invoke(value);
        };
        return button;
    }
}
