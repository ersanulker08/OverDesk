using System.Text.Json.Nodes;
using Overdesk.AI;

namespace Overdesk.Tests;

/// <summary>Provider tool loops against fake APIs: the call runs, and the follow-up request carries what the API needs.</summary>
public class ProviderLoopTests
{
    private sealed class Keys : IApiKeySource
    {
        public string? Get(string name) => "test-key";
    }

    private readonly List<string> _echoed = new();

    private AiContext Context() => new(new AiToolbox(new[]
    {
        new AiTool
        {
            Name = "echo", Description = "Echo.", Schema = AiTool.Object(("text", "string", "Text.", true)),
            Run = (a, _) => { _echoed.Add(AiToolbox.Str(a, "text") ?? ""); return Task.FromResult(new AiToolResult("echoed " + AiToolbox.Str(a, "text"))); },
        },
    }), null, false, null);

    private static async Task<string> Collect(IAsyncEnumerable<ChatChunk> stream)
    {
        var text = "";
        await foreach (var chunk in stream) text += chunk.Text;
        return text;
    }

    private static string J(object value) => System.Text.Json.JsonSerializer.Serialize(value);

    [Fact]
    public async Task Claude_runs_tools_and_sends_thinking_and_results_back()
    {
        using var server = new FakeApiServer();
        var usage = new { input_tokens = 10, output_tokens = 1, cache_creation_input_tokens = 0, cache_read_input_tokens = 0 };
        object Start() => new { type = "message_start", message = new { id = "msg_1", type = "message", role = "assistant", model = "claude-opus-5-5", content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage } };
        server.Enqueue("text/event-stream", FakeApiServer.Sse(
            ("message_start", J(Start())),
            ("content_block_start", J(new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "", signature = "" } })),
            ("content_block_delta", J(new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "Use the tool." } })),
            ("content_block_delta", J(new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature = "sig-123" } })),
            ("content_block_stop", J(new { type = "content_block_stop", index = 0 })),
            ("content_block_start", J(new { type = "content_block_start", index = 1, content_block = new { type = "text", text = "" } })),
            ("content_block_delta", J(new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = "Checking." } })),
            ("content_block_stop", J(new { type = "content_block_stop", index = 1 })),
            ("content_block_start", J(new { type = "content_block_start", index = 2, content_block = new { type = "tool_use", id = "toolu_1", name = "echo", input = new { } } })),
            ("content_block_delta", J(new { type = "content_block_delta", index = 2, delta = new { type = "input_json_delta", partial_json = "{\"text\": \"h" } })),
            ("content_block_delta", J(new { type = "content_block_delta", index = 2, delta = new { type = "input_json_delta", partial_json = "i\"}" } })),
            ("content_block_stop", J(new { type = "content_block_stop", index = 2 })),
            ("message_delta", J(new { type = "message_delta", delta = new { stop_reason = "tool_use", stop_sequence = (string?)null }, usage = new { output_tokens = 30 } })),
            ("message_stop", J(new { type = "message_stop" }))));
        server.Enqueue("text/event-stream", FakeApiServer.Sse(
            ("message_start", J(Start())),
            ("content_block_start", J(new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })),
            ("content_block_delta", J(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "Done." } })),
            ("content_block_stop", J(new { type = "content_block_stop", index = 0 })),
            ("message_delta", J(new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 5 } })),
            ("message_stop", J(new { type = "message_stop" }))));

        var provider = new ClaudeProvider(new Keys(), server.Url);
        var text = await Collect(provider.StreamAsync("claude-opus-5-5", "system", new[] { new ChatMessage("user", "hi") }, Context(), CancellationToken.None));

        Assert.Equal(new[] { "hi" }, _echoed);
        Assert.Contains("Checking.", text);
        Assert.Contains("Done.", text);
        Assert.Equal(2, server.Requests.Count);

        var first = server.Requests[0].Body!;
        Assert.Equal("echo", first["tools"]![0]!["name"]!.GetValue<string>());
        Assert.NotNull(first["cache_control"]);

        var messages = server.Requests[1].Body!["messages"]!.AsArray();
        var assistant = messages[^2]!["content"]!.AsArray();
        Assert.Equal("thinking", assistant[0]!["type"]!.GetValue<string>());
        Assert.Equal("sig-123", assistant[0]!["signature"]!.GetValue<string>());
        Assert.Equal("tool_use", assistant[2]!["type"]!.GetValue<string>());
        Assert.Equal("hi", assistant[2]!["input"]!["text"]!.GetValue<string>());
        var result = messages[^1]!["content"]![0]!;
        Assert.Equal("tool_result", result["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", result["tool_use_id"]!.GetValue<string>());
        Assert.Contains("echoed hi", result["content"]!.ToJsonString());
    }

    [Fact]
    public async Task Gemini_runs_tools_and_returns_the_model_parts_unchanged()
    {
        using var server = new FakeApiServer();
        server.Enqueue("text/event-stream", FakeApiServer.Sse((null, J(new
        {
            candidates = new[] { new { content = new { role = "model", parts = new object[] { new { functionCall = new { name = "echo", args = new { text = "yo" } }, thoughtSignature = "abc" } } } } },
        }))));
        server.Enqueue("text/event-stream", FakeApiServer.Sse((null, J(new
        {
            candidates = new[] { new { content = new { role = "model", parts = new object[] { new { text = "All good." } } } } },
            usageMetadata = new { candidatesTokenCount = 3 },
        }))));

        var provider = new GeminiProvider(new Keys(), server.Url);
        var text = await Collect(provider.StreamAsync("gemini-test", null, new[] { new ChatMessage("user", "hi") }, Context(), CancellationToken.None));

        Assert.Equal(new[] { "yo" }, _echoed);
        Assert.Equal("All good.", text);
        var contents = server.Requests[1].Body!["contents"]!.AsArray();
        Assert.Equal("abc", contents[1]!["parts"]![0]!["thoughtSignature"]!.GetValue<string>());
        Assert.Equal("echo", contents[2]!["parts"]![0]!["functionResponse"]!["name"]!.GetValue<string>());
        Assert.Equal("echoed yo", contents[2]!["parts"]![0]!["functionResponse"]!["response"]!["result"]!.GetValue<string>());
    }
}
