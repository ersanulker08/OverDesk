using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Overdesk.App.Services;

/// <summary>Remembers the last few texts copied anywhere on the machine (memory only, never saved to disk).</summary>
internal sealed class ClipboardHistory
{
    public const int MaxItems = 12;
    private const int MaxLength = 20_000;

    private readonly TopLevel _top;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _last;
    private bool _reading;

    public ClipboardHistory(TopLevel top)
    {
        _top = top;
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
    }

    public event Action? Changed;

    public List<string> Items { get; } = new();

    private async Task PollAsync()
    {
        if (_reading || _top.Clipboard is not { } clipboard) return;
        _reading = true;
        try
        {
            var text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text) || text == _last || text.Length > MaxLength) return;
            _last = text;
            Items.RemoveAll(i => i == text);
            Items.Insert(0, text);
            if (Items.Count > MaxItems) Items.RemoveRange(MaxItems, Items.Count - MaxItems);
            Changed?.Invoke();
        }
        catch (Exception) { /* clipboard busy or holding non-text data */ }
        finally { _reading = false; }
    }

    public async Task CopyAsync(string text)
    {
        if (_top.Clipboard is not { } clipboard) return;
        _last = text; // do not re-add it as a new entry
        Items.RemoveAll(i => i == text);
        Items.Insert(0, text);
        await clipboard.SetTextAsync(text);
        Changed?.Invoke();
    }

    public void Clear()
    {
        Items.Clear();
        Changed?.Invoke();
    }
}
