using System.Runtime.InteropServices;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.MacOS;

/// <summary>
/// Captures one window's image (even while covered) and replays mouse clicks. CGWindowListCreateImage is
/// obsolete in the SDK since macOS 15 but still present at runtime; it is loaded by name so a future macOS
/// that drops it simply turns mirroring off. Needs the Screen Recording permission.
/// </summary>
internal static class WindowCapture
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const uint ListOptionIncludingWindow = 1 << 3;
    private const uint ImageBoundsIgnoreFraming = 1 << 0;
    private const uint ImageBestResolution = 1 << 3;
    private const uint BitmapPremultipliedFirstLittle = 2 | (2 << 12); // kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little = BGRA

    private delegate IntPtr CreateImage(CGRect bounds, uint listOption, uint windowId, uint imageOption);
    private static readonly Lazy<CreateImage?> Create = new(() =>
    {
        try
        {
            var lib = NativeLibrary.Load(CoreGraphics);
            return NativeLibrary.TryGetExport(lib, "CGWindowListCreateImage", out var fn)
                ? Marshal.GetDelegateForFunctionPointer<CreateImage>(fn)
                : null;
        }
        catch (Exception) { return null; }
    });

    public static bool Available => Create.Value is not null && HasPermission;

    private static (DateTime At, bool Value) _permission;

    /// <summary>Screen Recording granted? The check is a round trip to the privacy daemon, so it is cached for a few seconds.</summary>
    public static bool HasPermission
    {
        get
        {
            if (DateTime.UtcNow - _permission.At < TimeSpan.FromSeconds(3)) return _permission.Value;
            bool value;
            try { value = CGPreflightScreenCaptureAccess(); }
            catch (EntryPointNotFoundException) { value = false; }
            _permission = (DateTime.UtcNow, value);
            return value;
        }
    }

    public static void RequestPermission()
    {
        try { CGRequestScreenCaptureAccess(); } catch (EntryPointNotFoundException) { }
    }

    public static IWindowFrame? Capture(uint windowId)
    {
        if (windowId == 0 || Create.Value is not { } create || !HasPermission) return null;
        var nullRect = new CGRect { X = double.PositiveInfinity, Y = double.PositiveInfinity };
        var image = create(nullRect, ListOptionIncludingWindow, windowId, ImageBoundsIgnoreFraming | ImageBestResolution);
        if (image == IntPtr.Zero) return null;
        var frame = new Frame(image);
        if (frame.Width > 1 && frame.Height > 1) return frame;
        frame.Dispose();
        return null;
    }

    private sealed class Frame : IWindowFrame
    {
        private IntPtr _image;

        public Frame(IntPtr image)
        {
            _image = image;
            Width = (int)CGImageGetWidth(image);
            Height = (int)CGImageGetHeight(image);
        }

        public int Width { get; }
        public int Height { get; }

        public void CopyTo(IntPtr buffer, int stride)
        {
            if (_image == IntPtr.Zero) return;
            var space = CGColorSpaceCreateDeviceRGB();
            var context = CGBitmapContextCreate(buffer, (nuint)Width, (nuint)Height, 8, (nuint)stride, space, BitmapPremultipliedFirstLittle);
            try
            {
                if (context != IntPtr.Zero)
                    CGContextDrawImage(context, new CGRect { Width = Width, Height = Height }, _image);
            }
            finally
            {
                if (context != IntPtr.Zero) CGContextRelease(context);
                CGColorSpaceRelease(space);
            }
        }

        public void Dispose()
        {
            if (_image != IntPtr.Zero) CGImageRelease(_image);
            _image = IntPtr.Zero;
        }
    }

    /// <summary>A real mouse click at a global screen point (points, top-left origin).</summary>
    public static void Click(double x, double y, bool right)
    {
        var point = new CGPoint { X = x, Y = y };
        var (down, up, button) = right ? (3u, 4u, 1u) : (1u, 2u, 0u);
        foreach (var type in new[] { down, up })
        {
            var evt = CGEventCreateMouseEvent(IntPtr.Zero, type, point, button);
            if (evt == IntPtr.Zero) continue;
            CGEventPost(0, evt); // kCGHIDEventTap
            Native.CFRelease(evt);
        }
    }

    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CGRequestScreenCaptureAccess();
    [DllImport(CoreGraphics)] private static extern nuint CGImageGetWidth(IntPtr image);
    [DllImport(CoreGraphics)] private static extern nuint CGImageGetHeight(IntPtr image);
    [DllImport(CoreGraphics)] private static extern void CGImageRelease(IntPtr image);
    [DllImport(CoreGraphics)] private static extern IntPtr CGColorSpaceCreateDeviceRGB();
    [DllImport(CoreGraphics)] private static extern void CGColorSpaceRelease(IntPtr space);
    [DllImport(CoreGraphics)]
    private static extern IntPtr CGBitmapContextCreate(IntPtr data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, IntPtr space, uint bitmapInfo);
    [DllImport(CoreGraphics)] private static extern void CGContextDrawImage(IntPtr context, CGRect rect, IntPtr image);
    [DllImport(CoreGraphics)] private static extern void CGContextRelease(IntPtr context);
    [DllImport(CoreGraphics)] private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint type, CGPoint point, uint button);
    [DllImport(CoreGraphics)] private static extern void CGEventPost(uint tap, IntPtr evt);
}
