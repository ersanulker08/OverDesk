using System.Runtime.InteropServices;

namespace Overdesk.Platform.MacOS;

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect { public double X, Y, Width, Height; }

/// <summary>Tiny Objective-C runtime bridge, enough to call a handful of AppKit methods.</summary>
internal static class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [DllImport(Lib)] private static extern IntPtr objc_getClass(string name);
    [DllImport(Lib)] private static extern IntPtr sel_registerName(string name);
    [DllImport(Lib)] public static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);
    [DllImport(Lib)] public static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);
    [DllImport(Lib)] public static extern IntPtr objc_getProtocol(string name);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);
    [DllImport(Lib)] public static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Lib)] public static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, nuint arg, IntPtr arg2);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, ref CGRect rect, IntPtr arg2, IntPtr arg3);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern nuint SendNUInt(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern int SendInt(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.U1)] bool arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr receiver, IntPtr selector, double arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, CGRect rect, IntPtr arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, nuint arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool SendBool(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool SendBool(IntPtr receiver, IntPtr selector, IntPtr arg);

    public static IntPtr Class(string name) => objc_getClass(name);
    public static IntPtr Selector(string name) => sel_registerName(name);
    public static double CallDouble(IntPtr receiver, string selector) => SendDouble(receiver, sel_registerName(selector));
    public static void CallBoolArg(IntPtr receiver, string selector, bool arg) => SendVoid(receiver, sel_registerName(selector), arg);
    public static void CallDoubleArg(IntPtr receiver, string selector, double arg) => SendVoid(receiver, sel_registerName(selector), arg);
    public static IntPtr CallRectArg(IntPtr receiver, string selector, CGRect rect, IntPtr arg) => Send(receiver, sel_registerName(selector), rect, arg);

    /// <summary>Copy an NSString into a .NET string (null for nil).</summary>
    public static string? ReadNSString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        var utf8 = Call(nsString, "UTF8String");
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }
    public static IntPtr Call(IntPtr receiver, string selector) => Send(receiver, sel_registerName(selector));
    public static IntPtr Call(IntPtr receiver, string selector, IntPtr arg) => Send(receiver, sel_registerName(selector), arg);
    public static IntPtr Call(IntPtr receiver, string selector, nuint arg, IntPtr arg2) => Send(receiver, sel_registerName(selector), arg, arg2);
    public static IntPtr Call(IntPtr receiver, string selector, ref CGRect rect, IntPtr arg2, IntPtr arg3)
        => Send(receiver, sel_registerName(selector), ref rect, arg2, arg3);
    public static nuint CallNUInt(IntPtr receiver, string selector) => SendNUInt(receiver, sel_registerName(selector));
    public static int CallInt(IntPtr receiver, string selector) => SendInt(receiver, sel_registerName(selector));
    public static IntPtr Call(IntPtr receiver, string selector, nuint arg) => Send(receiver, sel_registerName(selector), arg);
    public static bool CallBool(IntPtr receiver, string selector) => SendBool(receiver, sel_registerName(selector));

    public static bool RespondsTo(IntPtr receiver, string selector)
        => receiver != IntPtr.Zero && SendBool(receiver, sel_registerName("respondsToSelector:"), sel_registerName(selector));

    /// <summary>An autoreleased NSString.</summary>
    public static IntPtr NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return Call(Class("NSString"), "stringWithUTF8String:", utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }
}
