using System.Net;
using System.Runtime.InteropServices;

namespace Overdesk.Platform.MacOS;

internal sealed unsafe partial class MacWebView
{
    /// <summary>Shown in place of a page that could not be reached; its link and Reload() both try the address again.</summary>
    private void ShowLoadError(string url, string reason)
    {
        _failedUrl = url;
        var address = WebUtility.HtmlEncode(url);
        var html = $$"""
            <!doctype html><meta charset="utf-8"><title>Page not loaded</title>
            <style>
              html { height: 100%; background: #101014; color: #e8e8ee; font: 15px -apple-system, sans-serif; }
              body { height: 100%; margin: 0; display: flex; align-items: center; justify-content: center; }
              main { max-width: 460px; padding: 24px; }
              h1 { font-size: 22px; font-weight: 600; margin: 0 0 8px; }
              p { color: #a0a0ac; line-height: 1.45; overflow-wrap: anywhere; }
              a { display: inline-block; margin-top: 10px; padding: 8px 16px; border-radius: 8px; background: #2a2a33; color: #e8e8ee; text-decoration: none; }
            </style>
            <main><h1>This page could not be loaded</h1><p>{{WebUtility.HtmlEncode(reason)}}</p><p>{{address}}</p><a href="{{address}}">Try again</a></main>
            """;
        var baseUrl = ObjC.Call(ObjC.Class("NSURL"), "URLWithString:", ObjC.NSString(url));
        ObjC.Call(_view, "loadHTMLString:baseURL:", ObjC.NSString(html), baseUrl);
    }

    /// <summary>A WKNavigationDelegate class registered at runtime: links for other apps, downloads and load errors.</summary>
    private static class NavigationDelegate
    {
        private const nint PolicyCancel = 0, PolicyAllow = 1, PolicyDownload = 2;
        private const int UrlErrorCancelled = -999;
        private const int FrameLoadInterrupted = 102; // what a response that became a download reports
        private const int PlugInWillHandleLoad = 204;

        private static readonly HashSet<string> PageSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "about", "blob", "data", "file" };

        // Safe for a page to trigger by itself; anything else asks first, as Safari does.
        private static readonly HashSet<string> OpenWithoutAsking = new(StringComparer.OrdinalIgnoreCase) { "mailto", "tel" };

        public static readonly IntPtr Instance = Create();

        private static IntPtr Create()
        {
            var cls = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), "OverdeskWebNavigationDelegate", 0);
            if (cls == IntPtr.Zero) cls = ObjC.Class("OverdeskWebNavigationDelegate"); // already registered
            else
            {
                delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> action = &DecideAction;
                ObjC.class_addMethod(cls, ObjC.Selector("webView:decidePolicyForNavigationAction:decisionHandler:"), (IntPtr)action, "v@:@@@?");
                delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> response = &DecideResponse;
                ObjC.class_addMethod(cls, ObjC.Selector("webView:decidePolicyForNavigationResponse:decisionHandler:"), (IntPtr)response, "v@:@@@?");
                delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> download = &BecameDownload;
                ObjC.class_addMethod(cls, ObjC.Selector("webView:navigationAction:didBecomeDownload:"), (IntPtr)download, "v@:@@@");
                ObjC.class_addMethod(cls, ObjC.Selector("webView:navigationResponse:didBecomeDownload:"), (IntPtr)download, "v@:@@@");
                delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> failed = &DidFailProvisional;
                ObjC.class_addMethod(cls, ObjC.Selector("webView:didFailProvisionalNavigation:withError:"), (IntPtr)failed, "v@:@@@");
                var protocol = ObjC.objc_getProtocol("WKNavigationDelegate");
                if (protocol != IntPtr.Zero) ObjC.class_addProtocol(cls, protocol);
                ObjC.objc_registerClassPair(cls);
            }
            return ObjC.Call(ObjC.Call(cls, "alloc"), "init");
        }

        [UnmanagedCallersOnly]
        private static void DecideAction(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr action, IntPtr decision)
        {
            var policy = PolicyAllow;
            try
            {
                var url = ObjC.Call(ObjC.Call(action, "request"), "URL");
                var scheme = ObjC.ReadNSString(ObjC.Call(url, "scheme"));
                if (scheme is not null && !PageSchemes.Contains(scheme))
                {
                    policy = PolicyCancel;
                    // A frame inside the page (ads try this) never gets to start another app.
                    var target = ObjC.Call(action, "targetFrame");
                    if (target == IntPtr.Zero || ObjC.CallBool(target, "isMainFrame")) OpenInOtherApp(webView, url, scheme);
                }
                else if (ObjC.RespondsTo(action, "shouldPerformDownload") && ObjC.CallBool(action, "shouldPerformDownload"))
                    policy = PolicyDownload;
            }
            catch (Exception) { /* never let an exception cross into Objective-C */ }
            ObjC.InvokeBlock(decision, policy);
        }

        private static void OpenInOtherApp(IntPtr webView, IntPtr url, string scheme)
        {
            var workspace = ObjC.Call(ObjC.Class("NSWorkspace"), "sharedWorkspace");
            var app = ObjC.Call(workspace, "URLForApplicationToOpenURL:", url);
            if (app == IntPtr.Zero) return;
            if (OpenWithoutAsking.Contains(scheme))
            {
                ObjC.CallBool(workspace, "openURL:", url);
                return;
            }

            var name = Path.GetFileNameWithoutExtension(ObjC.ReadNSString(ObjC.Call(app, "path")));
            var kept = ObjC.Call(url, "retain");
            ShowAlert(webView, $"Open this link in {name}?", ObjC.ReadNSString(ObjC.Call(url, "absoluteString")), new[] { "Open", "Cancel" }, IntPtr.Zero, button =>
            {
                if (button == 0) ObjC.CallBool(workspace, "openURL:", kept);
                ObjC.Call(kept, "release");
            });
        }

        [UnmanagedCallersOnly]
        private static void DecideResponse(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr navigationResponse, IntPtr decision)
        {
            var policy = PolicyAllow;
            try
            {
                if (!ObjC.CallBool(navigationResponse, "canShowMIMEType") || IsAttachment(ObjC.Call(navigationResponse, "response")))
                    policy = PolicyDownload;
            }
            catch (Exception) { }
            ObjC.InvokeBlock(decision, policy);
        }

        private static bool IsAttachment(IntPtr response)
        {
            if (!ObjC.CallBool(response, "isKindOfClass:", ObjC.Class("NSHTTPURLResponse"))) return false;
            var header = ObjC.ReadNSString(ObjC.Call(response, "valueForHTTPHeaderField:", ObjC.NSString("Content-Disposition")));
            return header?.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase) == true;
        }

        [UnmanagedCallersOnly]
        private static void BecameDownload(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr origin, IntPtr download)
        {
            try { DownloadDelegate.Track(download, Views.GetValueOrDefault(webView)); }
            catch (Exception) { }
        }

        [UnmanagedCallersOnly]
        private static void DidFailProvisional(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr navigation, IntPtr error)
        {
            try
            {
                var domain = ObjC.ReadNSString(ObjC.Call(error, "domain"));
                var code = ObjC.CallInt(error, "code");
                if ((domain == "NSURLErrorDomain" && code == UrlErrorCancelled) ||
                    (domain == "WebKitErrorDomain" && code is FrameLoadInterrupted or PlugInWillHandleLoad))
                    return;

                var failing = ObjC.Call(ObjC.Call(error, "userInfo"), "objectForKey:", ObjC.NSString("NSErrorFailingURLKey"));
                if (Views.TryGetValue(webView, out var view) && ObjC.ReadNSString(ObjC.Call(failing, "absoluteString")) is { Length: > 0 } url)
                    view.ShowLoadError(url, ObjC.ReadNSString(ObjC.Call(error, "localizedDescription")) ?? "The page did not answer.");
            }
            catch (Exception) { }
        }
    }

    /// <summary>A WKDownloadDelegate class registered at runtime: everything goes to ~/Downloads under a free name.</summary>
    private static class DownloadDelegate
    {
        private sealed class Transfer(MacWebView? owner)
        {
            public MacWebView? Owner { get; } = owner;
            public string? Path { get; set; }
            public string Name => System.IO.Path.GetFileName(Path) ?? "file";
        }

        private static readonly Dictionary<IntPtr, Transfer> Transfers = new();
        private static readonly IntPtr Instance = Create();

        private static IntPtr Create()
        {
            var cls = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), "OverdeskWebDownloadDelegate", 0);
            if (cls == IntPtr.Zero) cls = ObjC.Class("OverdeskWebDownloadDelegate"); // already registered
            else
            {
                delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> destination = &DecideDestination;
                ObjC.class_addMethod(cls, ObjC.Selector("download:decideDestinationUsingResponse:suggestedFilename:completionHandler:"),
                    (IntPtr)destination, "v@:@@@@?");
                delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> finished = &DidFinish;
                ObjC.class_addMethod(cls, ObjC.Selector("downloadDidFinish:"), (IntPtr)finished, "v@:@");
                delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> failed = &DidFail;
                ObjC.class_addMethod(cls, ObjC.Selector("download:didFailWithError:resumeData:"), (IntPtr)failed, "v@:@@@");
                var protocol = ObjC.objc_getProtocol("WKDownloadDelegate");
                if (protocol != IntPtr.Zero) ObjC.class_addProtocol(cls, protocol);
                ObjC.objc_registerClassPair(cls);
            }
            return ObjC.Call(ObjC.Call(cls, "alloc"), "init");
        }

        public static void Track(IntPtr download, MacWebView? owner)
        {
            ObjC.Call(download, "retain");
            Transfers[download] = new Transfer(owner);
            ObjC.Call(download, "setDelegate:", Instance);
        }

        [UnmanagedCallersOnly]
        private static void DecideDestination(IntPtr self, IntPtr cmd, IntPtr download, IntPtr response, IntPtr suggestedName, IntPtr completion)
        {
            var destination = IntPtr.Zero;
            try
            {
                var path = FreePath(ObjC.ReadNSString(suggestedName));
                if (Transfers.TryGetValue(download, out var transfer))
                {
                    transfer.Path = path;
                    transfer.Owner?.DownloadStatus?.Invoke($"Downloading {transfer.Name}");
                }
                destination = ObjC.Call(ObjC.Class("NSURL"), "fileURLWithPath:", ObjC.NSString(path));
            }
            catch (Exception) { /* a nil destination cancels the download */ }
            ObjC.InvokeBlock(completion, destination);
        }

        /// <summary>WebKit refuses to write over a file, and two downloads of one name may be under way at once.</summary>
        private static string FreePath(string? suggested)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(folder);
            var name = Path.GetFileName(suggested);
            if (string.IsNullOrWhiteSpace(name)) name = "download";

            var path = Path.Combine(folder, name);
            for (var i = 2; File.Exists(path) || Directory.Exists(path) || Transfers.Values.Any(t => t.Path == path); i++)
                path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
            return path;
        }

        [UnmanagedCallersOnly]
        private static void DidFinish(IntPtr self, IntPtr cmd, IntPtr download)
        {
            try { Done(download, t => $"Saved {t.Name} to Downloads"); }
            catch (Exception) { }
        }

        [UnmanagedCallersOnly]
        private static void DidFail(IntPtr self, IntPtr cmd, IntPtr download, IntPtr error, IntPtr resumeData)
        {
            try { Done(download, t => $"Download failed: {t.Name}"); }
            catch (Exception) { }
        }

        private static void Done(IntPtr download, Func<Transfer, string> status)
        {
            if (!Transfers.Remove(download, out var transfer)) return;
            ObjC.Call(download, "release");
            transfer.Owner?.DownloadStatus?.Invoke(status(transfer));
        }
    }
}
