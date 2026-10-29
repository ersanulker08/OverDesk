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

    [JsonIgnore] public List<byte[]> Images { get; } = new();
    [JsonIgnore] public bool IsStreaming { get; set; }
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
        Current = new ChatThread();
    }

    public OllamaProvider Ollama { get; }
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

            var stream = Stream(provider, model, thread, answer, cts.Token);
            var lastUpdate = DateTime.MinValue;
            await foreach (var chunk in stream.ConfigureAwait(false))
            {
                answer.Text += chunk.Text;
                if (chunk.TokensPerSecond is { } speed) LastTokensPerSecond = speed;
                if (chunk.SessionId is { Length: > 0 } session)
                {
                    thread.SessionId = session;
                    thread.SessionProvider = provider.Id;
                }
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
            _store.Save(thread);
            Dispatcher.UIThread.Post(() => Changed?.Invoke());
        }
    }

    /// <summary>
    /// Session providers (Claude Code) get only the new message and continue their own session. When the conversation
    /// started elsewhere, the earlier turns are folded into that first message so nothing is lost.
    /// </summary>
    private IAsyncEnumerable<ChatChunk> Stream(IChatProvider provider, string model, ChatThread thread, AiTurn answer, CancellationToken cancellation)
    {
        var history = thread.Turns.Where(t => t != answer && t.Error is null)
            .Select(t => new ChatMessage(t.Role, t.Text, t.Images.Count > 0 ? t.Images : null)).ToList();
        if (provider is not ISessionChatProvider session) return provider.StreamAsync(model, _settings.AiSystemPrompt, history, cancellation);

        var resume = thread.SessionProvider == provider.Id ? thread.SessionId : null;
        var last = history[^1];
        if (resume is null && history.Count > 1)
        {
            var text = new StringBuilder("Conversation so far:\n");
            foreach (var m in history.Take(history.Count - 1)) text.AppendLine($"{(m.Role == ChatMessage.User ? "User" : "Assistant")}: {m.Text}");
            text.AppendLine().Append("New message: ").Append(last.Text);
            last = last with { Text = text.ToString() };
        }
        return session.StreamTurnAsync(model, _settings.AiSystemPrompt, last, resume, cancellation);
    }

    private static string Shorten(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 48 ? line[..47] + "…" : line;
    }

    /// <summary>Fill a saved prompt's variables ("{{clipboard}}").</summary>
    public static string Expand(string template, string? clipboard)
        => template.Replace("{{clipboard}}", clipboard ?? "", StringComparison.OrdinalIgnoreCase);
}
