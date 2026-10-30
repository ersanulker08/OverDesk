using Overdesk.AI;

namespace Overdesk.Tests;

/// <summary>Claude Code is driven through a fake `claude` script that records its arguments and prints canned stream-json.</summary>
public class ClaudeCodeProviderTests
{
    private static string FakeClaude(TempFolder dir, string body)
    {
        var path = Path.Combine(dir.Path, "claude");
        File.WriteAllText(path, "#!/bin/bash\n" +
                                $"printf '%s\\n' \"$@\" > '{dir.Path}/args.txt'\n" +
                                $"cat > '{dir.Path}/stdin.txt'\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task<List<ChatChunk>> Run(ClaudeCodeProvider provider, AiContext? context, string? system = "Be brief.")
    {
        var chunks = new List<ChatChunk>();
        await foreach (var chunk in provider.StreamTurnAsync("haiku", system, new ChatMessage("user", "hello"), null, context, CancellationToken.None))
            chunks.Add(chunk);
        return chunks;
    }

    [Fact]
    public async Task Streams_text_tool_lines_and_session_and_sends_the_right_flags()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempFolder();
        var project = Directory.CreateDirectory(Path.Combine(dir.Path, "project")).FullName;
        var exe = FakeClaude(dir, $$$$$"""
            echo '{"type":"system","session_id":"s-1"}'
            echo '{"type":"stream_event","event":{"type":"message_start"}}'
            echo '{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Looking."}}}'
            echo '{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"{{{{{project}}}}}/src/a.cs"}},{"type":"tool_use","name":"Bash","input":{}}]}}'
            echo '{"type":"stream_event","event":{"type":"message_start"}}'
            echo '{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Done."}}}'
            echo '{"type":"result","is_error":false,"session_id":"s-1","usage":{"output_tokens":4}}'
            """);
        var provider = new ClaudeCodeProvider(Path.Combine(dir.Path, "workspace"), exe);
        var context = new AiContext(new AiToolbox(Array.Empty<AiTool>()), project, AllowEdits: true, new McpEndpoint("http://127.0.0.1:1/mcp/app", "tok"));

        var chunks = await Run(provider, context);

        Assert.Equal("Looking.\n\nDone.", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(new[] { "Reading src/a.cs" }, chunks.Where(c => c.Activity is not null).Select(c => c.Activity));
        Assert.Equal("s-1", chunks.Last().SessionId);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dir.Path, "stdin.txt")));

        var args = File.ReadAllLines(Path.Combine(dir.Path, "args.txt")).ToList();
        string After(string flag) => args[args.IndexOf(flag) + 1];
        Assert.Equal("haiku", After("--model"));
        Assert.Equal("acceptEdits", After("--permission-mode"));
        Assert.Contains("Be brief.", File.ReadAllText(After("--append-system-prompt-file")));
        Assert.Contains("mcp__overdesk", args);
        Assert.Contains("\"Bearer tok\"", File.ReadAllText(After("--mcp-config")));
        var blocked = args.Skip(args.IndexOf("--disallowedTools") + 1).ToList();
        Assert.Contains("Bash", blocked);
        Assert.DoesNotContain("Edit", blocked);
    }

    [Fact]
    public async Task Without_a_project_or_edits_the_edit_tools_are_blocked()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempFolder();
        var exe = FakeClaude(dir, """echo '{"type":"result","is_error":false,"session_id":"s"}'""");
        await Run(new ClaudeCodeProvider(Path.Combine(dir.Path, "workspace"), exe), null);
        var args = File.ReadAllLines(Path.Combine(dir.Path, "args.txt")).ToList();
        var blocked = args.Skip(args.IndexOf("--disallowedTools") + 1).ToList();
        Assert.Contains("Edit", blocked);
        Assert.Contains("Write", blocked);
        Assert.DoesNotContain("--permission-mode", args);
    }

    [Fact]
    public async Task The_usage_limit_is_explained()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempFolder();
        var exe = FakeClaude(dir, """echo '{"type":"result","is_error":true,"session_id":"s","result":"You have hit your session limit · resets 2:40am"}'""");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ClaudeCodeProvider(Path.Combine(dir.Path, "w"), exe), null));
        Assert.Contains("usage limit", error.Message);
        Assert.Contains("resets 2:40am", error.Message);
    }

    [Fact]
    public async Task An_older_claude_without_the_prompt_file_flag_gets_the_prompt_inline()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempFolder();
        var exe = FakeClaude(dir, """
            for a in "$@"; do
              if [ "$a" = "--append-system-prompt-file" ]; then echo "error: unknown option '--append-system-prompt-file'" >&2; exit 1; fi
            done
            echo '{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"ok"}}}'
            echo '{"type":"result","is_error":false,"session_id":"s"}'
            """);
        var chunks = await Run(new ClaudeCodeProvider(Path.Combine(dir.Path, "w"), exe), null);
        Assert.Equal("ok", string.Concat(chunks.Select(c => c.Text)));
        var args = File.ReadAllLines(Path.Combine(dir.Path, "args.txt")).ToList();
        Assert.Equal("Be brief.", args[args.IndexOf("--append-system-prompt") + 1]);
    }
}
