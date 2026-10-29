using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Overdesk.AI;

namespace Overdesk.Tests;

public class McpServerTests : IDisposable
{
    private readonly List<string> _calls = new();
    private readonly McpServer _server;
    private readonly HttpClient _http = new();

    public McpServerTests()
    {
        var tools = new List<AiTool>
        {
            new()
            {
                Name = "echo", Description = "Echo text back.", Schema = AiTool.Object(("text", "string", "Text.", true)),
                Run = (a, _) => { _calls.Add(AiToolbox.Str(a, "text") ?? ""); return Task.FromResult(new AiToolResult("echo: " + AiToolbox.Str(a, "text"))); },
            },
            new()
            {
                Name = "read_file", Description = "A file tool.", Schema = AiTool.Object(), IsFileTool = true,
                Run = (_, _) => Task.FromResult(new AiToolResult("file")),
            },
        };
        var box = new AiToolbox(tools);
        _server = new McpServer(() => box);
        _server.Start();
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> PostAsync(string url, object body, string? token = "", string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Length == 0 ? _server.Token : token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    [Fact]
    public async Task Initialize_echoes_a_known_protocol_version()
    {
        var (status, body) = await PostAsync(_server.Endpoint(true).Url,
            new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-03-26" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("2025-03-26", body!["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notifications_get_202_without_body()
    {
        var (status, body) = await PostAsync(_server.Endpoint(true).Url, new { jsonrpc = "2.0", method = "notifications/initialized" });
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Null(body);
    }

    [Fact]
    public async Task App_endpoint_hides_file_tools()
    {
        var (_, all) = await PostAsync(_server.Endpoint(true).Url, new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        var (_, app) = await PostAsync(_server.Endpoint(false).Url, new { jsonrpc = "2.0", id = 3, method = "tools/list" });
        Assert.Equal(2, all!["result"]!["tools"]!.AsArray().Count);
        Assert.Single(app!["result"]!["tools"]!.AsArray());
    }

    [Fact]
    public async Task Tool_call_runs_the_tool()
    {
        var (_, body) = await PostAsync(_server.Endpoint(true).Url,
            new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "echo", arguments = new { text = "hi" } } });
        Assert.Equal("echo: hi", body!["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(new[] { "hi" }, _calls);
    }

    [Fact]
    public async Task File_tools_cannot_be_called_through_the_app_endpoint()
    {
        var (_, body) = await PostAsync(_server.Endpoint(false).Url,
            new { jsonrpc = "2.0", id = 5, method = "tools/call", @params = new { name = "read_file", arguments = new { } } });
        Assert.True(body!["result"]!["isError"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("wrong-token", null)]
    [InlineData("", "https://evil.example")]
    public async Task Requests_without_the_token_or_from_a_browser_are_refused(string? token, string? origin)
    {
        var (status, _) = await PostAsync(_server.Endpoint(true).Url, new { jsonrpc = "2.0", id = 6, method = "tools/list" }, token, origin);
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Unknown_methods_return_a_json_rpc_error()
    {
        var (_, body) = await PostAsync(_server.Endpoint(true).Url, new { jsonrpc = "2.0", id = 7, method = "resources/list" });
        Assert.Equal(-32601, body!["error"]!["code"]!.GetValue<int>());
    }

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
    }
}
