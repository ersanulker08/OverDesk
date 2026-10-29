namespace Overdesk.Core.Settings;

public sealed class UserTool
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>A named set of program tabs ("Work", "Games"...). Switching workspaces swaps the whole tab row.</summary>
public sealed class Workspace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Main";

    /// <summary>Accent theme name whose color marks this workspace.</summary>
    public string Color { get; set; } = "Crimson";

    public List<string> Tabs { get; set; } = new();

    /// <summary>Index into <see cref="Tabs"/> of the active tab; -1 means home.</summary>
    public int ActiveTab { get; set; } = -1;
}

/// <summary>One entry of the web strip: a known service, or a custom site (name + url).</summary>
public sealed class WebPin
{
    public string? Service { get; set; }
    public string? Name { get; set; }
    public string? Url { get; set; }

    /// <summary>When the service also exists as an installed app, open the website anyway.</summary>
    public bool PreferWebsite { get; set; }
}

/// <summary>A code project: a folder, the commands that start it, and what to open once it runs.</summary>
public sealed class DevProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";

    /// <summary>Shell commands started (in the folder) when the project is launched, e.g. "npm run dev".</summary>
    public List<string> Commands { get; set; } = new();

    /// <summary>Opened in a web tab after launch, e.g. http://localhost:5173.</summary>
    public string? Url { get; set; }

    /// <summary>Open the folder in the code editor on launch.</summary>
    public bool OpenEditor { get; set; } = true;
}

/// <summary>A pinned piece of text (code, command, address) kept for reuse.</summary>
public sealed class Snippet
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>A reusable AI prompt. "{{clipboard}}" is replaced by the clipboard text when it runs.</summary>
public sealed class SavedPrompt
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>Everything Overdesk remembers between runs.</summary>
public sealed class AppSettings
{
    public List<Workspace> Workspaces { get; set; } = new();
    public string? ActiveWorkspace { get; set; }

    /// <summary>Program paths of the open tabs, in order (pre-workspace settings; migrated into the first workspace).</summary>
    public List<string> OpenTabs { get; set; } = new();

    /// <summary>Index into <see cref="OpenTabs"/> of the active tab; -1 means the home page.</summary>
    public int ActiveTab { get; set; }

    public List<UserTool> UserTools { get; set; } = new();

    public bool ToolsPanelOpen { get; set; } = true;
    public double ToolsPanelWidth { get; set; } = 380;

    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;

    /// <summary>Programs pinned to the home page, in order.</summary>
    public List<string> PinnedPrograms { get; set; } = new();

    /// <summary>Recently opened programs, most recent first.</summary>
    public List<string> RecentPrograms { get; set; } = new();

    /// <summary>Name of the accent color theme (see the app's theme list).</summary>
    public string AccentTheme { get; set; } = "Crimson";

    public bool ShowSplash { get; set; } = true;
    public bool AnimatedBackground { get; set; } = true;
    public bool RestoreTabs { get; set; } = true;

    /// <summary>macOS: keep a live image of the program on stage while Overdesk itself is in front.</summary>
    public bool MirrorPrograms { get; set; } = true;

    /// <summary>Left dock expanded (true) or collapsed to its icon rail.</summary>
    public bool SidebarExpanded { get; set; } = true;

    /// <summary>Ids of the system gauges shown in the dock, in display order.</summary>
    public List<string> DockMetrics { get; set; } = new() { "cpu", "gpu", "ram", "net" };

    /// <summary>Services pinned to the web strip, in order: known service ids (see the app's catalog) or custom sites.</summary>
    public List<WebPin> WebPins { get; set; } = new()
    {
        new() { Service = "github" }, new() { Service = "chatgpt" }, new() { Service = "codex" },
        new() { Service = "claude" }, new() { Service = "claude-code" }, new() { Service = "gemini" },
    };

    /// <summary>Show the web strip under the program tabs.</summary>
    public bool ShowWebStrip { get; set; } = true;

    /// <summary>AI panel: provider id (ollama, lmstudio, claude, openai, gemini) and model last used.</summary>
    public string AiProvider { get; set; } = "ollama";
    public string? AiModel { get; set; }
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string LmStudioUrl { get; set; } = "http://localhost:1234/v1";
    public string AiSystemPrompt { get; set; } = "You are a helpful assistant inside OverDesk, a desktop workspace. Be concise. Use Markdown code blocks for code.";

    public List<SavedPrompt> Prompts { get; set; } = new()
    {
        new() { Name = "Summarize", Text = "Summarize this in a few bullet points:\n\n{{clipboard}}" },
        new() { Name = "Explain code", Text = "Explain what this code does, step by step, and point out anything risky:\n\n{{clipboard}}" },
        new() { Name = "Find bugs", Text = "Review this code for bugs and edge cases. List concrete problems with fixes:\n\n{{clipboard}}" },
        new() { Name = "Write tests", Text = "Write unit tests for this code, covering edge cases:\n\n{{clipboard}}" },
        new() { Name = "Translate to English", Text = "Translate to natural English:\n\n{{clipboard}}" },
        new() { Name = "Improve writing", Text = "Fix grammar and make this clearer, keep the meaning and tone:\n\n{{clipboard}}" },
        new() { Name = "Commit message", Text = "Write a concise conventional commit message (subject + short body) for this diff:\n\n{{clipboard}}" },
    };

    public List<DevProject> Projects { get; set; } = new();
    public string? ActiveProject { get; set; }
    public List<Snippet> Snippets { get; set; } = new();

    /// <summary>Dock cards the user switched off (ids: media, system, ai, projects, git, docker, ports, clipboard, notes, focus).</summary>
    public List<string> HiddenDockCards { get; set; } = new();

    /// <summary>Focus timer lengths in minutes.</summary>
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;

    /// <summary>Works: default fonts (names from the font catalog) and text size for works that do not set their own.</summary>
    public string WorkTitleFont { get; set; } = "Playfair Display";
    public string WorkBodyFont { get; set; } = "Lora";
    public double WorkFontSize { get; set; } = 15;

    /// <summary>Text of the dock's scratchpad.</summary>
    public string Notes { get; set; } = "";
}
