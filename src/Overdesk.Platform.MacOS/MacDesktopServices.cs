using System.Diagnostics;
using System.Runtime.InteropServices;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.MacOS;

/// <summary>Notifications through the notification center (bundled) or AppleScript, screenshots with screencapture, app badges read from the Dock.</summary>
public sealed class MacDesktopServices : IDesktopServices
{
    private const string UserNotifications = "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";
    private const nuint AuthorizationBadgeSoundAlert = 1 | 2 | 4;

    private static readonly IntPtr AttrChildren = Cf("AXChildren");
    private static readonly IntPtr AttrTitle = Cf("AXTitle");
    private static readonly IntPtr AttrStatusLabel = Cf("AXStatusLabel");

    // UNUserNotificationCenter throws (and kills the process) without a bundle, which is how `dotnet run` runs.
    private static readonly Lazy<bool> Bundled = new(() =>
        ObjC.ReadNSString(ObjC.Call(ObjC.Call(ObjC.Class("NSBundle"), "mainBundle"), "bundleIdentifier")) is { Length: > 0 });

    private static bool _authorizationRequested;

    private static IntPtr Cf(string s) => Native.CFStringCreateWithCString(IntPtr.Zero, s, Native.CFStringEncodingUTF8);

    public bool Notify(string title, string text)
        => (Bundled.Value && NotifyThroughCenter(title, text)) || NotifyThroughScript(title, text);

    /// <summary>The app's own notifications (name, icon and switch in System Settings). The first call asks the user.</summary>
    private static bool NotifyThroughCenter(string title, string text)
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            NativeLibrary.Load(UserNotifications);
            var center = ObjC.Call(ObjC.Class("UNUserNotificationCenter"), "currentNotificationCenter");
            if (center == IntPtr.Zero) return false;
            if (_authorizationRequested)
            {
                Post(center, title, text);
                return true;
            }
            _authorizationRequested = true;
            // Anything posted before the answer is dropped, so the first notification waits for it.
            var block = ObjC.CompletionBlock((granted, _) =>
            {
                if (granted == IntPtr.Zero) return;
                var inner = ObjC.objc_autoreleasePoolPush();
                try { Post(center, title, text); }
                catch (Exception) { }
                finally { ObjC.objc_autoreleasePoolPop(inner); }
            });
            ObjC.Call(center, "requestAuthorizationWithOptions:completionHandler:", AuthorizationBadgeSoundAlert, block);
            ObjC.ReleaseBlock(block);
            return true;
        }
        catch (Exception) { return false; }
        finally { ObjC.objc_autoreleasePoolPop(pool); }
    }

    private static void Post(IntPtr center, string title, string text)
    {
        var content = ObjC.Call(ObjC.Call(ObjC.Class("UNMutableNotificationContent"), "alloc"), "init");
        ObjC.Call(content, "setTitle:", ObjC.NSString(title));
        ObjC.Call(content, "setBody:", ObjC.NSString(text));
        var request = ObjC.Call(ObjC.Class("UNNotificationRequest"), "requestWithIdentifier:content:trigger:",
            ObjC.NSString(Guid.NewGuid().ToString("N")), content, IntPtr.Zero);
        var done = ObjC.CompletionBlock((_, _) => { });
        ObjC.Call(center, "addNotificationRequest:withCompletionHandler:", request, done);
        ObjC.ReleaseBlock(done);
        ObjC.Call(content, "release");
    }

    /// <summary>Unbundled (dotnet run): macOS shows these as Script Editor's.</summary>
    private static bool NotifyThroughScript(string title, string text)
    {
        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/osascript")
            {
                ArgumentList = { "-e", $"display notification {Quote(text)} with title {Quote(title)}" },
                UseShellExecute = false,
            });
            return p is not null;
        }
        catch (Exception) { return false; }
    }

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendBytes(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);

    /// <summary>[NSApp setApplicationIconImage:]: the Dock tile and the Cmd+Tab switcher use it right away.</summary>
    public void SetAppIcon(byte[] png)
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        var pinned = GCHandle.Alloc(png, GCHandleType.Pinned);
        try
        {
            var data = SendBytes(ObjC.Class("NSData"), ObjC.Selector("dataWithBytes:length:"), pinned.AddrOfPinnedObject(), (nuint)png.Length);
            var image = ObjC.Call(ObjC.Call(ObjC.Class("NSImage"), "alloc"), "initWithData:", data);
            if (image == IntPtr.Zero) return;
            ObjC.Call(ObjC.Call(ObjC.Class("NSApplication"), "sharedApplication"), "setApplicationIconImage:", image);
            ObjC.Call(image, "release");
        }
        catch (Exception) { }
        finally
        {
            pinned.Free();
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    public void ScreenshotToClipboard()
    {
        try { Process.Start(new ProcessStartInfo("/usr/sbin/screencapture") { ArgumentList = { "-i", "-c" }, UseShellExecute = false })?.Dispose(); }
        catch (Exception) { }
    }

    /// <summary>The Dock's accessibility tree has one item per app; its AXStatusLabel is the red badge text.</summary>
    public IReadOnlyDictionary<string, string> AppBadges()
    {
        var badges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Native.AXIsProcessTrusted()) return badges;
        var dockPid = RunningApps.Pids("com.apple.dock").FirstOrDefault();
        if (dockPid <= 0) return badges;
        var dock = Native.AXUIElementCreateApplication(dockPid);
        try
        {
            ForEachChild(dock, list => ForEachChild(list, item =>
            {
                var title = ReadString(item, AttrTitle);
                var badge = ReadString(item, AttrStatusLabel);
                if (title is { Length: > 0 } && badge is { Length: > 0 }) badges[title] = badge;
            }));
        }
        finally
        {
            Native.CFRelease(dock);
        }
        return badges;
    }

    /// <summary>Visit an element's children while their array is alive, then release it.</summary>
    private static void ForEachChild(IntPtr element, Action<IntPtr> visit)
    {
        if (Native.AXUIElementCopyAttributeValue(element, AttrChildren, out var array) != 0 || array == IntPtr.Zero) return;
        try
        {
            var count = Native.CFArrayGetCount(array);
            for (nint i = 0; i < count; i++) visit(Native.CFArrayGetValueAtIndex(array, i));
        }
        finally
        {
            Native.CFRelease(array);
        }
    }

    private static string? ReadString(IntPtr element, IntPtr attribute)
    {
        if (Native.AXUIElementCopyAttributeValue(element, attribute, out var value) != 0 || value == IntPtr.Zero) return null;
        try { return ObjC.ReadNSString(value); }
        finally { Native.CFRelease(value); }
    }
}
