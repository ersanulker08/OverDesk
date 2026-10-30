using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Overdesk.AI;

/// <summary>
/// A tiny Model Context Protocol server (streamable HTTP transport, plain JSON answers) on 127.0.0.1, so installed AI
/// apps that run as separate programs (Claude Code, Gemini CLI) can use OverDesk's tools: works, tabs, the project
/// folder. Every request must carry the random bearer token written into their config; nothing else can call it.
/// Paths: /mcp/all serves every tool, /mcp/app leaves out the file tools (for apps that have their own).
/// HTTP is spoken over a plain socket (one request per connection): HttpListener would need a URL reservation on
/// Windows for users without admin rights.
/// </summary>
public sealed class McpServer : IDisposable
{
    private static readonly string[] Versions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
    private const int MaxBody = 8 * 1024 * 1024;
    private readonly Func<AiToolbox?> _toolbox;
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;

    public McpServer(Func<AiToolbox?> toolbox)
    {
        _toolbox = toolbox;
        Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
    }

    public string Token { get; }
    public int Port { get; private set; }
    public bool IsRunning => _listener is not null;

    public McpEndpoint Endpoint(bool withFileTools) => new($"http://127.0.0.1:{Port}/mcp/{(withFileTools ? "all" : "app")}", Token);

    public void Start()
    {
        if (_listener is not null) return;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested && _listener is { } listener)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, string Body);

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30)); // reading the request; running the tool may take longer
            var request = await ReadRequestAsync(stream, timeout.Token);
            if (request is null) return;
            var (status, body) = await HandleAsync(request);
            await WriteResponseAsync(stream, status, body);
        }
        catch (Exception) { /* a broken connection only affects its own request */ }
    }

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellation)
    {
        var buffer = new List<byte>();
        var chunk = new byte[8192];
        int headerEnd;
        while ((headerEnd = IndexOf(buffer, "\r\n\r\n"u8)) < 0)
        {
            if (buffer.Count > 64 * 1024) return null;
            var read = await stream.ReadAsync(chunk, cancellation);
            if (read == 0) return null;
            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }
        var lines = Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray()).Split("\r\n");
        var start = lines[0].Split(' ');
        if (start.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
            if (line.IndexOf(':') is > 0 and var colon) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();

        var length = headers.TryGetValue("Content-Length", out var value) && int.TryParse(value, out var n) ? n : 0;
        if (length is < 0 or > MaxBody) return null;
        var body = buffer.Skip(headerEnd + 4).ToList();
        while (body.Count < length)
        {
            var read = await stream.ReadAsync(chunk, cancellation);
            if (read == 0) return null;
            body.AddRange(chunk.AsSpan(0, read).ToArray());
        }
        var path = start[1].Split('?')[0];
        return new Request(start[0], path, headers, Encoding.UTF8.GetString(body.Take(length).ToArray()));
    }

    private static int IndexOf(List<byte> data, ReadOnlySpan<byte> pattern)
    {
        for (var i = 0; i <= data.Count - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length && match; j++) match = data[i + j] == pattern[j];
            if (match) return i;
        }
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string? body)
    {
        var reason = status switch { 200 => "OK", 202 => "Accepted", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var bytes = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Length: {bytes.Length}\r\n" +
                   (body is null ? "" : "Content-Type: application/json\r\n") + "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        if (bytes.Length > 0) await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private async Task<(int Status, string? Body)> HandleAsync(Request request)
    {
        // Browsers send an Origin; MCP clients do not. Refusing it blocks web pages from reaching the server.
        if (request.Headers.ContainsKey("Origin") || !request.Headers.TryGetValue("Authorization", out var auth) || auth != "Bearer " + Token)
            return (403, null);
        if (!request.Path.StartsWith("/mcp/", StringComparison.Ordinal)) return (404, null);
        if (request.Method != "POST") return (405, null);
        try
        {
            var message = JsonNode.Parse(request.Body);
            var withFiles = request.Path.EndsWith("/all", StringComparison.Ordinal);
            JsonNode? answer;
            if (message is JsonArray batch)
            {
                var results = new JsonArray();
                foreach (var item in batch)
                    if (item is JsonObject one && await AnswerAsync(one, withFiles) is { } r) results.Add(r);
                answer = results.Count > 0 ? results : null;
            }
            else answer = message is JsonObject single ? await AnswerAsync(single, withFiles) : null;
            return answer is null ? (202, null) : (200, answer.ToJsonString());
        }
        catch (System.Text.Json.JsonException)
        {
            return (400, null);
        }
    }

    /// <summary>Answer one JSON-RPC message; null for notifications.</summary>
    private async Task<JsonObject?> AnswerAsync(JsonObject message, bool withFiles)
    {
        var id = message["id"]?.DeepClone();
        var method = message["method"]?.GetValue<string>();
        if (id is null) return null; // notifications (initialized, cancelled...) need no answer

        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(message["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(withFiles),
                "tools/call" => await CallAsync(message["params"] as JsonObject, withFiles),
                _ => throw new MissingMethodException(method),
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (MissingMethodException)
        {
            return Error(id, -32601, $"Method not found: {method}");
        }
        catch (Exception ex)
        {
            return Error(id, -32603, ex.Message);
        }
    }

    private static JsonObject Error(JsonNode id, int code, string text)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = text } };

    private static JsonObject Initialize(JsonObject? parameters)
    {
        var asked = parameters?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = asked is not null && Versions.Contains(asked) ? asked : Versions[1],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "overdesk", ["version"] = "0.1" },
            ["instructions"] = "Tools of OverDesk, the desktop workspace the user is chatting from: their works (notes, lists, stories, " +
                               "variables), open tabs and programs, and the project folder they picked.",
        };
    }

    private IEnumerable<AiTool> Tools(bool withFiles)
        => (_toolbox()?.Tools ?? Array.Empty<AiTool>()).Where(t => withFiles || !t.IsFileTool);

    private JsonObject ListTools(bool withFiles)
    {
        var list = new JsonArray();
        foreach (var tool in Tools(withFiles))
            list.Add(new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["inputSchema"] = tool.Schema.DeepClone() });
        return new JsonObject { ["tools"] = list };
    }

    private async Task<JsonObject> CallAsync(JsonObject? parameters, bool withFiles)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? "";
        var box = _toolbox();
        AiToolResult result;
        if (box is null || !Tools(withFiles).Any(t => t.Name == name))
            result = AiToolResult.Error("OverDesk has no chat running that may use this tool right now.");
        else
        {
            var args = parameters?["arguments"] as JsonObject ?? new JsonObject();
            result = await box.RunAsync(name, (JsonObject)args.DeepClone(), _stop.Token);
        }

        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = result.Text } };
        if (result.Image is { } png)
            content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = AiImages.MediaType(png) });
        return new JsonObject { ["content"] = content, ["isError"] = result.IsError };
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Stop(); } catch (Exception) { }
    }
}
