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
/// </summary>
public sealed class McpServer : IDisposable
{
    private static readonly string[] Versions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
    private readonly HttpListener _listener = new();
    private readonly Func<AiToolbox?> _toolbox;
    private readonly CancellationTokenSource _stop = new();

    public McpServer(Func<AiToolbox?> toolbox)
    {
        _toolbox = toolbox;
        Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
    }

    public string Token { get; }
    public int Port { get; private set; }
    public bool IsRunning => _listener.IsListening;

    public McpEndpoint Endpoint(bool withFileTools) => new($"http://127.0.0.1:{Port}/mcp/{(withFileTools ? "all" : "app")}", Token);

    public void Start()
    {
        if (_listener.IsListening) return;
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            // Browsers send an Origin; MCP clients do not. Refusing it blocks web pages from reaching the server.
            if (request.Headers["Origin"] is { Length: > 0 } || request.Headers["Authorization"] != "Bearer " + Token)
            {
                response.StatusCode = 403;
                return;
            }
            var path = request.Url?.AbsolutePath ?? "";
            if (!path.StartsWith("/mcp/", StringComparison.Ordinal)) { response.StatusCode = 404; return; }
            if (request.HttpMethod != "POST") { response.StatusCode = 405; return; }

            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
            var message = JsonNode.Parse(body);
            var withFiles = path.EndsWith("/all", StringComparison.Ordinal);

            JsonNode? answer;
            if (message is JsonArray batch)
            {
                var results = new JsonArray();
                foreach (var item in batch)
                    if (item is JsonObject one && await AnswerAsync(one, withFiles) is { } r) results.Add(r);
                answer = results.Count > 0 ? results : null;
            }
            else answer = message is JsonObject single ? await AnswerAsync(single, withFiles) : null;

            if (answer is null)
            {
                response.StatusCode = 202;
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(answer.ToJsonString());
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception)
        {
            try { response.StatusCode = 400; } catch (Exception) { }
        }
        finally
        {
            try { response.Close(); } catch (Exception) { }
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
            content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" });
        return new JsonObject { ["content"] = content, ["isError"] = result.IsError };
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Close(); } catch (Exception) { }
    }
}
