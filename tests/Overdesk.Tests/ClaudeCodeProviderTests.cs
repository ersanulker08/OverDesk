using System.Text.RegularExpressions;
using Overdesk.AI;

namespace Overdesk.Tests;

/// <summary>
/// Claude Code is driven through a fake `claude` script that records its arguments and prints canned stream-json.
/// On Windows the fake is a `claude.cmd` (like the npm shim), so the cmd.exe path, --append-system-prompt-file and the
/// prompt on stdin are covered there too.
/// </summary>
public class ClaudeCodeProviderTests
{
    private static string FakeClaude(TempFolder dir, string body, string? windowsBody = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(dir.Path, "claude.cmd");
            File.WriteAllText(cmd, "@echo off\r\n" +
                                   $"type nul > \"{dir.Path}\\args.txt\"\r\n" +
                                   ":args\r\n" +
                                   "if \"%~1\"==\"\" goto body\r\n" +
                                   $">> \"{dir.Path}\\args.txt\" echo %~1\r\n" +
                                   "shift\r\n" +
                                   "goto args\r\n" +
                                   ":body\r\n" +
                                   $"findstr \"^\" > \"{dir.Path}\\stdin.txt\"\r\n" +
                                   (windowsBody ?? ToCmd(body)) + "\r\n");
            return cmd;
        }
        var path = Path.Combine(dir.Path, "claude");
        File.WriteAllText(path, "#!/bin/bash\n" +
                                $"printf '%s\\n' \"$@\" > '{dir.Path}/args.txt'\n" +
                                $"cat > '{dir.Path}/stdin.txt'\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>The bash bodies are echo lines, echo-to-stderr lines and exit codes; anything else needs an explicit Windows body.</summary>
    private static string ToCmd(string bash)
    {
        var lines = new List<string>();
        foreach (var statement in bash.Split('\n').SelectMany(l => l.Split("; ")).Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var echo = Regex.Match(statement, @"^echo '(.*)'( >&2)?$");
            if (echo.Success) { lines.Add($"echo {echo.Groups[1].Value}{(echo.Groups[2].Success ? " 1>&2" : "")}"); continue; }
            var exit = Regex.Match(statement, @"^exit (\d+)$");
            if (exit.Success) { lines.Add($"exit {exit.Groups[1].Value}"); continue; }
            throw new InvalidOperationException($"No Windows translation for: {statement}");
        }
        return string.Join("\r\n", lines);
    }

    private static string Stdin(TempFolder dir)
    {
        var text = File.ReadAllText(Path.Combine(dir.Path, "stdin.txt"));
        // findstr writes CRLF line ends, including one after the last line.
        return OperatingSystem.IsWindows() ? text.Replace("\r\n", "\n").TrimEnd('\n') : text;
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
        using var dir = new TempFolder();
        var project = Directory.CreateDirectory(Path.Combine(dir.Path, "project")).FullName;
        var exe = FakeClaude(dir, $$$$$"""
            echo '{"type":"system","session_id":"s-1"}'
            echo '{"type":"stream_event","event":{"type":"message_start"}}'
            echo '{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Looking."}}}'
            echo '{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"{{{{{project.Replace("\\", "\\\\")}}}}}/src/a.cs"}},{"type":"tool_use","name":"Bash","input":{}}]}}'
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
        Assert.Equal("hello", Stdin(dir));

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
        using var dir = new TempFolder();
        var exe = FakeClaude(dir, """echo '{"type":"result","is_error":true,"session_id":"s","result":"You have hit your session limit · resets 2:40am"}'""");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ClaudeCodeProvider(Path.Combine(dir.Path, "w"), exe), null));
        Assert.Contains("usage limit", error.Message);
        Assert.Contains("resets 2:40am", error.Message);
    }

    [Fact]
    public async Task An_older_claude_without_the_prompt_file_flag_gets_the_prompt_inline()
    {
        using var dir = new TempFolder();
        var exe = FakeClaude(dir, """
            for a in "$@"; do
              if [ "$a" = "--append-system-prompt-file" ]; then echo "error: unknown option '--append-system-prompt-file'" >&2; exit 1; fi
            done
            echo '{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"ok"}}}'
            echo '{"type":"result","is_error":false,"session_id":"s"}'
            """, $$$$"""
            findstr /x /c:"--append-system-prompt-file" "{{{{dir.Path}}}}\args.txt" > nul
            if errorlevel 1 goto answer
            echo error: unknown option '--append-system-prompt-file' 1>&2
            exit 1
            :answer
            echo {"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"ok"}}}
            echo {"type":"result","is_error":false,"session_id":"s"}
            """);
        var chunks = await Run(new ClaudeCodeProvider(Path.Combine(dir.Path, "w"), exe), null);
        Assert.Equal("ok", string.Concat(chunks.Select(c => c.Text)));
        var args = File.ReadAllLines(Path.Combine(dir.Path, "args.txt")).ToList();
        Assert.Equal("Be brief.", args[args.IndexOf("--append-system-prompt") + 1]);
    }

    private static async Task<List<ChatChunk>> Resume(ClaudeCodeProvider provider, ChatMessage? message = null)
    {
        var chunks = new List<ChatChunk>();
        await foreach (var chunk in provider.StreamTurnAsync("haiku", null, message ?? new ChatMessage("user", "hello"), "old-session", null, CancellationToken.None))
            chunks.Add(chunk);
        return chunks;
    }

    [Theory]
    [InlineData("echo 'No conversation found with session ID: old-session' >&2; exit 1")]
    [InlineData("""echo '{"type":"result","is_error":true,"errors":["No conversation found with session ID: old-session"]}'; exit 1""")]
    public async Task An_expired_session_is_reported_as_such(string body)
    {
        using var dir = new TempFolder();
        var provider = new ClaudeCodeProvider(Path.Combine(dir.Path, "w"), FakeClaude(dir, body));
        await Assert.ThrowsAsync<ChatSessionNotFoundException>(() => Resume(provider));
    }

    [Fact]
    public async Task A_crash_after_the_session_started_is_an_error_not_an_empty_answer()
    {
        using var dir = new TempFolder();
        var exe = FakeClaude(dir, """
            echo '{"type":"system","session_id":"s-1"}'
            echo 'MCP connection failed' >&2
            exit 1
            """);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ClaudeCodeProvider(Path.Combine(dir.Path, "w"), exe), null));
        Assert.Contains("MCP connection failed", error.Message);
    }

    [Theory]
    [InlineData("exit 3", "exit code 3")]
    [InlineData("exit 0", "without an answer")]
    public async Task Ending_silently_is_an_error(string body, string expected)
    {
        using var dir = new TempFolder();
        var provider = new ClaudeCodeProvider(Path.Combine(dir.Path, "w"), FakeClaude(dir, body));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(provider, null));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task A_refused_tool_shows_as_failed()
    {
        using var dir = new TempFolder();
        var exe = FakeClaude(dir, """
            echo '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"WebSearch","input":{"query":"cats"}},{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"a.txt"}}]}}'
            echo '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"Permission denied\nsecond line"}]}}'
            echo '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":[{"type":"text","text":"fine"}]}]}}'
            echo '{"type":"result","is_error":false,"session_id":"s"}'
            """);
        var chunks = await Run(new ClaudeCodeProvider(Path.Combine(dir.Path, "w"), exe), null);
        Assert.Equal(new[] { "Searching the web for “cats”", "Reading a.txt", "⚠ Searching the web for “cats”: Permission denied" },
            chunks.Where(c => c.Activity is not null).Select(c => c.Activity));
    }

    [Fact]
    public async Task Screenshots_exist_during_the_run_and_are_gone_after_it()
    {
        using var dir = new TempFolder();
        var workspace = Directory.CreateDirectory(Path.Combine(dir.Path, "w")).FullName;
        File.WriteAllText(Path.Combine(workspace, "screenshot-120000-0.png"), "left by an older version");
        var exe = FakeClaude(dir, $$$"""
            ls '{{{workspace}}}' > '{{{dir.Path}}}/seen.txt'
            echo '{"type":"result","is_error":false,"session_id":"s"}'
            """, $$$"""
            dir /b "{{{workspace}}}" > "{{{dir.Path}}}\seen.txt"
            echo {"type":"result","is_error":false,"session_id":"s"}
            """);
        await Resume(new ClaudeCodeProvider(workspace, exe), new ChatMessage("user", "look", new[] { new byte[] { 1 }, new byte[] { 2 } }));

        var seen = File.ReadAllLines(Path.Combine(dir.Path, "seen.txt")).Where(f => f.StartsWith("screenshot-")).ToList();
        Assert.Equal(2, seen.Count);
        Assert.DoesNotContain("screenshot-120000-0.png", seen);
        Assert.All(seen, f => Assert.Contains(f, Stdin(dir)));
        Assert.Empty(Directory.GetFiles(workspace, "screenshot-*"));
    }
}
