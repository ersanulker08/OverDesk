using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Overdesk.Tests;

/// <summary>
/// A local HTTP server that answers each request with the next canned response and keeps the request bodies,
/// so provider tool loops can be checked without a real API key.
/// </summary>
internal sealed class FakeApiServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Queue<(string ContentType, string Body)> _responses = new();

    public FakeApiServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string Url { get; }
    public List<(string Path, JsonNode? Body)> Requests { get; } = new();

    public void Enqueue(string contentType, string body) => _responses.Enqueue((contentType, body));

    /// <summary>Server-sent events: one "event:/data:" pair per (type, json).</summary>
    public static string Sse(params (string? Event, string Data)[] events)
    {
        var sb = new StringBuilder();
        foreach (var (name, data) in events)
        {
            if (name is not null) sb.Append("event: ").Append(name).Append('\n');
            sb.Append("data: ").Append(data).Append("\n\n");
        }
        return sb.ToString();
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            string body;
            using (var reader = new StreamReader(context.Request.InputStream)) body = await reader.ReadToEndAsync();
            lock (Requests) Requests.Add((context.Request.Url!.AbsolutePath, body.Length > 0 ? JsonNode.Parse(body) : null));
            var (type, text) = _responses.Count > 0 ? _responses.Dequeue() : ("application/json", "{\"error\":\"no more responses\"}");
            var bytes = Encoding.UTF8.GetBytes(text);
            context.Response.StatusCode = text.Contains("no more responses") ? 500 : 200;
            context.Response.ContentType = type;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        try { _listener.Close(); } catch (Exception) { }
    }
}
