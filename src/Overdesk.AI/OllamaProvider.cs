using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Overdesk.AI;

/// <summary>Local models through Ollama's HTTP API (default http://localhost:11434). Streams NDJSON.</summary>
public sealed class OllamaProvider : IChatProvider
{
    private readonly HttpClient _http;

    public OllamaProvider(string baseUrl = "http://localhost:11434")
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string BaseUrl { get; }
    public string Id => "ollama";
    public string Name => "Ollama";
    public bool IsLocal => true;

    public async Task<string?> CheckAsync(CancellationToken cancellation)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await _http.GetAsync(BaseUrl + "/api/version", cts.Token);
            return response.IsSuccessStatusCode ? null : "Ollama answered with an error.";
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            return "Ollama is not running. Start it (ollama serve) or install it from ollama.com.";
        }
    }

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellation)
    {
        var json = await _http.GetFromJsonAsync<JsonObject>(BaseUrl + "/api/tags", cancellation);
        return (json?["models"]?.AsArray() ?? new JsonArray())
            .Select(m => new AiModel(
                m!["name"]!.GetValue<string>(),
                m["name"]!.GetValue<string>(),
                m["details"]?["parameter_size"]?.GetValue<string>()))
            .ToList();
    }

    /// <summary>Models worth suggesting to someone who has none yet: (name, size to download, what it is good at).</summary>
    public static readonly (string Name, string Size, string About)[] Suggested =
    {
        ("llama3.2", "2.0 GB", "Meta · small and quick, good all-rounder"),
        ("gemma3:4b", "3.3 GB", "Google · strong for its size, sees images"),
        ("qwen2.5-coder:7b", "4.7 GB", "Alibaba · made for code"),
        ("deepseek-r1:8b", "5.2 GB", "DeepSeek · thinks step by step"),
        ("mistral", "4.1 GB", "Mistral · solid general model"),
    };

    /// <summary>Download a model, reporting (status text, 0..1 progress when known).</summary>
    public async Task PullAsync(string model, IProgress<(string Status, double? Fraction)> progress, CancellationToken cancellation)
    {
        var body = new JsonObject { ["model"] = model, ["stream"] = true };
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/pull")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation));
        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            if (line.Length == 0) continue;
            var node = JsonNode.Parse(line);
            if (node?["error"]?.GetValue<string>() is { } error) throw new InvalidOperationException(error);
            var status = node?["status"]?.GetValue<string>() ?? "";
            var total = node?["total"]?.GetValue<long>() ?? 0;
            var done = node?["completed"]?.GetValue<long>() ?? 0;
            progress.Report((status, total > 0 ? (double)done / total : null));
        }
    }

    /// <summary>Models currently loaded in memory, with how much of each sits in GPU memory.</summary>
    public async Task<IReadOnlyList<(string Name, long Size, long VramSize)>> RunningAsync(CancellationToken cancellation)
    {
        var json = await _http.GetFromJsonAsync<JsonObject>(BaseUrl + "/api/ps", cancellation);
        return (json?["models"]?.AsArray() ?? new JsonArray())
            .Select(m => (m!["name"]!.GetValue<string>(), m["size"]?.GetValue<long>() ?? 0, m["size_vram"]?.GetValue<long>() ?? 0))
            .ToList();
    }

    private const int MaxRounds = 30;

    public async IAsyncEnumerable<ChatChunk> StreamAsync(string model, string? system, IReadOnlyList<ChatMessage> messages, AiContext? context,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var list = new JsonArray();
        if (!string.IsNullOrWhiteSpace(system)) list.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        foreach (var m in messages)
        {
            var item = new JsonObject { ["role"] = m.Role, ["content"] = m.Text };
            if (m.Images is { Count: > 0 } images)
                item["images"] = new JsonArray(images.Select(i => (JsonNode)Convert.ToBase64String(i)).ToArray());
            list.Add(item);
        }
        var tools = context is null ? null : new JsonArray(context.Tools.Tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Schema.DeepClone() },
        }).ToArray());

        for (var round = 0; round < MaxRounds; round++)
        {
            var body = new JsonObject { ["model"] = model, ["messages"] = list.DeepClone(), ["stream"] = true };
            if (tools is { Count: > 0 }) body["tools"] = tools.DeepClone();

            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/chat")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellation);
                // "does not support tools": many small models cannot call tools; they still chat.
                if (tools is not null && error.Contains("tools", StringComparison.OrdinalIgnoreCase))
                {
                    tools = null;
                    round--;
                    continue;
                }
                throw new InvalidOperationException($"Ollama: {(int)response.StatusCode} {error}");
            }

            var text = new StringBuilder();
            var calls = new List<JsonObject>();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation));
            while (await reader.ReadLineAsync(cancellation) is { } line)
            {
                if (line.Length == 0) continue;
                var node = JsonNode.Parse(line);
                if (node?["error"]?.GetValue<string>() is { } error) throw new InvalidOperationException("Ollama: " + error);
                foreach (var call in node?["message"]?["tool_calls"]?.AsArray() ?? new JsonArray())
                    if (call is JsonObject c) calls.Add((JsonObject)c.DeepClone());
                var piece = node?["message"]?["content"]?.GetValue<string>() ?? "";
                text.Append(piece);
                double? speed = null;
                if (node?["done"]?.GetValue<bool>() == true
                    && node["eval_count"]?.GetValue<long>() is > 0 and var count
                    && node["eval_duration"]?.GetValue<long>() is > 0 and var nanos)
                    speed = count / (nanos / 1e9);
                if (piece.Length > 0 || speed is not null) yield return new ChatChunk(piece, speed);
            }
            if (calls.Count == 0 || context is null) break;

            list.Add(new JsonObject { ["role"] = ChatMessage.Assistant, ["content"] = text.ToString(), ["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)c.DeepClone()).ToArray()) });
            foreach (var call in calls)
            {
                var name = call["function"]?["name"]?.GetValue<string>() ?? "";
                var args = call["function"]?["arguments"] switch
                {
                    JsonObject o => (JsonObject)o.DeepClone(),
                    JsonValue v when v.TryGetValue<string>(out var json) => OpenAiCompatibleProvider.ParseArgs(json),
                    _ => new JsonObject(),
                };
                var result = args is null ? AiToolResult.Error("The arguments were not valid JSON.") : await context.Tools.RunAsync(name, args, cancellation);
                var answer = new JsonObject { ["role"] = "tool", ["tool_name"] = name, ["content"] = (result.IsError ? "Error: " : "") + result.Text };
                if (result.Image is { } png) answer["images"] = new JsonArray(Convert.ToBase64String(png));
                list.Add(answer);
            }
            if (text.Length > 0) yield return new ChatChunk("\n\n");
        }
    }
}
