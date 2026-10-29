using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Overdesk.Tool.DevKit;

/// <summary>The DevKit panel: pick a utility, type on top, read the result below. Built in code so the plugin ships as one DLL.</summary>
internal sealed class DevKitView : UserControl
{
    private readonly ComboBox _picker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _hint = new() { FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _input = Box(editable: true);
    private readonly TextBox _output = Box(editable: false);
    private readonly TextBlock _error = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _copy = new() { Content = "Copy result", HorizontalAlignment = HorizontalAlignment.Right };

    public DevKitView()
    {
        _picker.ItemsSource = Converters.All.Select(c => c.Name).ToList();
        _picker.SelectedIndex = 0;
        _picker.SelectionChanged += (_, _) => Recompute();
        _input.TextChanged += (_, _) => Recompute();
        _copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && !string.IsNullOrEmpty(_output.Text))
                await clipboard.SetTextAsync(_output.Text);
        };
        _error.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AccentBrush"));

        var grid = new Grid { Margin = new Thickness(14), RowSpacing = 8 };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Place(grid, _picker, 0);
        Place(grid, _hint, 1);
        Place(grid, _input, 2);
        Place(grid, _error, 3);
        Place(grid, _output, 4);
        Place(grid, _copy, 5);
        Content = grid;
        Recompute();
    }

    private static void Place(Grid grid, Control c, int row)
    {
        Grid.SetRow(c, row);
        grid.Children.Add(c);
    }

    private static TextBox Box(bool editable)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = !editable,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            FontSize = 12.5,
            VerticalContentAlignment = VerticalAlignment.Top,
            MinHeight = 80,
        };
        if (!editable) box.PlaceholderText = "Result";
        return box;
    }

    private void Recompute()
    {
        var converter = Converters.All[Math.Max(0, _picker.SelectedIndex)];
        _hint.Text = converter.Hint;
        _input.PlaceholderText = converter.Hint;
        if (string.IsNullOrWhiteSpace(_input.Text) && converter.Name is not ("UUID" or "Unix time"))
        {
            _output.Text = "";
            _error.IsVisible = false;
            return;
        }
        try
        {
            _output.Text = converter.Run(_input.Text ?? "");
            _error.IsVisible = false;
        }
        catch (Exception ex)
        {
            _output.Text = "";
            _error.Text = ex.Message;
            _error.IsVisible = true;
        }
    }
}
