namespace Overdesk.Core.Platform;

/// <summary>
/// Per-OS layer that launches a program and embeds its window. The core only sees this interface.
/// </summary>
public interface IWindowEmbedder
{
    string PlatformName { get; }

    /// <summary>File patterns the "add program" picker should offer, e.g. "*.exe" or "*.app".</summary>
    IReadOnlyList<string> ProgramPatterns { get; }

    /// <summary>True when the OS permissions this layer needs have been granted.</summary>
    bool HasPermissions { get; }

    /// <summary>Human-readable explanation shown when permissions are missing; null when none needed.</summary>
    string? PermissionHelp { get; }

    /// <summary>Trigger the OS permission prompt / open the relevant settings page.</summary>
    void RequestPermissions();

    /// <summary>
    /// Adjust the Overdesk main window for hosting (call after it opened and after window-state changes).
    /// macOS: the green button zooms instead of moving the window into its own full-screen Space,
    /// where hosted programs could never follow.
    /// </summary>
    void PrepareOwnerWindow(IntPtr ownerHandle);

    /// <summary>
    /// True when hosted windows can be mirrored (see <see cref="IEmbeddedWindow.CaptureFrame"/>). Null when the
    /// platform never needs mirroring (Windows: real child windows).
    /// </summary>
    bool? CanMirror { get; }

    /// <summary>Ask the OS for the permission mirroring needs (macOS: Screen Recording).</summary>
    void RequestMirrorPermission();

    /// <param name="programPath">Executable, or an .app bundle on macOS.</param>
    /// <param name="ownerHandle">Native handle of the Overdesk main window (may be zero).</param>
    /// <exception cref="EmbeddingException">The program could not be launched or showed no window.</exception>
    Task<IEmbeddedWindow> LaunchAsync(string programPath, IntPtr ownerHandle, CancellationToken cancellation);
}

public sealed class EmbeddingException : Exception
{
    public EmbeddingException(string message) : base(message) { }
    public EmbeddingException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Used on platforms without an embedding layer so the app still starts.</summary>
public sealed class UnsupportedWindowEmbedder : IWindowEmbedder
{
    public string PlatformName => "Unsupported";
    public IReadOnlyList<string> ProgramPatterns => new[] { "*" };
    public bool HasPermissions => true;
    public string? PermissionHelp => null;
    public void RequestPermissions() { }
    public void PrepareOwnerWindow(IntPtr ownerHandle) { }
    public bool? CanMirror => null;
    public void RequestMirrorPermission() { }

    public Task<IEmbeddedWindow> LaunchAsync(string programPath, IntPtr ownerHandle, CancellationToken cancellation)
        => throw new EmbeddingException("Window embedding is not supported on this operating system.");
}
