using System.Runtime.Versioning;

namespace Overdesk.Platform.Windows;

/// <summary>What a .lnk file starts. Squirrel apps (Discord, GitHub Desktop) only run with the shortcut's arguments.</summary>
internal sealed record Shortcut(string Target, string Arguments, string? WorkingDirectory, string? IconFile, int IconIndex);

/// <summary>Reads Windows shortcut (.lnk) files.</summary>
[SupportedOSPlatform("windows")]
internal static class Shortcuts
{
    private static readonly Lazy<object?> Shell = new(() =>
    {
        try { return Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); }
        catch (Exception) { return null; }
    });

    /// <summary>The contents of a .lnk file, or null when it cannot be read or points nowhere.</summary>
    public static Shortcut? Resolve(string link)
    {
        if (Shell.Value is not { } shell) return null;
        try
        {
            dynamic shortcut = ((dynamic)shell).CreateShortcut(link);
            string target = shortcut.TargetPath;
            if (!File.Exists(target)) return null;

            string arguments = shortcut.Arguments;
            string folder = shortcut.WorkingDirectory;
            string icon = shortcut.IconLocation; // "file,index"
            folder = Environment.ExpandEnvironmentVariables(folder);

            var comma = icon.LastIndexOf(',');
            var iconFile = Environment.ExpandEnvironmentVariables(comma >= 0 ? icon[..comma] : icon);
            var iconIndex = comma >= 0 && int.TryParse(icon[(comma + 1)..], out var index) ? index : 0;

            return new Shortcut(target, Environment.ExpandEnvironmentVariables(arguments),
                Directory.Exists(folder) ? folder : null, File.Exists(iconFile) ? iconFile : null, iconIndex);
        }
        catch (Exception)
        {
            return null; // broken or unusual shortcut
        }
    }
}
