using System.Runtime.InteropServices;

namespace Overdesk.Platform.Windows;

internal static class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const long WS_POPUP = 0x80000000L;
    public const long WS_CHILD = 0x40000000L;
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_THICKFRAME = 0x00040000L;
    public const long WS_SYSMENU = 0x00080000L;
    public const long WS_MINIMIZEBOX = 0x00020000L;
    public const long WS_MAXIMIZEBOX = 0x00010000L;
    public const long WS_CLIPSIBLINGS = 0x04000000L;
    public const long WS_EX_APPWINDOW = 0x00040000L;
    public const long WS_EX_WINDOWEDGE = 0x00000100L;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const uint WM_CLOSE = 0x0010;

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    public const uint GW_OWNER = 4;
}
