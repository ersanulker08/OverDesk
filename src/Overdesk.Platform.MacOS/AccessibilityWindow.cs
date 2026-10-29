namespace Overdesk.Platform.MacOS;

/// <summary>Thin wrapper over the Accessibility objects of one external application and its main window.</summary>
internal sealed class AccessibilityWindow : IDisposable
{
    private readonly IntPtr _app;
    private IntPtr _window;

    private static readonly IntPtr AttrWindows = Cf("AXWindows");
    private static readonly IntPtr AttrMainWindow = Cf("AXMainWindow");
    private static readonly IntPtr AttrFocusedWindow = Cf("AXFocusedWindow");
    private static readonly IntPtr AttrSubrole = Cf("AXSubrole");
    private static readonly IntPtr AttrMinimized = Cf("AXMinimized");
    private static readonly IntPtr AttrFullScreen = Cf("AXFullScreen");
    private static readonly IntPtr AttrFullScreenButton = Cf("AXFullScreenButton");
    private static readonly IntPtr AttrEnabled = Cf("AXEnabled");
    private static readonly IntPtr AttrPosition = Cf("AXPosition");
    private static readonly IntPtr AttrSize = Cf("AXSize");
    private static readonly IntPtr AttrHidden = Cf("AXHidden");
    private static readonly IntPtr AttrFrontmost = Cf("AXFrontmost");
    private static readonly IntPtr ActionRaise = Cf("AXRaise");
    private static readonly IntPtr StandardWindow = Cf("AXStandardWindow");
    private static readonly IntPtr True = Native.ReadGlobal(Native.CoreFoundationPath, "kCFBooleanTrue");
    private static readonly IntPtr False = Native.ReadGlobal(Native.CoreFoundationPath, "kCFBooleanFalse");

    public AccessibilityWindow(int pid)
    {
        Pid = pid;
        _app = Native.AXUIElementCreateApplication(pid);
    }

    public int Pid { get; }

    private static IntPtr Cf(string s) => Native.CFStringCreateWithCString(IntPtr.Zero, s, Native.CFStringEncodingUTF8);

    /// <summary>
    /// True once the application has a real document window we can drive. Dialogs, panels and palettes are skipped
    /// so we do not grab, say, an "Open file" sheet instead of the app's main window.
    /// </summary>
    public bool TryAcquireWindow()
    {
        if (_window != IntPtr.Zero) return true;

        foreach (var attr in new[] { AttrMainWindow, AttrFocusedWindow })
        {
            if (Native.AXUIElementCopyAttributeValue(_app, attr, out var w) == 0 && w != IntPtr.Zero)
            {
                if (IsStandard(w)) { _window = w; return true; }
                Native.CFRelease(w);
            }
        }

        if (Native.AXUIElementCopyAttributeValue(_app, AttrWindows, out var array) != 0 || array == IntPtr.Zero)
            return false;
        try
        {
            var count = Native.CFArrayGetCount(array);
            for (nint i = 0; i < count; i++)
            {
                var w = Native.CFArrayGetValueAtIndex(array, i);
                if (!IsStandard(w)) continue;
                _window = Native.CFRetain(w);
                return true;
            }
            return false;
        }
        finally { Native.CFRelease(array); }
    }

    private static bool IsStandard(IntPtr window)
    {
        if (Native.AXUIElementCopyAttributeValue(window, AttrSubrole, out var subrole) != 0 || subrole == IntPtr.Zero)
            return false;
        try { return Native.CFEqual(subrole, StandardWindow); }
        finally { Native.CFRelease(subrole); }
    }

    /// <summary>Current position and size in screen points, or null when the window is gone.</summary>
    public (double X, double Y, double Width, double Height)? GetFrame()
    {
        if (!TryAcquireWindow()) return null;
        if (Native.AXUIElementCopyAttributeValue(_window, AttrPosition, out var p) != 0 || p == IntPtr.Zero)
        {
            ReleaseWindow();
            return null;
        }
        try
        {
            if (Native.AXUIElementCopyAttributeValue(_window, AttrSize, out var s) != 0 || s == IntPtr.Zero) return null;
            try
            {
                Native.AXValueGetPoint(p, Native.AXValueCGPoint, out var point);
                Native.AXValueGetSize(s, Native.AXValueCGSize, out var size);
                return (point.X, point.Y, size.Width, size.Height);
            }
            finally { Native.CFRelease(s); }
        }
        finally { Native.CFRelease(p); }
    }

    /// <summary>Position and size in screen points (top-left origin).</summary>
    public bool SetFrame(double x, double y, double width, double height)
    {
        if (!TryAcquireWindow()) return false;
        Native.AXUIElementSetAttributeValue(_window, AttrMinimized, False);
        var point = new CGPoint { X = x, Y = y };
        var size = new CGSize { Width = width, Height = height };
        var p = Native.AXValueCreatePoint(Native.AXValueCGPoint, ref point);
        var s = Native.AXValueCreateSize(Native.AXValueCGSize, ref size);
        try
        {
            // Move first, then size, then move again: some apps clamp the size against the screen edge
            // using their old position, and the second move settles the final spot.
            var r1 = Native.AXUIElementSetAttributeValue(_window, AttrPosition, p);
            var r2 = Native.AXUIElementSetAttributeValue(_window, AttrSize, s);
            Native.AXUIElementSetAttributeValue(_window, AttrPosition, p);
            if (r1 != 0 && r2 != 0) { ReleaseWindow(); return false; }
            return true;
        }
        finally
        {
            Native.CFRelease(p);
            Native.CFRelease(s);
        }
    }

    /// <summary>The window server's id for this window (0 when unknown).</summary>
    public uint WindowId()
    {
        if (!TryAcquireWindow()) return 0;
        try { return Native._AXUIElementGetWindow(_window, out var id) == 0 ? id : 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }

    /// <summary>True when the window is in macOS full screen (its own Space).</summary>
    public bool IsFullScreen()
    {
        if (!TryAcquireWindow()) return false;
        if (Native.AXUIElementCopyAttributeValue(_window, AttrFullScreen, out var value) != 0 || value == IntPtr.Zero) return false;
        try { return Native.CFEqual(value, True); }
        finally { Native.CFRelease(value); }
    }

    public void SetFullScreen(bool fullScreen)
    {
        if (TryAcquireWindow()) Native.AXUIElementSetAttributeValue(_window, AttrFullScreen, fullScreen ? True : False);
    }

    /// <summary>
    /// Try to grey out the window's green full-screen button. Most apps refuse (the attribute is read-only for
    /// other processes); the caller still undoes full screen when it happens.
    /// </summary>
    public void DisableFullScreenButton()
    {
        if (!TryAcquireWindow()) return;
        if (Native.AXUIElementCopyAttributeValue(_window, AttrFullScreenButton, out var button) != 0 || button == IntPtr.Zero) return;
        try { Native.AXUIElementSetAttributeValue(button, AttrEnabled, False); }
        finally { Native.CFRelease(button); }
    }

    public void SetHidden(bool hidden)
        => Native.AXUIElementSetAttributeValue(_app, AttrHidden, hidden ? True : False);

    /// <summary>Raise the window above others without activating the app (keyboard stays where it is).</summary>
    public void Raise()
    {
        if (TryAcquireWindow()) Native.AXUIElementPerformAction(_window, ActionRaise);
    }

    /// <summary>Activate the app and raise its window so it gets the keyboard.</summary>
    public void BringToFront()
    {
        Native.AXUIElementSetAttributeValue(_app, AttrFrontmost, True);
        Raise();
    }

    private void ReleaseWindow()
    {
        if (_window != IntPtr.Zero) Native.CFRelease(_window);
        _window = IntPtr.Zero;
    }

    public void Dispose()
    {
        ReleaseWindow();
        if (_app != IntPtr.Zero) Native.CFRelease(_app);
    }
}
