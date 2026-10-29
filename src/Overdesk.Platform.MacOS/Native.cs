using System.Runtime.InteropServices;

namespace Overdesk.Platform.MacOS;

[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint { public double X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize { public double Width, Height; }

/// <summary>P/Invoke surface for the macOS Accessibility API (ApplicationServices) and CoreFoundation.</summary>
internal static class Native
{
    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const int AXValueCGPoint = 1;
    public const int AXValueCGSize = 2;
    public const uint CFStringEncodingUTF8 = 0x08000100;

    [DllImport(AppServices)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool AXIsProcessTrusted();
    [DllImport(AppServices)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool AXIsProcessTrustedWithOptions(IntPtr options);
    [DllImport(AppServices)] public static extern IntPtr AXUIElementCreateApplication(int pid);

    /// <summary>Private but long-stable HIServices call: the window-server id behind an AX window element.</summary>
    [DllImport(AppServices)] public static extern int _AXUIElementGetWindow(IntPtr element, out uint windowId);
    [DllImport(AppServices)] public static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
    [DllImport(AppServices)] public static extern int AXUIElementSetAttributeValue(IntPtr element, IntPtr attribute, IntPtr value);
    [DllImport(AppServices)] public static extern int AXUIElementPerformAction(IntPtr element, IntPtr action);
    [DllImport(AppServices, EntryPoint = "AXValueCreate")] public static extern IntPtr AXValueCreatePoint(int type, ref CGPoint value);
    [DllImport(AppServices, EntryPoint = "AXValueCreate")] public static extern IntPtr AXValueCreateSize(int type, ref CGSize value);
    [DllImport(AppServices, EntryPoint = "AXValueGetValue")] [return: MarshalAs(UnmanagedType.U1)] public static extern bool AXValueGetPoint(IntPtr value, int type, out CGPoint point);
    [DllImport(AppServices, EntryPoint = "AXValueGetValue")] [return: MarshalAs(UnmanagedType.U1)] public static extern bool AXValueGetSize(IntPtr value, int type, out CGSize size);

    /// <summary>Is a mouse button held down anywhere on the desktop (kCGEventSourceStateCombinedSessionState = 0).</summary>
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CGEventSourceButtonState(int stateId, int button);

    [DllImport(CoreFoundation)] public static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(CoreFoundation)] public static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundation)] public static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CoreFoundation)] public static extern IntPtr CFRetain(IntPtr cf);
    [DllImport(CoreFoundation)] public static extern void CFRelease(IntPtr cf);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool CFEqual(IntPtr a, IntPtr b);
    [DllImport(CoreFoundation)]
    public static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);

    /// <summary>Read a global CFTypeRef constant such as kCFBooleanTrue by symbol name.</summary>
    public static IntPtr ReadGlobal(string library, string symbol)
    {
        var lib = NativeLibrary.Load(library);
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, symbol));
    }

    public static string AppServicesPath => AppServices;
    public static string CoreFoundationPath => CoreFoundation;
}
