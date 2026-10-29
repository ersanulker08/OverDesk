namespace Overdesk.Core.Plugins;

/// <summary>
/// Contract for in-process (C#) tools. The core has no UI dependency, so the view is returned as
/// <c>object</c>; the app accepts an Avalonia <c>Control</c> here.
/// </summary>
public interface IToolPlugin
{
    object CreateView();
}
