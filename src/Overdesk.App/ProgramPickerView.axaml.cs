using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Overdesk.App.Controls;
using Overdesk.App.Services;
using Overdesk.Core.Tabs;

namespace Overdesk.App;

/// <summary>
/// In-window "pick a program" dialog: every installed program with its icon, a search box, and a
/// "Browse…" fallback to the OS file picker for anything not in the list. Same look on macOS and Windows.
/// </summary>
public partial class ProgramPickerView : UserControl
{
    private Action<string>? _onPicked;
    private IReadOnlyList<string> _installed = Array.Empty<string>();
    private readonly List<(string Path, string Name, Button Tile)> _tiles = new();

    public ProgramPickerView()
    {
        InitializeComponent();
        Backdrop.PointerPressed += (_, _) => Hide();
        CloseButton.Click += (_, _) => Hide();
        BrowseButton.Click += (_, _) =>
        {
            var picked = _onPicked;
            Hide(() => BrowseRequested?.Invoke(picked));
        };
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        SearchBox.KeyDown += OnSearchKeyDown;
    }

    /// <summary>The user wants the OS file picker; the argument is the callback to hand the chosen paths to.</summary>
    public event Action<Action<string>?>? BrowseRequested;

    public async void Show(string title, string subtitle, Action<string> onPicked)
    {
        _onPicked = onPicked;
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        SearchBox.Text = "";
        IsVisible = true;

        Backdrop.Opacity = 0;
        Dialog.Opacity = 0;
        Animator.Run(this, 0.3, t =>
        {
            var e = Ease.OutCubic(t);
            Backdrop.Opacity = e;
            Dialog.Opacity = e;
            var s = 0.96 + 0.04 * Ease.OutBack(t);
            Dialog.RenderTransform = new TransformGroup
            {
                Children = { new ScaleTransform(s, s), new TranslateTransform(0, 14 * (1 - e)) },
            };
        });
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Background);

        if (_tiles.Count == 0)
        {
            CountText.Text = "Looking for programs…";
            _installed = await ProgramLibrary.Installed;
            BuildTiles();
        }
        ApplyFilter();
    }

    public void Hide(Action? done = null)
    {
        if (!IsVisible) return;
        Animator.Run(this, 0.16, t =>
        {
            Backdrop.Opacity = 1 - t;
            Dialog.Opacity = 1 - t;
            Dialog.RenderTransform = new TranslateTransform(0, 8 * t);
        }, () =>
        {
            // Run the follow-up first so the host knows what comes next (e.g. a file dialog) when the dialog closes.
            done?.Invoke();
            IsVisible = false;
        });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void BuildTiles()
    {
        Programs.Children.Clear();
        _tiles.Clear();
        foreach (var path in _installed)
        {
            var name = ProgramNames.FromPath(path);
            var stack = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new AppIcon { ProgramPath = path, Width = 46, Height = 46, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock
            {
                Text = name, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
            });
            var tile = new Button { Classes = { "tile", "pick" }, Content = stack };
            ToolTip.SetTip(tile, path);
            tile.Click += (_, _) => Pick(path);
            _tiles.Add((path, name, tile));
            Programs.Children.Add(tile);
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var shown = 0;
        foreach (var (_, name, tile) in _tiles)
        {
            var match = query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase);
            tile.IsVisible = match;
            if (match) shown++;
        }

        EmptyState.IsVisible = shown == 0;
        EmptyText.Text = _tiles.Count == 0
            ? "No installed programs were found.\nUse “Browse for a program…” to pick one."
            : $"Nothing matches “{query}”.\nTry another name, or browse for the program.";
        CountText.Text = _tiles.Count == 0 ? "" : query.Length == 0 ? $"{_tiles.Count} programs" : $"{shown} of {_tiles.Count} programs";
        Scroller.Offset = default;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var first = _tiles.FirstOrDefault(t => t.Tile.IsVisible);
        if (first.Path is not null) Pick(first.Path);
        e.Handled = true;
    }

    private void Pick(string path)
    {
        var picked = _onPicked;
        Hide(() => picked?.Invoke(path));
    }
}
