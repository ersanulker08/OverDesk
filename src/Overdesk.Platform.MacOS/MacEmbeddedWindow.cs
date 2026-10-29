using System.Diagnostics;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.MacOS;

/// <summary>
/// macOS cannot re-parent another app's window. Instead the program keeps its own window and we
/// glue it to the Overdesk content region: same frame, kept above Overdesk, snapped back if it is moved,
/// hidden with its tab. Needs Accessibility permission.
/// </summary>
internal sealed class MacEmbeddedWindow : IEmbeddedWindow
{
    private const double Tolerance = 1.5;

    private readonly Process _process;
    private readonly AccessibilityWindow _ax;
    private readonly bool _startedByUs;
    private (double X, double Y, double Width, double Height)? _original;
    private (double X, double Y, double Width, double Height)? _applied;
    private bool _disposed;

    public MacEmbeddedWindow(Process process, AccessibilityWindow ax, bool startedByUs)
    {
        _process = process;
        _ax = ax;
        _startedByUs = startedByUs;
        _original = startedByUs ? null : ax.GetFrame();
        ax.DisableFullScreenButton();
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
    }

    public bool IsAlive
    {
        get
        {
            try { return !_disposed && !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public bool HasWindow => IsAlive && _ax.TryAcquireWindow();

    public event EventHandler? Exited;

    public void SetBounds(HostBounds b)
    {
        if (!IsAlive) return;
        var scale = b.Scale <= 0 ? 1 : b.Scale;
        // Screen* is already in points on macOS; the size is in pixels.
        double x = b.ScreenX, y = b.ScreenY, w = Math.Max(b.Width, 1) / scale, h = Math.Max(b.Height, 1) / scale;
        var target = (x, y, w, h);
        var sameTarget = _applied is { } prev && Near(prev.X, x) && Near(prev.Y, y) && Near(prev.Width, w) && Near(prev.Height, h);
        _applied = target;

        // The target did not move but the mouse is down: the user may be dragging the program's own window.
        // Do not snap it back; the host follows it instead (see TakeUserChange). When Overdesk itself moves the
        // target changes, so the program still follows Overdesk during a drag of Overdesk's top bar.
        if (sameTarget && UserIsHandling()) return;

        // A window left in full screen lives in another Space where we cannot reach it: bring it back first.
        if (_ax.IsFullScreen()) _ax.SetFullScreen(false);

        // Reading is cheaper than writing and avoids nudging apps that redraw on every resize.
        if (_ax.GetFrame() is { } f && Near(f.X, x) && Near(f.Y, y) && Near(f.Width, w) && Near(f.Height, h)) return;
        _ax.SetFrame(x, y, w, h);
    }

    public UserWindowChange? TakeUserChange()
    {
        if (!IsAlive || _applied is not { } a) return null;

        // The green button (or ctrl+cmd+F) inside the program: undo it and let Overdesk handle it its own way.
        if (_ax.IsFullScreen())
        {
            _ax.SetFullScreen(false);
            return new UserWindowChange(0, 0, 0, 0, WantsFullScreen: true);
        }

        // Only a change made while the mouse is held (or just released, to catch the last bit of a drag) is the
        // user's; anything else is the app itself and gets undone by the next SetBounds.
        if (!UserIsHandling() || _ax.GetFrame() is not { } f) return null;
        if (Near(f.X, a.X) && Near(f.Y, a.Y) && Near(f.Width, a.Width) && Near(f.Height, a.Height)) return null;
        _applied = f;
        return new UserWindowChange(f.X - a.X, f.Y - a.Y, f.Width - a.Width, f.Height - a.Height, WantsFullScreen: false);
    }

    private static DateTime _lastMouseDown;

    private static bool MouseDown()
    {
        var down = Native.CGEventSourceButtonState(0, 0);
        if (down) _lastMouseDown = DateTime.UtcNow;
        return down;
    }

    private static bool UserIsHandling() => MouseDown() || DateTime.UtcNow - _lastMouseDown < TimeSpan.FromMilliseconds(250);

    private static bool Near(double a, double b) => Math.Abs(a - b) <= Tolerance;

    public void SetVisible(bool visible)
    {
        if (!IsAlive) return;
        _ax.SetHidden(!visible);
        if (visible) _ax.Raise();
    }

    public void Focus()
    {
        if (IsAlive) _ax.BringToFront();
    }

    public IWindowFrame? CaptureFrame() => IsAlive ? WindowCapture.Capture(_ax.WindowId()) : null;

    public async Task FocusAndClickAsync(double screenX, double screenY, bool rightButton)
    {
        if (!IsAlive) return;
        _ax.BringToFront();
        // Replay the click only once the program is really in front, or it would land on Overdesk again.
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(25);
            if (RunningApps.FrontmostPid() == _process.Id)
            {
                await Task.Delay(30);
                WindowCapture.Click(screenX, screenY, rightButton);
                return;
            }
        }
    }

    public void Close()
    {
        if (_disposed || !IsAlive) return;
        if (_startedByUs)
        {
            // Quit the normal way so the app can ask to save; force it only if it refuses to go.
            if (!RunningApps.Terminate(_process.Id) && !_process.WaitForExit(0))
            {
                try { _process.Kill(); } catch (InvalidOperationException) { }
            }
            return;
        }

        // The app was already running before Overdesk adopted it: leave it running and give its window back.
        _ax.SetHidden(false);
        if (_original is { } o) _ax.SetFrame(o.X, o.Y, o.Width, o.Height);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        _ax.Dispose();
        _process.Dispose();
    }
}
