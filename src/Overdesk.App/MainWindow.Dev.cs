using System.Text.Json.Nodes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Overdesk.App.Services;
using Overdesk.Core.Settings;
using Overdesk.Core.Tabs;

namespace Overdesk.App;

/// <summary>What the side dock asks the window to do (projects, tasks, web, AI...).</summary>
internal interface IDockActions
{
    void LaunchProject(DevProject project);
    void StopProject(DevProject project);
    Task AddProjectAsync();
    void EditProjects();
    void ShowTask(RunningTask task);
    void OpenWeb(string url, string title, string? key = null);
    void OpenAi(string? prompt = null, string? question = null);
    void RunCommand(string command, string folder);
    void Toast(string text, string icon);
    string? CurrentProjectFolder();
}

/// <summary>
/// Developer features: projects (folder + start commands + URL, one click to launch everything), a task runner with
/// live logs, git helpers (status, pull/push, AI commit messages) and shell commands from the command palette.
/// </summary>
public partial class MainWindow : IDockActions
{
    private static readonly string[] Editors = { "Visual Studio Code", "Cursor", "Zed", "Windsurf", "Rider", "Sublime Text", "Code" };
    private readonly TaskRunner _runner = new();

    internal TaskRunner Runner => _runner;

    void IDockActions.Toast(string text, string icon) => ShowToast(text, icon);
    void IDockActions.OpenWeb(string url, string title, string? key) => OpenWeb(url, title, key);
    void IDockActions.ShowTask(RunningTask task) => ShowTask(task);
    void IDockActions.OpenAi(string? prompt, string? question) => OpenAi(prompt, question);

    public string? CurrentProjectFolder()
        => _settings.Projects.FirstOrDefault(p => p.Id == _settings.ActiveProject)?.Folder ?? _settings.Projects.FirstOrDefault()?.Folder;

    public void RunCommand(string command, string folder)
    {
        folder = string.IsNullOrEmpty(folder) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : folder;
        var task = _runner.Start(command.Length > 40 ? command[..39] + "…" : command, command, folder);
        ShowTask(task);
    }

    internal void ShowTask(RunningTask task)
    {
        if (OverlayOpen && !TaskLog.IsVisible) return;
        TaskLog.Show(task, _runner);
        HideAllEmbedded();
    }

    // ---------------------------------------------------------------- projects

    public void LaunchProject(DevProject project)
    {
        _settings.ActiveProject = project.Id;
        if (!Directory.Exists(project.Folder))
        {
            ShowToast($"Folder not found: {project.Folder}", "IconAlert");
            return;
        }

        // 1. The editor, as a tab, opened on the project folder.
        if (project.OpenEditor && FindEditor() is { } editor)
        {
            _runner.Start($"{project.Name}: editor", OperatingSystem.IsMacOS()
                ? $"open -a {Quote(editor)} {Quote(project.Folder)}"
                : $"{Quote(editor)} {Quote(project.Folder)}", project.Folder, project.Id);
            OpenProgram(editor);
        }

        // 2. The start commands, each as a task with its own log.
        foreach (var command in project.Commands.Where(c => !string.IsNullOrWhiteSpace(c)))
            _runner.Start($"{project.Name}: {command}", command, project.Folder, project.Id);

        // 3. The app itself, once the dev server has had a moment to come up.
        if (project.Url is { Length: > 0 } url)
            DispatcherTimer.RunOnce(() => OpenWeb(url, project.Name, "project:" + project.Id), TimeSpan.FromSeconds(project.Commands.Count > 0 ? 4 : 0.1));

        SaveSettings();
        ShowToast($"Launching {project.Name}", "IconRocket");
    }

    public void StopProject(DevProject project)
    {
        foreach (var task in _runner.Tasks.Where(t => t.ProjectId == project.Id && t.IsRunning)) _runner.Stop(task);
        ShowToast($"Stopped {project.Name}", "IconPower");
    }

    public async Task AddProjectAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a project folder", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder) return;
        var project = DetectProject(folder);
        _settings.Projects.Add(project);
        _settings.ActiveProject = project.Id;
        SaveSettings();
        Dock.RefreshDev();
        ShowToast($"Added {project.Name}" + (project.Commands.Count > 0 ? $" · {project.Commands[0]}" : ""), "IconRocket");
    }

    public void EditProjects() => OpenSettings();

    /// <summary>Guess how a folder starts: package.json scripts, .NET, Rust, Python, Go...</summary>
    private static DevProject DetectProject(string folder)
    {
        var project = new DevProject { Name = Path.GetFileName(folder.TrimEnd('/', '\\')), Folder = folder };
        try
        {
            var packageJson = Path.Combine(folder, "package.json");
            if (File.Exists(packageJson))
            {
                var scripts = JsonNode.Parse(File.ReadAllText(packageJson))?["scripts"]?.AsObject();
                var runner = File.Exists(Path.Combine(folder, "pnpm-lock.yaml")) ? "pnpm" : File.Exists(Path.Combine(folder, "yarn.lock")) ? "yarn" : File.Exists(Path.Combine(folder, "bun.lockb")) ? "bun" : "npm";
                var script = new[] { "dev", "start", "serve" }.FirstOrDefault(s => scripts?.ContainsKey(s) == true);
                if (script is not null) project.Commands.Add(runner == "npm" ? $"npm run {script}" : $"{runner} {script}");
                var text = scripts?[script ?? ""]?.ToString() ?? "";
                project.Url = text.Contains("vite") ? "http://localhost:5173" : text.Contains("next") || text.Contains("react-scripts") ? "http://localhost:3000"
                    : text.Contains("astro") ? "http://localhost:4321" : text.Contains("ng ") ? "http://localhost:4200" : null;
            }
            else if (Directory.EnumerateFiles(folder, "*.sln*").Any() || Directory.EnumerateFiles(folder, "*.csproj").Any())
                project.Commands.Add("dotnet run");
            else if (File.Exists(Path.Combine(folder, "Cargo.toml"))) project.Commands.Add("cargo run");
            else if (File.Exists(Path.Combine(folder, "go.mod"))) project.Commands.Add("go run .");
            else if (File.Exists(Path.Combine(folder, "manage.py"))) { project.Commands.Add("python manage.py runserver"); project.Url = "http://localhost:8000"; }
            else if (File.Exists(Path.Combine(folder, "pyproject.toml")) || File.Exists(Path.Combine(folder, "main.py"))) project.Commands.Add("python main.py");
        }
        catch (Exception) { /* unreadable folder: keep the bare project */ }
        return project;
    }

    private string? FindEditor()
    {
        var installed = ProgramLibrary.Installed.IsCompletedSuccessfully ? ProgramLibrary.Installed.Result : Array.Empty<string>();
        foreach (var name in Editors)
            if (installed.FirstOrDefault(p => string.Equals(ProgramNames.FromPath(p), name, StringComparison.OrdinalIgnoreCase)) is { } path)
                return path;
        return null;
    }

    private static string Quote(string s) => OperatingSystem.IsWindows() ? "\"" + s + "\"" : "'" + s.Replace("'", "'\\''") + "'";

    // ---------------------------------------------------------------- badges

    private bool _readingBadges;

    /// <summary>Copy the OS's unread badges (Mail "3", Discord "•") onto the matching program tabs.</summary>
    private async Task UpdateBadgesAsync()
    {
        if (_readingBadges) return;
        _readingBadges = true;
        try
        {
            var badges = await Task.Run(_desktop.AppBadges);
            foreach (var tab in _tabs.Tabs.Where(t => !t.IsInternal))
                tab.Badge = badges.TryGetValue(tab.Title, out var b) ? b : null;
        }
        catch (Exception) { }
        finally
        {
            _readingBadges = false;
        }
    }

    // ---------------------------------------------------------------- git + AI

    /// <summary>Ask the AI for a commit message from the staged diff (or the whole diff when nothing is staged).</summary>
    internal async Task CommitMessageWithAiAsync(string folder)
    {
        var diff = await TaskRunner.CaptureAsync("git diff --staged", folder);
        if (string.IsNullOrWhiteSpace(diff)) diff = await TaskRunner.CaptureAsync("git diff", folder);
        if (string.IsNullOrWhiteSpace(diff))
        {
            ShowToast("No changes to describe", "IconBranch");
            return;
        }
        if (diff.Length > 60_000) diff = diff[..60_000] + "\n… (diff truncated for the AI)";
        var template = _settings.Prompts.FirstOrDefault(p => p.Name == "Commit message")?.Text
                       ?? "Write a concise conventional commit message for this diff:\n\n{{clipboard}}";
        OpenAi(question: AiService.Expand(template, "```diff\n" + diff + "\n```"));
    }
}
