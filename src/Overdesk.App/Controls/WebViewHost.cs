using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Overdesk.Core.Platform;

namespace Overdesk.App.Controls;

/// <summary>
/// Hosts one native web view inside the Avalonia layout. The view is created on first attach and kept alive
/// across detaches, so switching tabs never reloads a page; <see cref="DisposeView"/> ends it when the tab closes.
/// </summary>
public sealed class WebViewHost : NativeControlHost
{
    private readonly IWebViewFactory _factory;
    private readonly string _startUrl;

    public WebViewHost(IWebViewFactory factory, string url)
    {
        _factory = factory;
        _startUrl = url;
    }

    /// <summary>Host a view that already exists (a popup the page opened).</summary>
    public WebViewHost(IWebView existing)
    {
        _factory = null!;
        _startUrl = "";
        Attach(existing);
    }

    public IWebView? View { get; private set; }

    /// <summary>The page opened a popup window (forwarded from the view).</summary>
    public event Action<IWebView>? PopupRequested;

    /// <summary>The page closed its own window.</summary>
    public event Action? CloseRequested;

    private void Attach(IWebView view)
    {
        View = view;
        view.PopupRequested += popup => PopupRequested?.Invoke(popup);
        view.CloseRequested += () => CloseRequested?.Invoke();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (View is null) Attach(_factory.Create(parent.Handle, _startUrl));
        else View.AttachTo(parent.Handle);
        ResizeView();
        return new PlatformHandle(View!.Handle, View.HandleDescriptor);
    }

    // Keep the native view: it is disposed explicitly when its tab closes.
    protected override void DestroyNativeControlCore(IPlatformHandle control) { }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ResizeView();
    }

    private void ResizeView()
    {
        if (View is null) return;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        View.Resize((int)Math.Round(Bounds.Width * scale), (int)Math.Round(Bounds.Height * scale));
    }

    public void DisposeView()
    {
        View?.Dispose();
        View = null;
    }
}
