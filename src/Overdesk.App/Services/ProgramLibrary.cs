using Overdesk.Core.Platform;

namespace Overdesk.App.Services;

/// <summary>The machine's installed programs, scanned once in the background and shared by every screen.</summary>
internal static class ProgramLibrary
{
    private static Task<IReadOnlyList<string>>? _installed;

    public static void Start(IProgramCatalog catalog)
        => _installed ??= Task.Run(() =>
        {
            try { return catalog.FindInstalledPrograms(); }
            catch (Exception) { return (IReadOnlyList<string>)Array.Empty<string>(); }
        });

    public static Task<IReadOnlyList<string>> Installed
        => _installed ?? Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}
