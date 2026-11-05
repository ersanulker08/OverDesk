namespace Overdesk.Core.Works;

/// <summary>Undo stack for structural row edits (delete, convert, move, duplicate, add).</summary>
public sealed class BlockHistory
{
    private const int Limit = 50;
    private readonly List<List<WorkBlock>> _stack = new();

    public bool CanUndo => _stack.Count > 0;

    public void Push(IEnumerable<WorkBlock> blocks)
    {
        _stack.Add(blocks.Select(b => b.Snapshot()).ToList());
        if (_stack.Count > Limit) _stack.RemoveAt(0);
    }

    /// <summary>The rows as they were before the last structural edit, or null when there is nothing to undo.</summary>
    public List<WorkBlock>? Pop()
    {
        if (_stack.Count == 0) return null;
        var last = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return last;
    }
}
