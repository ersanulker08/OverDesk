using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Overdesk.App.Controls;
using Overdesk.App.Theme;
using Overdesk.Core.Settings;
using Overdesk.Core.Tabs;

namespace Overdesk.App;

/// <summary>
/// Workspaces: named sets of program tabs ("Work", "Games"...). Switching swaps the whole tab row; the programs of
/// the other workspaces keep running hidden, so coming back is instant. The web strip is shared by all of them.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, (List<HostedTab> Tabs, HostedTab? Active)> _workspaceTabs = new();
    private bool _switchingWorkspace;

    private Workspace CurrentWorkspace
        => _settings.Workspaces.FirstOrDefault(w => w.Id == _settings.ActiveWorkspace) ?? _settings.Workspaces[0];

    private void InitializeWorkspaces()
    {
        // Settings from before workspaces: their tab list becomes the first workspace.
        if (_settings.Workspaces.Count == 0)
        {
            _settings.Workspaces.Add(new Workspace
            {
                Name = "Main", Color = _settings.AccentTheme, Tabs = _settings.OpenTabs.ToList(), ActiveTab = _settings.ActiveTab,
            });
            _settings.OpenTabs.Clear();
        }
        if (_settings.Workspaces.All(w => w.Id != _settings.ActiveWorkspace)) _settings.ActiveWorkspace = _settings.Workspaces[0].Id;
        WorkspaceButton.Click += (_, _) => ShowWorkspaceMenu();
        UpdateWorkspaceButton();
    }

    /// <summary>Restore the active workspace's tabs at startup.</summary>
    private void RestoreWorkspaceTabs()
    {
        var ws = CurrentWorkspace;
        if (!_settings.RestoreTabs) ws.Tabs.Clear();
        var tabs = BuildTabs(ws);
        _switchingWorkspace = true; // do not save the half-filled row while it loads
        try { _tabs.Load(tabs, ws.ActiveTab >= 0 && ws.ActiveTab < tabs.Count ? tabs[ws.ActiveTab] : null); }
        finally { _switchingWorkspace = false; }
    }

    private static List<HostedTab> BuildTabs(Workspace ws)
        => ws.Tabs.Where(p => File.Exists(p) || Directory.Exists(p)).Select(p => new HostedTab(p)).ToList();

    /// <summary>Called by SaveSettings: remember the current tab row in the current workspace.</summary>
    private void SaveWorkspaceTabs()
    {
        if (_switchingWorkspace) return;
        var programs = _tabs.Tabs.Where(t => !t.IsInternal).ToList();
        var ws = CurrentWorkspace;
        ws.Tabs = programs.Select(t => t.ProgramPath).ToList();
        ws.ActiveTab = _tabs.Active is { IsInternal: false } active ? programs.IndexOf(active) : -1;
    }

    private void SwitchWorkspace(Workspace target)
    {
        if (target.Id == CurrentWorkspace.Id) return;
        SaveWorkspaceTabs();
        var from = CurrentWorkspace;

        // Park the current row: its programs stay running but out of sight.
        foreach (var t in _tabs.Tabs) _embedding.SetVisible(TabKey(t), false);
        _workspaceTabs[from.Id] = (_tabs.Tabs.ToList(), _tabs.Active);

        _switchingWorkspace = true;
        try
        {
            _settings.ActiveWorkspace = target.Id;
            _activeWeb = null;
            if (_workspaceTabs.TryGetValue(target.Id, out var parked)) _tabs.Load(parked.Tabs, parked.Active);
            else
            {
                var tabs = BuildTabs(target);
                _tabs.Load(tabs, target.ActiveTab >= 0 && target.ActiveTab < tabs.Count ? tabs[target.ActiveTab] : null);
            }
        }
        finally
        {
            _switchingWorkspace = false;
        }

        OnActiveTabChanged();
        UpdateTabContent();
        BuildWebPills();
        UpdateWorkspaceButton();
        SaveSettings();
        ShowToast($"Workspace: {target.Name}", "IconTabs");

        // A little slide so the change of row is felt.
        Animator.Run(TabStrip, 0.28, t =>
        {
            var e = Ease.OutCubic(t);
            TabStrip.Opacity = e;
            TabStrip.RenderTransform = new TranslateTransform(14 * (1 - e), 0);
        });
    }

    private void UpdateWorkspaceButton()
    {
        var ws = CurrentWorkspace;
        var theme = AccentThemes.Find(ws.Color);
        WorkspaceBadge.Background = AccentThemes.Gradient(theme.Primary, theme.Secondary, horizontal: false);
        WorkspaceBadge.BoxShadow = new BoxShadows(new BoxShadow { Blur = 10, Color = AccentThemes.WithAlpha(theme.Primary, 0x80) });
        WorkspaceInitial.Text = ws.Name.Length > 0 ? ws.Name[..1].ToUpperInvariant() : "?";
        WorkspaceName.Text = ws.Name;
        var mod = OperatingSystem.IsMacOS() ? "⌘⌥" : "Ctrl+Alt+";
        ToolTip.SetTip(WorkspaceButton, $"Workspaces  ({mod}1–9)");
    }

    private void ShowWorkspaceMenu()
    {
        Flyout? flyout = null;
        var panel = new StackPanel { Spacing = 4, Width = 300 };
        panel.Children.Add(new TextBlock { Text = "WORKSPACES", Classes = { "eyebrow" }, FontSize = 9.5, Margin = new Thickness(4, 0, 0, 6) });
        var mod = OperatingSystem.IsMacOS() ? "⌘⌥" : "Ctrl+Alt+";

        for (var i = 0; i < _settings.Workspaces.Count; i++)
        {
            var ws = _settings.Workspaces[i];
            var theme = AccentThemes.Find(ws.Color);
            var count = ws.Id == CurrentWorkspace.Id ? _tabs.Tabs.Count(t => !t.IsInternal) : ws.Tabs.Count;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(8),
                Background = AccentThemes.Gradient(theme.Primary, theme.Secondary, horizontal: false),
                Child = new TextBlock
                {
                    Text = ws.Name.Length > 0 ? ws.Name[..1].ToUpperInvariant() : "?", FontWeight = FontWeight.Bold, FontSize = 12,
                    Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
            var text = new StackPanel { Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = ws.Name, FontWeight = FontWeight.SemiBold, FontSize = 13 });
            text.Children.Add(new TextBlock { Text = count == 1 ? "1 program" : $"{count} programs", Classes = { "faint" }, FontSize = 11 });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (i < 9)
            {
                var chip = new Border { Classes = { "chip" }, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = $"{mod}{i + 1}" } };
                Grid.SetColumn(chip, 2);
                row.Children.Add(chip);
            }
            var button = new Button { Classes = { "wsrow" }, Content = row };
            button.Classes.Set("current", ws.Id == CurrentWorkspace.Id);
            button.Click += (_, _) =>
            {
                flyout?.Hide();
                SwitchWorkspace(ws);
            };
            button.ContextMenu = WorkspaceItemMenu(ws, () => flyout?.Hide());
            panel.Children.Add(button);
        }

        panel.Children.Add(new Separator { Margin = new Thickness(0, 6) });
        var name = new TextBox { PlaceholderText = "New workspace name…" };
        void Create()
        {
            var text = name.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            var used = _settings.Workspaces.Select(w => w.Color).ToHashSet();
            var color = AccentThemes.All.FirstOrDefault(t => !used.Contains(t.Name))?.Name ?? AccentThemes.All[_settings.Workspaces.Count % AccentThemes.All.Count].Name;
            var ws = new Workspace { Name = text, Color = color };
            _settings.Workspaces.Add(ws);
            flyout?.Hide();
            SwitchWorkspace(ws);
        }
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Create(); };
        var add = new Button { Classes = { "accent" }, Content = "Create", Margin = new Thickness(8, 0, 0, 0) };
        add.Click += (_, _) => Create();
        var createRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        createRow.Children.Add(name);
        Grid.SetColumn(add, 1);
        createRow.Children.Add(add);
        panel.Children.Add(createRow);
        panel.Children.Add(new TextBlock { Text = "Right-click a workspace to rename, recolor or delete it.", Classes = { "faint" }, FontSize = 11, Margin = new Thickness(4, 4, 0, 0) });

        flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.ShowAt(WorkspaceButton);
    }

    private ContextMenu WorkspaceItemMenu(Workspace ws, Action closeFlyout)
    {
        var rename = MenuEntry("Rename…", "IconNote", () =>
        {
            closeFlyout();
            var box = new TextBox { Text = ws.Name, Width = 220 };
            var f = new Flyout { Content = box, Placement = PlacementMode.BottomEdgeAlignedLeft };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(box.Text)) return;
                ws.Name = box.Text.Trim();
                UpdateWorkspaceButton();
                SaveSettings();
                f.Hide();
            };
            f.ShowAt(WorkspaceButton);
            box.Focus();
            box.SelectAll();
        });
        var colors = new MenuItem { Header = "Color", Icon = new Avalonia.Controls.Shapes.Path { Classes = { "icon" }, Data = Res.Icon("IconSparkle") } };
        colors.ItemsSource = AccentThemes.All.Select(theme =>
        {
            var item = new MenuItem
            {
                Header = theme.Name,
                Icon = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = AccentThemes.Gradient(theme.Primary, theme.Secondary, false) },
            };
            item.Click += (_, _) =>
            {
                ws.Color = theme.Name;
                UpdateWorkspaceButton();
                SaveSettings();
            };
            return item;
        }).ToList();
        var delete = MenuEntry("Delete workspace", "IconClose", () =>
        {
            closeFlyout();
            DeleteWorkspace(ws);
        });
        delete.Classes.Add("danger");
        delete.IsEnabled = _settings.Workspaces.Count > 1;
        return new ContextMenu { ItemsSource = new Control[] { rename, colors, new Separator(), delete } };
    }

    /// <summary>Close the workspace's programs and remove it; switch away first when it is the current one.</summary>
    private void DeleteWorkspace(Workspace ws)
    {
        if (_settings.Workspaces.Count <= 1) return;
        if (ws.Id == CurrentWorkspace.Id) SwitchWorkspace(_settings.Workspaces.First(w => w.Id != ws.Id));
        if (_workspaceTabs.Remove(ws.Id, out var parked))
            foreach (var t in parked.Tabs) _embedding.Stop(TabKey(t));
        _settings.Workspaces.Remove(ws);
        SaveSettings();
        ShowToast($"Deleted {ws.Name}", "IconClose");
    }

    private void SwitchWorkspaceByIndex(int index)
    {
        if (index < _settings.Workspaces.Count) SwitchWorkspace(_settings.Workspaces[index]);
    }
}
