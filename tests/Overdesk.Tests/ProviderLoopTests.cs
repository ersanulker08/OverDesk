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

    [Fact]
    public async Task OpenAi_compatible_joins_split_arguments_and_keeps_calls_with_new_ids_apart()
    {
        using var server = new FakeApiServer();
        static string Delta(object delta) => J(new { choices = new[] { new { delta } } });
        server.Enqueue("text/event-stream", FakeApiServer.Sse(
            (null, Delta(new { content = "On it. " })),
            (null, Delta(new { tool_calls = new[] { new { index = 0, id = "c1", function = new { name = "echo", arguments = "{\"te" } } } })),
            (null, Delta(new { tool_calls = new[] { new { index = 0, function = new { arguments = "xt\":\"a\"}" } } } })),
            // A server that numbers every call 0: a new id must start a new call.
            (null, Delta(new { tool_calls = new[] { new { index = 0, id = "c2", function = new { name = "echo", arguments = "{\"text\":\"b\"}" } } } })),
            (null, "[DONE]")));
        server.Enqueue("text/event-stream", FakeApiServer.Sse((null, Delta(new { content = "Both echoed." })), (null, "[DONE]")));

        var provider = new OpenAiCompatibleProvider("test", "Test", server.Url, isLocal: true);
        var text = await Collect(provider.StreamAsync("m", "sys", new[] { new ChatMessage("user", "hi") }, Context(), CancellationToken.None));

        Assert.Equal(new[] { "a", "b" }, _echoed);
        Assert.Contains("Both echoed.", text);
        var messages = server.Requests[1].Body!["messages"]!.AsArray();
        Assert.Equal(2, messages[^3]!["tool_calls"]!.AsArray().Count);
        Assert.Equal("c1", messages[^2]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("echoed b", messages[^1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAi_compatible_retries_without_tools_when_the_model_cannot_use_them()
    {
        using var server = new FakeApiServer();
        server.Enqueue("application/json", "{\"error\":\"this model does not support tools\"}", status: 400);
        server.Enqueue("text/event-stream", FakeApiServer.Sse((null, J(new { choices = new[] { new { delta = new { content = "Plain answer." } } } })), (null, "[DONE]")));

        var provider = new OpenAiCompatibleProvider("test", "Test", server.Url, isLocal: true);
        var text = await Collect(provider.StreamAsync("m", null, new[] { new ChatMessage("user", "hi") }, Context(), CancellationToken.None));

        Assert.Equal("Plain answer.", text);
        Assert.NotNull(server.Requests[0].Body!["tools"]);
        Assert.Null(server.Requests[1].Body!["tools"]);
    }

    [Fact]
    public async Task Ollama_runs_tools_from_ndjson_and_asks_for_a_larger_context()
    {
        using var server = new FakeApiServer();
        server.Enqueue("application/x-ndjson",
            J(new { message = new { role = "assistant", content = "", tool_calls = new[] { new { function = new { name = "echo", arguments = new { text = "z" } } } } }, done = false }) + "\n" +
            J(new { message = new { role = "assistant", content = "" }, done = true }) + "\n");
        server.Enqueue("application/x-ndjson",
            J(new { message = new { role = "assistant", content = "Fine." }, done = false }) + "\n" +
            J(new { message = new { role = "assistant", content = "" }, done = true, eval_count = 10, eval_duration = 1_000_000_000 }) + "\n");

        var provider = new OllamaProvider(server.Url);
        var text = await Collect(provider.StreamAsync("m", null, new[] { new ChatMessage("user", "hi") }, Context(), CancellationToken.None));

        Assert.Equal(new[] { "z" }, _echoed);
        Assert.Equal("Fine.", text);
        Assert.Equal(16384, server.Requests[0].Body!["options"]!["num_ctx"]!.GetValue<int>());
        var last = server.Requests[1].Body!["messages"]!.AsArray()[^1]!;
        Assert.Equal("tool", last["role"]!.GetValue<string>());
        Assert.Equal("echo", last["tool_name"]!.GetValue<string>());
    }

    private static string ClaudeReply() => FakeApiServer.Sse(
        ("message_start", J(new { type = "message_start", message = new { id = "m", type = "message", role = "assistant", model = "x", content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 1, output_tokens = 1, cache_creation_input_tokens = 0, cache_read_input_tokens = 0 } } })),
        ("content_block_start", J(new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })),
        ("content_block_delta", J(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "Hi." } })),
        ("content_block_stop", J(new { type = "content_block_stop", index = 0 })),
        ("message_delta", J(new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 1 } })),
        ("message_stop", J(new { type = "message_stop" })));

    [Theory]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("claude-fable-5-1", true)]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("claude-haiku-4-5", false)]
    [InlineData("claude-haiku-4-5-20251001", false)]
    [InlineData("claude-sonnet-4-5", false)]
    [InlineData("claude-opus-4-5-20251101", false)]
    [InlineData("claude-opus-4-20250514", false)]
    [InlineData("claude-3-5-haiku-20241022", false)]
    public async Task Claude_gets_thinking_and_effort_only_on_models_that_take_them(string model, bool modern)
    {
        using var server = new FakeApiServer();
        server.Enqueue("text/event-stream", ClaudeReply());

        await Collect(new ClaudeProvider(new Keys(), server.Url).StreamAsync(model, null, new[] { new ChatMessage("user", "hi") }, null, CancellationToken.None));

        var body = server.Requests[0].Body!;
        Assert.Equal(modern, body["thinking"] is not null);
        Assert.Equal(modern, body["output_config"] is not null);
        if (modern) Assert.Equal("adaptive", body["thinking"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Gemini_tools_without_arguments_send_no_parameters()
    {
        using var server = new FakeApiServer();
        server.Enqueue("text/event-stream", FakeApiServer.Sse((null, J(new { candidates = new[] { new { content = new { role = "model", parts = new object[] { new { text = "ok" } } } } } }))));
        var context = new AiContext(new AiToolbox(new[]
        {
            new AiTool { Name = "state", Description = "State.", Schema = AiTool.Object(), Run = (_, _) => Task.FromResult(new AiToolResult("")) },
            new AiTool { Name = "echo", Description = "Echo.", Schema = AiTool.Object(("text", "string", "Text.", true)), Run = (_, _) => Task.FromResult(new AiToolResult("")) },
        }), null, false, null);

        await Collect(new GeminiProvider(new Keys(), server.Url).StreamAsync("gemini-test", null, new[] { new ChatMessage("user", "hi") }, context, CancellationToken.None));

        var declarations = server.Requests[0].Body!["tools"]![0]!["functionDeclarations"]!.AsArray();
        Assert.False(declarations[0]!.AsObject().ContainsKey("parameters"));
        Assert.NotNull(declarations[1]!["parameters"]!["properties"]!["text"]);
    }

    [Fact]
    public async Task OpenAi_lists_only_chat_models_newest_first()
    {
        using var server = new FakeApiServer();
        var ids = new (string Id, long Created)[]
        {
            ("gpt-4-0125-preview", 100), ("gpt-5.1", 300), ("o4-mini", 200), ("omni-moderation-latest", 400), ("gpt-image-1", 500),
            ("gpt-4o-search-preview", 500), ("text-embedding-3-small", 500), ("gpt-4o-audio-preview", 500), ("gpt-3.5-turbo-instruct", 500), ("dall-e-3", 500),
        };
        server.Enqueue("application/json", J(new { data = ids.Select(m => new { id = m.Id, created = m.Created }) }));

        var models = await new OpenAiCompatibleProvider("openai", "OpenAI", server.Url, isLocal: false).ListModelsAsync(CancellationToken.None);

        Assert.Equal(new[] { "gpt-5.1", "o4-mini", "gpt-4-0125-preview" }, models.Select(m => m.Id));
    }

    [Fact]
    public async Task Local_servers_use_the_address_from_settings_as_it_changes()
    {
        using var before = new FakeApiServer();
        using var after = new FakeApiServer();
        before.Enqueue("application/json", J(new { models = new[] { new { name = "old:1b" } } }));
        after.Enqueue("application/json", J(new { models = new[] { new { name = "new:1b" } } }));
        after.Enqueue("application/json", J(new { data = new[] { new { id = "local" } } }));
        var url = before.Url;
        var ollama = new OllamaProvider(() => url);
        var lmStudio = new OpenAiCompatibleProvider("lmstudio", "LM Studio", () => url + "/", isLocal: true);

        Assert.Equal("old:1b", (await ollama.ListModelsAsync(CancellationToken.None))[0].Id);
        url = after.Url;
        Assert.Equal("new:1b", (await ollama.ListModelsAsync(CancellationToken.None))[0].Id);
        Assert.Equal("local", (await lmStudio.ListModelsAsync(CancellationToken.None))[0].Id);
    }
}
