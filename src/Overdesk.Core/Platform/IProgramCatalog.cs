namespace Overdesk.Core.Platform;

/// <summary>Icon pixels for a program. Either encoded PNG bytes or raw 32-bit BGRA pixels (straight alpha, top-down rows).</summary>
public sealed record ProgramIcon(byte[] Data, bool IsPng, int Width, int Height);

/// <summary>
/// Per-OS knowledge about installed programs: where they live and what their icons look like.
/// Lives next to <see cref="IWindowEmbedder"/> so the core and UI never touch OS APIs.
/// </summary>
public interface IProgramCatalog
{
    /// <summary>Paths of programs installed on this machine (an .app bundle on macOS, an .exe on Windows).</summary>
    IReadOnlyList<string> FindInstalledPrograms();

    /// <summary>The program's icon at roughly <paramref name="size"/> pixels, or null when it cannot be read.</summary>
    ProgramIcon? GetIcon(string programPath, int size);
}

/// <summary>Used on platforms without a catalog: no installed list, no icons.</summary>
public sealed class EmptyProgramCatalog : IProgramCatalog
{
    public IReadOnlyList<string> FindInstalledPrograms() => Array.Empty<string>();
    public ProgramIcon? GetIcon(string programPath, int size) => null;
}
