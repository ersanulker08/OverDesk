using System.Runtime.Loader;
using Overdesk.Core.Plugins;

namespace Overdesk.Tests;

public class PluginLoadingTests
{
    private static void Manifest(TempFolder root, string folder, string json) => root.Write($"{folder}/{PluginManifest.FileName}", json);

    [Fact]
    public void Valid_manifests_are_found_and_broken_ones_are_reported()
    {
        using var root = new TempFolder();
        Manifest(root, "a", """
            // comments and trailing commas are fine
            { "id": "t.a", "name": "A", "run": "inProcess", "assembly": "A.dll", "typeName": "T.A", }
            """);
        Manifest(root, "b", """{ "id": "t.b", "name": "B", "run": "process", "entry": "bin/b" }""");
        Manifest(root, "dup", """{ "id": "t.b", "name": "Again", "run": "process", "entry": "x" }""");
        Manifest(root, "notype", """{ "id": "t.c", "name": "C", "run": "inProcess", "assembly": "C.dll" }""");
        Manifest(root, "noentry", """{ "id": "t.d", "name": "D", "run": "process" }""");
        Manifest(root, "garbage", "{ nope");

        var registry = new PluginRegistry();
        registry.Discover(new[] { root.Path, Path.Combine(root.Path, "missing") });

        Assert.Equal(new[] { "t.a", "t.b" }, registry.Tools.Select(t => t.Id));
        Assert.Equal(4, registry.Problems.Count);
        Assert.Contains(registry.Problems, p => p.Contains("duplicate id"));
    }

    [Fact]
    public void An_external_in_process_plugin_assembly_really_loads()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "HelloTool"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var plugin = Path.Combine(dir.FullName, "samples", "HelloTool", "plugin");
        var manifest = new PluginManifest
        {
            Id = "overdesk.samples.hello", Name = "Hello", Run = PluginRunMode.InProcess,
            Assembly = "HelloTool.dll", TypeName = "Overdesk.Samples.HelloTool.HelloPlugin", Directory = plugin,
        };

        var tool = PluginRegistry.LoadInProcess(manifest);

        // The plugin lives in its own load context but shares the host's contract assembly.
        Assert.IsAssignableFrom<IToolPlugin>(tool);
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(tool.GetType().Assembly));
        Assert.Same(tool.GetType().Assembly, PluginRegistry.LoadInProcess(manifest).GetType().Assembly);
    }
}
