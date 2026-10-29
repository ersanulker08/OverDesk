using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Overdesk.AI;

/// <summary>Google Gemini through the Generative Language REST API (needs an API key). Streams server-sent events.</summary>
public sealed class GeminiProvider : IChatProvider
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly IApiKeySource _keys;

    public GeminiProvider(IApiKeySource keys) => _keys = keys;

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

    public async IAsyncEnumerable<ChatChunk> StreamAsync(string model, string? system, IReadOnlyList<ChatMessage> messages,
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
        var body = new JsonObject { ["contents"] = contents };
        if (!string.IsNullOrWhiteSpace(system))
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = system } } };

        using var request = Request(HttpMethod.Post, $"/models/{model}:streamGenerateContent?alt=sse",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellation)}");

        var clock = Stopwatch.StartNew();
        long? tokens = null;
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation));
        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var node = JsonNode.Parse(line[5..]);
            tokens = node?["usageMetadata"]?["candidatesTokenCount"]?.GetValue<long>() ?? tokens;
            var parts = node?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
            var text = string.Concat(parts?.Select(p => p?["text"]?.GetValue<string>() ?? "") ?? Array.Empty<string>());
            if (text.Length > 0) yield return new ChatChunk(text);
        }
        if (tokens is > 0 && clock.Elapsed.TotalSeconds > 0) yield return new ChatChunk("", tokens.Value / clock.Elapsed.TotalSeconds);
    }
}
