using System.Diagnostics;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.MacOS;

/// <summary>Notifications through AppleScript, screenshots with screencapture, app badges read from the Dock.</summary>
public sealed class MacDesktopServices : IDesktopServices
{
    private static readonly IntPtr AttrChildren = Cf("AXChildren");
    private static readonly IntPtr AttrTitle = Cf("AXTitle");
    private static readonly IntPtr AttrStatusLabel = Cf("AXStatusLabel");

    private static IntPtr Cf(string s) => Native.CFStringCreateWithCString(IntPtr.Zero, s, Native.CFStringEncodingUTF8);

    public bool Notify(string title, string text)
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
