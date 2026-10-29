namespace Overdesk.Core.Platform;

/// <summary>A running external program whose window is shown inside an Overdesk region.</summary>
public interface IEmbeddedWindow : IDisposable
{
    bool IsAlive { get; }

    /// <summary>False when the program still runs but its window is gone (for example the user closed it).</summary>
    bool HasWindow { get; }

    /// <summary>Raised (possibly on a background thread) when the external program exits.</summary>
    event EventHandler? Exited;

    /// <summary>
    /// What the user did to the hosted window since the last call: dragged it, resized it or asked for full screen.
    /// Null when nothing happened, or where the window cannot be handled by the user directly (Windows child windows).
    /// Deltas are in the same desktop units as <see cref="HostBounds.ScreenX"/>.
    /// </summary>
    UserWindowChange? TakeUserChange();

    /// <summary>Move/resize the hosted window to the region.</summary>
    void SetBounds(HostBounds bounds);

    void SetVisible(bool visible);

    /// <summary>Give keyboard focus / bring the hosted window to the front.</summary>
    void Focus();

    /// <summary>
    /// A still image of the hosted window as it looks now, even while other windows cover it. Used to "mirror" the
    /// program inside Overdesk while Overdesk itself is in front. Null where not needed or not permitted.
    /// </summary>
    IWindowFrame? CaptureFrame();

    /// <summary>
    /// Bring the window to the front and replay a click at a screen point (desktop units, see
    /// <see cref="HostBounds.ScreenX"/>), so a click on the mirror reaches the real program.
    /// </summary>
    Task FocusAndClickAsync(double screenX, double screenY, bool rightButton);

    /// <summary>Ask the program to close; kill it if it does not.</summary>
    void Close();
}

/// <summary>A change the user made to a hosted window by hand (see <see cref="IEmbeddedWindow.TakeUserChange"/>).</summary>
public readonly record struct UserWindowChange(double DeltaX, double DeltaY, double DeltaWidth, double DeltaHeight, bool WantsFullScreen);

/// <summary>One captured frame of a hosted window; copy it out, then dispose it.</summary>
public interface IWindowFrame : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Draw the frame into a 32-bit BGRA premultiplied buffer of Width x Height pixels.</summary>
    void CopyTo(IntPtr buffer, int stride);
}
