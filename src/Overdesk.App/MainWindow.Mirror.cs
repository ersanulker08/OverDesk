using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Overdesk.Core.Tabs;

namespace Overdesk.App;

/// <summary>
/// The "mirror" (macOS): another app's window cannot live inside Overdesk, so whenever Overdesk itself comes to the
/// front (a click on the dock, the settings, Cmd+Tab...) the program's real window drops behind it. To keep the
/// illusion, the stage shows a live image of that window, captured from the window server, and a click on the
/// image brings the real window back and replays the click on it.
/// The image is kept on stage all the time, right behind the real window (which covers it while the program is in
/// front, refreshed every 1.5 s). So when Overdesk comes forward the picture is already there: no blank frame at all.
/// In split view each half has its own mirror.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan MirrorLiveInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MirrorCacheInterval = TimeSpan.FromSeconds(1.5);

    /// <summary>One mirrored program: its image control, the last frame, and a reusable pixel buffer.</summary>
    private sealed class MirrorSurface(Image image)
    {
        public Image Image { get; } = image;
        public string? Key { get; set; }
        public WriteableBitmap? Bitmap { get; set; }
        public byte[] Pixels { get; set; } = Array.Empty<byte>(); // reused between captures (frames are tens of MB)
        public DateTime LastCapture { get; set; }
        public bool Capturing { get; set; }
    }

    private readonly DispatcherTimer _mirrorTimer = new() { Interval = MirrorLiveInterval };
    private MirrorSurface _mainMirror = null!;
    private MirrorSurface _splitMirror = null!;
    private DateTime _lastForward;

    private void InitializeMirror()
    {
        _mainMirror = new MirrorSurface(Mirror);
        _splitMirror = new MirrorSurface(SplitMirror);
        _mirrorTimer.Tick += (_, _) => MirrorTick();
        _mirrorTimer.Start();
        Activated += (_, _) => UpdateMirror();
        Deactivated += (_, _) => UpdateMirror();
        foreach (var surface in new[] { _mainMirror, _splitMirror })
        {
            var s = surface;
            s.Image.PointerPressed += (_, e) => OnMirrorPressed(s, e);
            s.Image.PointerWheelChanged += (_, _) => FocusHostedWindow();
        }
    }

    private bool MirrorEnabled => _settings.MirrorPrograms && _embedding.Embedder.CanMirror == true;

    /// <summary>Key of the hosted program a surface shows, or null when there is nothing to mirror.</summary>
    private string? MirrorTarget(MirrorSurface surface)
    {
        if (!MirrorEnabled) return null;
        var tab = surface == _mainMirror ? _tabs.Active : _splitTab;
        return tab is { Status: TabStatus.Running, IsInternal: false } && _embedding.Has(TabKey(tab)) ? TabKey(tab) : null;
    }

    /// <summary>Keep each mirror on stage (behind its real window) whenever its program is shown and a frame exists.</summary>
    private void UpdateMirror()
    {
        foreach (var surface in new[] { _mainMirror, _splitMirror })
        {
            var key = MirrorTarget(surface);
            if (key != surface.Key)
            {
                // Another program: a frame of the old one must never show.
                surface.Key = key;
                surface.Image.Source = null;
                surface.Bitmap = null;
            }
            surface.Image.IsVisible = key is not null && WindowState != WindowState.Minimized && surface.Bitmap is not null;
            if (key is not null && IsActive) _ = CaptureMirrorAsync(surface); // refresh right away
        }
    }

    private void MirrorTick()
    {
        foreach (var surface in new[] { _mainMirror, _splitMirror })
        {
            if (MirrorTarget(surface) is null)
            {
                if (surface.Image.IsVisible || surface.Key is not null) UpdateMirror();
                continue;
            }
            var due = IsActive ? MirrorLiveInterval : MirrorCacheInterval;
            if (DateTime.UtcNow - surface.LastCapture >= due) _ = CaptureMirrorAsync(surface);
        }
    }

    private async Task CaptureMirrorAsync(MirrorSurface surface)
    {
        if (surface.Capturing || MirrorTarget(surface) is not { } key) return;
        surface.Capturing = true;
        surface.LastCapture = DateTime.UtcNow;
        try
        {
            // Capture and convert off the UI thread; only the final copy into the bitmap happens here.
            var pixels = await Task.Run(() =>
            {
                using var frame = _embedding.CaptureFrame(key);
                if (frame is null) return ((byte[], int, int)?)null;
                var size = frame.Width * frame.Height * 4;
                if (surface.Pixels.Length != size) surface.Pixels = new byte[size];
                var buffer = surface.Pixels;
                unsafe
                {
                    fixed (byte* p = buffer) frame.CopyTo((IntPtr)p, frame.Width * 4);
                }
                return (buffer, frame.Width, frame.Height);
            });
            if (pixels is not { } px || key != MirrorTarget(surface)) return;

            var (data, width, height) = px;
            if (surface.Bitmap is null || surface.Bitmap.PixelSize.Width != width || surface.Bitmap.PixelSize.Height != height)
                surface.Bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = surface.Bitmap.Lock())
            {
                for (var row = 0; row < height; row++)
                    System.Runtime.InteropServices.Marshal.Copy(data, row * width * 4, fb.Address + row * fb.RowBytes, width * 4);
            }
            surface.Image.Source = null; // make the Image pick up the new pixels
            surface.Image.Source = surface.Bitmap;
            surface.Image.IsVisible = WindowState != WindowState.Minimized;
        }
        catch (Exception)
        {
            // Capturing is best effort; without a frame the stage simply stays empty as before.
        }
        finally
        {
            surface.Capturing = false;
        }
    }

    /// <summary>A click on the image means a click on the program: bring it back and replay the click there.</summary>
    private void OnMirrorPressed(MirrorSurface surface, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (surface.Key is not { } key || DateTime.UtcNow - _lastForward < TimeSpan.FromMilliseconds(500)) return;
        _lastForward = DateTime.UtcNow;
        var point = e.GetCurrentPoint(this);
        var screen = this.PointToScreen(point.Position);
        _ = _embedding.FocusAndClickAsync(key, screen.X, screen.Y, point.Properties.IsRightButtonPressed);
    }
}
