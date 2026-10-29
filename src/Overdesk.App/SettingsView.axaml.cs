using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Overdesk.App.Controls;
using Overdesk.App.Services;
using Overdesk.App.Theme;
using Overdesk.Core.Platform;
using Overdesk.Core.Settings;

namespace Overdesk.App;

/// <summary>
/// OverDesk's own tab: a full settings page with a section navigation on the left. Changes apply immediately;
/// the host saves them on <see cref="Changed"/>.
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly List<(Button Button, Control Section)> _nav = new();
    private AppSettings _settings = new();
    private IWindowEmbedder? _embedder;
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
        PermissionButton.Click += (_, _) => _embedder?.RequestPermissions();
        MirrorButton.Click += (_, _) => _embedder?.RequestMirrorPermission();
        ClearRecentButton.Click += (_, _) =>
        {
            _settings.RecentPrograms.Clear();
            ClearRecentButton.IsEnabled = false;
            Changed?.Invoke();
        };

        AnimatedToggle.IsCheckedChanged += (_, _) =>
        {
            Update(s => s.AnimatedBackground = AnimatedToggle.IsChecked == true);
            Waves.IsAnimated = AnimatedToggle.IsChecked == true;
        };
        SplashToggle.IsCheckedChanged += (_, _) => Update(s => s.ShowSplash = SplashToggle.IsChecked == true);
        RestoreToggle.IsCheckedChanged += (_, _) => Update(s => s.RestoreTabs = RestoreToggle.IsChecked == true);
        DockToggle.IsCheckedChanged += (_, _) => Update(s => s.SidebarExpanded = DockToggle.IsChecked == true);
        MirrorToggle.IsCheckedChanged += (_, _) => Update(s => s.MirrorPrograms = MirrorToggle.IsChecked == true);

        var version = typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var os = OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : "Linux";
        AboutVersion.Text = $"Version {version}  ·  {os}  ·  .NET {Environment.Version.ToString(2)}";
        NavVersion.Text = $"OverDesk {version}";

        BuildNavigation();
        BuildShortcuts();
        InitializeSections();
        Scroller.ScrollChanged += (_, _) => HighlightVisibleSection();
    }

    public event Action? CloseRequested;
    public event Action? Changed;

    /// <summary>Shown under "Data" so people can find their settings.</summary>
    public string SettingsFilePath { set => DataPath.Text = value; }

    /// <summary>Asked by the gauge list: can this machine measure the gauge?</summary>
    internal Func<string, bool>? IsGaugeAvailable { get; set; }

    public void Show(AppSettings settings, IWindowEmbedder embedder, bool animated)
    {
        _settings = settings;
        _embedder = embedder;
        Waves.IsAnimated = settings.AnimatedBackground;
        LoadToggles();
        BuildThemeCards();
        BuildGaugeChips();
        LoadSections();
        UpdatePermission();
        IsVisible = true;
        if (!animated) return;

        // Navigation slides in from the left, the sections rise in.
        NavPane.Opacity = 0;
        Sections.Opacity = 0;
        Animator.Run(this, 0.45, t =>
        {
            var a = Ease.OutCubic(Math.Clamp(t / 0.7, 0, 1));
            var b = Ease.OutCubic(Math.Clamp((t - 0.15) / 0.85, 0, 1));
            NavPane.Opacity = a;
            NavPane.RenderTransform = new TranslateTransform(-24 * (1 - a), 0);
            Sections.Opacity = b;
            Sections.RenderTransform = new TranslateTransform(0, 22 * (1 - b));
        });
    }

    public void Hide() => IsVisible = false;

    /// <summary>The settings may have been changed elsewhere (dock gear, Cmd+B): refresh what is shown.</summary>
    public void Refresh()
    {
        if (!IsVisible) return;
        LoadToggles();
        BuildGaugeChips();
        UpdatePermission();
    }

    private void LoadToggles()
    {
        _loading = true;
        AnimatedToggle.IsChecked = _settings.AnimatedBackground;
        SplashToggle.IsChecked = _settings.ShowSplash;
        RestoreToggle.IsChecked = _settings.RestoreTabs;
        DockToggle.IsChecked = _settings.SidebarExpanded;
        MirrorToggle.IsChecked = _settings.MirrorPrograms;
        ClearRecentButton.IsEnabled = _settings.RecentPrograms.Count > 0;
        _loading = false;
    }

    public void UpdatePermission()
    {
        if (_embedder is null) return;

        AccessibilityCard.IsVisible = _embedder.PermissionHelp is not null || _embedder.PlatformName == "macOS";
        var ok = _embedder.HasPermissions;
        PermissionTitle.Text = ok ? "Accessibility: granted" : "Accessibility: needed";
        PermissionText.Text = ok
            ? "OverDesk can place other programs inside its window."
            : "Required to place other programs inside OverDesk's window.";
        PermissionIcon.Stroke = Res.Brush(ok ? "SuccessBrush" : "WarningBrush");
        PermissionButton.IsVisible = !ok;

        var mirror = _embedder.CanMirror;
        MirrorCard.IsVisible = mirror is not null;
        MirrorTitle.Text = mirror == true ? "Live mirror" : "Screen Recording: recommended";
        MirrorText.Text = mirror == true
            ? "Programs stay visible inside OverDesk while you use the dock or the top bar. Turn it off to save a little power with very heavy programs."
            : "Keeps a program visible inside OverDesk while you use the dock or the top bar. Without it the stage goes blank until you click the program's tab.";
        MirrorIcon.Stroke = Res.Brush(mirror == true ? "SuccessBrush" : "WarningBrush");
        MirrorButton.IsVisible = mirror != true;
        MirrorToggle.IsVisible = mirror == true;
    }

    private void Update(Action<AppSettings> change)
    {
        if (_loading) return;
        change(_settings);
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- navigation

    private void BuildNavigation()
    {
        var items = new (string Label, string Icon, Control Section)[]
        {
            ("Appearance", "IconSparkle", SectionAppearance),
            ("Side dock", "IconPanel", SectionDock),
            ("Web strip", "IconGlobe", SectionWeb),
            ("AI", "IconSparkle", SectionAi),
            ("Projects", "IconRocket", SectionProjects),
            ("Startup", "IconTabs", SectionStartup),
            ("Permissions", "IconShield", SectionPermissions),
            ("Shortcuts", "IconCommand", SectionShortcuts),
            ("Data", "IconFolder", SectionData),
            ("About", "IconInfo", SectionAbout),
        };
        foreach (var (label, icon, section) in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(new Avalonia.Controls.Shapes.Path { Classes = { "icon" }, Data = Res.Icon(icon) });
            row.Children.Add(new TextBlock { Text = label, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Classes = { "navitem" }, Content = row };
            button.Click += (_, _) => ScrollTo(section);
            _nav.Add((button, section));
            Nav.Children.Add(button);
        }
        SetActiveNav(0);
    }

    /// <summary>Scroll to a section by its navigation label ("AI"), once the page has laid out.</summary>
    public void ShowSection(string label)
    {
        var index = _nav.FindIndex(n => n.Button.Content is StackPanel { Children: [_, TextBlock t] } && t.Text == label);
        if (index >= 0) Dispatcher.UIThread.Post(() => ScrollTo(_nav[index].Section), DispatcherPriority.Background);
    }

    private void ScrollTo(Control section)
    {
        if (section.TranslatePoint(default, Sections) is not { } p) return;
        var max = Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height);
        var target = Math.Clamp(p.Y - 24, 0, max);
        var from = Scroller.Offset.Y;
        Animator.Run(this, 0.35, t => Scroller.Offset = new Vector(0, from + (target - from) * Ease.OutCubic(t)));
    }

    private void HighlightVisibleSection()
    {
        var current = 0;
        for (var i = 0; i < _nav.Count; i++)
        {
            if (_nav[i].Section.TranslatePoint(default, Scroller) is { } p && p.Y <= Scroller.Bounds.Height * 0.35) current = i;
        }
        // At the very bottom the last section wins even if it is short.
        if (Scroller.Offset.Y > 0 && Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - 4) current = _nav.Count - 1;
        SetActiveNav(current);
    }

    private void SetActiveNav(int index)
    {
        for (var i = 0; i < _nav.Count; i++) _nav[i].Button.Classes.Set("active", i == index);
    }

    // ---------------------------------------------------------------- appearance

    private void BuildThemeCards()
    {
        ThemeCards.Children.Clear();
        var current = AccentThemes.Find(_settings.AccentTheme);
        foreach (var theme in AccentThemes.All)
        {
            var selected = theme == current;
            var gradient = AccentThemes.Gradient(theme.Primary, theme.Secondary, horizontal: false);

            // A tiny OverDesk: a top bar with an accent tab above a glowing stage.
            var preview = new Grid { RowDefinitions = new RowDefinitions("16,*"), Height = 74 };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 5, 0, 0) };
            bar.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = gradient });
            bar.Children.Add(new Border { Width = 30, Height = 8, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(AccentThemes.WithAlpha(theme.Primary, 0x70)) });
            bar.Children.Add(new Border { Width = 22, Height = 8, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)) });
            preview.Children.Add(bar);
            var stage = new Border
            {
                Margin = new Thickness(8, 5, 8, 8), CornerRadius = new CornerRadius(5),
                BorderBrush = new SolidColorBrush(AccentThemes.WithAlpha(theme.Primary, 0xAA)), BorderThickness = new Thickness(1),
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#15151D"), 0), new GradientStop(AccentThemes.WithAlpha(theme.Secondary, 0x48), 1) },
                },
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 12, Color = AccentThemes.WithAlpha(theme.Primary, 0x60) }),
            };
            Grid.SetRow(stage, 1);
            preview.Children.Add(stage);

            var label = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2, 10, 2, 0) };
            label.Children.Add(new TextBlock { Text = theme.Name, FontWeight = FontWeight.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            if (selected)
            {
                var check = new Border
                {
                    Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = gradient,
                    Child = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon("IconCheck"), Stroke = Brushes.White, StrokeThickness = 2.4, Height = 9 },
                };
                Grid.SetColumn(check, 1);
                label.Children.Add(check);
            }

            var content = new StackPanel();
            content.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.Parse("#0B0B10")), ClipToBounds = true, Child = preview });
            content.Children.Add(label);

            var card = new Button { Classes = { "themecard" }, Content = content };
            card.Classes.Set("selected", selected);
            if (selected) card.BorderBrush = new SolidColorBrush(theme.Primary);
            card.Click += (_, _) =>
            {
                _settings.AccentTheme = theme.Name;
                AccentThemes.Apply(theme.Name);
                BuildThemeCards();
                Changed?.Invoke();
            };
            ThemeCards.Children.Add(card);
        }
    }

    // ---------------------------------------------------------------- dock gauges

    private void BuildGaugeChips()
    {
        GaugeChips.Children.Clear();
        foreach (var info in SystemStatsService.Catalog.Where(m => !m.WindowsOnly || OperatingSystem.IsWindows()))
        {
            var available = IsGaugeAvailable?.Invoke(info.Id) ?? true;
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = info.Label, FontWeight = FontWeight.SemiBold, FontSize = 12.5 });
            text.Children.Add(new TextBlock { Text = available ? info.Description : "Not available here", Classes = { "faint" }, FontSize = 11 });
            var chip = new ToggleButton
            {
                Classes = { "gaugechip" }, Content = text, IsEnabled = available,
                IsChecked = _settings.DockMetrics.Contains(info.Id),
            };
            chip.IsCheckedChanged += (_, _) =>
            {
                _settings.DockMetrics.Remove(info.Id);
                if (chip.IsChecked == true) _settings.DockMetrics.Add(info.Id);
                Changed?.Invoke();
            };
            GaugeChips.Children.Add(chip);
        }
    }

    // ---------------------------------------------------------------- shortcuts

    private void BuildShortcuts()
    {
        var mod = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
        var shift = OperatingSystem.IsMacOS() ? "⇧" : "Shift+";
        var list = new (string Keys, string What)[]
        {
            ($"{mod}T", "Add a program"), ($"{mod}W", "Close the tab"),
            ($"{mod}1 – 9", "Jump to a tab"), ($"{mod}Tab", "Next tab"),
            ($"{mod}P", "Command palette"), ($"{mod}K", "Search programs"),
            ($"{mod}B", "Side dock"), ($"{mod},", "Settings"),
            ($"{mod}{shift}H", "Home"), ("Middle click", "Close a tab"),
        };
        foreach (var (keys, what) in list)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 7, 24, 7) };
            row.Children.Add(new TextBlock { Text = what, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center });
            var chip = new Border { Classes = { "chip" }, Child = new TextBlock { Text = keys, FontWeight = FontWeight.SemiBold } };
            Grid.SetColumn(chip, 1);
            row.Children.Add(chip);
            ShortcutGrid.Children.Add(row);
        }
    }
}
