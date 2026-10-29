namespace Overdesk.Core.Plugins;

/// <summary>
/// Something a plugin lets the AI do. Parameters is a JSON Schema object (as text); Run gets the arguments as JSON
/// and returns text for the model. Throw to report an error. Approval, when set, is the question the user must say yes
/// to before each call ("Convert 12 files?").
/// </summary>
public sealed record PluginAiTool(string Name, string Description, string Parameters, Func<string, CancellationToken, Task<string>> Run)
{
    public Func<string, string>? Approval { get; init; }

    /// <summary>A short line for the chat about one call ("Converting 3 files to WEBP"), from its arguments.</summary>
    public Func<string, string>? Label { get; init; }
}

/// <summary>
/// Optional for in-process tools: abilities the AI may use. The manifest must set "ai": true, so OverDesk loads the
/// plugin for the AI even before its panel is opened.
/// </summary>
public interface IAiToolSource
{
    IReadOnlyList<PluginAiTool> AiTools { get; }
}
