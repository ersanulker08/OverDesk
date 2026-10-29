using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using Overdesk.Core.Settings;

namespace Overdesk.Core.Plugins;

/// <summary>Finds plugin manifests and keeps the list of tools shown in the right-hand panel.</summary>
public sealed class PluginRegistry
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public ObservableCollection<PluginManifest> Tools { get; } = new();

    /// <summary>Problems found while discovering plugins (shown to the user, never fatal).</summary>
    public List<string> Problems { get; } = new();

    /// <summary>Scan each root: every immediate sub-folder with an overdesk.plugin.json is a plugin.</summary>
    public void Discover(IEnumerable<string> roots)
    {
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(dir, PluginManifest.FileName);
                if (File.Exists(file)) TryAdd(file);
            }
        }
    }

    /// <summary>Register a tool that ships with Overdesk (shown first in the tools strip).</summary>
    public void AddBuiltIn(PluginManifest manifest)
    {
        if (Tools.Any(t => t.Id == manifest.Id)) return;
        Tools.Insert(0, manifest);
    }

    public void AddUserTools(IEnumerable<UserTool> userTools)
    {
        foreach (var t in userTools) Tools.Add(FromUserTool(t));
    }

    public PluginManifest AddUserTool(UserTool tool)
    {
        var manifest = FromUserTool(tool);
        Tools.Add(manifest);
        return manifest;
    }

    public void Remove(PluginManifest manifest) => Tools.Remove(manifest);

    public static PluginManifest FromUserTool(UserTool tool) => new()
    {
        Id = "user:" + tool.Path,
        Name = tool.Name,
        Run = PluginRunMode.Process,
        Entry = tool.Path,
        Directory = Path.GetDirectoryName(tool.Path) ?? "",
        IsUserAdded = true,
    };

    private void TryAdd(string manifestFile)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestFile), Json)
                           ?? throw new JsonException("empty manifest");
            manifest.Directory = Path.GetDirectoryName(manifestFile)!;
            if (manifest.Validate() is { } problem)
            {
                Problems.Add($"{manifestFile}: {problem}");
                return;
            }
            if (Tools.Any(t => t.Id == manifest.Id))
            {
                Problems.Add($"{manifestFile}: duplicate id \"{manifest.Id}\"");
                return;
            }
            Tools.Add(manifest);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Problems.Add($"{manifestFile}: {ex.Message}");
        }
    }

    /// <summary>Instantiate an in-process plugin from its manifest.</summary>
    public static IToolPlugin LoadInProcess(PluginManifest manifest)
    {
        if (manifest.BuiltIn is { } create) return create();
        var assemblyPath = Path.GetFullPath(Path.Combine(manifest.Directory, manifest.Assembly!));
        var assembly = Assembly.LoadFrom(assemblyPath);
        var type = assembly.GetType(manifest.TypeName!, throwOnError: true)!;
        return Activator.CreateInstance(type) as IToolPlugin
               ?? throw new InvalidOperationException($"{type.FullName} does not implement {nameof(IToolPlugin)}.");
    }
}
