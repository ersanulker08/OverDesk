using System.Runtime.InteropServices;
using System.Text;

namespace Overdesk.Platform.Windows;

internal static class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const long WS_POPUP = 0x80000000L;
    public const long WS_CHILD = 0x40000000L;
    public const long WS_MINIMIZE = 0x20000000L;
    public const long WS_MAXIMIZE = 0x01000000L;
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_THICKFRAME = 0x00040000L;
    public const long WS_SYSMENU = 0x00080000L;
    public const long WS_MINIMIZEBOX = 0x00020000L;
    public const long WS_MAXIMIZEBOX = 0x00010000L;
    public const long WS_CLIPSIBLINGS = 0x04000000L;
    public const long WS_CLIPCHILDREN = 0x02000000L;
    public const long WS_EX_APPWINDOW = 0x00040000L;
    public const long WS_EX_WINDOWEDGE = 0x00000100L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOW = 5;
    public const int SW_SHOWNA = 8;
    public const int SW_RESTORE = 9;
    public const uint WM_CLOSE = 0x0010;

    public const int DWMWA_CLOAKED = 14;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Windows of different DPI awareness may be parented into each other (Windows 10 1803+).</summary>
    public const int DPI_HOSTING_BEHAVIOR_MIXED = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetMenu(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiHostingBehavior(int behavior);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    public const uint GW_OWNER = 4;

    public static long Style(IntPtr hwnd) => GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
    public static long ExStyle(IntPtr hwnd) => GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>Full path of the program behind a process id, or null when Windows will not tell (elevated, gone).</summary>
    public static string? ProcessPath(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageNameW(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>Hidden by DWM although "visible" (suspended Store apps, windows on other virtual desktops).</summary>
    public static bool IsCloaked(IntPtr hwnd)
        => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
}
