using Overdesk.Core.Platform;
using Windows.Management.Deployment;

namespace Overdesk.Platform.Windows;

/// <summary>
/// Packaged (Store) apps. They have no .exe a user could start or a .lnk in the Start menu folders, so a program path
/// for one is its shell address: shell:AppsFolder\&lt;application user model id&gt;.
/// </summary>
internal static class StoreApps
{
    private const string Prefix = @"shell:AppsFolder\";

    public static bool IsStoreApp(string path) => path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string AppId(string path) => path[Prefix.Length..];

    /// <summary>Every packaged app the Start menu lists for this user.</summary>
    public static List<(string Name, string Path)> Find()
    {
        var apps = new List<(string, string)>();
        foreach (var package in new PackageManager().FindPackagesForUser(""))
        {
            if (package.IsFramework || package.IsResourcePackage) continue;
            try
            {
                foreach (var entry in package.GetAppListEntries())
                    if (entry.DisplayInfo.DisplayName is { Length: > 0 } name) apps.Add((name, Prefix + entry.AppUserModelId));
            }
            catch (Exception)
            {
                // A package that is being updated or removed refuses; the others still count.
            }
        }
        return apps;
    }

    /// <summary>The app's Start menu logo as PNG bytes.</summary>
    public static ProgramIcon? GetIcon(string path, int size)
    {
        var appId = AppId(path);
        var family = appId.Split('!')[0];
        var entry = new PackageManager().FindPackagesForUser("", family)
            .SelectMany(p => p.GetAppListEntries())
            .FirstOrDefault(e => string.Equals(e.AppUserModelId, appId, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;

        var logo = entry.DisplayInfo.GetLogo(new global::Windows.Foundation.Size(size, size));
        using var stream = logo.OpenReadAsync().AsTask().GetAwaiter().GetResult().AsStreamForRead();
        using var png = new MemoryStream();
        stream.CopyTo(png);
        return new ProgramIcon(png.ToArray(), IsPng: true, size, size);
    }
}
