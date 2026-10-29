using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Overdesk.AI;

/// <summary>Google Gemini through the Generative Language REST API (needs an API key). Streams server-sent events.</summary>
public sealed class GeminiProvider : IChatProvider
{
    private readonly string BaseUrl;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly IApiKeySource _keys;

    /// <param name="baseUrl">Another API address (tests use a local fake); null for Google's.</param>
    public GeminiProvider(IApiKeySource keys, string? baseUrl = null)
    {
        _keys = keys;
        BaseUrl = (baseUrl ?? "https://generativelanguage.googleapis.com/v1beta").TrimEnd('/');
    }

    public string Id => "gemini";
    public string Name => "Gemini";
    public bool IsLocal => false;

    private string? Key => _keys.Get("gemini");

    private HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path) { Content = content };
        request.Headers.Add("x-goog-api-key", Key ?? "");
        return request;
    }

    public async Task<string?> CheckAsync(CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(Key)) return "Add your Gemini API key in OverDesk settings → AI.";
        try
        {
            using var response = await _http.SendAsync(Request(HttpMethod.Get, "/models?pageSize=1"), cancellation);
            return response.IsSuccessStatusCode ? null : $"Gemini answered {(int)response.StatusCode} (check the key).";
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            return "Could not reach Gemini.";
        }
    }

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellation)
    {
        using var response = await _http.SendAsync(Request(HttpMethod.Get, "/models?pageSize=200"), cancellation);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellation);
        return (json?["models"]?.AsArray() ?? new JsonArray())
            .Where(m => m!["supportedGenerationMethods"]?.AsArray().Any(x => x?.GetValue<string>() == "generateContent") == true)
            .Select(m => new AiModel(m!["name"]!.GetValue<string>().Replace("models/", ""), m["displayName"]?.GetValue<string>() ?? m["name"]!.GetValue<string>()))
            .Where(m => m.Id.StartsWith("gemini", StringComparison.Ordinal))
            .ToList();
    }

    private const int MaxRounds = 30;

    public async IAsyncEnumerable<ChatChunk> StreamAsync(string model, string? system, IReadOnlyList<ChatMessage> messages, AiContext? context,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var contents = new JsonArray();
        foreach (var m in messages)
        {
            var parts = new JsonArray { new JsonObject { ["text"] = m.Text } };
            foreach (var image in m.Images ?? Array.Empty<byte[]>())
                parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "image/png", ["data"] = Convert.ToBase64String(image) } });
            contents.Add(new JsonObject { ["role"] = m.Role == ChatMessage.Assistant ? "model" : "user", ["parts"] = parts });
        }
        JsonArray? tools = context is { Tools.Tools.Count: > 0 }
            ? new JsonArray(new JsonObject
            {
                ["functionDeclarations"] = new JsonArray(context.Tools.Tools.Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Schema.DeepClone(),
                }).ToArray()),
            })
            : null;

        var clock = Stopwatch.StartNew();
        long tokens = 0;
        for (var round = 0; round < MaxRounds; round++)
        {
            var body = new JsonObject { ["contents"] = contents.DeepClone() };
            if (!string.IsNullOrWhiteSpace(system))
                body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = system } } };
            if (tools is not null) body["tools"] = tools.DeepClone();

            using var request = Request(HttpMethod.Post, $"/models/{model}:streamGenerateContent?alt=sse",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Gemini: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellation)}");

            // Every part the model sent, kept as-is: function calls carry thought signatures Gemini wants back.
            var modelParts = new JsonArray();
            var calls = new List<JsonObject>();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation));
            while (await reader.ReadLineAsync(cancellation) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var node = JsonNode.Parse(line[5..]);
                tokens = node?["usageMetadata"]?["candidatesTokenCount"]?.GetValue<long>() ?? tokens;
                foreach (var part in node?["candidates"]?[0]?["content"]?["parts"]?.AsArray() ?? new JsonArray())
                {
                    if (part is not JsonObject p) continue;
                    modelParts.Add(p.DeepClone());
                    if (p["functionCall"] is JsonObject call) calls.Add(call);
                    else if (p["text"]?.GetValue<string>() is { Length: > 0 } text && p["thought"]?.GetValue<bool>() != true)
                        yield return new ChatChunk(text);
                }
            }
            if (calls.Count == 0 || context is null) break;

            contents.Add(new JsonObject { ["role"] = "model", ["parts"] = modelParts });
            var answers = new JsonArray();
            foreach (var call in calls)
            {
                var name = call["name"]?.GetValue<string>() ?? "";
                var result = await context.Tools.RunAsync(name, call["args"]?.DeepClone() as JsonObject ?? new JsonObject(), cancellation);
                answers.Add(new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"] = name,
                        ["response"] = new JsonObject { [result.IsError ? "error" : "result"] = result.Text },
                    },
                });
                if (result.Image is { } png)
                    answers.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "image/png", ["data"] = Convert.ToBase64String(png) } });
            }
            contents.Add(new JsonObject { ["role"] = "user", ["parts"] = answers });
            if (modelParts.Any(p => p?["text"] is not null)) yield return new ChatChunk("\n\n");
        }
        if (tokens > 0 && clock.Elapsed.TotalSeconds > 0) yield return new ChatChunk("", tokens / clock.Elapsed.TotalSeconds);
    }
}
