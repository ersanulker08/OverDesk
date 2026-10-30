using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Overdesk.AI;

/// <summary>
/// Chat-completions style servers: OpenAI's API (cloud, needs a key) and LM Studio's local server
/// (http://localhost:1234/v1), which speaks the same format. Streams server-sent events.
/// </summary>
public sealed class OpenAiCompatibleProvider : IChatProvider
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly IApiKeySource? _keys;
    private readonly string? _keyName;

    public OpenAiCompatibleProvider(string id, string name, string baseUrl, bool isLocal, IApiKeySource? keys = null, string? keyName = null)
    {
        Id = id;
        Name = name;
        BaseUrl = baseUrl.TrimEnd('/');
        IsLocal = isLocal;
        _keys = keys;
        _keyName = keyName;
    }

    public string Id { get; }
    public string Name { get; }
    public string BaseUrl { get; }
    public bool IsLocal { get; }

    private string? Key => _keyName is null ? null : _keys?.Get(_keyName);

    private HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path) { Content = content };
        if (Key is { Length: > 0 } key) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    public async Task<string?> CheckAsync(CancellationToken cancellation)
    {
        if (_keyName is not null && string.IsNullOrEmpty(Key)) return $"Add your {Name} API key in OverDesk settings → AI.";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            cts.CancelAfter(TimeSpan.FromSeconds(IsLocal ? 2 : 6));
            using var response = await _http.SendAsync(Request(HttpMethod.Get, "/models"), cts.Token);
            return response.IsSuccessStatusCode ? null : $"{Name} answered {(int)response.StatusCode}.";
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            return IsLocal ? $"{Name} is not running (start its local server)." : $"Could not reach {Name}.";
        }
    }

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellation)
    {
        using var response = await _http.SendAsync(Request(HttpMethod.Get, "/models"), cancellation);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellation);
        var ids = (json?["data"]?.AsArray() ?? new JsonArray()).Select(m => m!["id"]!.GetValue<string>());
        // OpenAI lists every model it has (audio, embeddings...): keep the chat ones.
        if (!IsLocal) ids = ids.Where(id => id.StartsWith("gpt", StringComparison.Ordinal) || id.StartsWith("o", StringComparison.Ordinal))
                               .Where(id => !id.Contains("audio") && !id.Contains("realtime") && !id.Contains("transcribe") && !id.Contains("tts"));
        return ids.OrderBy(i => i).Select(id => new AiModel(id, id)).ToList();
    }

    private const int MaxRounds = 30;

    public async IAsyncEnumerable<ChatChunk> StreamAsync(string model, string? system, IReadOnlyList<ChatMessage> messages, AiContext? context,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var list = new JsonArray();
        if (!string.IsNullOrWhiteSpace(system)) list.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        foreach (var m in messages)
        {
            if (m.Images is not { Count: > 0 } images)
            {
                list.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Text });
                continue;
            }
            list.Add(new JsonObject { ["role"] = m.Role, ["content"] = WithImages(m.Text, images) });
        }
        var tools = context is null ? null : new JsonArray(context.Tools.Tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Schema.DeepClone() },
        }).ToArray());

        var clock = Stopwatch.StartNew();
        var pieces = 0;
        for (var round = 0; round < MaxRounds; round++)
        {
            var body = new JsonObject { ["model"] = model, ["messages"] = list.DeepClone(), ["stream"] = true };
            if (tools is { Count: > 0 }) body["tools"] = tools.DeepClone();

            using var request = Request(HttpMethod.Post, "/chat/completions", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellation);
                // Local models without tool support refuse the whole request: ask again without tools.
                if (tools is not null && (int)response.StatusCode is 400 or 422 && error.Contains("tool", StringComparison.OrdinalIgnoreCase))
                {
                    tools = null;
                    round--;
                    continue;
                }
                throw new InvalidOperationException($"{Name}: {(int)response.StatusCode} {error}");
            }

            var text = new StringBuilder();
            var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation));
            while (await reader.ReadLineAsync(cancellation) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                var delta = JsonNode.Parse(data)?["choices"]?[0]?["delta"];
                foreach (var call in delta?["tool_calls"]?.AsArray() ?? new JsonArray())
                {
                    var index = call?["index"]?.GetValue<int>() ?? calls.Count;
                    var id = call?["id"]?.GetValue<string>();
                    // Some servers number every call 0: a new id at a used index is a new call, not more arguments.
                    if (id is { Length: > 0 } && calls.TryGetValue(index, out var known) && known.Id.Length > 0 && known.Id != id) index = 1000 + calls.Count;
                    if (!calls.TryGetValue(index, out var entry)) entry = calls[index] = ("", "", new StringBuilder());
                    var name = call?["function"]?["name"]?.GetValue<string>();
                    calls[index] = (id ?? entry.Id, name ?? entry.Name, entry.Args.Append(call?["function"]?["arguments"]?.GetValue<string>() ?? ""));
                }
                var piece = delta?["content"]?.GetValue<string>();
                if (string.IsNullOrEmpty(piece)) continue;
                pieces++;
                text.Append(piece);
                yield return new ChatChunk(piece);
            }
            if (calls.Count == 0 || context is null) break;

            // The assistant turn with its calls, then one "tool" message per call.
            var callList = new JsonArray();
            foreach (var (index, call) in calls)
                callList.Add(new JsonObject
                {
                    ["id"] = call.Id.Length > 0 ? call.Id : $"call_{round}_{index}", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Args.Length > 0 ? call.Args.ToString() : "{}" },
                });
            list.Add(new JsonObject { ["role"] = ChatMessage.Assistant, ["content"] = text.Length > 0 ? text.ToString() : null, ["tool_calls"] = callList });
            var images = new List<byte[]>();
            foreach (var call in callList)
            {
                var args = ParseArgs(call!["function"]!["arguments"]!.GetValue<string>());
                var result = args is null ? AiToolResult.Error("The arguments were not valid JSON.")
                    : await context.Tools.RunAsync(call["function"]!["name"]!.GetValue<string>(), args, cancellation);
                if (result.Image is { } png) images.Add(png);
                list.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call["id"]!.GetValue<string>(), ["content"] = (result.IsError ? "Error: " : "") + result.Text });
            }
            // Tool messages carry text only: pictures a tool returned follow as a user message.
            if (images.Count > 0) list.Add(new JsonObject { ["role"] = ChatMessage.User, ["content"] = WithImages("(Images returned by the tools above.)", images) });
            if (text.Length > 0) yield return new ChatChunk("\n\n");
        }
        // Chunks are roughly one token each: a good enough speed estimate for the status line.
        if (pieces > 0 && clock.Elapsed.TotalSeconds > 0) yield return new ChatChunk("", pieces / clock.Elapsed.TotalSeconds);
    }

    private static JsonArray WithImages(string text, IEnumerable<byte[]> images)
    {
        var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
        foreach (var image in images)
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{AiImages.MediaType(image)};base64," + Convert.ToBase64String(image) },
            });
        return parts;
    }

    internal static JsonObject? ParseArgs(string json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
