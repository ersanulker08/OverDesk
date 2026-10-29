namespace Overdesk.Core.Plugins;

/// <summary>
/// A tool view that takes files dropped on the side panel (instead of OverDesk opening them as tabs).
/// DropHint is the line shown on the drop overlay ("Drop to convert").
/// </summary>
public interface IFileDropTarget
{
    string DropHint { get; }
    void FilesDropped(IReadOnlyList<string> paths);
}
