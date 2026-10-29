using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Overdesk.AI;

/// <summary>
/// Claude through the user's installed Claude Code (the `claude` command): it signs in with their own Claude account,
/// so no API key is needed. Runs headless (`claude -p --output-format stream-json`), streams the answer and continues
/// the same conversation with `--resume`. OverDesk's tools (works, tabs...) reach it through OverDesk's local MCP
/// server. With a project folder it runs inside that folder: it reads freely and edits files only when edits are
/// allowed. Shell commands stay off.
/// </summary>
public sealed class ClaudeCodeProvider : ISessionChatProvider
{
    private static readonly string[] AlwaysBlocked = { "Bash", "KillShell", "NotebookEdit" };
    private static readonly string[] EditTools = { "Edit", "Write", "MultiEdit" };
    private static readonly string[] ReadTools = { "Read", "Grep", "Glob", "LS" };

    private readonly string _workspace;

    /// <param name="workspace">An empty folder chats without a project run in (and where screenshots are dropped).</param>
    public ClaudeCodeProvider(string workspace) => _workspace = workspace;

    public string Id => "claude-code";
    public string Name => "Claude Code";
    public bool IsLocal => false;

    public Task<string?> CheckAsync(CancellationToken cancellation)
        => Task.Run(() => CliTools.Find("claude") is null
            ? "Claude Code is not installed. Install it from claude.com/claude-code (it uses your Claude account)."
            : null, cancellation);

    public Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellation)
        => Task.FromResult<IReadOnlyList<AiModel>>(new AiModel[]
        {
            new("default", "Your plan's default", "whatever Claude Code uses"),
            new("opus", "Opus", "most capable"),
            new("sonnet", "Sonnet", "fast and capable"),
            new("haiku", "Haiku", "fastest"),
        });

    /// <summary>The folder a chat runs in: Claude Code keeps sessions per folder, so a resumed one must use the same.</summary>
    public string WorkingFolder(AiContext? context) => context?.Folder is { } folder && Directory.Exists(folder) ? folder : _workspace;

    public IAsyncEnumerable<ChatChunk> StreamAsync(string model, string? system, IReadOnlyList<ChatMessage> messages, AiContext? context, CancellationToken cancellation)
        => StreamTurnAsync(model, system, messages[^1], null, context, cancellation);

    public async IAsyncEnumerable<ChatChunk> StreamTurnAsync(string model, string? system, ChatMessage message, string? sessionId, AiContext? context,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var exe = CliTools.Find("claude") ?? throw new InvalidOperationException("Claude Code is not installed.");
        Directory.CreateDirectory(_workspace);
        var folder = WorkingFolder(context);
        var inProject = folder != _workspace;
        var edits = inProject && context!.AllowEdits;

        var info = CliTools.StartInfo(exe, folder);
        // OverDesk keeps its own memory (the memory tool); Claude Code's would write notes into ~/.claude for OverDesk's workspace.
        info.Environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1";
        foreach (var arg in new[] { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages" }) info.ArgumentList.Add(arg);
        if (model is { Length: > 0 } && model != "default") { info.ArgumentList.Add("--model"); info.ArgumentList.Add(model); }
        if (sessionId is { Length: > 0 }) { info.ArgumentList.Add("--resume"); info.ArgumentList.Add(sessionId); }
        if (!string.IsNullOrWhiteSpace(system))
        {
            // From a file: the prompt is long and multi-line (project overview, memory), which a command line on
            // Windows (claude.cmd goes through cmd.exe) would mangle.
            var promptFile = Path.Combine(_workspace, "system-prompt.md");
            await File.WriteAllTextAsync(promptFile, system, cancellation);
            info.ArgumentList.Add("--append-system-prompt-file");
            info.ArgumentList.Add(promptFile);
        }
        if (inProject) { info.ArgumentList.Add("--add-dir"); info.ArgumentList.Add(_workspace); }
        if (edits) { info.ArgumentList.Add("--permission-mode"); info.ArgumentList.Add("acceptEdits"); }

        var allowed = new List<string>(ReadTools);
        if (context?.Mcp is { } mcp)
        {
            var config = Path.Combine(_workspace, "overdesk-mcp.json");
            await File.WriteAllTextAsync(config, new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["overdesk"] = new JsonObject
                    {
                        ["type"] = "http", ["url"] = mcp.Url,
                        ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + mcp.Token },
                    },
                },
            }.ToJsonString(), cancellation);
            info.ArgumentList.Add("--mcp-config");
            info.ArgumentList.Add(config);
            allowed.Add("mcp__overdesk");
        }
        if (edits) allowed.AddRange(EditTools);
        // Both lists are variadic: each is closed by the next flag, and the prompt itself goes in on stdin.
        info.ArgumentList.Add("--allowedTools");
        foreach (var tool in allowed) info.ArgumentList.Add(tool);
        info.ArgumentList.Add("--disallowedTools");
        foreach (var tool in edits ? AlwaysBlocked : AlwaysBlocked.Concat(EditTools)) info.ArgumentList.Add(tool);

        // Screenshots go next to the chat as files Claude Code can look at.
        var prompt = message.Text;
        var images = message.Images ?? Array.Empty<byte[]>();
        for (var i = 0; i < images.Count; i++)
        {
            var file = Path.Combine(_workspace, $"screenshot-{DateTime.Now:HHmmss}-{i}.png");
            await File.WriteAllBytesAsync(file, images[i], cancellation);
            prompt += $"\n\n(Attached screenshot: {file} — read it to see it.)";
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start Claude Code.");
        using var kill = cancellation.Register(() => { try { process.Kill(entireProcessTree: true); } catch (Exception) { } });
        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();
        var errors = process.StandardError.ReadToEndAsync(cancellation);

        var clock = Stopwatch.StartNew();
        var sawDelta = false;
        var wroteText = false;
        string? session = null;
        while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); } catch (Exception) { continue; }
            var type = node?["type"]?.GetValue<string>();
            session ??= node?["session_id"]?.GetValue<string>();

            switch (type)
            {
                case "stream_event":
                    var evt = node!["event"];
                    var eventType = evt?["type"]?.GetValue<string>();
                    // A new model message after tools ran: keep its text apart from the text before.
                    if (eventType == "message_start" && wroteText) yield return new ChatChunk("\n\n", SessionId: session);
                    if (eventType == "content_block_delta" && evt!["delta"]?["type"]?.GetValue<string>() == "text_delta")
                    {
                        sawDelta = wroteText = true;
                        yield return new ChatChunk(evt["delta"]!["text"]!.GetValue<string>(), SessionId: session);
                    }
                    break;
                case "assistant":
                    var blocks = node!["message"]?["content"]?.AsArray() ?? new JsonArray();
                    foreach (var block in blocks)
                        if (block?["type"]?.GetValue<string>() == "tool_use" && Describe(block, folder) is { } activity)
                            yield return new ChatChunk("", SessionId: session, Activity: activity);
                    if (sawDelta) break;
                    // Older Claude Code versions send whole messages only.
                    var text = string.Concat(blocks.Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b!["text"]!.GetValue<string>()));
                    if (text.Length > 0)
                    {
                        wroteText = true;
                        yield return new ChatChunk(text, SessionId: session);
                    }
                    break;
                case "result":
                    if (node!["is_error"]?.GetValue<bool>() == true)
                        throw new InvalidOperationException("Claude Code: " + (node["result"]?.ToString() ?? "the request failed."));
                    var outputTokens = node["usage"]?["output_tokens"]?.GetValue<double>() ?? 0;
                    double? speed = outputTokens > 0 && clock.Elapsed.TotalSeconds > 0 ? outputTokens / clock.Elapsed.TotalSeconds : null;
                    yield return new ChatChunk("", speed, node["session_id"]?.GetValue<string>() ?? session);
                    break;
            }
        }
        await process.WaitForExitAsync(cancellation);
        if (process.ExitCode != 0 && session is null)
        {
            var error = (await errors).Trim();
            throw new InvalidOperationException(error.Contains("login", StringComparison.OrdinalIgnoreCase) || error.Contains("auth", StringComparison.OrdinalIgnoreCase)
                ? "Claude Code is not signed in. Open a terminal and run: claude  (then /login)."
                : "Claude Code stopped: " + (error.Length > 300 ? error[..300] + "…" : error));
        }
    }

    /// <summary>A chat line for one of Claude Code's own tools. OverDesk's MCP tools report themselves, so they are skipped.</summary>
    private static string? Describe(JsonNode block, string folder)
    {
        var name = block["name"]?.GetValue<string>() ?? "";
        var input = block["input"];
        string File(string key)
        {
            if (input?[key]?.GetValue<string>() is not { } path) return "a file";
            var relative = Path.GetRelativePath(folder, path);
            if (!relative.StartsWith("..", StringComparison.Ordinal)) return relative;
            // Outside the project (an attached screenshot...): the name says enough.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..] : Path.GetFileName(path);
        }
        return name switch
        {
            "Read" => $"Reading {File("file_path")}",
            "Edit" or "MultiEdit" => $"Editing {File("file_path")}",
            "Write" => $"Writing {File("file_path")}",
            "Grep" => $"Searching for “{input?["pattern"]?.GetValue<string>()}”",
            "Glob" => $"Finding {input?["pattern"]?.GetValue<string>()}",
            "LS" => "Listing files",
            "WebSearch" => $"Searching the web for “{input?["query"]?.GetValue<string>()}”",
            "WebFetch" => $"Opening {input?["url"]?.GetValue<string>()}",
            "Task" or "Agent" => "Working on a sub-task",
            // Internal bookkeeping, or tools OverDesk switched off (the attempt is refused, nothing happens).
            "ToolSearch" or "TodoWrite" or "Bash" or "KillShell" or "NotebookEdit" => null,
            _ when name.StartsWith("mcp__", StringComparison.Ordinal) => null,
            _ => name,
        };
    }
}
