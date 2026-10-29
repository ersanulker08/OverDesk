using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Overdesk.App.Controls;
using Overdesk.App.Services;
using Overdesk.App.Theme;
using Overdesk.Core.Platform;
using Overdesk.Core.Settings;
using Overdesk.Core.Tabs;

namespace Overdesk.App;

/// <summary>
/// The start page shown when no program tab is active: clock, a search over every installed program,
/// and quick-launch tiles for pinned and recent programs.
/// </summary>
public partial class HomeView : UserControl
{
    private const int MaxTiles = 15;
    private const int MaxResults = 7;

    // Shown on first run (before anything is pinned or recent) when installed.
    private static readonly string[] Suggested =
    {
        "Safari", "Notes", "Calculator", "TextEdit", "Terminal", "Music", "Calendar", "Preview", "Photos", "Mail",
        "Google Chrome", "Visual Studio Code", "Spotify", "Discord", "Slack", "Notepad", "Calc", "Paint", "Explorer",
    };

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<Button> _resultButtons = new();
    private AppSettings _settings = new();
    private IReadOnlyList<string> _installed = Array.Empty<string>();
    private int _highlight;

    public HomeView()
    {
        InitializeComponent();
        _clockTimer.Tick += (_, _) => UpdateClock();
        SearchBox.TextChanged += (_, _) => UpdateResults();
        SearchBox.KeyDown += OnSearchKeyDown;
        SearchBox.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // Let a click on a result land before the list disappears.
            if (!SearchBox.IsFocused && !ResultsCard.IsPointerOver) ResultsCard.IsVisible = false;
        }, DispatcherPriority.Background);
        SearchBox.GotFocus += (_, _) => UpdateResults();

        var mod = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl";
        SearchHint.Text = OperatingSystem.IsMacOS() ? "⌘ K" : "Ctrl K";
        foreach (var (keys, label) in new[]
                 {
                     ($"{mod} T", "Add program"), ($"{mod} W", "Close tab"), ($"{mod} 1–9", "Switch tab"),
                     ($"{mod} P", "Command palette"), ($"{mod} B", "Side dock"),
                 })
            Shortcuts.Children.Add(Shortcut(keys, label));
    }

    public event Action<string>? ProgramRequested;

    /// <summary>The search text should go to the AI as a question.</summary>
    public event Action<string>? AskAiRequested;
    public event Action? AddProgramRequested;
    public event Action<string>? PinToggled;
    public event Action<string>? RecentRemoved;

    public void Initialize(AppSettings settings, IProgramCatalog catalog)
    {
        _settings = settings;
        ProgramLibrary.Start(catalog);
        ProgramLibrary.Installed.ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _installed = t.Result;
            RebuildTiles();
            UpdateResults();
        }), TaskScheduler.Default);
    }

    /// <summary>Home | Works switch: the Works tab shows the user's notes, lists and stories as cards.</summary>
    internal void InitializeWorks(WorkService works)
    {
        WorksHub.Initialize(works);
        HomeTabButton.Click += (_, _) => ShowWorksTab(false);
        WorksTabButton.Click += (_, _) => ShowWorksTab(true);
    }

    public bool WorksTabVisible => WorksHub.IsVisible;

    public void ShowWorksTab(bool works)
    {
        WorksHub.IsVisible = works;
        Scroller.IsVisible = !works;
        HomeTabButton.Classes.Set("selected", !works);
        WorksTabButton.Classes.Set("selected", works);
        if (works) ResultsCard.IsVisible = false;
    }

    public void SetAnimated(bool animated) => Waves.IsAnimated = animated;

    internal void DevSearch(string text)
    {
        SearchBox.Focus();
        SearchBox.Text = text;
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) OnVisibilityChanged();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        OnVisibilityChanged();
    }

    private void OnVisibilityChanged()
    {
        if (IsVisible)
        {
            UpdateClock();
            _clockTimer.Start();
            RebuildTiles();
            PlayEntrance();
        }
        else
        {
            _clockTimer.Stop();
            ResultsCard.IsVisible = false;
        }
    }

    private void PlayEntrance()
    {
        Hero.Opacity = 0;
        SearchBorder.Opacity = 0;
        Animator.Run(this, 0.55, t =>
        {
            var a = Ease.OutCubic(Math.Clamp(t / 0.8, 0, 1));
            var b = Ease.OutCubic(Math.Clamp((t - 0.2) / 0.8, 0, 1));
            Hero.Opacity = a;
            Hero.RenderTransform = new TranslateTransform(0, 14 * (1 - a));
            SearchBorder.Opacity = b;
            SearchBorder.RenderTransform = new TranslateTransform(0, 14 * (1 - b));
        });
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        Clock.Text = now.ToString("HH:mm");
        DateText.Text = now.ToString("dddd, d MMMM", System.Globalization.CultureInfo.InvariantCulture);
        Greeting.Text = now.Hour switch
        {
            < 5 => "WORKING LATE",
            < 12 => "GOOD MORNING",
            < 18 => "GOOD AFTERNOON",
            _ => "GOOD EVENING",
        };
    }

    // ---------------------------------------------------------------- quick launch tiles

    public void RebuildTiles()
    {
        var pinned = _settings.PinnedPrograms.Where(Exists).ToList();
        var recent = _settings.RecentPrograms.Where(Exists)
            .Where(p => !pinned.Any(x => ProgramHistory.SamePath(x, p))).ToList();
        var items = pinned.Concat(recent).Take(MaxTiles).ToList();

        SectionTitle.Text = items.Count > 0 ? "QUICK LAUNCH" : "SUGGESTED FOR YOU";
        if (items.Count == 0)
        {
            items = Suggested
                .Select(name => _installed.FirstOrDefault(p => string.Equals(ProgramNames.FromPath(p), name, StringComparison.OrdinalIgnoreCase)))
                .OfType<string>().Take(7).ToList();
        }

        Tiles.Children.Clear();
        var index = 0;
        foreach (var path in items)
            Tiles.Children.Add(Animate(ProgramTile(path, pinned.Any(p => ProgramHistory.SamePath(p, path))), index++));
        Tiles.Children.Add(Animate(AddTile(), index));
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private Button ProgramTile(string path, bool isPinned)
    {
        var name = ProgramNames.FromPath(path);
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(new Border
        {
            Classes = { "tileglow" },
            Width = 70, Height = 70, CornerRadius = new CornerRadius(35),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = Res.Get<BoxShadows>("AccentGlowLarge"),
            Margin = new Thickness(0, 4, 0, 0),
        });
        grid.Children.Add(new AppIcon
        {
            ProgramPath = path, Width = 50, Height = 50,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });
        var label = new TextBlock
        {
            Text = name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 6, 2, 2),
        };
        Grid.SetRow(label, 1);
        grid.Children.Add(label);
        if (isPinned)
        {
            grid.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Classes = { "icon", "small", "accent" }, Data = Res.Icon("IconPin"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -2, -2, 0), Width = 11, Height = 11,
            });
        }

        var tile = new Button { Classes = { "tile" }, Content = grid };
        ToolTip.SetTip(tile, path);
        tile.Click += (_, _) => ProgramRequested?.Invoke(path);
        tile.ContextMenu = TileMenu(path, isPinned);
        return tile;
    }

    private ContextMenu TileMenu(string path, bool isPinned)
    {
        var open = MenuItemWithIcon("Open in a new tab", "IconTabs");
        open.Click += (_, _) => ProgramRequested?.Invoke(path);
        var pin = MenuItemWithIcon(isPinned ? "Unpin from home" : "Pin to home", "IconPin");
        pin.Click += (_, _) => PinToggled?.Invoke(path);
        var items = new List<Control> { open, pin };
        if (_settings.RecentPrograms.Any(p => ProgramHistory.SamePath(p, path)) && !isPinned)
        {
            var remove = MenuItemWithIcon("Remove from recent", "IconClose");
            remove.Click += (_, _) => RecentRemoved?.Invoke(path);
            items.Add(remove);
        }
        return new ContextMenu { ItemsSource = items };
    }

    internal MenuItem MenuItemWithIcon(string header, string iconKey) => new()
    {
        Header = header,
        Icon = new Avalonia.Controls.Shapes.Path { Classes = { "icon" }, Data = Res.Icon(iconKey) },
    };

    private Button AddTile()
    {
        var stack = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(21),
            Background = Res.Brush("Bg3Brush"), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "large" }, Data = Res.Icon("IconPlus"), Height = 18 },
        });
        stack.Children.Add(new TextBlock { Text = "Add program", FontSize = 12, Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center });
        var tile = new Button { Classes = { "tile", "add" }, Content = stack };
        tile.Click += (_, _) => AddProgramRequested?.Invoke();
        return tile;
    }

    /// <summary>Staggered rise-and-fade entrance; afterwards the style's hover transform takes over again.</summary>
    private static Button Animate(Button tile, int index)
    {
        tile.Opacity = 0;
        tile.RenderTransform = TransformOperations.Parse("translateY(16px)");
        DispatcherTimer.RunOnce(() =>
        {
            tile.ClearValue(OpacityProperty);
            tile.ClearValue(RenderTransformProperty);
        }, TimeSpan.FromMilliseconds(120 + 35 * index));
        return tile;
    }

    // ---------------------------------------------------------------- search

    private void UpdateResults()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        if (query.Length == 0 || !SearchBox.IsFocused)
        {
            ResultsCard.IsVisible = false;
            return;
        }

        var known = _settings.PinnedPrograms.Concat(_settings.RecentPrograms).Where(Exists);
        var matches = known.Concat(_installed)
            .DistinctBy(p => p.TrimEnd('/', '\\'))
            .Select(p => (Path: p, Name: ProgramNames.FromPath(p)))
            .Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.Name.Length)
            .Take(MaxResults)
            .ToList();

        Results.Children.Clear();
        _resultButtons.Clear();
        foreach (var (path, name) in matches)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(new AppIcon { ProgramPath = path, Width = 30, Height = 30 });
            var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, FontSize = 13.5 });
            text.Children.Add(new TextBlock
            {
                Text = path, FontSize = 11, Classes = { "faint" }, TextTrimming = TextTrimming.PathSegmentEllipsis,
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var chip = new Border { Classes = { "chip" }, Child = new TextBlock { Text = "Open ↵" }, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chip, 2);
            row.Children.Add(chip);

            var button = new Button { Classes = { "result" }, Content = row, Tag = chip };
            button.Click += (_, _) => OpenResult(path);
            _resultButtons.Add(button);
            Results.Children.Add(button);
        }

        // Anything that is not a program name can be a question: the last row sends it to the AI.
        if (AskAiRequested is not null && query.Length >= 2)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = Res.Brush("AccentGradientDiagonal"),
                Child = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon("IconSparkle"), Stroke = Brushes.White },
            });
            var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = $"Ask AI: {query}", FontWeight = FontWeight.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = "It can answer, or do it for you in OverDesk", FontSize = 11, Classes = { "faint" } });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var chip = new Border { Classes = { "chip" }, Child = new TextBlock { Text = "Ask ↵" }, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chip, 2);
            row.Children.Add(chip);
            var ask = new Button { Classes = { "result" }, Content = row, Tag = chip };
            ask.Click += (_, _) =>
            {
                SearchBox.Text = "";
                ResultsCard.IsVisible = false;
                AskAiRequested?.Invoke(query);
            };
            _resultButtons.Add(ask);
            Results.Children.Add(ask);
        }

        NoResults.IsVisible = _resultButtons.Count == 0;
        ResultsCard.IsVisible = true;
        SetHighlight(0);
    }

    private void SetHighlight(int index)
    {
        _highlight = _resultButtons.Count == 0 ? 0 : Math.Clamp(index, 0, _resultButtons.Count - 1);
        for (var i = 0; i < _resultButtons.Count; i++)
        {
            var on = i == _highlight;
            _resultButtons[i].Classes.Set("highlight", on);
            if (_resultButtons[i].Tag is Control chip) chip.IsVisible = on;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                SetHighlight(_highlight + 1);
                e.Handled = true;
                break;
            case Key.Up:
                SetHighlight(_highlight - 1);
                e.Handled = true;
                break;
            case Key.Enter when _resultButtons.Count > 0:
                _resultButtons[_highlight].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
                break;
            case Key.Escape:
                SearchBox.Text = "";
                ResultsCard.IsVisible = false;
                e.Handled = true;
                break;
        }
    }

    private void OpenResult(string path)
    {
        SearchBox.Text = "";
        ResultsCard.IsVisible = false;
        ProgramRequested?.Invoke(path);
    }

    private Border Shortcut(string keys, string label)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        stack.Children.Add(new TextBlock { Text = keys, FontWeight = FontWeight.SemiBold, Foreground = Res.Brush("TextBrush") });
        stack.Children.Add(new TextBlock { Text = label });
        return new Border { Classes = { "chip" }, Padding = new Thickness(9, 5), Child = stack };
    }
}
