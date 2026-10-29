using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Overdesk.AI;

/// <summary>
/// Gemini through Google's Gemini CLI (`gemini`), signed in with the user's Google account. The CLI keeps no
/// conversation between runs, so the earlier turns are sent along with each question.
/// </summary>
public sealed class GeminiCliProvider : IChatProvider
{
    private readonly string _workspace;

    public GeminiCliProvider(string workspace) => _workspace = workspace;

    public string Id => "gemini-cli";
    public string Name => "Gemini CLI";
    public bool IsLocal => false;

    public Task<string?> CheckAsync(CancellationToken cancellation)
        => Task.Run(() => CliTools.Find("gemini") is null ? "Gemini CLI is not installed (npm install -g @google/gemini-cli)." : null, cancellation);

    public Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellation)
        => Task.FromResult<IReadOnlyList<AiModel>>(new[] { new AiModel("default", "Your account's default") });

    public async IAsyncEnumerable<ChatChunk> StreamAsync(string model, string? system, IReadOnlyList<ChatMessage> messages, AiContext? context,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var exe = CliTools.Find("gemini") ?? throw new InvalidOperationException("Gemini CLI is not installed.");
        Directory.CreateDirectory(_workspace);
        await WriteSettingsAsync(context?.Mcp, cancellation);
        var prompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(system)) prompt.AppendLine(system).AppendLine();
        if (messages.Count > 1)
        {
            prompt.AppendLine("Conversation so far:");
            foreach (var m in messages.Take(messages.Count - 1)) prompt.AppendLine($"{(m.Role == ChatMessage.User ? "User" : "Assistant")}: {m.Text}");
            prompt.AppendLine().AppendLine("Now answer the user's new message:");
        }
        prompt.Append(messages[^1].Text);

        var info = CliTools.StartInfo(exe, _workspace);
        // The project folder is readable through Gemini CLI's own tools; changes go through OverDesk's file tools.
        if (context?.Folder is { } folder && Directory.Exists(folder)) { info.ArgumentList.Add("--include-directories"); info.ArgumentList.Add(folder); }
        // The conversation goes in on stdin (Gemini CLI runs headless when stdin is not a terminal): a long,
        // multi-line prompt would not survive a command line on Windows, where gemini.cmd goes through cmd.exe.
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start Gemini CLI.");
        using var kill = cancellation.Register(() => { try { process.Kill(entireProcessTree: true); } catch (Exception) { } });
        await process.StandardInput.WriteAsync(prompt.ToString());
        process.StandardInput.Close();
        var errors = process.StandardError.ReadToEndAsync(cancellation);

        var buffer = new char[256];
        int read;
        while ((read = await process.StandardOutput.ReadAsync(buffer, cancellation)) > 0)
            yield return new ChatChunk(new string(buffer, 0, read));
        await process.WaitForExitAsync(cancellation);
        if (process.ExitCode != 0) throw new InvalidOperationException("Gemini CLI: " + (await errors).Trim());
    }

    /// <summary>
    /// Gemini CLI reads MCP servers from .gemini/settings.json in the folder it runs in (its own workspace, never the
    /// user's project). "trust" lets OverDesk's tools run without the confirmation prompt a headless run cannot show.
    /// </summary>
    private async Task WriteSettingsAsync(McpEndpoint? mcp, CancellationToken cancellation)
    {
        var folder = Path.Combine(_workspace, ".gemini");
        var file = Path.Combine(folder, "settings.json");
        if (mcp is null)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        Directory.CreateDirectory(folder);
        var settings = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["overdesk"] = new JsonObject
                {
                    ["httpUrl"] = mcp.Url, ["trust"] = true,
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + mcp.Token },
                },
            },
        };
        await File.WriteAllTextAsync(file, settings.ToJsonString(), cancellation);
    }
}
