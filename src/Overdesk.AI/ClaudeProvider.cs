using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>Tool rounds per answer before Claude is asked to wrap up (a runaway loop guard).</summary>
    private const int MaxRounds = 30;

    public async IAsyncEnumerable<ChatChunk> StreamAsync(string model, string? system, IReadOnlyList<ChatMessage> messages, AiContext? context,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var history = messages.Select(ToParam).ToList();
        var tools = context?.Tools.Tools.Select(ToTool).ToList();

        var clock = Stopwatch.StartNew();
        long outputTokens = 0;
        for (var round = 0; round < MaxRounds; round++)
        {
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
            if (tools is { Count: > 0 }) parameters = parameters with { Tools = tools };

            // The streamed blocks of this round, rebuilt so the turn can be sent back with the tool results.
            var blocks = new SortedDictionary<long, StreamedBlock>();
            string? stopReason = null;
            await foreach (var evt in Client().Messages.CreateStreaming(parameters, cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                if (evt.TryPickContentBlockStart(out var start))
                {
                    var block = new StreamedBlock();
                    if (start.ContentBlock.TryPickToolUse(out var use)) { block.Kind = "tool"; block.Id = use.ID; block.Name = use.Name; }
                    else if (start.ContentBlock.TryPickThinking(out _)) block.Kind = "thinking";
                    else if (start.ContentBlock.TryPickRedactedThinking(out var redacted)) { block.Kind = "redacted"; block.Data = redacted.Data; }
                    else if (start.ContentBlock.TryPickText(out _)) block.Kind = "text";
                    blocks[start.Index] = block;
                }
                else if (evt.TryPickContentBlockDelta(out var delta) && blocks.TryGetValue(delta.Index, out var target))
                {
                    if (delta.Delta.TryPickText(out var text))
                    {
                        target.Text.Append(text.Text);
                        yield return new ChatChunk(text.Text);
                    }
                    else if (delta.Delta.TryPickInputJson(out var json)) target.Text.Append(json.PartialJson);
                    else if (delta.Delta.TryPickThinking(out var thinking)) target.Text.Append(thinking.Thinking);
                    else if (delta.Delta.TryPickSignature(out var signature)) target.Data = signature.Signature;
                }
                else if (evt.TryPickDelta(out var messageDelta))
                {
                    outputTokens += messageDelta.Usage.OutputTokens;
                    stopReason = messageDelta.Delta.StopReason?.Raw();
                }
            }

            if (stopReason == "refusal")
            {
                yield return new ChatChunk("\n\n_(Claude declined to answer this request.)_");
                break;
            }
            var calls = blocks.Values.Where(b => b.Kind == "tool").ToList();
            if (stopReason != "tool_use" || calls.Count == 0 || context is null) break;

            // Echo the assistant turn (thinking blocks unchanged, as the API requires), then answer every call at once.
            var assistant = new List<ContentBlockParam>();
            foreach (var b in blocks.Values)
            {
                switch (b.Kind)
                {
                    case "thinking": assistant.Add(new ThinkingBlockParam { Thinking = b.Text.ToString(), Signature = b.Data ?? "" }); break;
                    case "redacted": assistant.Add(new RedactedThinkingBlockParam { Data = b.Data ?? "" }); break;
                    case "text" when b.Text.Length > 0: assistant.Add(new TextBlockParam { Text = b.Text.ToString() }); break;
                    case "tool": assistant.Add(new ToolUseBlockParam { ID = b.Id!, Name = b.Name!, Input = InputOf(b) }); break;
                }
            }
            history.Add(new AssistantMessageParam { Content = assistant });

            var results = new List<ContentBlockParam>();
            foreach (var call in calls)
            {
                JsonObject? args;
                try { args = call.Text.Length == 0 ? new JsonObject() : JsonNode.Parse(call.Text.ToString()) as JsonObject; }
                catch (JsonException) { args = null; }
                var result = args is null
                    ? AiToolResult.Error("The tool input was not valid JSON: " + call.Text)
                    : await context.Tools.RunAsync(call.Name!, args, cancellation);
                results.Add(ToolResult(call.Id!, result));
            }
            history.Add(new UserMessageParam { Content = results });
            // A visible break between the text before and after the tools.
            if (blocks.Values.Any(b => b.Kind == "text" && b.Text.Length > 0)) yield return new ChatChunk("\n\n");
        }
        if (outputTokens > 0 && clock.Elapsed.TotalSeconds > 0) yield return new ChatChunk("", outputTokens / clock.Elapsed.TotalSeconds);
    }

    private sealed class StreamedBlock
    {
        public string Kind = "";
        public string? Id, Name, Data;
        public readonly StringBuilder Text = new();
    }

    private static IReadOnlyDictionary<string, JsonElement> InputOf(StreamedBlock block)
    {
        try
        {
            if (block.Text.Length > 0 && JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(block.Text.ToString()) is { } input) return input;
        }
        catch (JsonException) { }
        return new Dictionary<string, JsonElement>();
    }

    private static ToolUnion ToTool(AiTool tool) => new Tool
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = InputSchema.FromRawUnchecked(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tool.Schema.ToJsonString())!),
    };

    /// <summary>A tool_result block; images go along as image blocks so Claude can look at them.</summary>
    private static ContentBlockParam ToolResult(string id, AiToolResult result)
    {
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = result.Text.Length > 0 ? result.Text : "(done)" } };
        if (result.Image is { } png)
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = Convert.ToBase64String(png) },
            });
        var raw = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = content, ["is_error"] = result.IsError };
        return ToolResultBlockParam.FromRawUnchecked(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw.ToJsonString())!);
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
