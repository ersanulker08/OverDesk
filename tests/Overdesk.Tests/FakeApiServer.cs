using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Overdesk.Tests;

/// <summary>
/// A local HTTP server that answers each request with the next canned response and keeps the request bodies,
/// so provider tool loops can be checked without a real API key. Plain sockets like McpServer: HttpListener needs
/// a URL reservation on Windows unless the tests run elevated.
/// </summary>
internal sealed class FakeApiServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Queue<(string ContentType, string Body, int Status)> _responses = new();
    private readonly CancellationTokenSource _stop = new();

    public FakeApiServer()
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = Task.Run(LoopAsync);
    }

    public string Url { get; }
    public List<(string Path, JsonNode? Body)> Requests { get; } = new();

    public void Enqueue(string contentType, string body, int status = 200) => _responses.Enqueue((contentType, body, status));

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
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var (path, body) = await ReadRequestAsync(stream);
            lock (Requests) Requests.Add((path, body.Length > 0 ? JsonNode.Parse(body) : null));
            var (type, text, status) = _responses.Count > 0 ? _responses.Dequeue() : ("application/json", "{\"error\":\"no more responses\"}", 500);
            var bytes = Encoding.UTF8.GetBytes(text);
            var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
            await stream.WriteAsync(bytes, _stop.Token);
            await stream.FlushAsync(_stop.Token);
        }
        catch (Exception) { /* a broken connection only affects its own request */ }
    }

    private async Task<(string Path, string Body)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd(buffer)) < 0)
        {
            var read = await stream.ReadAsync(chunk, _stop.Token);
            if (read == 0) throw new IOException("the request ended before its headers");
            buffer.Write(chunk, 0, read);
        }
        var lines = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd).Split("\r\n");
        var path = lines[0].Split(' ')[1].Split('?')[0];
        var length = lines.Skip(1)
            .Select(l => l.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(parts => int.Parse(parts[1].Trim()))
            .FirstOrDefault();
        var bodyStart = headerEnd + 4;
        while (buffer.Length - bodyStart < length)
        {
            var read = await stream.ReadAsync(chunk, _stop.Token);
            if (read == 0) throw new IOException("the request ended before its body");
            buffer.Write(chunk, 0, read);
        }
        return (path, Encoding.UTF8.GetString(buffer.GetBuffer(), bodyStart, length));
    }

    private static int IndexOfHeaderEnd(MemoryStream buffer) => buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf("\r\n\r\n"u8);

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch (Exception) { }
    }
}
