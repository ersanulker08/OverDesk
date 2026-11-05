using Overdesk.Core.Tabs;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.Windows;

/// <summary>
/// Windows catalog: Start menu shortcuts resolved to their .exe (a shortcut that passes arguments stays a .lnk, since the
/// .exe alone would not start the program), packaged apps as shell:AppsFolder addresses, icons through the shell.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32ProgramCatalog : IProgramCatalog
{
    public IReadOnlyList<string> FindInstalledPrograms()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };

        // Shortcut name → target. The name is what the Start menu shows ("Google Chrome"), so programs are listed by it,
        // and several shortcuts with the same name (x86 / x64 copies of a tool) count once.
        var programs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var link in links)
            {
                if (Shortcuts.Resolve(link) is not { } shortcut
                    || !shortcut.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || IsUninstaller(link, shortcut))
                    continue;
                var name = Path.GetFileNameWithoutExtension(link);
                var program = shortcut.Arguments.Length > 0 ? link : shortcut.Target;
                if (programs.ContainsKey(name) || programs.ContainsValue(program)) continue;
                programs[name] = program;
                ProgramNames.Register(program, name);
            }
        }

        try
        {
            foreach (var (name, path) in StoreApps.Find())
                if (programs.TryAdd(name, path)) ProgramNames.Register(path, name);
        }
        catch (Exception)
        {
            // No package service (stripped-down Windows): the shortcuts above still count.
        }

        return programs.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Value).ToList();
    }

    private static bool IsUninstaller(string link, Shortcut shortcut)
    {
        var exe = Path.GetFileName(shortcut.Target);
        return exe.Contains("unins", StringComparison.OrdinalIgnoreCase)
               || exe.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
               || Path.GetFileName(link).Contains("unins", StringComparison.OrdinalIgnoreCase)
               || shortcut.Arguments.Contains("uninstall", StringComparison.OrdinalIgnoreCase);
    }

    public ProgramIcon? GetIcon(string programPath, int size)
    {
        if (StoreApps.IsStoreApp(programPath)) return StoreApps.GetIcon(programPath, size);
        if (!programPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return ExtractIcon(programPath, 0, size);

        if (Shortcuts.Resolve(programPath) is not { } shortcut) return null;
        return (shortcut.IconFile is { } file ? ExtractIcon(file, shortcut.IconIndex, size) : null)
               ?? ExtractIcon(shortcut.Target, 0, size);
    }

    private static ProgramIcon? ExtractIcon(string file, int index, int size)
    {
        if (!File.Exists(file)) return null;

        var icons = new IntPtr[1];
        var ids = new uint[1];
        if (PrivateExtractIcons(file, index, size, size, icons, ids, 1, 0) == 0 || icons[0] == IntPtr.Zero)
            return null;

        try { return ToBgra(icons[0]); }
        finally { DestroyIcon(icons[0]); }
    }

    private static ProgramIcon? ToBgra(IntPtr icon)
    {
        if (!GetIconInfo(icon, out var info)) return null;
        try
        {
            if (info.hbmColor == IntPtr.Zero || GetObject(info.hbmColor, Marshal.SizeOf<BITMAP>(), out var bmp) == 0)
                return null;

            var width = bmp.bmWidth;
            var height = bmp.bmHeight;
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };
            var maskHeader = header; // GetDIBits writes into the header it gets
            var pixels = new byte[width * height * 4];
            var mask = new byte[pixels.Length];
            var maskRead = false;
            var dc = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(dc, info.hbmColor, 0, (uint)height, pixels, ref header, 0) == 0) return null;
                // Asked for as 32 bits so that the 1-bit mask needs no color table after the header.
                maskRead = info.hbmMask != IntPtr.Zero && GetDIBits(dc, info.hbmMask, 0, (uint)height, mask, ref maskHeader, 0) != 0;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }

            // Old icons carry no alpha channel: their shape is in the mask, where white means transparent.
            var hasAlpha = false;
            for (var i = 3; i < pixels.Length && !hasAlpha; i += 4) hasAlpha = pixels[i] != 0;
            if (!hasAlpha)
                for (var i = 0; i < pixels.Length; i += 4) pixels[i + 3] = maskRead && mask[i] != 0 ? (byte)0 : (byte)255;

            return new ProgramIcon(pixels, IsPng: false, width, height);
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr obj, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
}
