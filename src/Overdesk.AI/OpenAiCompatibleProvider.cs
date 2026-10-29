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

    public async IAsyncEnumerable<ChatChunk> StreamAsync(string model, string? system, IReadOnlyList<ChatMessage> messages,
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
            var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = m.Text } };
            foreach (var image in images)
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(image) },
                });
            list.Add(new JsonObject { ["role"] = m.Role, ["content"] = parts });
        }
        var body = new JsonObject { ["model"] = model, ["messages"] = list, ["stream"] = true };

        using var request = Request(HttpMethod.Post, "/chat/completions", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{Name}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellation)}");

        var clock = Stopwatch.StartNew();
        var pieces = 0;
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation));
        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            var text = JsonNode.Parse(data)?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
            if (string.IsNullOrEmpty(text)) continue;
            pieces++;
            yield return new ChatChunk(text);
        }
        // Chunks are roughly one token each: a good enough speed estimate for the status line.
        if (pieces > 0 && clock.Elapsed.TotalSeconds > 0) yield return new ChatChunk("", pieces / clock.Elapsed.TotalSeconds);
    }
}
