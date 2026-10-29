namespace Overdesk.AI;

/// <summary>One turn of a conversation. Images are encoded PNG/JPEG bytes (for models that can see).</summary>
public sealed record ChatMessage(string Role, string Text, IReadOnlyList<byte[]>? Images = null)
{
    public const string User = "user";
    public const string Assistant = "assistant";
}

public sealed record AiModel(string Id, string Name, string? Detail = null);

/// <summary>
/// A chunk of streamed output, plus the generation speed once the provider reports it, and (for providers that keep
/// the conversation on their side) the session id to continue it.
/// </summary>
public sealed record ChatChunk(string Text, double? TokensPerSecond = null, string? SessionId = null);

/// <summary>
/// A place that can answer chats: a local server (Ollama, LM Studio) or a cloud API (Claude, OpenAI, Gemini).
/// Every provider streams; the UI shows text as it arrives.
/// </summary>
public interface IChatProvider
{
    string Id { get; }
    string Name { get; }
    bool IsLocal { get; }

    /// <summary>Short reason the provider cannot be used right now (server not running, no API key), or null.</summary>
    Task<string?> CheckAsync(CancellationToken cancellation);

    Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellation);

    IAsyncEnumerable<ChatChunk> StreamAsync(string model, string? system, IReadOnlyList<ChatMessage> messages, CancellationToken cancellation);
}

/// <summary>
/// A provider that keeps the conversation itself (an installed AI app's command-line tool): it receives only the new
/// message plus the session id of the earlier turns.
/// </summary>
public interface ISessionChatProvider : IChatProvider
{
    IAsyncEnumerable<ChatChunk> StreamTurnAsync(string model, string? system, ChatMessage message, string? sessionId, CancellationToken cancellation);
}

/// <summary>Where API keys come from (the OS keychain in the app).</summary>
public interface IApiKeySource
{
    string? Get(string name);
}
