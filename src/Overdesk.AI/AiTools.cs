using System.Text.Json.Nodes;

namespace Overdesk.AI;

/// <summary>What a tool hands back to the model: text, optionally an image (PNG) it should look at.</summary>
public sealed record AiToolResult(string Text, byte[]? Image = null, bool IsError = false)
{
    public static AiToolResult Error(string text) => new(text, IsError: true);
}

/// <summary>
/// Something the AI can do inside OverDesk (open a work, add a to-do, read a project file...). The schema is a JSON
/// Schema object; every provider turns it into its own tool-calling format.
/// </summary>
public sealed class AiTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>A JSON Schema of type "object" describing the arguments.</summary>
    public required JsonObject Schema { get; init; }

    public required Func<JsonObject, CancellationToken, Task<AiToolResult>> Run { get; init; }

    /// <summary>A short line for the chat ("Created “Groceries”"), built from the arguments before the tool runs.</summary>
    public Func<JsonObject, string>? Label { get; init; }

    /// <summary>
    /// For actions that are hard to undo (running a command, deleting a work): what to ask the user ("Run `npm test`
    /// in OverDesk?"). The call waits until they allow or deny it in the chat.
    /// </summary>
    public Func<JsonObject, string>? Approval { get; init; }

    /// <summary>File tools for the chosen project folder. Claude Code brings its own, so it gets only the others.</summary>
    public bool IsFileTool { get; init; }

    /// <summary>Builds { "type": "object", "properties": {...}, "required": [...] } from (name, type, description, required) rows.</summary>
    public static JsonObject Object(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, description, isRequired) in properties)
        {
            props[name] = type switch
            {
                "string[]" => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description },
                _ => new JsonObject { ["type"] = type, ["description"] = description },
            };
            if (isRequired) required.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required };
    }
}

/// <summary>
/// The tools one chat turn may use, plus the place their activity is reported to (the chat shows a line per call).
/// Every provider runs tools through <see cref="RunAsync"/>, so errors become results the model can react to.
/// </summary>
public sealed class AiToolbox
{
    public AiToolbox(IReadOnlyList<AiTool> tools) => Tools = tools;

    public IReadOnlyList<AiTool> Tools { get; }

    /// <summary>Raised before each call with a short human line ("Reading src/App.cs").</summary>
    public event Action<string>? Activity;

    /// <summary>Raised after a call that failed, with its error.</summary>
    public event Action<string>? Failed;

    /// <summary>Asks the user to allow an action (the text says what); without it, actions that need approval are refused.</summary>
    public Func<string, CancellationToken, Task<bool>>? Approve { get; set; }

    public AiToolbox Without(Func<AiTool, bool> skip)
    {
        var copy = new AiToolbox(Tools.Where(t => !skip(t)).ToList()) { Approve = Approve };
        copy.Activity += line => Activity?.Invoke(line);
        copy.Failed += line => Failed?.Invoke(line);
        return copy;
    }

    public async Task<AiToolResult> RunAsync(string name, JsonObject? arguments, CancellationToken cancellation)
    {
        var tool = Tools.FirstOrDefault(t => t.Name == name);
        if (tool is null) return AiToolResult.Error($"There is no tool named {name}.");
        arguments ??= new JsonObject();
        string label;
        try { label = tool.Label?.Invoke(arguments) ?? name.Replace('_', ' '); }
        catch (Exception) { label = name.Replace('_', ' '); }
        if (tool.Approval is not null)
        {
            string question;
            try { question = tool.Approval(arguments); }
            catch (Exception) { question = label; }
            if (Approve is null || !await Approve(question, cancellation))
            {
                Failed?.Invoke($"{label}: not allowed");
                return AiToolResult.Error("The user did not allow this action. Do not try it again unless they ask for it.");
            }
        }
        Activity?.Invoke(label);
        try
        {
            var result = await tool.Run(arguments, cancellation);
            if (result.IsError) Failed?.Invoke($"{label}: {result.Text}");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Failed?.Invoke($"{label}: {ex.Message}");
            return AiToolResult.Error(ex.Message);
        }
    }

    /// <summary>A string argument, or null when it is missing or empty.</summary>
    public static string? Str(JsonObject args, string name) => args[name] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0 ? s : null,
        JsonValue v => v.ToJsonString(),
        _ => null,
    };

    public static int? Int(JsonObject args, string name)
    {
        if (args[name] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null;
    }

    public static bool? Bool(JsonObject args, string name)
    {
        if (args[name] is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        return v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) ? p : null;
    }
}

/// <summary>
/// Everything a provider needs for one turn beyond the messages: the tools, the project folder the user picked
/// (the AI reads it and may change files in it when edits are allowed) and, for installed AI apps that cannot be
/// handed C# tools, the local MCP endpoint that serves the same tools.
/// </summary>
public sealed record AiContext(AiToolbox Tools, string? Folder, bool AllowEdits, McpEndpoint? Mcp);

/// <summary>Where OverDesk's MCP server listens (127.0.0.1 only) and the secret every call must carry.</summary>
public sealed record McpEndpoint(string Url, string Token);
