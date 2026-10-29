using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.Windows;

/// <summary>Windows catalog: Start menu shortcuts resolved to their .exe, icons through the shell.</summary>
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

        var programs = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var link in links)
            {
                if (Shortcuts.Resolve(link) is { } target
                    && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(target).Contains("unins", StringComparison.OrdinalIgnoreCase))
                    programs.Add(target);
            }
        }

        return programs
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ProgramIcon? GetIcon(string programPath, int size)
    {
        if (!File.Exists(programPath)) return null;

        var icons = new IntPtr[1];
        var ids = new uint[1];
        if (PrivateExtractIcons(programPath, 0, size, size, icons, ids, 1, 0) == 0 || icons[0] == IntPtr.Zero)
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
            var pixels = new byte[width * height * 4];
            var dc = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(dc, info.hbmColor, 0, (uint)height, pixels, ref header, 0) == 0) return null;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }

            // Old icons carry no alpha channel: treat them as fully opaque.
            var hasAlpha = false;
            for (var i = 3; i < pixels.Length && !hasAlpha; i += 4) hasAlpha = pixels[i] != 0;
            if (!hasAlpha)
                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

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
