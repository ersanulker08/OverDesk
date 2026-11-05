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
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg, IntPtr arg2);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg, IntPtr arg2, IntPtr arg3);
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
    public static IntPtr Call(IntPtr receiver, string selector, IntPtr arg, IntPtr arg2) => Send(receiver, sel_registerName(selector), arg, arg2);
    public static IntPtr Call(IntPtr receiver, string selector, IntPtr arg, IntPtr arg2, IntPtr arg3) => Send(receiver, sel_registerName(selector), arg, arg2, arg3);
    public static IntPtr Call(IntPtr receiver, string selector, ref CGRect rect, IntPtr arg2, IntPtr arg3)
        => Send(receiver, sel_registerName(selector), ref rect, arg2, arg3);
    public static nuint CallNUInt(IntPtr receiver, string selector) => SendNUInt(receiver, sel_registerName(selector));
    public static int CallInt(IntPtr receiver, string selector) => SendInt(receiver, sel_registerName(selector));
    public static IntPtr Call(IntPtr receiver, string selector, nuint arg) => Send(receiver, sel_registerName(selector), arg);
    public static bool CallBool(IntPtr receiver, string selector) => SendBool(receiver, sel_registerName(selector));
    public static bool CallBool(IntPtr receiver, string selector, IntPtr arg) => SendBool(receiver, sel_registerName(selector), arg);

    public static bool RespondsTo(IntPtr receiver, string selector)
        => receiver != IntPtr.Zero && SendBool(receiver, sel_registerName("respondsToSelector:"), sel_registerName(selector));

    /// <summary>An autoreleased NSString.</summary>
    public static IntPtr NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return Call(Class("NSString"), "stringWithUTF8String:", utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    /// <summary>
    /// An Objective-C block of the shape ^(id result, NSError *error) calling back into .NET; it also serves ^(NSInteger),
    /// where <c>error</c> is then meaningless. Hand it to the method, then call <see cref="ReleaseBlock"/>: the receiver
    /// keeps its own reference for as long as it needs, and the callback goes away with the last one.
    /// </summary>
    public static unsafe IntPtr CompletionBlock(Action<IntPtr, IntPtr> callback)
    {
        var template = new BlockLiteral
        {
            Isa = StackBlockClass.Value,
            Flags = (1 << 25) | (1 << 30), // BLOCK_HAS_COPY_DISPOSE | BLOCK_HAS_SIGNATURE
            Invoke = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&InvokeCompletion,
            Descriptor = Descriptor.Value,
            Context = GCHandle.ToIntPtr(GCHandle.Alloc(callback)),
        };
        return _Block_copy((IntPtr)(&template));
    }

    /// <summary>Keep a block handed to us past the call it came with.</summary>
    public static IntPtr CopyBlock(IntPtr block) => _Block_copy(block);

    public static void ReleaseBlock(IntPtr block) => _Block_release(block);

    /// <summary>Call a block with one word-sized argument (an object, an NSInteger, a BOOL); blocks with none ignore it.</summary>
    public static unsafe void InvokeBlock(IntPtr block, IntPtr argument)
        => ((delegate* unmanaged<IntPtr, IntPtr, void>)((BlockLiteral*)block)->Invoke)(block, argument);

    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    [DllImport(LibSystem)] private static extern IntPtr _Block_copy(IntPtr block);
    [DllImport(LibSystem)] private static extern void _Block_release(IntPtr block);

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public IntPtr Descriptor;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
        public IntPtr Copy;
        public IntPtr Dispose;
        public IntPtr Signature;
    }

    private static readonly Lazy<IntPtr> StackBlockClass = new(() => NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_NSConcreteStackBlock"));

    private static readonly unsafe Lazy<IntPtr> Descriptor = new(() =>
    {
        var descriptor = (BlockDescriptor*)Marshal.AllocHGlobal(sizeof(BlockDescriptor));
        descriptor->Reserved = 0;
        descriptor->Size = (nuint)sizeof(BlockLiteral);
        descriptor->Copy = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&CopyHelper;
        descriptor->Dispose = (IntPtr)(delegate* unmanaged<IntPtr, void>)&DisposeHelper;
        descriptor->Signature = Marshal.StringToHGlobalAnsi("v@?@@");
        return (IntPtr)descriptor;
    });

    // Runs once, when the template becomes the heap block; the handle simply moves over with it.
    [UnmanagedCallersOnly]
    private static void CopyHelper(IntPtr destination, IntPtr source) { }

    [UnmanagedCallersOnly]
    private static unsafe void DisposeHelper(IntPtr block) => GCHandle.FromIntPtr(((BlockLiteral*)block)->Context).Free();

    [UnmanagedCallersOnly]
    private static unsafe void InvokeCompletion(IntPtr block, IntPtr result, IntPtr error)
    {
        try { ((Action<IntPtr, IntPtr>)GCHandle.FromIntPtr(((BlockLiteral*)block)->Context).Target!)(result, error); }
        catch (Exception) { /* never let an exception cross into Objective-C */ }
    }
}
