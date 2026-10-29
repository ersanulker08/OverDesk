using System.Text;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using Overdesk.AI;
using Overdesk.Core.Platform;
using Overdesk.Core.Settings;

namespace Overdesk.App.Services;

/// <summary>One message in the AI panel, with its live text while it streams.</summary>
internal sealed class AiTurn
{
    public required string Role { get; init; }
    public string Text { get; set; } = "";
    public string? Error { get; set; }

    /// <summary>What the AI did while answering ("Created “Groceries”", "Editing src/App.cs"), in order.</summary>
    public List<string> Actions { get; set; } = new();

    [JsonIgnore] public List<byte[]> Images { get; } = new();
    [JsonIgnore] public bool IsStreaming { get; set; }

    /// <summary>An action waiting for the user's yes or no.</summary>
    [JsonIgnore] public AiApproval? Pending { get; set; }
}

/// <summary>A question the AI asked before doing something hard to undo (shown as a card in its answer).</summary>
internal sealed class AiApproval
{
    public required string Text { get; init; }
    public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Where a provider comes from, which is how the model picker groups them.</summary>
internal enum AiSourceKind { App, Local, Cloud }

/// <summary>
/// The AI panel's brain: the providers (installed AI apps such as Claude Code, local Ollama / LM Studio, cloud APIs),
/// the chosen model, the conversations (saved to disk) and streaming. Keys come from the OS keychain.
/// </summary>
internal sealed class AiService
{
    private sealed class KeySource(ISecretStore secrets) : IApiKeySource
    {
        public string? Get(string name) => secrets.Get("ai." + name);
    }

    private readonly AppSettings _settings;
    private readonly ISecretStore _secrets;
    private readonly ChatStore _store;
    private readonly Dictionary<string, IReadOnlyList<AiModel>> _models = new();
    private CancellationTokenSource? _running;
    private McpServer? _mcp;
    private AiToolbox? _activeTools;
    private (string Folder, DateTime When, string Text)? _overview;

    public AiService(AppSettings settings, ISecretStore secrets, string dataFolder)
    {
        _settings = settings;
        _secrets = secrets;
        _store = new ChatStore(Path.Combine(dataFolder, "chats"));
        var workspace = Path.Combine(dataFolder, "ai-workspace");
        var keys = new KeySource(secrets);
        Ollama = new OllamaProvider(settings.OllamaUrl);
        Providers = new IChatProvider[]
        {
            new ClaudeCodeProvider(workspace),
            new GeminiCliProvider(workspace),
            Ollama,
            new OpenAiCompatibleProvider("lmstudio", "LM Studio", settings.LmStudioUrl, isLocal: true),
            new ClaudeProvider(keys),
            new OpenAiCompatibleProvider("openai", "OpenAI", "https://api.openai.com/v1", isLocal: false, keys, "openai"),
            new GeminiProvider(keys),
        };
        Threads = _store.LoadAll();
        Memory = new AiMemory(Path.Combine(dataFolder, "ai-memory"));
        Current = new ChatThread();
    }

    public OllamaProvider Ollama { get; }

    /// <summary>Notes the AI keeps between chats (about the user, and per project folder).</summary>
    public AiMemory Memory { get; }
    public IReadOnlyList<IChatProvider> Providers { get; }

    /// <summary>Saved conversations, newest first (the current one only once it has a message).</summary>
    public List<ChatThread> Threads { get; }

    public ChatThread Current { get; private set; }
    public List<AiTurn> Conversation => Current.Turns;

    public IChatProvider Provider => Providers.FirstOrDefault(p => p.Id == _settings.AiProvider) ?? Providers[0];
    public string? Model => _settings.AiModel;
    public double? LastTokensPerSecond { get; private set; }
    public bool IsBusy => _running is not null;

    /// <summary>Raised on the UI thread whenever the conversation or status changes.</summary>
    public event Action? Changed;

    /// <summary>OverDesk's own tools (works, tabs, programs...), supplied by the main window.</summary>
    public Func<IReadOnlyList<AiTool>>? AppTools { get; set; }

    /// <summary>A line about what is on stage, included with every message while tools are on.</summary>
    public Func<string>? StageInfo { get; set; }

    /// <summary>The project folder chats work in (chosen at the bottom of the panel), or null.</summary>
    public string? Folder => _settings.AiFolder is { } f && Directory.Exists(f) ? f : null;

    public IReadOnlyList<string> RecentFolders => _settings.AiRecentFolders;

    /// <summary>Raised when the user picks a project folder, or none (the window remembers it as a project and saves).</summary>
    public event Action<string?>? FolderChanged;

    public void SetFolder(string? folder)
    {
        folder = folder?.TrimEnd('/', '\\');
        _settings.AiFolder = folder;
        if (folder is not null)
        {
            _settings.AiRecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            _settings.AiRecentFolders.Insert(0, folder);
            if (_settings.AiRecentFolders.Count > 8) _settings.AiRecentFolders.RemoveRange(8, _settings.AiRecentFolders.Count - 8);
        }
        FolderChanged?.Invoke(folder);
        Changed?.Invoke();
    }

    public static AiSourceKind KindOf(IChatProvider provider)
        => provider is ISessionChatProvider or GeminiCliProvider ? AiSourceKind.App : provider.IsLocal ? AiSourceKind.Local : AiSourceKind.Cloud;

    public string? GetKey(string provider) => _secrets.Get("ai." + provider);
    public void SetKey(string provider, string? key) => _secrets.Set("ai." + provider, key);

    /// <summary>
    /// First run: start with the best thing that works without setup. Claude Code (the user's own Claude account)
    /// beats a local server that has no model yet.
    /// </summary>
    public async Task PickStartingProviderAsync()
    {
        if (_settings.AiModel is not null) return;
        foreach (var id in new[] { "claude-code", "ollama", "gemini-cli" })
        {
            var provider = Providers.First(p => p.Id == id);
            if (await provider.CheckAsync(CancellationToken.None) is not null) continue;
            var models = await ModelsAsync(provider, CancellationToken.None);
            if (models.Count == 0) continue;
            _settings.AiProvider = id;
            _settings.AiModel = models[0].Id;
            Dispatcher.UIThread.Post(() => Changed?.Invoke());
            return;
        }
    }

    public void Choose(IChatProvider provider, string? model)
    {
        _settings.AiProvider = provider.Id;
        _settings.AiModel = model;
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<AiModel>> ModelsAsync(IChatProvider provider, CancellationToken cancellation)
    {
        if (_models.TryGetValue(provider.Id, out var cached) && cached.Count > 0) return cached;
        if (await provider.CheckAsync(cancellation) is not null) return Array.Empty<AiModel>();
        try
        {
            var models = await provider.ListModelsAsync(cancellation);
            _models[provider.Id] = models;
            return models;
        }
        catch (Exception)
        {
            return Array.Empty<AiModel>();
        }
    }

    public void ForgetModels(string providerId) => _models.Remove(providerId);

    // ---------------------------------------------------------------- conversations

    public void NewChat()
    {
        Stop();
        Current = new ChatThread();
        Changed?.Invoke();
    }

    public void Open(ChatThread thread)
    {
        Stop();
        Current = thread;
        if (thread.Folder is { } folder && Directory.Exists(folder)) _settings.AiFolder = folder;
        // Continue with the model the conversation used, when it still exists.
        if (thread.ProviderId is { } id && Providers.Any(p => p.Id == id))
        {
            _settings.AiProvider = id;
            _settings.AiModel = thread.Model;
        }
        Changed?.Invoke();
    }

    public void Delete(ChatThread thread)
    {
        Threads.Remove(thread);
        _store.Delete(thread);
        if (Current == thread) Current = new ChatThread();
        Changed?.Invoke();
    }

    public void Stop()
    {
        _running?.Cancel();
        _running = null;
    }

    /// <summary>Send a user message and stream the answer into the current conversation.</summary>
    public async Task SendAsync(string text, IReadOnlyList<byte[]>? images = null)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(text)) return;
        var thread = Current;
        var user = new AiTurn { Role = ChatMessage.User, Text = text.Trim() };
        if (images is not null) user.Images.AddRange(images);
        var answer = new AiTurn { Role = ChatMessage.Assistant, IsStreaming = true };
        thread.Turns.Add(user);
        thread.Turns.Add(answer);
        if (thread.Turns.Count == 2) thread.Title = Shorten(user.Text);
        if (!Threads.Contains(thread)) Threads.Insert(0, thread);
        var cts = _running = new CancellationTokenSource();
        Changed?.Invoke();

        var provider = Provider;
        try
        {
            if (await provider.CheckAsync(cts.Token) is { } problem) throw new InvalidOperationException(problem);
            var model = Model;
            if (string.IsNullOrEmpty(model))
            {
                var models = await ModelsAsync(provider, cts.Token);
                model = provider is ClaudeProvider ? ClaudeProvider.DefaultModel : models.FirstOrDefault()?.Id
                        ?? throw new InvalidOperationException(provider == Ollama
                            ? "Ollama has no models yet: pick one under “Get a model” in the model menu."
                            : $"{provider.Name} has no models available.");
                _settings.AiModel = model;
            }
            thread.ProviderId = provider.Id;
            thread.Model = model;
            var folder = Folder;
            thread.Folder = folder;

            var context = BuildContext(provider, folder, thread, answer, cts.Token);
            var system = await SystemPromptAsync(context, cts.Token);
            var stream = Stream(provider, model, system, context, thread, answer, cts.Token);
            var lastUpdate = DateTime.MinValue;
            await foreach (var chunk in stream.ConfigureAwait(false))
            {
                answer.Text += chunk.Text;
                if (chunk.TokensPerSecond is { } speed) LastTokensPerSecond = speed;
                if (chunk.SessionId is { Length: > 0 } session)
                {
                    thread.SessionId = session;
                    thread.SessionProvider = provider.Id;
                    thread.SessionFolder = folder;
                }
                if (chunk.Activity is { } activity) Dispatcher.UIThread.Post(() => answer.Actions.Add(activity));
                // Repaint at most ~20 times a second, however fast tokens arrive.
                if (DateTime.UtcNow - lastUpdate > TimeSpan.FromMilliseconds(50))
                {
                    lastUpdate = DateTime.UtcNow;
                    Dispatcher.UIThread.Post(() => Changed?.Invoke());
                }
            }
        }
        catch (OperationCanceledException)
        {
            answer.Text += answer.Text.Length > 0 ? "\n\n_(stopped)_" : "_(stopped)_";
        }
        catch (Exception ex)
        {
            answer.Error = ex.Message;
        }
        finally
        {
            answer.IsStreaming = false;
            thread.Updated = DateTime.Now;
            if (ReferenceEquals(_running, cts)) _running = null;
            _activeTools = null;
            // Posted, so it runs after the tool lines that are still queued for the UI thread.
            Dispatcher.UIThread.Post(() =>
            {
                _store.Save(thread);
                Changed?.Invoke();
            });
        }
    }

    /// <summary>
    /// Ask the last question again: the last answer (and its question) leave the conversation and the question is sent
    /// anew. A provider session is not resumed, since it already holds the old answer; the history is folded in instead.
    /// </summary>
    public async Task RetryLastAsync()
    {
        if (IsBusy) return;
        var turns = Current.Turns;
        var questionIndex = turns.FindLastIndex(t => t.Role == ChatMessage.User);
        if (questionIndex < 0) return;
        var question = turns[questionIndex];
        turns.RemoveRange(questionIndex, turns.Count - questionIndex);
        Current.SessionId = null;
        Changed?.Invoke();
        await SendAsync(question.Text, question.Images.Count > 0 ? question.Images.ToList() : null);
    }

    /// <summary>
    /// Session providers (Claude Code) get only the new message and continue their own session. When the conversation
    /// started elsewhere, the earlier turns are folded into that first message so nothing is lost.
    /// </summary>
    private IAsyncEnumerable<ChatChunk> Stream(IChatProvider provider, string model, string system, AiContext? context, ChatThread thread, AiTurn answer,
        CancellationToken cancellation)
    {
        var history = thread.Turns.Where(t => t != answer && t.Error is null)
            .Select(t => new ChatMessage(t.Role, WithActions(t), t.Images.Count > 0 ? t.Images : null)).ToList();
        if (provider is not ISessionChatProvider session) return provider.StreamAsync(model, system, history, context, cancellation);

        // Claude Code keeps sessions per folder: a session started in another folder cannot be resumed from this one.
        var resume = thread.SessionProvider == provider.Id && thread.SessionFolder == context?.Folder ? thread.SessionId : null;
        var last = history[^1];
        if (resume is null && history.Count > 1)
        {
            var text = new StringBuilder("Conversation so far:\n");
            foreach (var m in history.Take(history.Count - 1)) text.AppendLine($"{(m.Role == ChatMessage.User ? "User" : "Assistant")}: {m.Text}");
            text.AppendLine().Append("New message: ").Append(last.Text);
            last = last with { Text = text.ToString() };
        }
        return session.StreamTurnAsync(model, system, last, resume, context, cancellation);
    }

    /// <summary>Earlier answers carry a note of what was done, so the model remembers it without the tool calls.</summary>
    private static string WithActions(AiTurn turn)
        => turn.Actions.Count == 0 ? turn.Text : turn.Text + "\n\n(Actions taken: " + string.Join("; ", turn.Actions) + ")";

    // ---------------------------------------------------------------- tools & project

    /// <summary>Every tool the AI could get right now (for the list in Settings), whether switched off or not.</summary>
    public IReadOnlyList<AiTool> AllTools()
    {
        var tools = new List<AiTool>();
        if (AppTools is not null) tools.AddRange(AppTools());
        tools.Add(Memory.Tool(() => Folder));
        // File tools are described even without a folder, so they can be switched off ahead of time.
        tools.AddRange(ProjectTools.Create(Folder ?? Path.GetTempPath(), allowEdits: true));
        return tools.DistinctBy(t => t.Name).ToList();
    }

    /// <summary>
    /// The tools of this turn: OverDesk's own (when allowed) plus the project folder's file tools. Every call shows up
    /// in the answer as a line. Installed AI apps reach them over the local MCP server.
    /// </summary>
    private AiContext? BuildContext(IChatProvider provider, string? folder, ChatThread thread, AiTurn answer, CancellationToken stop)
    {
        var tools = new List<AiTool>();
        if (_settings.AiTools && AppTools is not null)
        {
            tools.AddRange(AppTools());
            tools.Add(Memory.Tool(() => folder));
        }
        if (folder is not null) tools.AddRange(ProjectTools.Create(folder, _settings.AiAllowEdits));
        tools.RemoveAll(t => _settings.AiDisabledTools.Contains(t.Name));
        if (tools.Count == 0 && folder is null) return null;

        var box = new AiToolbox(tools);
        box.Activity += line => Dispatcher.UIThread.Post(() =>
        {
            answer.Actions.Add(line);
            Changed?.Invoke();
        });
        box.Failed += line => Dispatcher.UIThread.Post(() => answer.Actions.Add("⚠ " + line));
        box.Approve = (question, cancellation) => AskAsync(thread, answer, question, cancellation, stop);
        _activeTools = box;

        McpEndpoint? mcp = null;
        if (tools.Count > 0 && provider is ClaudeCodeProvider or GeminiCliProvider)
        {
            try
            {
                _mcp ??= new McpServer(() => _activeTools);
                _mcp.Start();
                // Claude Code has its own file tools (and runs inside the folder); Gemini CLI uses OverDesk's.
                mcp = _mcp.Endpoint(withFileTools: provider is GeminiCliProvider);
            }
            catch (Exception) { /* no local port: the app still chats, without OverDesk's tools */ }
        }
        return new AiContext(box, folder, _settings.AiAllowEdits, mcp);
    }

    /// <summary>
    /// Show the question in the answer and wait for Allow / Deny. Stopping the chat counts as a no. Calls over MCP
    /// carry the server's token, so the chat's own stop token is watched too.
    /// </summary>
    private async Task<bool> AskAsync(ChatThread thread, AiTurn answer, string question, CancellationToken cancellation, CancellationToken stop)
    {
        if (thread.AllowAll)
        {
            Dispatcher.UIThread.Post(() => answer.Actions.Add("Allowed for this chat: " + question));
            return true;
        }
        var approval = new AiApproval { Text = question };
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            answer.Pending = approval;
            Changed?.Invoke();
        });
        await using var a = cancellation.Register(() => approval.Answer.TrySetResult(false));
        await using var b = stop.Register(() => approval.Answer.TrySetResult(false));
        var allowed = await approval.Answer.Task;
        Dispatcher.UIThread.Post(() =>
        {
            if (answer.Pending == approval) answer.Pending = null;
            Changed?.Invoke();
        });
        return allowed;
    }

    /// <summary>The user's answer to an approval card. "Always" allows every further action in this conversation.</summary>
    public void Answer(AiApproval approval, bool allow, bool always = false)
    {
        if (always) Current.AllowAll = true;
        approval.Answer.TrySetResult(allow);
    }

    private async Task<string> SystemPromptAsync(AiContext? context, CancellationToken cancellation)
    {
        var prompt = new StringBuilder(_settings.AiSystemPrompt);
        var memory = Memory.PromptSection(context?.Folder);
        if (memory.Length > 0) prompt.Append("\n\n").Append(memory);
        if (context is null) return prompt.ToString();
        if (context.Tools.Tools.Any(t => !t.IsFileTool))
            prompt.Append("\n\nYou are part of OverDesk and can act in it with your tools: the user's works (notes, to-do lists, stories, ")
                  .Append("game variable sheets), program and web tabs, installed programs, panel tools, the clipboard and a screenshot of the ")
                  .Append("program on stage. When the user asks for something in OverDesk, do it with the tools instead of explaining how. ")
                  .Append("Read a work before changing it. Delete or close things only when the user asked. Afterwards, say briefly what you did.");
        if (_settings.AiTools && StageInfo is not null)
        {
            try { prompt.Append("\n\n").Append(Dispatcher.UIThread.CheckAccess() ? StageInfo() : await Dispatcher.UIThread.InvokeAsync(StageInfo)); }
            catch (Exception) { /* the window is closing */ }
        }
        if (context.Folder is { } folder)
        {
            prompt.Append("\n\nThe user chose a project folder for this chat: answer about this project. Before answering about specific ")
                  .Append("code, look at the real files with your tools. ")
                  .Append(context.AllowEdits
                      ? "You may create and change files in this folder when the user asks for it: make focused changes, then use " +
                        "open_in_editor (when available) to show the changed file."
                      : "You can read its files but not change them (file changes are switched off in OverDesk's settings): show changes as code blocks.")
                  .Append(context.Tools.Tools.Any(t => t.Name == "run_command")
                      ? " Run shell commands only with run_command (the user approves each one; other shell tools are off): use it to build, " +
                        "test and lint, and after changing code check that it still builds."
                      : "")
                  .Append("\n\n").Append(await OverviewAsync(folder, cancellation));
        }
        return prompt.ToString();
    }

    /// <summary>The folder overview, refreshed at most once a minute.</summary>
    private async Task<string> OverviewAsync(string folder, CancellationToken cancellation)
    {
        if (_overview is { } cached && cached.Folder == folder && DateTime.UtcNow - cached.When < TimeSpan.FromMinutes(1)) return cached.Text;
        var text = await Task.Run(() => ProjectTools.Overview(folder), cancellation);
        _overview = (folder, DateTime.UtcNow, text);
        return text;
    }

    private static string Shorten(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 48 ? line[..47] + "…" : line;
    }

    /// <summary>Development snapshots: a finished exchange that used tools, without calling any model.</summary>
    internal void DevShowDemo(string folder)
    {
        _settings.AiFolder = folder;
        Current = new ChatThread { Title = "Plan the release", Folder = folder };
        Current.Turns.Add(new AiTurn { Role = ChatMessage.User, Text = "Add the release steps to my to-do list and fix the typo in README" });
        Current.Turns.Add(new AiTurn
        {
            Role = ChatMessage.Assistant,
            Actions = { "Reading “Release to-dos”", "Adding to “Release to-dos”", "Reading README.md", "Editing README.md", "Opening README.md in the editor" },
            Text = "Done. Here is what changed:\n\n## Release to-dos\n1. **Tag** the version (`v0.2.0`)\n2. Build with `scripts/package-macos.sh`\n3. Write the _release notes_\n4. Upload to [GitHub releases](https://github.com)\n\n> README.md: fixed ~~recieve~~ → receive, now open in VS Code.\n\n| File | Change |\n|---|---|\n| README.md | 1 line |\n| Release to-dos | +4 rows |\n\n```bash\ngit tag v0.2.0\n```",
        });
        Current.Turns.Add(new AiTurn { Role = ChatMessage.User, Text = "Now run the tests" });
        Current.Turns.Add(new AiTurn
        {
            Role = ChatMessage.Assistant, Actions = { "Reading package.json" },
            Pending = new AiApproval { Text = "Run `npm test` in OverDesk?" },
        });
        Changed?.Invoke();
    }

    /// <summary>Fill a saved prompt's variables ("{{clipboard}}").</summary>
    public static string Expand(string template, string? clipboard)
        => template.Replace("{{clipboard}}", clipboard ?? "", StringComparison.OrdinalIgnoreCase);
}
