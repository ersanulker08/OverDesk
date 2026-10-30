using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Overdesk.Tools.Shared;

namespace Overdesk.Tool.DevKit;

/// <summary>
/// The DevKit panel: pick a group and a utility, type on top, read the result below. Some utilities take a second value
/// (a key, a pattern, the other text). Built in code so the plugin ships as one DLL.
/// </summary>
internal sealed class DevKitView : UserControl
{
    private readonly WrapPanel _groups = new();
    private readonly ComboBox _picker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _hint = new() { FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _input = Box(editable: true);
    private readonly TextBox _second = new() { FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 12.5, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 140 };
    private readonly TextBox _output = Box(editable: false);
    private readonly TextBlock _error = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private string _group = Utilities.Groups[0];
    private List<Utility> _shown = new();
    private bool _quiet;

    public DevKitView()
    {
        _picker.SelectionChanged += (_, _) => { if (!_quiet) Recompute(); };
        _input.TextChanged += (_, _) => Recompute();
        _second.TextChanged += (_, _) => Recompute();
        _error.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("DangerBrush"));

        var copy = ToolUi.SmallButton("Copy result", async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && !string.IsNullOrEmpty(_output.Text))
                await clipboard.SetTextAsync(_output.Text);
        });
        var chain = ToolUi.SmallButton("Use as input", () => { if (!string.IsNullOrEmpty(_output.Text)) _input.Text = _output.Text; });
        ToolTip.SetTip(chain, "Put the result in the input box, to run another utility on it");
        var again = ToolUi.SmallButton("Again", Recompute);
        ToolTip.SetTip(again, "Run again (new UUIDs, passwords…)");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(again);
        actions.Children.Add(chain);
        actions.Children.Add(copy);

        var grid = new Grid { Margin = new Thickness(14, 6, 14, 14), RowSpacing = 8 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, new GridLength(1.3, GridUnitType.Star), GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition(height));
        Place(grid, _groups, 0);
        Place(grid, _picker, 1);
        Place(grid, _hint, 2);
        Place(grid, _input, 3);
        Place(grid, _second, 4);
        Place(grid, _error, 5);
        Place(grid, _output, 6);
        Place(grid, actions, 7);
        Content = grid;
        ShowGroup(_group);
    }

    private void ShowGroup(string group)
    {
        _group = group;
        _groups.Children.Clear();
        foreach (var g in Utilities.Groups)
            _groups.Children.Add(ToolUi.Chip(g, g == group, () => ShowGroup(g)));
        _shown = Utilities.All.Where(u => u.Group == group).ToList();
        _quiet = true;
        _picker.ItemsSource = _shown.Select(u => u.Name).ToList();
        _picker.SelectedIndex = 0;
        _quiet = false;
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
        if (_shown.Count == 0) return;
        var utility = _shown[Math.Clamp(_picker.SelectedIndex, 0, _shown.Count - 1)];
        _hint.Text = utility.Hint;
        _input.PlaceholderText = utility.Hint;
        _second.IsVisible = utility.Second is not null;
        _second.PlaceholderText = utility.Second;
        if (string.IsNullOrWhiteSpace(_input.Text) && !utility.RunsEmpty)
        {
            _output.Text = "";
            _error.IsVisible = false;
            return;
        }
        try
        {
            _output.Text = utility.Run(_input.Text ?? "", _second.Text ?? "");
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
