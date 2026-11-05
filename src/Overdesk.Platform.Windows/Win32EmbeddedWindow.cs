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

    /// <summary>Longest the UI thread waits for a program to answer before leaving it to a background thread.</summary>
    private const uint AnswerTimeoutMs = 100;

    /// <summary>How long a window may stay hidden before it counts as gone; programs also hide for a moment while switching views.</summary>
    private static readonly TimeSpan HiddenGrace = TimeSpan.FromSeconds(1.5);

    private readonly Process _process;
    private readonly IntPtr _hwnd;
    private readonly bool _owned;
    private readonly long _oldStyle, _oldExStyle;
    private readonly RECT _oldRect;
    private bool _visible = true;
    private bool _closed;
    private bool _disposed;
    private DateTime? _hiddenSince;
    private (int W, int H) _regionSize;

    public Win32EmbeddedWindow(Process process, IntPtr hwnd, IntPtr owner, bool owned)
    {
        _process = process;
        _hwnd = hwnd;
        _owned = owned;
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
            Restore(_oldRect, show: true);
            throw new EmbeddingException(
                "Windows refused to move this program into OverDesk. Programs running as administrator cannot be embedded " +
                "unless OverDesk runs as administrator too.");
        }
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED);

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
    }

    public bool IsAlive => !_disposed && !_process.HasExited;

    private bool Exists => IsAlive && IsWindow(_hwnd);

    /// <summary>The style bit, not IsWindowVisible: that one also turns false when Overdesk itself is hidden.</summary>
    private bool HiddenByProgram => _visible && (Style(_hwnd) & WS_VISIBLE) == 0;

    /// <summary>Also false when the program hid its window itself (close to tray): the stage would stay empty for good.</summary>
    public bool HasWindow
    {
        get
        {
            if (!Exists) return false;
            if (!HiddenByProgram)
            {
                _hiddenSince = null;
                return true;
            }
            _hiddenSince ??= DateTime.UtcNow;
            return DateTime.UtcNow - _hiddenSince < HiddenGrace;
        }
    }

    /// <summary>A child window has no frame of its own to drag or resize.</summary>
    public UserWindowChange? TakeUserChange() => null;

    public event EventHandler? Exited;

    public void SetBounds(HostBounds b)
    {
        if (!Exists) return;
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
        if (Exists) ShowWindow(_hwnd, visible ? SW_SHOWNA : SW_HIDE);
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
        if (!Exists) return;
        var theirs = GetWindowThreadProcessId(_hwnd, out _);
        var ours = GetCurrentThreadId();
        var attached = theirs != ours && AttachThreadInput(ours, theirs, true);
        try { SetFocus(_hwnd); }
        finally { if (attached) AttachThreadInput(ours, theirs, false); }
    }

    /// <summary>
    /// Never waits for the program and never kills it: it may be asking the user to save. Calls that touch a window of
    /// another process wait for that program, so one that does not answer is handled on a background thread. A program
    /// that answers is released right here, because a child still attached when Overdesk quits is destroyed with it.
    /// </summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        if (!IsWindow(_hwnd)) return;
        if (SendMessageTimeoutW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, AnswerTimeoutMs, out _) != IntPtr.Zero) Release();
        else _ = Task.Run(Release);
    }

    /// <summary>
    /// A window that was open before Overdesk took it goes back where it was. One Overdesk opened is told to close as a
    /// normal desktop window on the spot, so a save prompt has a real owner the user can reach; one the program hid
    /// itself stays hidden and running in its tray.
    /// </summary>
    private void Release()
    {
        if (!IsWindow(_hwnd)) return;
        var hidden = HiddenByProgram;
        GetWindowRect(_hwnd, out var stage);
        Restore(_owned ? stage : _oldRect, show: !hidden);
        if (!_owned || hidden) return;
        SetForegroundWindow(_hwnd);
        PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Back to a normal desktop window.</summary>
    private void Restore(RECT at, bool show)
    {
        if (!IsWindow(_hwnd)) return;
        var visible = Style(_hwnd) & WS_VISIBLE; // setting this bit by hand would mark the window visible without showing it
        SetWindowRgn(_hwnd, IntPtr.Zero, false);
        SetParent(_hwnd, IntPtr.Zero);
        SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr((_oldStyle & ~(WS_MINIMIZE | WS_MAXIMIZE | WS_VISIBLE)) | visible));
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(_oldExStyle));
        SetWindowPos(_hwnd, IntPtr.Zero, at.Left, at.Top, Math.Max(at.Right - at.Left, 200), Math.Max(at.Bottom - at.Top, 150),
            SWP_NOZORDER | SWP_FRAMECHANGED | (show ? SWP_SHOWWINDOW : 0));
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        _process.Dispose();
    }
}
