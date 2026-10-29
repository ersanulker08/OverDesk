using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Overdesk.AI;

/// <summary>
/// Claude through the user's installed Claude Code (the `claude` command): it signs in with their own Claude account,
/// so no API key is needed. Runs headless (`claude -p --output-format stream-json`), streams the answer and continues
/// the same conversation with `--resume`. In the side panel it is a chat: tools that change files or run commands are off.
/// </summary>
public sealed class ClaudeCodeProvider : ISessionChatProvider
{
    private static readonly string[] BlockedTools = { "Bash", "Edit", "Write", "MultiEdit", "NotebookEdit", "KillShell" };

    private readonly string _workspace;

    /// <param name="workspace">An empty folder the chats run in (they never see the user's projects).</param>
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

    public IAsyncEnumerable<ChatChunk> StreamAsync(string model, string? system, IReadOnlyList<ChatMessage> messages, CancellationToken cancellation)
        => StreamTurnAsync(model, system, messages[^1], null, cancellation);

    public async IAsyncEnumerable<ChatChunk> StreamTurnAsync(string model, string? system, ChatMessage message, string? sessionId,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var exe = CliTools.Find("claude") ?? throw new InvalidOperationException("Claude Code is not installed.");
        Directory.CreateDirectory(_workspace);
        var info = CliTools.StartInfo(exe, _workspace);
        foreach (var arg in new[] { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages" }) info.ArgumentList.Add(arg);
        if (model is { Length: > 0 } && model != "default") { info.ArgumentList.Add("--model"); info.ArgumentList.Add(model); }
        if (sessionId is { Length: > 0 }) { info.ArgumentList.Add("--resume"); info.ArgumentList.Add(sessionId); }
        if (!string.IsNullOrWhiteSpace(system)) { info.ArgumentList.Add("--append-system-prompt"); info.ArgumentList.Add(system); }
        info.ArgumentList.Add("--disallowedTools");
        foreach (var tool in BlockedTools) info.ArgumentList.Add(tool);

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
        // The prompt goes in on stdin: no quoting problems, and the variadic --disallowedTools cannot swallow it.
        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();
        var errors = process.StandardError.ReadToEndAsync(cancellation);

        var clock = Stopwatch.StartNew();
        var sawDelta = false;
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
                    if (evt?["type"]?.GetValue<string>() == "content_block_delta" && evt["delta"]?["type"]?.GetValue<string>() == "text_delta")
                    {
                        sawDelta = true;
                        yield return new ChatChunk(evt["delta"]!["text"]!.GetValue<string>(), SessionId: session);
                    }
                    break;
                case "assistant" when !sawDelta:
                    // Older Claude Code versions send whole messages only.
                    var blocks = node!["message"]?["content"]?.AsArray();
                    var text = string.Concat(blocks?.Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b!["text"]!.GetValue<string>()) ?? Array.Empty<string>());
                    if (text.Length > 0) yield return new ChatChunk(text, SessionId: session);
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
}
