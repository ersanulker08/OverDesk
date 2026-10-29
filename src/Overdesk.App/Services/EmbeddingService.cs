using Overdesk.Core.Platform;

namespace Overdesk.App.Services;

/// <summary>
/// Tracks every embedded program by key (a tab id or a tool id) and remembers what was last sent to
/// each, so the layout timer only talks to the OS when something actually changed.
/// All members must be called on the UI thread, except that <see cref="Exited"/> may be raised on any thread.
/// </summary>
internal sealed class EmbeddingService : IDisposable
{
    private sealed class Entry
    {
        public required IEmbeddedWindow Window { get; init; }
        public HostBounds? Bounds { get; set; }
        public bool? Visible { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new();

    public EmbeddingService(IWindowEmbedder embedder) => Embedder = embedder;

    public IWindowEmbedder Embedder { get; }

    /// <summary>Raised when a program exits by itself. Arguments: key, the window that exited.</summary>
    public event Action<string, IEmbeddedWindow>? Exited;

    public bool Has(string key) => _entries.ContainsKey(key);

    /// <summary>True when <paramref name="window"/> is still the live window registered under <paramref name="key"/>.</summary>
    public bool IsCurrent(string key, IEmbeddedWindow window)
        => _entries.TryGetValue(key, out var e) && ReferenceEquals(e.Window, window);

    public async Task StartAsync(string key, string programPath, IntPtr owner, CancellationToken cancellation)
    {
        Stop(key);
        var window = await Embedder.LaunchAsync(programPath, owner, cancellation);
        if (cancellation.IsCancellationRequested)
        {
            window.Dispose();
            cancellation.ThrowIfCancellationRequested();
        }
        _entries[key] = new Entry { Window = window };
        window.Exited += (_, _) => Exited?.Invoke(key, window);
    }

    /// <param name="force">Send the bounds even if unchanged (re-glues a window the user dragged away).</param>
    public void Layout(string key, HostBounds bounds, bool force = false)
    {
        if (!_entries.TryGetValue(key, out var e) || (!force && e.Bounds == bounds)) return;
        e.Bounds = bounds;
        e.Window.SetBounds(bounds);
    }

    public void SetVisible(string key, bool visible)
    {
        if (!_entries.TryGetValue(key, out var e) || e.Visible == visible) return;
        e.Visible = visible;
        e.Window.SetVisible(visible);
        if (visible && e.Bounds is { } b) e.Window.SetBounds(b);
    }

    /// <summary>Keys of shown programs that still run but whose window has disappeared.</summary>
    public IReadOnlyList<string> FindLostWindows()
        => _entries.Where(e => e.Value.Visible == true && e.Value.Window.IsAlive && !e.Value.Window.HasWindow)
            .Select(e => e.Key).ToList();

    /// <summary>What the user did to the window by hand since the last call (drag, resize, full screen), if anything.</summary>
    public UserWindowChange? TakeUserChange(string key)
        => _entries.TryGetValue(key, out var e) && e.Visible == true ? e.Window.TakeUserChange() : null;

    public IWindowFrame? CaptureFrame(string key)
        => _entries.TryGetValue(key, out var e) ? e.Window.CaptureFrame() : null;

    public Task FocusAndClickAsync(string key, double x, double y, bool right)
        => _entries.TryGetValue(key, out var e) ? e.Window.FocusAndClickAsync(x, y, right) : Task.CompletedTask;

    public void Focus(string key)
    {
        if (_entries.TryGetValue(key, out var e)) e.Window.Focus();
    }

    /// <summary>Close the program behind <paramref name="key"/> (no <see cref="Exited"/> event is raised for it).</summary>
    public void Stop(string key)
    {
        if (!_entries.Remove(key, out var e)) return;
        e.Window.Dispose();
    }

    public void Dispose()
    {
        foreach (var key in _entries.Keys.ToList()) Stop(key);
    }
}
