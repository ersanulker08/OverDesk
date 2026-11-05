using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace Overdesk.Core.Plugins;

/// <summary>
/// Gives each in-process plugin its own copy of its private dependencies, while the host's assemblies
/// (Overdesk.Core, Avalonia, the framework) stay shared so the plugin contract types are the same types.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly ConcurrentDictionary<string, PluginLoadContext> Contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly AssemblyDependencyResolver _resolver;

    private PluginLoadContext(string assemblyPath) : base(Path.GetFileNameWithoutExtension(assemblyPath), isCollectible: false)
        => _resolver = new AssemblyDependencyResolver(assemblyPath);

    public static PluginLoadContext For(string assemblyPath)
        => Contexts.GetOrAdd(Path.GetDirectoryName(assemblyPath) ?? "", _ => new PluginLoadContext(assemblyPath));

    protected override Assembly? Load(AssemblyName name)
    {
        var simple = name.Name ?? "";
        if (simple.StartsWith("Overdesk.Core", StringComparison.Ordinal) || simple.StartsWith("Avalonia", StringComparison.Ordinal)
            || simple.StartsWith("System.", StringComparison.Ordinal) || simple.StartsWith("Microsoft.", StringComparison.Ordinal)
            || Default.Assemblies.Any(a => a.GetName().Name == simple))
            return null;
        var path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
