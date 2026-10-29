using System.Runtime.InteropServices;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.MacOS;

/// <summary>WKWebView created through the Objective-C runtime. Cookies and logins persist like in Safari.</summary>
public sealed class MacWebViewFactory : IWebViewFactory
{
    private const string WebKit = "/System/Library/Frameworks/WebKit.framework/WebKit";
    private static readonly Lazy<bool> Loaded = new(() => NativeLibrary.TryLoad(WebKit, out _));

    public bool IsSupported => Loaded.Value;

    public IWebView Create(IntPtr parentHandle, string url)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("WebKit is not available.");
        var view = new MacWebView(MacWebView.CreateView(IntPtr.Zero));
        view.Navigate(url);
        return view;
    }
}

internal sealed unsafe class MacWebView : IWebView
{
    // A current Safari user agent: some sign-in pages refuse "unknown" embedded browsers.
    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Safari/605.1.15";

    /// <summary>Native view → wrapper, so the delegate callbacks (static) find the right object.</summary>
    private static readonly Dictionary<IntPtr, MacWebView> Views = new();

    private IntPtr _view;

    public MacWebView(IntPtr view)
    {
        _view = view;
        Views[view] = this;
    }

    /// <summary>
    /// A configured WKWebView. Pass the configuration WebKit hands to a popup (so it shares the opener's session and
    /// window.opener link), or zero for a fresh one.
    /// </summary>
    internal static IntPtr CreateView(IntPtr configuration)
    {
        var ownConfig = configuration == IntPtr.Zero;
        var config = ownConfig ? ObjC.Call(ObjC.Call(ObjC.Class("WKWebViewConfiguration"), "alloc"), "init") : configuration;
        // Sign-in flows open their popup from script, sometimes without a click: allow it.
        var preferences = ObjC.Call(config, "preferences");
        if (preferences != IntPtr.Zero) ObjC.CallBoolArg(preferences, "setJavaScriptCanOpenWindowsAutomatically:", true);

        var frame = new CGRect { Width = 800, Height = 600 };
        var view = ObjC.CallRectArg(ObjC.Call(ObjC.Class("WKWebView"), "alloc"), "initWithFrame:configuration:", frame, config);
        if (ownConfig) ObjC.Call(config, "release");

        ObjC.Call(view, "setCustomUserAgent:", ObjC.NSString(UserAgent));
        ObjC.CallBoolArg(view, "setAllowsBackForwardNavigationGestures:", true);
        ObjC.CallBoolArg(view, "setAllowsMagnification:", true);
        if (ObjC.RespondsTo(view, "setInspectable:")) ObjC.CallBoolArg(view, "setInspectable:", true);
        // Rounded corners so the page sits nicely inside Overdesk's glowing stage frame.
        ObjC.CallBoolArg(view, "setWantsLayer:", true);
        var layer = ObjC.Call(view, "layer");
        if (layer != IntPtr.Zero)
        {
            ObjC.CallDoubleArg(layer, "setCornerRadius:", 12);
            ObjC.CallBoolArg(layer, "setMasksToBounds:", true);
        }
        ObjC.Call(view, "setUIDelegate:", UIDelegate.Instance);
        return view;
    }

    public event Action<IWebView>? PopupRequested;
    public event Action? CloseRequested;

    public IntPtr Handle => _view;
    public string HandleDescriptor => "NSView";

    public string? Title => ReadString("title");
    public string? Url => _view == IntPtr.Zero ? null : ObjC.ReadNSString(ObjC.Call(ObjC.Call(_view, "URL"), "absoluteString"));
    public bool IsLoading => _view != IntPtr.Zero && ObjC.CallBool(_view, "isLoading");
    public double Progress => _view == IntPtr.Zero ? 0 : ObjC.CallDouble(_view, "estimatedProgress");

    private string? ReadString(string selector)
        => _view == IntPtr.Zero ? null : ObjC.ReadNSString(ObjC.Call(_view, selector));

    public Task<string?> ExecuteScriptAsync(string script)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_view == IntPtr.Zero)
        {
            done.SetException(new ObjectDisposedException(nameof(MacWebView)));
            return done.Task;
        }
        var block = ObjC.CompletionBlock((result, error) =>
        {
            if (error != IntPtr.Zero)
            {
                done.TrySetException(new InvalidOperationException(ObjC.ReadNSString(ObjC.Call(error, "localizedDescription")) ?? "The script failed."));
                return;
            }
            if (result == IntPtr.Zero) { done.TrySetResult(null); return; }
            // Strings come back as NSString; numbers, arrays and objects are described.
            var isString = ObjC.CallBool(result, "isKindOfClass:", ObjC.Class("NSString"));
            done.TrySetResult(ObjC.ReadNSString(isString ? result : ObjC.Call(result, "description")));
        });
        ObjC.Call(_view, "evaluateJavaScript:completionHandler:", ObjC.NSString(script), block);
        return done.Task;
    }

    public void Navigate(string url)
    {
        if (_view == IntPtr.Zero) return;
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var nsUrl = ObjC.Call(ObjC.Class("NSURL"), "URLWithString:", ObjC.NSString(url));
            if (nsUrl == IntPtr.Zero) return;
            var request = ObjC.Call(ObjC.Class("NSURLRequest"), "requestWithURL:", nsUrl);
            ObjC.Call(_view, "loadRequest:", request);
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    public void Reload() { if (_view != IntPtr.Zero) ObjC.Call(_view, "reload"); }
    public void GoBack() { if (_view != IntPtr.Zero) ObjC.Call(_view, "goBack"); }
    public void GoForward() { if (_view != IntPtr.Zero) ObjC.Call(_view, "goForward"); }
    public void Resize(int width, int height) { } // the native-control host sets the NSView frame
    public void AttachTo(IntPtr parentHandle) { } // the native-control host adds the NSView to its parent

    public void Dispose()
    {
        if (_view == IntPtr.Zero) return;
        Views.Remove(_view);
        ObjC.Call(_view, "stopLoading");
        ObjC.Call(_view, "removeFromSuperview");
        ObjC.Call(_view, "release");
        _view = IntPtr.Zero;
    }

    /// <summary>A WKUIDelegate class registered at runtime: real popups (sign-in windows) and window.close().</summary>
    private static class UIDelegate
    {
        public static readonly IntPtr Instance = Create();

        private static IntPtr Create()
        {
            var cls = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), "OverdeskWebUIDelegate2", 0);
            if (cls == IntPtr.Zero) cls = ObjC.Class("OverdeskWebUIDelegate2"); // already registered
            else
            {
                delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> create = &CreateWebView;
                ObjC.class_addMethod(cls, ObjC.Selector("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:"),
                    (IntPtr)create, "@@:@@@@");
                delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> close = &DidClose;
                ObjC.class_addMethod(cls, ObjC.Selector("webViewDidClose:"), (IntPtr)close, "v@:@");
                var protocol = ObjC.objc_getProtocol("WKUIDelegate");
                if (protocol != IntPtr.Zero) ObjC.class_addProtocol(cls, protocol);
                ObjC.objc_registerClassPair(cls);
            }
            return ObjC.Call(ObjC.Call(cls, "alloc"), "init");
        }

        [UnmanagedCallersOnly]
        private static IntPtr CreateWebView(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr configuration, IntPtr action, IntPtr features)
        {
            try
            {
                if (Views.TryGetValue(webView, out var opener) && opener.PopupRequested is { } handler)
                {
                    // WebKit loads the popup's first request itself into the view we return.
                    var popup = new MacWebView(CreateView(configuration));
                    handler(popup);
                    return popup.Handle;
                }
                // Nobody shows popups here: open the link in the same view instead.
                var request = ObjC.Call(action, "request");
                if (request != IntPtr.Zero) ObjC.Call(webView, "loadRequest:", request);
            }
            catch (Exception) { /* never let an exception cross into Objective-C */ }
            return IntPtr.Zero;
        }

        [UnmanagedCallersOnly]
        private static void DidClose(IntPtr self, IntPtr cmd, IntPtr webView)
        {
            try { if (Views.TryGetValue(webView, out var view)) view.CloseRequested?.Invoke(); }
            catch (Exception) { }
        }
    }
}
