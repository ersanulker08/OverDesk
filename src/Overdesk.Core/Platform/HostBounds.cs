namespace Overdesk.Core.Platform;

/// <summary>
/// Where a hosted window should sit.
/// Client*, Width and Height are physical pixels relative to the owner window's client area (what Win32 reparenting needs).
/// Screen* is the absolute screen position in the OS's own desktop units: physical pixels on Windows,
/// points on macOS (what macOS overlay tracking needs). <see cref="Scale"/> converts pixels to those units.
/// </summary>
public readonly record struct HostBounds(
    int ClientX, int ClientY,
    int ScreenX, int ScreenY,
    int Width, int Height,
    double Scale);
