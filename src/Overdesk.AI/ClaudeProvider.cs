using System.Diagnostics;
using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Messages;

namespace Overdesk.AI;

/// <summary>Claude through the official Anthropic SDK (API key from the keychain or ANTHROPIC_API_KEY).</summary>
public sealed class ClaudeProvider : IChatProvider
{
    /// <summary>Shown first in the model picker; the current Opus is the default.</summary>
    public const string DefaultModel = "claude-opus-5-5";

    private static readonly AiModel[] KnownModels =
    {
        new("claude-opus-5-5", "Claude Opus 5.5", "most capable everyday model"),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", "fast and capable"),
        new("claude-haiku-5-5", "Claude Haiku 5.5", "fastest, cheapest"),
        new("claude-fable-5-1", "Claude Fable 5.1", "deepest reasoning"),
    };

    private readonly IApiKeySource _keys;

    public ClaudeProvider(IApiKeySource keys) => _keys = keys;

    public string Id => "claude";
    public string Name => "Claude";
    public bool IsLocal => false;

    private string? Key => _keys.Get("anthropic") is { Length: > 0 } k ? k : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

    private AnthropicClient Client() => new() { ApiKey = Key };

    public Task<string?> CheckAsync(CancellationToken cancellation)
        => Task.FromResult(string.IsNullOrEmpty(Key) ? "Add your Anthropic API key in OverDesk settings → AI." : null);

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellation)
    {
        try
        {
            var page = await Client().Models.List();
            var live = page.Items.Select(m => new AiModel(m.ID, m.DisplayName)).ToList();
            // Keep the curated order first, then anything newer the API reports.
            return KnownModels.Where(k => live.Any(l => l.Id == k.Id))
                .Concat(live.Where(l => KnownModels.All(k => k.Id != l.Id)))
                .ToList() is { Count: > 0 } list ? list : KnownModels;
        }
        catch (Exception)
        {
            return KnownModels;
        }
    }

    public async IAsyncEnumerable<ChatChunk> StreamAsync(string model, string? system, IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var history = messages.Select(ToParam).ToList();

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 64000,
            Messages = history,
            // A side-panel chat: answer quickly, still think when the question needs it.
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Effort = Effort.Medium },
        };

        if (!string.IsNullOrWhiteSpace(system)) parameters = parameters with { System = system };

        var clock = Stopwatch.StartNew();
        long outputTokens = 0;
        await foreach (var evt in Client().Messages.CreateStreaming(parameters, cancellation))
        {
            cancellation.ThrowIfCancellationRequested();
            if (evt.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                yield return new ChatChunk(text.Text);
            else if (evt.TryPickDelta(out var messageDelta))
            {
                outputTokens = messageDelta.Usage.OutputTokens;
                if (messageDelta.Delta.StopReason is { } reason && reason == StopReason.Refusal)
                    yield return new ChatChunk("\n\n_(Claude declined to answer this request.)_");
            }
        }
        if (outputTokens > 0 && clock.Elapsed.TotalSeconds > 0) yield return new ChatChunk("", outputTokens / clock.Elapsed.TotalSeconds);
    }

    /// <summary>MessageParam is a union of user / assistant / system turns in this SDK version.</summary>
    private static MessageParam ToParam(ChatMessage m)
    {
        if (m.Role == ChatMessage.Assistant) return new AssistantMessageParam { Content = m.Text };
        return m.Images is { Count: > 0 } ? new UserMessageParam { Content = Blocks(m) } : new UserMessageParam { Content = m.Text };
    }

    /// <summary>Images first, then the question (the order Claude reads best).</summary>
    private static List<ContentBlockParam> Blocks(ChatMessage m)
    {
        var blocks = new List<ContentBlockParam>();
        foreach (var image in m.Images!)
            blocks.Add(new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(image), MediaType = MediaType.ImagePng } });
        blocks.Add(new TextBlockParam { Text = m.Text });
        return blocks;
    }
}
