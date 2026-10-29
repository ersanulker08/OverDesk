using Overdesk.Core.Plugins;

namespace Overdesk.Tool.DevKit;

/// <summary>Entry point named by <c>typeName</c> in overdesk.plugin.json.</summary>
public sealed class DevKitPlugin : IToolPlugin
{
    public object CreateView() => new DevKitView();
}
