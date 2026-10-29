using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Overdesk.App.Services;
using Overdesk.Core.Plugins;
using Overdesk.Core.Tabs;

namespace Overdesk.App;

/// <summary>
/// AI inside Overdesk: the AI panel is the first built-in tool of the right-hand tools panel (it uses the very same
/// plugin contract as third-party tools), reachable from the tools strip, the command palette and quick actions.
/// </summary>
public partial class MainWindow
{
    private const string AiToolId = "overdesk.ai";
    private readonly AiService _ai;
    private AiPanelView? _aiPanel;

    private sealed class BuiltInTool(Func<object> create) : IToolPlugin
    {
        public object CreateView() => create();
    }

    private void RegisterBuiltInTools()
    {
        _plugins.AddBuiltIn(new PluginManifest
        {
            Id = AiToolId, Name = "AI", Run = PluginRunMode.InProcess, Icon = "IconSparkle",
            Description = "Chat with local and cloud AI models",
            BuiltIn = () => new BuiltInTool(() => AiPanel()),
        });
        _ai.AppTools = BuildAiTools;
        _ai.StageInfo = StageSummary;
        _ai.FolderChanged += OnAiFolderChanged;
    }

    /// <summary>A folder picked for the AI becomes (or selects) one of the user's projects, so the dock knows it too.</summary>
    private void OnAiFolderChanged(string? folder)
    {
        if (folder is not null)
        {
            var project = _settings.Projects.FirstOrDefault(p => string.Equals(p.Folder.TrimEnd('/', '\\'), folder, StringComparison.OrdinalIgnoreCase));
            if (project is null)
            {
                project = DetectProject(folder);
                _settings.Projects.Add(project);
                ShowToast($"{project.Name} is now one of your projects", "IconRocket");
            }
            _settings.ActiveProject = project.Id;
            Dock.RefreshDev();
        }
        SaveSettings();
    }

    private AiPanelView AiPanel()
    {
        if (_aiPanel is not null) return _aiPanel;
        _aiPanel = new AiPanelView { CaptureStage = CaptureStagePngAsync };
        _aiPanel.Initialize(_ai, _settings);
        _aiPanel.Copied += () => ShowToast("Copied to clipboard", "IconCopy");
        _aiPanel.SettingsChanged += SaveSettings;
        _aiPanel.OpenAiSettings += () =>
        {
            OpenSettings();
            Settings.ShowSection("AI");
        };
        return _aiPanel;
    }

    /// <summary>Open the AI panel (and optionally run a prompt or a question in it).</summary>
    internal void OpenAi(string? prompt = null, string? question = null)
    {
        if (_plugins.Tools.FirstOrDefault(t => t.Id == AiToolId) is not { } tool) return;
        if (!_toolPanelOpen || _activeTool != tool) OpenTool(tool);
        var panel = AiPanel();
        if (prompt is not null) _ = panel.RunPromptAsync(prompt);
        else if (question is not null) _ = panel.AskAsync(question);
        else panel.FocusInput();
    }

    /// <summary>⌘J: open the AI panel with the cursor in the message box, or close it when it already has the focus.</summary>
    private void ToggleAi()
    {
        if (_toolPanelOpen && _activeTool?.Id == AiToolId && _aiPanel?.IsKeyboardFocusWithin == true) CloseToolPanel();
        else OpenAi();
    }

    private void AddAiPaletteItems(List<PaletteItem> items)
    {
        items.Add(new PaletteItem("AI: new chat", "AI", () =>
        {
            OpenAi();
            _ai.NewChat();
        }, "IconPlus"));
        items.Add(new PaletteItem("AI: choose a project folder…", "AI", () =>
        {
            OpenAi();
            AiPanel().ChooseFolder();
        }, "IconFolder", Detail: _ai.Folder is { } f ? $"now: {Path.GetFileName(f)}" : "the AI reads and answers about it"));
        foreach (var folder in _ai.RecentFolders.Where(f => f != _ai.Folder && Directory.Exists(f)).Take(5))
            items.Add(new PaletteItem($"AI folder: {Path.GetFileName(folder)}", "AI", () =>
            {
                _ai.SetFolder(folder);
                OpenAi();
            }, "IconFolder", Detail: folder));
        // The newest failed task is usually the one the user wants fixed.
        if (_runner.Tasks.FirstOrDefault(t => !t.IsRunning && t.ExitCode is not 0 and not null) is { } failed)
            items.Add(new PaletteItem($"AI: fix “{failed.Name}”", "AI", () => FixTaskWithAi(failed), "IconSparkle", Detail: $"exited with code {failed.ExitCode}"));
    }

    /// <summary>Point the AI at a failed task: its folder becomes the project folder, the log is one tool call away.</summary>
    internal void FixTaskWithAi(RunningTask task)
    {
        if (Directory.Exists(task.Folder)) _ai.SetFolder(task.Folder);
        OpenAi(question: $"The command `{task.Command}` (task “{task.Name}”) failed with exit code {task.ExitCode}. " +
                         "Read its log with read_tasks, find the cause in the project and fix it, then run the command again to check.");
    }

    /// <summary>A PNG of the program on stage, scaled to a size vision models read well (long edge ≤ 1568 px).</summary>
    private async Task<byte[]?> CaptureStagePngAsync()
    {
        if (_tabs.Active is not { Status: TabStatus.Running, IsInternal: false } tab) return null;
        var key = TabKey(tab);
        return await Task.Run(() =>
        {
            using var frame = _embedding.CaptureFrame(key);
            if (frame is null) return null;
            using var full = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = full.Lock()) frame.CopyTo(fb.Address, fb.RowBytes);
            var scale = Math.Min(1.0, 1568.0 / Math.Max(frame.Width, frame.Height));
            using var scaled = full.CreateScaledBitmap(new PixelSize((int)(frame.Width * scale), (int)(frame.Height * scale)), BitmapInterpolationMode.HighQuality);
            using var ms = new MemoryStream();
            scaled.Save(ms, new PngBitmapEncoderOptions());
            return ms.ToArray();
        });
    }
}
