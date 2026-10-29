using System.Diagnostics;
using Overdesk.Core.Platform;
using static Overdesk.Platform.Windows.NativeMethods;

namespace Overdesk.Platform.Windows;

/// <summary>
/// Embeds an external top-level window by re-parenting it into the Overdesk window
/// (strip the frame, make it WS_CHILD, SetParent). Fragile for some apps (UWP, GPU-heavy, apps that
/// fight reparenting); callers show the error if a program refuses.
/// </summary>
internal sealed class Win32EmbeddedWindow : IEmbeddedWindow
{
    private readonly Process _process;
    private readonly IntPtr _hwnd;
    private bool _visible = true;
    private bool _disposed;

    public Win32EmbeddedWindow(Process process, IntPtr hwnd, IntPtr owner)
    {
        _process = process;
        _hwnd = hwnd;

        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_POPUP);
        style |= WS_CHILD | WS_CLIPSIBLINGS;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex &= ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));

        if (owner != IntPtr.Zero) SetParent(hwnd, owner);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
    }

    public bool IsAlive => !_disposed && !_process.HasExited && IsWindow(_hwnd);

    public bool HasWindow => IsAlive;

    /// <summary>A child window has no frame of its own to drag or resize.</summary>
    public UserWindowChange? TakeUserChange() => null;

    public event EventHandler? Exited;

    public void SetBounds(HostBounds b)
    {
        if (!IsAlive) return;
        var flags = SWP_NOZORDER | SWP_NOACTIVATE | (_visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW);
        SetWindowPos(_hwnd, IntPtr.Zero, b.ClientX, b.ClientY, Math.Max(b.Width, 1), Math.Max(b.Height, 1), flags);
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (IsAlive) ShowWindow(_hwnd, visible ? SW_SHOW : SW_HIDE);
    }

    public IWindowFrame? CaptureFrame() => null;

    public Task FocusAndClickAsync(double screenX, double screenY, bool rightButton)
    {
        Focus();
        return Task.CompletedTask;
    }

    public void Focus()
    {
        if (IsAlive) SetFocus(_hwnd);
    }

    public void Close()
    {
        if (_disposed) return;
        try
        {
            if (!_process.HasExited)
            {
                PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { /* already gone */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        _process.Dispose();
    }
}
