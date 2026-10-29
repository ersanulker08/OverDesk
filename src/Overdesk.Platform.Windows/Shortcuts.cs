using System.Runtime.Versioning;

namespace Overdesk.Platform.Windows;

/// <summary>Resolves Windows shortcut (.lnk) files to the program they start.</summary>
[SupportedOSPlatform("windows")]
internal static class Shortcuts
{
    private static readonly Lazy<object?> Shell = new(() =>
    {
        try { return Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); }
        catch (Exception) { return null; }
    });

    /// <summary>The target of a .lnk file, or null when it cannot be read or points nowhere.</summary>
    public static string? Resolve(string link)
    {
        if (Shell.Value is not { } shell) return null;
        try
        {
            dynamic shortcut = ((dynamic)shell).CreateShortcut(link);
            string target = shortcut.TargetPath;
            return File.Exists(target) ? target : null;
        }
        catch (Exception)
        {
            return null; // broken or unusual shortcut
        }
    }
}
