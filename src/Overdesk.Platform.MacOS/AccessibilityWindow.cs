namespace Overdesk.Platform.MacOS;

/// <summary>Thin wrapper over the Accessibility objects of one external application and its main window.</summary>
internal sealed class AccessibilityWindow : IDisposable
{
    private const int ErrorCannotComplete = -25204; // kAXErrorCannotComplete: the app did not answer in time
    private const float MessagingTimeout = 0.25f;

    // Everything here runs on the UI thread at layout-timer speed, so a program that stopped answering is left alone for
    // a moment instead of costing one timeout per call.
    private static readonly TimeSpan BusyPause = TimeSpan.FromSeconds(1);

    private static bool _windowIdCallExists = true;

    private readonly IntPtr _app;
    private IntPtr _window;
    private IntPtr _firstWindow;
    private volatile uint _windowId;
    private DateTime _busyUntil;

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
        Native.AXUIElementSetMessagingTimeout(_app, MessagingTimeout);
    }

    public int Pid { get; }

    /// <summary>The window server's id for this window (0 when unknown). The only member safe to read off the UI thread.</summary>
    public uint WindowId => _windowId;

    /// <summary>True while we still hold the window first acquired, not a sibling picked up after it closed.</summary>
    public bool HoldsFirstWindow => _window != IntPtr.Zero && Native.CFEqual(_window, _firstWindow);

    private bool Busy => DateTime.UtcNow < _busyUntil;

    private int Read(IntPtr element, IntPtr attribute, out IntPtr value)
    {
        value = IntPtr.Zero;
        return Busy ? ErrorCannotComplete : Noted(Native.AXUIElementCopyAttributeValue(element, attribute, out value));
    }

    private int Write(IntPtr element, IntPtr attribute, IntPtr value)
        => Busy ? ErrorCannotComplete : Noted(Native.AXUIElementSetAttributeValue(element, attribute, value));

    private int Noted(int status)
    {
        if (status == ErrorCannotComplete) _busyUntil = DateTime.UtcNow + BusyPause;
        return status;
    }

    private static IntPtr Cf(string s) => Native.CFStringCreateWithCString(IntPtr.Zero, s, Native.CFStringEncodingUTF8);

    /// <summary>
    /// True once the application has a real document window we can drive. Dialogs, panels and palettes are skipped
    /// so we do not grab, say, an "Open file" sheet instead of the app's main window.
    /// </summary>
    public bool TryAcquireWindow()
    {
        if (_window != IntPtr.Zero)
        {
            if (_windowId == 0) ResolveWindowId();
            return true;
        }

        foreach (var attr in new[] { AttrMainWindow, AttrFocusedWindow })
        {
            if (Read(_app, attr, out var w) == 0 && w != IntPtr.Zero)
            {
                Native.AXUIElementSetMessagingTimeout(w, MessagingTimeout);
                if (IsStandard(w)) { Hold(w); return true; }
                Native.CFRelease(w);
            }
        }

        if (Read(_app, AttrWindows, out var array) != 0 || array == IntPtr.Zero)
            return false;
        try
        {
            var count = Native.CFArrayGetCount(array);
            for (nint i = 0; i < count; i++)
            {
                var w = Native.CFArrayGetValueAtIndex(array, i);
                Native.AXUIElementSetMessagingTimeout(w, MessagingTimeout);
                if (!IsStandard(w)) continue;
                Hold(Native.CFRetain(w));
                return true;
            }
            return false;
        }
        finally { Native.CFRelease(array); }
    }

    private void Hold(IntPtr window)
    {
        _window = window;
        if (_firstWindow == IntPtr.Zero) _firstWindow = Native.CFRetain(window);
        ResolveWindowId();
    }

    private void ResolveWindowId()
    {
        if (!_windowIdCallExists) return;
        try { if (Native._AXUIElementGetWindow(_window, out var id) == 0) _windowId = id; }
        catch (EntryPointNotFoundException) { _windowIdCallExists = false; }
    }

    private bool IsStandard(IntPtr window)
    {
        if (Read(window, AttrSubrole, out var subrole) != 0 || subrole == IntPtr.Zero)
            return false;
        try { return Native.CFEqual(subrole, StandardWindow); }
        finally { Native.CFRelease(subrole); }
    }

    /// <summary>Another normal window of this app besides ours, so hiding the whole app would take that one away too.</summary>
    public bool HasOtherWindows()
    {
        if (!TryAcquireWindow() || Read(_app, AttrWindows, out var array) != 0 || array == IntPtr.Zero) return false;
        try
        {
            var count = Native.CFArrayGetCount(array);
            for (nint i = 0; i < count; i++)
            {
                var w = Native.CFArrayGetValueAtIndex(array, i);
                if (!Native.CFEqual(w, _window) && IsStandard(w)) return true;
            }
            return false;
        }
        finally { Native.CFRelease(array); }
    }

    /// <summary>Current position and size in screen points, or null when the window is gone.</summary>
    public (double X, double Y, double Width, double Height)? GetFrame()
    {
        if (!TryAcquireWindow()) return null;
        var status = Read(_window, AttrPosition, out var p);
        if (status != 0 || p == IntPtr.Zero)
        {
            if (status != ErrorCannotComplete) ReleaseWindow();
            return null;
        }
        try
        {
            if (Read(_window, AttrSize, out var s) != 0 || s == IntPtr.Zero) return null;
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
        if (Busy || !TryAcquireWindow()) return false;
        Write(_window, AttrMinimized, False);
        var point = new CGPoint { X = x, Y = y };
        var size = new CGSize { Width = width, Height = height };
        var p = Native.AXValueCreatePoint(Native.AXValueCGPoint, ref point);
        var s = Native.AXValueCreateSize(Native.AXValueCGSize, ref size);
        try
        {
            // Move first, then size, then move again: some apps clamp the size against the screen edge
            // using their old position, and the second move settles the final spot.
            var r1 = Write(_window, AttrPosition, p);
            var r2 = Write(_window, AttrSize, s);
            Write(_window, AttrPosition, p);
            if (r1 == 0 || r2 == 0) return true;
            if (r1 != ErrorCannotComplete) ReleaseWindow();
            return false;
        }
        finally
        {
            Native.CFRelease(p);
            Native.CFRelease(s);
        }
    }

    /// <summary>True when the window is in macOS full screen (its own Space).</summary>
    public bool IsFullScreen()
    {
        if (!TryAcquireWindow()) return false;
        return ReadFlag(AttrFullScreen);
    }

    /// <summary>True when the window sits in the Dock (its own yellow button, or we put it there).</summary>
    public bool IsMinimized() => TryAcquireWindow() && ReadFlag(AttrMinimized);

    public void SetMinimized(bool minimized)
    {
        if (TryAcquireWindow()) Native.AXUIElementSetAttributeValue(_window, AttrMinimized, minimized ? True : False);
    }

    private bool ReadFlag(IntPtr attribute)
    {
        if (Read(_window, attribute, out var value) != 0 || value == IntPtr.Zero) return false;
        try { return Native.CFEqual(value, True); }
        finally { Native.CFRelease(value); }
    }

    public void SetFullScreen(bool fullScreen)
    {
        if (TryAcquireWindow()) Write(_window, AttrFullScreen, fullScreen ? True : False);
    }

    /// <summary>
    /// Try to grey out the window's green full-screen button. Most apps refuse (the attribute is read-only for
    /// other processes); the caller still undoes full screen when it happens.
    /// </summary>
    public bool DisableFullScreenButton() => TryAcquireWindow() && SetFullScreenButtonEnabled(false);

    /// <summary>Undo <see cref="DisableFullScreenButton"/> on the window we hold; never picks up another one.</summary>
    public void EnableFullScreenButton()
    {
        if (_window != IntPtr.Zero) SetFullScreenButtonEnabled(true);
    }

    private bool SetFullScreenButtonEnabled(bool enabled)
    {
        if (Read(_window, AttrFullScreenButton, out var button) != 0 || button == IntPtr.Zero) return false;
        try { return Write(button, AttrEnabled, enabled ? True : False) == 0; }
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
        _windowId = 0;
    }

    public void Dispose()
    {
        ReleaseWindow();
        if (_firstWindow != IntPtr.Zero) Native.CFRelease(_firstWindow);
        _firstWindow = IntPtr.Zero;
        if (_app != IntPtr.Zero) Native.CFRelease(_app);
    }
}
