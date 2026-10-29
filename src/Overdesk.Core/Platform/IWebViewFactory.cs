namespace Overdesk.Core.Platform;

/// <summary>
/// A real browser view living inside Overdesk's own window (WebKit on macOS, WebView2 on Windows).
/// Unlike hosted programs it is a true child of Overdesk, so it never falls behind or needs mirroring.
/// </summary>
public interface IWebView : IDisposable
{
    /// <summary>Native handle for the UI toolkit's native-control host (NSView / HWND).</summary>
    IntPtr Handle { get; }
    string HandleDescriptor { get; }

    string? Title { get; }
    string? Url { get; }
    bool IsLoading { get; }

    /// <summary>0..1 while loading.</summary>
    double Progress { get; }

    void Navigate(string url);
    void Reload();
    void GoBack();
    void GoForward();

    /// <summary>
    /// Run JavaScript in the page and get its result as text (strings as they are, other values as JSON or their
    /// description); null for undefined. Must be called on the UI thread.
    /// </summary>
    Task<string?> ExecuteScriptAsync(string script);

    /// <summary>Called when the host area changes size (physical pixels). A no-op where the OS sizes the view itself.</summary>
    void Resize(int width, int height);

    /// <summary>Move the view under another native parent (Windows HWND); a no-op where the UI toolkit re-parents it.</summary>
    void AttachTo(IntPtr parentHandle);

    /// <summary>
    /// The page opened a new window (sign-in popups, "open in new window" links). The popup shares the page's session and
    /// keeps its link back to the opener, which sign-in flows need; the host shows it and disposes it when it closes.
    /// When nobody handles it the platform loads the link in this view instead.
    /// </summary>
    event Action<IWebView>? PopupRequested;

    /// <summary>The page asked to close its own window (window.close()), e.g. at the end of a sign-in popup.</summary>
    event Action? CloseRequested;
}

public interface IWebViewFactory
{
    bool IsSupported { get; }

    /// <param name="parentHandle">The native parent the view goes into (needed on Windows, ignored on macOS).</param>
    IWebView Create(IntPtr parentHandle, string url);
}

public sealed class NoWebViewFactory : IWebViewFactory
{
    public bool IsSupported => false;
    public IWebView Create(IntPtr parentHandle, string url) => throw new PlatformNotSupportedException("No web view on this system.");
}
