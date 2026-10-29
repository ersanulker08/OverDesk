using System.Text.Json.Serialization;

namespace Overdesk.Core.Plugins;

public enum PluginRunMode
{
    /// <summary>Loaded as a .NET assembly into the host (C# plugins).</summary>
    InProcess,

    /// <summary>Started as a separate program and embedded; cannot crash the host, any language works.</summary>
    Process,
}

/// <summary>
/// Contents of an <c>overdesk.plugin.json</c> file. Third parties use exactly the same format.
/// <code>
/// { "id": "acme.converter", "name": "Converter", "version": "1.0.0",
///   "run": "process", "entry": "bin/converter" }
/// </code>
/// </summary>
public sealed class PluginManifest
{
    public const string FileName = "overdesk.plugin.json";

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string? Description { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PluginRunMode Run { get; set; } = PluginRunMode.Process;

    /// <summary>Process mode: program to launch (relative to the manifest folder, or absolute).</summary>
    public string? Entry { get; set; }

    /// <summary>InProcess mode: assembly file (relative to the manifest folder).</summary>
    public string? Assembly { get; set; }

    /// <summary>InProcess mode: class implementing <see cref="IToolPlugin"/>.</summary>
    public string? TypeName { get; set; }

    /// <summary>Optional icon: a built-in icon name (e.g. "IconSparkle"); otherwise the tool's first letter is shown.</summary>
    public string? Icon { get; set; }

    /// <summary>Tools that ship inside Overdesk itself: created directly instead of loading an assembly.</summary>
    [JsonIgnore]
    public Func<IToolPlugin>? BuiltIn { get; set; }

    /// <summary>Folder the manifest was read from. Set by the loader, never serialized.</summary>
    [JsonIgnore]
    public string Directory { get; set; } = "";

    /// <summary>True for tools the user added themselves (can be removed from the panel).</summary>
    [JsonIgnore]
    public bool IsUserAdded { get; set; }

    /// <summary>Absolute path of <see cref="Entry"/>.</summary>
    [JsonIgnore]
    public string? ResolvedEntry => Entry is null ? null : Path.GetFullPath(Path.Combine(Directory, Entry));

    /// <summary>Returns a description of the first problem, or null when the manifest is usable.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) return "missing \"id\"";
        if (string.IsNullOrWhiteSpace(Name)) return "missing \"name\"";
        return Run switch
        {
            PluginRunMode.Process when string.IsNullOrWhiteSpace(Entry) => "process plugins need \"entry\"",
            PluginRunMode.InProcess when BuiltIn is null && (string.IsNullOrWhiteSpace(Assembly) || string.IsNullOrWhiteSpace(TypeName))
                => "in-process plugins need \"assembly\" and \"typeName\"",
            _ => null,
        };
    }
}
