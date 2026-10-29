using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Overdesk.App.Controls;
using Overdesk.App.Services;
using Overdesk.App.Theme;
using Overdesk.Core.Platform;
using Overdesk.Core.Settings;

namespace Overdesk.App;

/// <summary>
/// The dock's productivity and developer cards: focus timer, local AI monitor, projects and running tasks,
/// git status of the active project, docker containers and pinned snippets.
/// </summary>
public partial class SideDock
{
    private readonly DispatcherTimer _devPoll = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly DispatcherTimer _focusTick = new() { Interval = TimeSpan.FromSeconds(1) };
    private IDockActions? _actions;
    private TaskRunner? _runner;
    private AiService? _ai;
    private IDesktopServices? _desktop;
    private bool _polling;

    // Focus timer state (lives for the session only).
    private TimeSpan _focusLeft;
    private bool _focusRunning;
    private bool _onBreak;

    internal void InitializeDev(IDockActions actions, TaskRunner runner, AiService ai, IDesktopServices desktop)
    {
        _actions = actions;
        _runner = runner;
        _ai = ai;
        _desktop = desktop;
        _focusLeft = TimeSpan.FromMinutes(_settings.FocusMinutes);
        runner.Changed += RenderProjects;
        _devPoll.Tick += (_, _) => _ = PollDevAsync();
        _devPoll.Start();
        _focusTick.Tick += (_, _) => FocusTick();

        ProjectsActions.Children.Add(CardButton("IconPlus", "Add a project folder", () => _ = actions.AddProjectAsync()));
        GitActions.Children.Add(CardButton("IconRefresh", "Refresh", () => _ = PollGitAsync()));
        DockerActions.Children.Add(CardButton("IconRefresh", "Refresh", () => _ = PollDockerAsync()));
        AiActions.Children.Add(CardButton("IconSparkle", "Open the AI panel", () => actions.OpenAi()));
        FocusActions.Children.Add(CardButton("IconRefresh", "Reset", ResetFocus));

        ApplyCardVisibility();
        RenderFocus();
        RefreshDev();
        _ = PollDevAsync();
    }

    /// <summary>Settings changed (projects added, cards switched off...): redraw the dev cards.</summary>
    internal void RefreshDev()
    {
        ApplyCardVisibility();
        RenderProjects();
        RenderSnippets();
        _ = PollGitAsync();
    }

    internal void ApplyCardVisibility()
    {
        var hidden = _settings.HiddenDockCards;
        MediaCard.IsVisible = !hidden.Contains("media");
        FocusCard.IsVisible = !hidden.Contains("focus");
        SystemCard.IsVisible = !hidden.Contains("system");
        AiCard.IsVisible = !hidden.Contains("ai");
        ProjectsCard.IsVisible = !hidden.Contains("projects");
        GitCard.IsVisible = !hidden.Contains("git");
        DockerCard.IsVisible = !hidden.Contains("docker");
        PortsCard.IsVisible = !hidden.Contains("ports");
        ClipCard.IsVisible = !hidden.Contains("clipboard");
        NotesCard.IsVisible = !hidden.Contains("notes");
    }

    /// <summary>The cards the user can switch on and off (id, label), in dock order.</summary>
    internal static readonly (string Id, string Label)[] Cards =
    {
        ("media", "Now playing"), ("focus", "Focus timer"), ("system", "System"), ("ai", "Local AI"),
        ("projects", "Projects"), ("git", "Git"), ("docker", "Docker"), ("ports", "Localhost"),
        ("clipboard", "Clipboard & snippets"), ("notes", "Scratchpad"),
    };

    private static Button CardButton(string icon, string tip, Action click)
    {
        var button = new Button { Classes = { "icon" }, Width = 22, Height = 22, Content = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon(icon), Height = 11 } };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static TextBlock Faint(string text) => new() { Text = text, Classes = { "faint" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };

    private static Border Dot(string brush, bool glow = false) => new()
    {
        Width = 7, Height = 7, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
        Background = Res.Brush(brush),
        BoxShadow = glow ? new BoxShadows(new BoxShadow { Blur = 6, Color = ((ISolidColorBrush)Res.Brush(brush)).Color }) : default,
    };

    private async Task PollDevAsync()
    {
        if (_polling || !_settings.SidebarExpanded) return;
        _polling = true;
        try
        {
            await Task.WhenAll(
                AiCard.IsVisible ? PollAiAsync() : Task.CompletedTask,
                GitCard.IsVisible ? PollGitAsync() : Task.CompletedTask,
                DockerCard.IsVisible ? PollDockerAsync() : Task.CompletedTask);
        }
        finally
        {
            _polling = false;
        }
    }

    // ---------------------------------------------------------------- focus timer

    private void FocusTick()
    {
        _focusLeft -= TimeSpan.FromSeconds(1);
        if (_focusLeft <= TimeSpan.Zero)
        {
            var finished = _onBreak ? "Break is over, back to it!" : $"{_settings.FocusMinutes} minutes of focus done. Take a break.";
            _onBreak = !_onBreak;
            _focusLeft = TimeSpan.FromMinutes(_onBreak ? _settings.BreakMinutes : _settings.FocusMinutes);
            _actions?.Toast(finished, "IconTimer");
            _desktop?.Notify("OverDesk", finished);
            Animator.Run(FocusCard, 0.9, t => FocusCard.BorderBrush = new SolidColorBrush(AccentThemes.WithAlpha(AccentThemes.Current.Primary, (byte)(255 * Math.Sin(t * Math.PI)))),
                () => FocusCard.ClearValue(BorderBrushProperty));
        }
        RenderFocus();
    }

    private void ResetFocus()
    {
        _focusTick.Stop();
        _focusRunning = false;
        _onBreak = false;
        _focusLeft = TimeSpan.FromMinutes(_settings.FocusMinutes);
        RenderFocus();
    }

    private void RenderFocus()
    {
        FocusBody.Children.Clear();
        var total = TimeSpan.FromMinutes(_onBreak ? _settings.BreakMinutes : _settings.FocusMinutes);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = _focusLeft.ToString(@"mm\:ss"), FontSize = 30, FontWeight = FontWeight.Light, LetterSpacing = -0.5 });
        text.Children.Add(new TextBlock { Text = _onBreak ? "Break" : _focusRunning ? "Focusing" : "Ready to focus", Classes = { "muted" }, FontSize = 11.5 });
        row.Children.Add(text);
        var play = new Button { Classes = { "playbtn", "small" }, VerticalAlignment = VerticalAlignment.Center };
        var icon = new Avalonia.Controls.Shapes.Path { Classes = { "icon" }, Data = Res.Icon(_focusRunning ? "IconPause" : "IconPlay"), Stroke = Brushes.White, Fill = Brushes.White, Height = 12, Margin = new Thickness(_focusRunning ? 0 : 2, 0, 0, 0) };
        play.Content = icon;
        ToolTip.SetTip(play, _focusRunning ? "Pause" : "Start");
        play.Click += (_, _) =>
        {
            _focusRunning = !_focusRunning;
            if (_focusRunning) _focusTick.Start(); else _focusTick.Stop();
            RenderFocus();
        };
        Grid.SetColumn(play, 1);
        row.Children.Add(play);
        FocusBody.Children.Add(row);
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, MinHeight = 4, CornerRadius = new CornerRadius(2), Background = Res.Brush("Bg4Brush"),
            Value = total.TotalSeconds <= 0 ? 0 : 1 - _focusLeft.TotalSeconds / total.TotalSeconds };
        bar.Bind(ProgressBar.ForegroundProperty, this.GetResourceObservable(_onBreak ? "Accent2Brush" : "AccentGradient"));
        FocusBody.Children.Add(bar);
    }

    // ---------------------------------------------------------------- local AI

    private async Task PollAiAsync()
    {
        if (_ai is null) return;
        var ollama = _ai.Ollama;
        var problem = await ollama.CheckAsync(CancellationToken.None);
        IReadOnlyList<Overdesk.AI.AiModel> models = Array.Empty<Overdesk.AI.AiModel>();
        IReadOnlyList<(string Name, long Size, long VramSize)> running = Array.Empty<(string, long, long)>();
        if (problem is null)
        {
            try
            {
                models = await ollama.ListModelsAsync(CancellationToken.None);
                running = await ollama.RunningAsync(CancellationToken.None);
            }
            catch (Exception) { }
        }

        AiBody.Children.Clear();
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        status.Children.Add(Dot(problem is null ? "SuccessBrush" : "FaintBrush", problem is null));
        status.Children.Add(new TextBlock
        {
            Text = problem is null ? $"Ollama · {models.Count} model{(models.Count == 1 ? "" : "s")}" : "Ollama is not running",
            FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });
        AiBody.Children.Add(status);

        foreach (var (name, size, vram) in running)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = name, Classes = { "mono" }, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
            var where = size > 0 ? $"{size / 1024d / 1024 / 1024:0.0} GB · {(vram * 100.0 / size):0}% GPU" : "loaded";
            var detail = new TextBlock { Text = where, Classes = { "faint" }, FontSize = 11, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(detail, 1);
            row.Children.Add(detail);
            AiBody.Children.Add(new Border { Classes = { "dockrowstatic" }, Child = row });
        }
        if (problem is null && running.Count == 0) AiBody.Children.Add(Faint("No model loaded right now. Ask something in the AI panel to load one."));
        if (problem is not null) AiBody.Children.Add(Faint("Install Ollama (ollama.com) to run models on this computer; LM Studio works too."));
        if (_ai.LastTokensPerSecond is { } speed)
            AiBody.Children.Add(new TextBlock { Text = $"Last answer: {speed:0} tokens/s ({_ai.Provider.Name})", Classes = { "faint", "mono" }, FontSize = 10.5 });
    }

    // ---------------------------------------------------------------- projects & tasks

    private void RenderProjects()
    {
        if (_actions is null || _runner is null) return;
        ProjectsBody.Children.Clear();
        if (_settings.Projects.Count == 0) ProjectsBody.Children.Add(Faint("Add a project folder to start its dev server, editor and browser in one click."));
        foreach (var project in _settings.Projects)
        {
            var running = _runner.Tasks.Any(t => t.ProjectId == project.Id && t.IsRunning && !t.Name.EndsWith(": editor", StringComparison.Ordinal));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(Dot(running ? "SuccessBrush" : "FaintBrush", running));
            var text = new StackPanel { Margin = new Thickness(8, 0, 4, 0) };
            text.Children.Add(new TextBlock { Text = project.Name + (project.Id == _settings.ActiveProject ? "  ·  active" : ""), FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = project.Commands.FirstOrDefault() ?? project.Folder, Classes = { "faint", "mono" }, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var go = CardButton(running ? "IconStop" : "IconPlay", running ? "Stop" : "Launch", () =>
            {
                if (running) _actions.StopProject(project); else _actions.LaunchProject(project);
                RenderProjects();
            });
            Grid.SetColumn(go, 2);
            row.Children.Add(go);
            var button = new Button { Classes = { "dockrow" }, Content = row };
            button.Click += (_, _) =>
            {
                _settings.ActiveProject = project.Id;
                SettingsChanged?.Invoke();
                RenderProjects();
                _ = PollGitAsync();
            };
            button.ContextMenu = ProjectMenu(project);
            ProjectsBody.Children.Add(button);
        }

        var tasks = _runner.Tasks.Where(t => !t.Name.EndsWith(": editor", StringComparison.Ordinal)).Take(6).ToList();
        if (tasks.Count > 0) ProjectsBody.Children.Add(new TextBlock { Text = "TASKS", Classes = { "eyebrow" }, FontSize = 9, Margin = new Thickness(0, 6, 0, 0) });
        foreach (var task in tasks)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(Dot(task.IsRunning ? "SuccessBrush" : task.ExitCode == 0 ? "MutedBrush" : "DangerBrush", task.IsRunning));
            var name = new TextBlock { Text = task.Name, Classes = { "mono" }, FontSize = 11.5, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var button = new Button { Classes = { "dockrow" }, Content = row };
            ToolTip.SetTip(button, $"{task.Command}\nClick to see the output");
            button.Click += (_, _) => _actions.ShowTask(task);
            button.ContextMenu = new ContextMenu
            {
                ItemsSource = new Control[]
                {
                    MenuItem("Show output", "IconTerminal", () => _actions.ShowTask(task)),
                    MenuItem("Stop", "IconStop", () => _runner.Stop(task)),
                    MenuItem("Remove from the list", "IconClose", () => _runner.Remove(task)),
                },
            };
            ProjectsBody.Children.Add(button);
        }
    }

    private ContextMenu ProjectMenu(DevProject project) => new()
    {
        ItemsSource = new Control[]
        {
            MenuItem("Launch", "IconRocket", () => _actions!.LaunchProject(project)),
            MenuItem("Stop", "IconStop", () => _actions!.StopProject(project)),
            new Separator(),
            MenuItem("Open terminal here", "IconTerminal", () => _actions!.RunCommand(
                OperatingSystem.IsMacOS() ? $"open -a Terminal '{project.Folder}'" : $"start cmd /k cd /d \"{project.Folder}\"", project.Folder)),
            MenuItem("Reveal the folder", "IconFolder", () => _actions!.RunCommand(
                OperatingSystem.IsMacOS() ? $"open '{project.Folder}'" : $"explorer \"{project.Folder}\"", project.Folder)),
            MenuItem("Edit projects…", "IconSliders", () => _actions!.EditProjects()),
            new Separator(),
            MenuItem("Remove project", "IconClose", () =>
            {
                _settings.Projects.Remove(project);
                SettingsChanged?.Invoke();
                RenderProjects();
            }),
        },
    };

    private static MenuItem MenuItem(string header, string icon, Action click)
    {
        var item = new MenuItem { Header = header, Icon = new Avalonia.Controls.Shapes.Path { Classes = { "icon" }, Data = Res.Icon(icon) } };
        item.Click += (_, _) => click();
        return item;
    }

    // ---------------------------------------------------------------- git

    private async Task PollGitAsync()
    {
        if (_actions?.CurrentProjectFolder() is not { } folder || !Directory.Exists(folder))
        {
            GitBody.Children.Clear();
            GitBody.Children.Add(Faint("Add a project to see its branch and changes here."));
            return;
        }
        var status = await TaskRunner.CaptureAsync("git status --porcelain=v2 --branch", folder);
        GitBody.Children.Clear();
        if (status is null)
        {
            GitBody.Children.Add(Faint($"{Path.GetFileName(folder)} is not a git repository."));
            return;
        }

        string branch = "?";
        int ahead = 0, behind = 0, changed = 0, untracked = 0;
        foreach (var line in status.Split('\n'))
        {
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal)) branch = line[14..].Trim();
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = line[12..].Split(' ');
                int.TryParse(parts[0].TrimStart('+'), out ahead);
                if (parts.Length > 1) int.TryParse(parts[1].TrimStart('-'), out behind);
            }
            else if (line.StartsWith("1 ", StringComparison.Ordinal) || line.StartsWith("2 ", StringComparison.Ordinal) || line.StartsWith("u ", StringComparison.Ordinal)) changed++;
            else if (line.StartsWith("? ", StringComparison.Ordinal)) untracked++;
        }

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        head.Children.Add(new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small", "accent" }, Data = Res.Icon("IconBranch") });
        head.Children.Add(new TextBlock { Text = branch, Classes = { "mono" }, FontWeight = FontWeight.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        if (ahead > 0) head.Children.Add(new Border { Classes = { "chip" }, Child = new TextBlock { Text = $"↑{ahead}" } });
        if (behind > 0) head.Children.Add(new Border { Classes = { "chip" }, Child = new TextBlock { Text = $"↓{behind}" } });
        GitBody.Children.Add(head);
        GitBody.Children.Add(Faint(changed + untracked == 0 ? "Working tree clean" : $"{changed} changed · {untracked} new"));

        var buttons = new WrapPanel();
        void Add(string text, string icon, Action click)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            row.Children.Add(new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon(icon), Height = 10 });
            row.Children.Add(new TextBlock { Text = text, FontSize = 11.5 });
            var b = new Button { Classes = { "webpill" }, Height = 26, Margin = new Thickness(0, 0, 5, 5), Content = row };
            b.Click += (_, _) => click();
            buttons.Children.Add(b);
        }
        Add("Pull", "IconChevronDown", () => _actions.RunCommand("git pull", folder));
        Add("Push", "IconDrop", () => _actions.RunCommand("git push", folder));
        Add("Commit msg", "IconSparkle", () => _ = ((MainWindow)TopLevel.GetTopLevel(this)!).CommitMessageWithAiAsync(folder));
        if (await TaskRunner.CaptureAsync("git remote get-url origin", folder) is { } remote && RemoteWebUrl(remote.Trim()) is { } web)
            Add("GitHub", "IconGlobe", () => _actions.OpenWeb(web, Path.GetFileName(folder), "repo:" + web));
        GitBody.Children.Add(buttons);
    }

    /// <summary>git@github.com:me/repo.git or https://github.com/me/repo.git → https://github.com/me/repo</summary>
    private static string? RemoteWebUrl(string remote)
    {
        if (remote.StartsWith("git@", StringComparison.Ordinal))
            remote = "https://" + remote[4..].Replace(':', '/');
        if (!remote.StartsWith("http", StringComparison.Ordinal)) return null;
        return remote.EndsWith(".git", StringComparison.Ordinal) ? remote[..^4] : remote;
    }

    // ---------------------------------------------------------------- docker

    private async Task PollDockerAsync()
    {
        var output = await TaskRunner.CaptureAsync("docker ps -a --format \"{{json .}}\"", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        DockerBody.Children.Clear();
        if (output is null)
        {
            DockerBody.Children.Add(Faint("Docker is not running (or not installed)."));
            return;
        }
        var containers = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => { try { return JsonNode.Parse(l); } catch { return null; } })
            .OfType<JsonNode>().ToList();
        if (containers.Count == 0) DockerBody.Children.Add(Faint("No containers."));
        foreach (var c in containers.Take(8))
        {
            var name = c["Names"]?.ToString() ?? "?";
            var up = c["State"]?.ToString() == "running";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(Dot(up ? "SuccessBrush" : "FaintBrush", up));
            var text = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            text.Children.Add(new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = $"{c["Image"]} · {c["Status"]}", Classes = { "faint" }, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var button = new Button { Classes = { "dockrow" }, Content = row };
            ToolTip.SetTip(button, c["Ports"]?.ToString() is { Length: > 0 } ports ? ports : name);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            button.Click += (_, _) => _actions?.RunCommand($"docker logs --tail 200 -f {name}", home);
            button.ContextMenu = new ContextMenu
            {
                ItemsSource = new Control[]
                {
                    MenuItem("Logs", "IconTerminal", () => _actions?.RunCommand($"docker logs --tail 200 -f {name}", home)),
                    MenuItem(up ? "Stop" : "Start", up ? "IconStop" : "IconPlay", () => _actions?.RunCommand($"docker {(up ? "stop" : "start")} {name}", home)),
                    MenuItem("Restart", "IconRefresh", () => _actions?.RunCommand($"docker restart {name}", home)),
                },
            };
            DockerBody.Children.Add(button);
        }
    }

    // ---------------------------------------------------------------- snippets

    private void RenderSnippets()
    {
        SnippetList.Children.Clear();
        foreach (var snippet in _settings.Snippets)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small", "accent" }, Data = Res.Icon("IconPin"), Height = 10 });
            var text = new TextBlock { Text = snippet.Name, FontSize = 11.5, Classes = { "mono" }, Margin = new Thickness(7, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var button = new Button { Classes = { "dockrow" }, Content = row };
            ToolTip.SetTip(button, snippet.Text.Length > 400 ? snippet.Text[..400] + "…" : snippet.Text);
            button.Click += async (_, _) =>
            {
                if (_clips is not null) await _clips.CopyAsync(snippet.Text);
                Toast?.Invoke("Snippet copied", "IconCopy");
            };
            button.ContextMenu = new ContextMenu
            {
                ItemsSource = new Control[]
                {
                    MenuItem("Unpin snippet", "IconClose", () =>
                    {
                        _settings.Snippets.Remove(snippet);
                        SettingsChanged?.Invoke();
                        RenderSnippets();
                    }),
                },
            };
            SnippetList.Children.Add(button);
        }
    }

    /// <summary>Pin a clipboard entry so it stays (saved in settings).</summary>
    private void PinSnippet(string text)
    {
        var name = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        _settings.Snippets.Insert(0, new Snippet { Name = name.Length > 48 ? name[..47] + "…" : name, Text = text });
        SettingsChanged?.Invoke();
        RenderSnippets();
        Toast?.Invoke("Pinned as a snippet", "IconPin");
    }
}
