using System.Diagnostics;
using Overdesk.Core.Platform;
using static Overdesk.Platform.Windows.NativeMethods;

namespace Overdesk.Platform.Windows;

/// <summary>
/// Embeds an external top-level window by re-parenting it into the Overdesk window
/// (strip the frame, make it WS_CHILD, SetParent). Fragile for some apps (UWP, GPU-heavy, apps that
/// fight reparenting); callers show the error if a program refuses.
/// A window Overdesk did not open (the program was already running) is given back as it was when its tab closes.
/// </summary>
internal sealed class Win32EmbeddedWindow : IEmbeddedWindow
{
    /// <summary>Matches the stage's inner corner radius (13 px minus the 1 px frame), in DIPs.</summary>
    private const double CornerRadius = 12;

    private readonly Process _process;
    private readonly IntPtr _hwnd;
    private readonly bool _owned;
    private readonly bool _killOnClose;
    private readonly long _oldStyle, _oldExStyle;
    private readonly RECT _oldRect;
    private bool _visible = true;
    private bool _disposed;
    private (int W, int H) _regionSize;

    public Win32EmbeddedWindow(Process process, IntPtr hwnd, IntPtr owner, bool owned, bool killOnClose)
    {
        _process = process;
        _hwnd = hwnd;
        _owned = owned;
        _killOnClose = killOnClose;
        _oldStyle = Style(hwnd);
        _oldExStyle = ExStyle(hwnd);
        GetWindowRect(hwnd, out _oldRect);

        // A minimized or maximized window keeps that state as a child and then ignores our sizes.
        if (IsIconic(hwnd) || (_oldStyle & WS_MAXIMIZE) != 0) ShowWindow(hwnd, SW_RESTORE);

        var style = Style(hwnd);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_POPUP | WS_MAXIMIZE | WS_MINIMIZE);
        // A child window cannot show a menu bar, so classic programs with menus stay overlapped (still parented).
        if (GetMenu(hwnd) == IntPtr.Zero) style |= WS_CHILD;
        style |= WS_CLIPSIBLINGS;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

        var ex = ExStyle(hwnd);
        ex &= ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));

        if (owner != IntPtr.Zero && SetParent(hwnd, owner) == IntPtr.Zero && GetParent(hwnd) != owner)
        {
            Restore();
            throw new EmbeddingException(
                "Windows refused to move this program into OverDesk. Programs running as administrator cannot be embedded " +
                "unless OverDesk runs as administrator too.");
        }
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED);

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
    }

    public bool IsAlive => !_disposed && !_process.HasExited;

    public bool HasWindow => IsAlive && IsWindow(_hwnd);

    /// <summary>A child window has no frame of its own to drag or resize.</summary>
    public UserWindowChange? TakeUserChange() => null;

    public event EventHandler? Exited;

    public void SetBounds(HostBounds b)
    {
        if (!HasWindow) return;
        var w = Math.Max(b.Width, 1);
        var h = Math.Max(b.Height, 1);
        var flags = SWP_NOZORDER | SWP_NOACTIVATE | (_visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW);
        SetWindowPos(_hwnd, IntPtr.Zero, b.ClientX, b.ClientY, w, h, flags);

        // Round the corners so the program sits inside the stage frame instead of poking over it.
        if (_regionSize != (w, h))
        {
            _regionSize = (w, h);
            var d = (int)Math.Round(CornerRadius * 2 * b.Scale);
            var region = CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d);
            if (region != IntPtr.Zero && SetWindowRgn(_hwnd, region, true) == 0) DeleteObject(region); // the system owns it on success
        }
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (HasWindow) ShowWindow(_hwnd, visible ? SW_SHOWNA : SW_HIDE);
    }

    public IWindowFrame? CaptureFrame() => null;

    public Task FocusAndClickAsync(double screenX, double screenY, bool rightButton)
    {
        Focus();
        return Task.CompletedTask;
    }

    /// <summary>SetFocus only works for windows of the calling thread, so borrow the program's input queue for a moment.</summary>
    public void Focus()
    {
        if (!HasWindow) return;
        var theirs = GetWindowThreadProcessId(_hwnd, out _);
        var ours = GetCurrentThreadId();
        var attached = theirs != ours && AttachThreadInput(ours, theirs, true);
        try { SetFocus(_hwnd); }
        finally { if (attached) AttachThreadInput(ours, theirs, false); }
    }

    public void Close()
    {
        if (_disposed) return;
        try
        {
            if (!_owned)
            {
                Restore(); // it was open before Overdesk took it: give it back
                return;
            }
            if (_process.HasExited) return;
            if (!_killOnClose)
            {
                // A window we asked a program that already ran to open (another Chrome window): close only that window.
                PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already gone */ }
    }

    /// <summary>Back to a normal desktop window where it was before.</summary>
    private void Restore()
    {
        if (!IsWindow(_hwnd)) return;
        SetWindowRgn(_hwnd, IntPtr.Zero, false);
        SetParent(_hwnd, IntPtr.Zero);
        SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr(_oldStyle & ~(WS_MINIMIZE | WS_MAXIMIZE)));
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(_oldExStyle));
        SetWindowPos(_hwnd, IntPtr.Zero, _oldRect.Left, _oldRect.Top,
            Math.Max(_oldRect.Right - _oldRect.Left, 200), Math.Max(_oldRect.Bottom - _oldRect.Top, 150),
            SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        _process.Dispose();
    }
}
