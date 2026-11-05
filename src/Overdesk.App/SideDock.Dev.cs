using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Overdesk.App.Controls;
using Overdesk.App.Controls.Depth;
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
    private int _devTicks;
    private bool _dockerDown;
    private string? _gitSkipFolder;

    // Focus timer state (lives for the session only).
    private TimeSpan _focusLeft;
    private DateTime _focusEnds;
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
        GitActions.Children.Add(CardButton("IconRefresh", "Refresh", () => { _gitSkipFolder = null; _ = PollGitAsync(); }));
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
        RefreshProjects();
        RenderSnippets();
        RefreshFocus();
    }

    /// <summary>The project list or the active project changed: redraw the rows and read git again.</summary>
    internal void RefreshProjects()
    {
        _gitSkipFolder = null;
        RenderProjects();
        _ = PollGitAsync();
    }

    /// <summary>A new session or break length applies to the idle timer right away.</summary>
    internal void RefreshFocus()
    {
        if (_focusRunning) return;
        _focusLeft = TimeSpan.FromMinutes(_onBreak ? _settings.BreakMinutes : _settings.FocusMinutes);
        RenderFocus();
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
        RailMedia.IsVisible = MediaCard.IsVisible;
        RailSystem.IsVisible = SystemCard.IsVisible;
        RailPorts.IsVisible = PortsCard.IsVisible;
        RailClips.IsVisible = ClipCard.IsVisible;
        RailNotes.IsVisible = NotesCard.IsVisible;
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
        var button = new Button { Classes = { "icon", "capbtn" }, Width = 24, Height = 24, Content = new Avalonia.Controls.Shapes.Path { Classes = { "icon", "small" }, Data = Res.Icon(icon), Height = 11 } };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static TextBlock Faint(string text) => new() { Text = text, Classes = { "faint" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };

    /// <summary>A glass status bead; live ones glow and breathe.</summary>
    private static Control Dot(string brush, bool glow = false)
        => new Led3D(Res.Brush(brush) is ISolidColorBrush b ? b.Color : Colors.Gray, glow);

    private async Task PollDevAsync()
    {
        if (_polling || !_settings.SidebarExpanded) return;
        _polling = true;
        _devTicks++;
        try
        {
            // Docker that is missing or stopped is asked again only once a minute (Refresh asks at once).
            await Task.WhenAll(
                AiCard.IsVisible ? PollAiAsync() : Task.CompletedTask,
                GitCard.IsVisible ? PollGitAsync() : Task.CompletedTask,
                DockerCard.IsVisible && (!_dockerDown || _devTicks % 10 == 0) ? PollDockerAsync() : Task.CompletedTask);
        }
        finally
        {
            _polling = false;
        }
    }

    // ---------------------------------------------------------------- focus timer

    private void FocusTick()
    {
        var now = DateTime.UtcNow;
        _focusLeft = _focusEnds - now;
        if (_focusLeft <= TimeSpan.Zero)
        {
            var finished = _onBreak ? "Break is over, back to it!" : $"{_settings.FocusMinutes} minutes of focus done. Take a break.";
            _onBreak = !_onBreak;
            var length = TimeSpan.FromMinutes(_onBreak ? _settings.BreakMinutes : _settings.FocusMinutes);
            // Keep the schedule exact after a stall, but never start a phase that is already over (sleep).
            _focusEnds = _focusEnds + length > now ? _focusEnds + length : now + length;
            _focusLeft = _focusEnds - now;
            _actions?.Toast(finished, "IconTimer");
            _desktop?.Notify("OverDesk", finished);
            FocusCard.Flash();
        }
        RenderFocus();
    }

    /// <summary>For the AI: start a focus session (optionally of a new length), stop it, or describe it.</summary>
    internal void StartFocus(int? minutes)
    {
        if (minutes is > 0 and <= 240) _settings.FocusMinutes = minutes.Value;
        ResetFocus();
        _focusRunning = true;
        _focusEnds = DateTime.UtcNow + _focusLeft;
        _focusTick.Start();
        RenderFocus();
    }

    internal void StopFocus() => ResetFocus();

    internal string FocusStatus => _focusRunning
        ? $"{(_onBreak ? "On a break" : "Focusing")}, {_focusLeft.ToString(@"mm\:ss")} left (sessions of {_settings.FocusMinutes} min, breaks of {_settings.BreakMinutes} min)."
        : $"Not running (sessions of {_settings.FocusMinutes} min).";

    private void ResetFocus()
    {
        _focusTick.Stop();
        _focusRunning = false;
        _onBreak = false;
        _focusLeft = TimeSpan.FromMinutes(_settings.FocusMinutes);
        RenderFocus();
    }

    private DialRing3D? _focusDial;
    private Readout3D? _focusTime;
    private TextBlock? _focusState;
    private Button? _focusPlay;

    /// <summary>Development snapshots: hover the third-newest block of the first chart.</summary>
    internal void DevInspectChart() => _metricRows.FirstOrDefault(r => r.Chart is not null)?.Chart?.DevInspect(3);

    /// <summary>Development snapshots: a running focus session with 40% of it gone.</summary>
    internal void DevFocusDemo()
    {
        ResetFocus();
        _focusLeft = TimeSpan.FromMinutes(_settings.FocusMinutes * 0.6);
        _focusEnds = DateTime.UtcNow + _focusLeft;
        _focusRunning = true;
        RenderFocus();
    }

    /// <summary>The timer is a 3D dial with the time in extruded digits floating in its middle; the play orb sits beside it.</summary>
    private void RenderFocus()
    {
        if (_focusDial is null)
        {
            _focusDial = new DialRing3D { Width = 176, Height = 118, HorizontalAlignment = HorizontalAlignment.Left };
            _focusDial.Bind(DialRing3D.ColorProperty, this.GetResourceObservable("AccentColor"));
            _focusDial.Bind(DialRing3D.Color2Property, this.GetResourceObservable("Accent2Color"));
            _focusTime = new Readout3D { Size = 24, Weight = FontWeight.SemiBold, Depth = 4, HorizontalAlignment = HorizontalAlignment.Center };
            _focusTime.Bind(Readout3D.ColorProperty, this.GetResourceObservable("AccentColor"));
            _focusState = new TextBlock { Classes = { "eyebrow" }, FontSize = 8.5, HorizontalAlignment = HorizontalAlignment.Center };
            var middle = new StackPanel
            {
                Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10), Children = { _focusTime, _focusState },
            };
            var dial = new Panel { Width = 176, HorizontalAlignment = HorizontalAlignment.Left, Children = { _focusDial, middle } };

            _focusPlay = new Button { Classes = { "playbtn" }, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            _focusPlay.Click += (_, _) =>
            {
                _focusRunning = !_focusRunning;
                if (_focusRunning)
                {
                    _focusEnds = DateTime.UtcNow + _focusLeft;
                    _focusTick.Start();
                }
                else
                {
                    _focusLeft = _focusEnds - DateTime.UtcNow;
                    if (_focusLeft < TimeSpan.Zero) _focusLeft = TimeSpan.Zero;
                    _focusTick.Stop();
                }
                RenderFocus();
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(-6, -4, 0, -6) };
            row.Children.Add(dial);
            Grid.SetColumn(_focusPlay, 1);
            row.Children.Add(_focusPlay);
            FocusBody.Children.Add(row);
        }

        var total = TimeSpan.FromMinutes(_onBreak ? _settings.BreakMinutes : _settings.FocusMinutes);
        _focusDial.Fraction = total.TotalSeconds <= 0 ? 1 : Math.Clamp(_focusLeft.TotalSeconds / total.TotalSeconds, 0, 1);
        _focusDial.IsActive = _focusRunning;
        _focusTime!.Text = _focusLeft.ToString(@"mm\:ss");
        _focusState!.Text = _onBreak ? "BREAK" : _focusRunning ? "FOCUSING" : "READY";
        _focusPlay!.Content = new Avalonia.Controls.Shapes.Path
        {
            Classes = { "icon" }, Data = Res.Icon(_focusRunning ? "IconPause" : "IconPlay"), Stroke = Brushes.White, Fill = Brushes.White,
            Height = 15, Margin = new Thickness(_focusRunning ? 0 : 3, 0, 0, 0),
        };
        ToolTip.SetTip(_focusPlay, _focusRunning ? "Pause" : "Start");
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
                OperatingSystem.IsMacOS() ? $"open -a Terminal '{project.Folder}'" : $"start cmd /k cd /d \"{project.Folder}\"", project.Folder, showLog: false)),
            MenuItem("Reveal the folder", "IconFolder", () => _actions!.RevealFolder(project.Folder)),
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

    private static MenuItem MenuItem(string header, string icon, Action click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, Icon = new Avalonia.Controls.Shapes.Path { Classes = { "icon" }, Data = Res.Icon(icon) } };
        item.Click += (_, _) => click();
        return item;
    }

    // ---------------------------------------------------------------- git

    private async Task PollGitAsync()
    {
        if (_actions?.CurrentProjectFolder() is not { } folder || !Directory.Exists(folder))
        {
            SetGitCard(Faint("Add a project to see its branch and changes here."));
            return;
        }
        if (folder == _gitSkipFolder) return;
        if (await TaskRunner.FindExecutableAsync("git") is not { } git)
        {
            _gitSkipFolder = folder;
            SetGitCard(Faint("git is not installed."));
            return;
        }
        // Both captures finish before the card is touched, so overlapping polls cannot interleave their rows.
        var statusTask = TaskRunner.CaptureAsync(git, ["status", "--porcelain=v2", "--branch"], folder);
        var remoteTask = TaskRunner.CaptureAsync(git, ["remote", "get-url", "origin"], folder);
        var status = await statusTask;
        var remote = await remoteTask;
        if (status is null)
        {
            _gitSkipFolder = folder; // a folder without a repository stays one until the project changes or Refresh is pressed
            SetGitCard(Faint($"{Path.GetFileName(folder)} is not a git repository."));
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
        if (remote is not null && RemoteWebUrl(remote.Trim()) is { } web)
            Add("GitHub", "IconGlobe", () => _actions.OpenWeb(web, Path.GetFileName(folder), "repo:" + web));
        SetGitCard(head, Faint(changed + untracked == 0 ? "Working tree clean" : $"{changed} changed · {untracked} new"), buttons);
    }

    private void SetGitCard(params Control[] rows)
    {
        GitBody.Children.Clear();
        GitBody.Children.AddRange(rows);
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
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var docker = await TaskRunner.FindExecutableAsync("docker");
        var output = docker is null ? null : await TaskRunner.CaptureAsync(docker, ["ps", "-a", "--format", "{{json .}}"], home);
        DockerBody.Children.Clear();
        _dockerDown = output is null;
        if (output is null)
        {
            DockerBody.Children.Add(Faint(docker is null ? "Docker is not installed." : "Docker is not running."));
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
            button.Click += (_, _) => ShowLogs(name, home);
            button.ContextMenu = new ContextMenu
            {
                ItemsSource = new Control[]
                {
                    MenuItem("Logs", "IconTerminal", () => ShowLogs(name, home)),
                    MenuItem(up ? "Stop" : "Start", up ? "IconStop" : "IconPlay", () => _actions?.RunCommand($"docker {(up ? "stop" : "start")} {name}", home, showLog: false)),
                    MenuItem("Restart", "IconRefresh", () => _actions?.RunCommand($"docker restart {name}", home, showLog: false)),
                },
            };
            DockerBody.Children.Add(button);
        }
    }

    private void ShowLogs(string container, string home)
    {
        var command = $"docker logs --tail 200 -f {container}";
        if (_runner?.Tasks.FirstOrDefault(t => t.IsRunning && t.Command == command) is { } follower) _actions?.ShowTask(follower);
        else _actions?.RunCommand(command, home);
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
            var index = _settings.Snippets.IndexOf(snippet);
            button.ContextMenu = new ContextMenu
            {
                ItemsSource = new Control[]
                {
                    MenuItem("Rename…", "IconNote", () => EditSnippet(button, snippet, name: true)),
                    MenuItem("Edit text…", "IconTerminal", () => EditSnippet(button, snippet, name: false)),
                    new Separator(),
                    MenuItem("Move up", "IconChevronUp", () => MoveSnippet(snippet, -1), enabled: index > 0),
                    MenuItem("Move down", "IconChevronDown", () => MoveSnippet(snippet, 1), enabled: index < _settings.Snippets.Count - 1),
                    new Separator(),
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

    private void MoveSnippet(Snippet snippet, int delta)
    {
        var index = _settings.Snippets.IndexOf(snippet);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _settings.Snippets.Count) return;
        _settings.Snippets.RemoveAt(index);
        _settings.Snippets.Insert(target, snippet);
        SettingsChanged?.Invoke();
        RenderSnippets();
    }

    /// <summary>A small flyout over the row: Enter keeps the new name (or text; Shift+Enter adds a line), Escape drops it.</summary>
    private void EditSnippet(Control anchor, Snippet snippet, bool name)
    {
        var box = new TextBox
        {
            Text = name ? snippet.Name : snippet.Text, Width = 260, Classes = { "mono" }, FontSize = 12,
            AcceptsReturn = !name, TextWrapping = name ? TextWrapping.NoWrap : TextWrapping.Wrap, MaxHeight = 220,
        };
        var flyout = new Flyout { Content = box, Placement = PlacementMode.BottomEdgeAlignedLeft };
        var keep = false;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; flyout.Hide(); }
            else if (e.Key == Key.Enter && (name || !e.KeyModifiers.HasFlag(KeyModifiers.Shift))) { e.Handled = true; keep = true; flyout.Hide(); }
        };
        flyout.Closed += (_, _) =>
        {
            var text = box.Text ?? "";
            if (!keep || text.Trim().Length == 0 || text == (name ? snippet.Name : snippet.Text)) return;
            if (name) snippet.Name = text.Trim();
            else snippet.Text = text;
            SettingsChanged?.Invoke();
            RenderSnippets();
        };
        flyout.ShowAt(anchor);
        box.Focus();
        box.SelectAll();
    }

    /// <summary>Pin a clipboard entry so it stays (saved in settings).</summary>
    private void PinSnippet(string text)
    {
        var name = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        _settings.Snippets.RemoveAll(sn => sn.Text == text);
        _settings.Snippets.Insert(0, new Snippet { Name = name.Length > 48 ? name[..47] + "…" : name, Text = text });
        SettingsChanged?.Invoke();
        RenderSnippets();
        Toast?.Invoke("Pinned as a snippet", "IconPin");
    }
}
