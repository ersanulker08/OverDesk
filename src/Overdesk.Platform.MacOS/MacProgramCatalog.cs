using System.Runtime.InteropServices;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.MacOS;

/// <summary>macOS catalog: .app bundles from the usual Applications folders, icons through NSWorkspace.</summary>
public sealed class MacProgramCatalog : IProgramCatalog
{
    private const nuint NSBitmapImageFileTypePNG = 4;

    public IReadOnlyList<string> FindInstalledPrograms()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            "/Applications", "/Applications/Utilities", "/System/Applications", "/System/Applications/Utilities",
            Path.Combine(home, "Applications"),
        };

        var apps = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                apps.AddRange(Directory.EnumerateDirectories(root, "*.app"));
                // One level of grouping folders, e.g. /Applications/Microsoft Office/Word.app.
                foreach (var folder in Directory.EnumerateDirectories(root).Where(d => !d.EndsWith(".app", StringComparison.OrdinalIgnoreCase)))
                    apps.AddRange(Directory.EnumerateDirectories(folder, "*.app"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        return apps
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ProgramIcon? GetIcon(string programPath, int size)
    {
        if (!Directory.Exists(programPath) && !File.Exists(programPath)) return null;

        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var workspace = ObjC.Call(ObjC.Class("NSWorkspace"), "sharedWorkspace");
            var image = ObjC.Call(workspace, "iconForFile:", ObjC.NSString(programPath));
            if (image == IntPtr.Zero) return null;

            // Let AppKit choose the best representation for the requested pixel size.
            var rect = new CGRect { Width = size, Height = size };
            var cgImage = ObjC.Call(image, "CGImageForProposedRect:context:hints:", ref rect, IntPtr.Zero, IntPtr.Zero);
            if (cgImage == IntPtr.Zero) return null;

            var rep = ObjC.Call(ObjC.Call(ObjC.Class("NSBitmapImageRep"), "alloc"), "initWithCGImage:", cgImage);
            if (rep == IntPtr.Zero) return null;
            try
            {
                var props = ObjC.Call(ObjC.Class("NSDictionary"), "dictionary");
                var png = ObjC.Call(rep, "representationUsingType:properties:", NSBitmapImageFileTypePNG, props);
                if (png == IntPtr.Zero) return null;

                var length = (int)ObjC.CallNUInt(png, "length");
                var bytes = ObjC.Call(png, "bytes");
                if (length <= 0 || bytes == IntPtr.Zero) return null;

                var data = new byte[length];
                Marshal.Copy(bytes, data, 0, length);
                return new ProgramIcon(data, IsPng: true, size, size);
            }
            finally
            {
                ObjC.Call(rep, "release");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }
}
