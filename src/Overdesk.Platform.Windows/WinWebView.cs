using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.Windows;

/// <summary>WebView2 (Edge) inside a child window that Overdesk's native-control host places for us.</summary>
public sealed class WinWebViewFactory : IWebViewFactory
{
    private static Task<CoreWebView2Environment>? _environment;

    public bool IsSupported
    {
        get
        {
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString() is { Length: > 0 }; }
            catch (Exception) { return false; }
        }
    }

    public IWebView Create(IntPtr parentHandle, string url)
    {
        // One shared profile for all web tabs, kept next to Overdesk's settings.
        _environment ??= CoreWebView2Environment.CreateAsync(null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Overdesk", "WebView2"));
        return new WinWebView(parentHandle, url, _environment);
    }
}

internal sealed class WinWebView : IWebView
{
    private const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipChildren = 0x02000000;

    private readonly IntPtr _hwnd;
    private readonly Task<CoreWebView2Environment> _environment;
    private readonly TaskCompletionSource<CoreWebView2?> _ready = new();
    private CoreWebView2Controller? _controller;
    private string? _pendingUrl;
    private (int W, int H) _size = (800, 600);

    /// <param name="url">The first page, or null for a popup whose content WebView2 supplies itself.</param>
    public WinWebView(IntPtr parent, string? url, Task<CoreWebView2Environment> environment)
    {
        _pendingUrl = url;
        _environment = environment;
        _hwnd = CreateWindowEx(0, "Static", "", WsChild | WsVisible | WsClipChildren, 0, 0, 800, 600, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _ = InitializeAsync();
    }

    public event Action<IWebView>? PopupRequested;
    public event Action? CloseRequested;

    /// <summary>The engine once it is ready (null when WebView2 is missing).</summary>
    internal Task<CoreWebView2?> Ready => _ready.Task;

    private async Task InitializeAsync()
    {
        try
        {
            var env = await _environment;
            _controller = await env.CreateCoreWebView2ControllerAsync(_hwnd);
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, _size.W, _size.H);
            var core = _controller.CoreWebView2;
            core.NewWindowRequested += OnNewWindow;
            core.WindowCloseRequested += (_, _) => CloseRequested?.Invoke();
            _ready.TrySetResult(core);
            if (_pendingUrl is not null) core.Navigate(_pendingUrl);
        }
        catch (Exception)
        {
            _controller = null; // no WebView2 runtime: the tab stays empty
            _ready.TrySetResult(null);
        }
    }

    /// <summary>
    /// A real popup (sign-in windows need their window.opener link): a second WebView2 handed back to the page.
    /// Without a host to show it, the link opens in this view.
    /// </summary>
    private async void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (PopupRequested is not { } handler || sender is not CoreWebView2 core)
        {
            e.Handled = true;
            (sender as CoreWebView2)?.Navigate(e.Uri);
            return;
        }
        using var deferral = e.GetDeferral();
        var popup = new WinWebView(GetParent(_hwnd), null, _environment);
        handler(popup);
        if (await popup.Ready is { } popupCore) e.NewWindow = popupCore;
        else
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        }
    }

    public IntPtr Handle => _hwnd;
    public string HandleDescriptor => "HWND";

    public string? Title => _controller?.CoreWebView2?.DocumentTitle;
    public string? Url => _controller?.CoreWebView2?.Source ?? _pendingUrl;
    public bool IsLoading { get; private set; }
    public double Progress => 0;

    public void Navigate(string url)
    {
        // Before the engine is ready the address waits; it is loaded as soon as it is.
        if (_controller?.CoreWebView2 is { } core) core.Navigate(url);
        else _pendingUrl = url;
    }

    /// <summary>WebView2 answers with JSON; a JSON string is unwrapped to its text.</summary>
    public async Task<string?> ExecuteScriptAsync(string script)
    {
        if (await Ready is not { } core) throw new InvalidOperationException("The web view is not ready.");
        var json = await core.ExecuteScriptAsync(script);
        if (json is null or "null" or "undefined") return null;
        return json.StartsWith('"') ? System.Text.Json.JsonSerializer.Deserialize<string>(json) : json;
    }

    public void Reload() => _controller?.CoreWebView2?.Reload();
    public void GoBack() { if (_controller?.CoreWebView2 is { CanGoBack: true } c) c.GoBack(); }
    public void GoForward() { if (_controller?.CoreWebView2 is { CanGoForward: true } c) c.GoForward(); }

    public void AttachTo(IntPtr parentHandle)
    {
        if (parentHandle != IntPtr.Zero) SetParent(_hwnd, parentHandle);
    }

    public void Resize(int width, int height)
    {
        _size = (Math.Max(width, 1), Math.Max(height, 1));
        MoveWindow(_hwnd, 0, 0, _size.W, _size.H, true);
        if (_controller is not null) _controller.Bounds = new System.Drawing.Rectangle(0, 0, _size.W, _size.H);
    }

    public void Dispose()
    {
        _controller?.Close();
        _controller = null;
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
}
