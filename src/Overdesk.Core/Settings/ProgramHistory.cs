namespace Overdesk.Core.Settings;

/// <summary>Keeps the "recent programs" list short, unique and most-recent-first.</summary>
public static class ProgramHistory
{
    public const int MaxRecent = 16;

    public static void Touch(List<string> recent, string path)
    {
        recent.RemoveAll(p => SamePath(p, path));
        recent.Insert(0, path);
        if (recent.Count > MaxRecent) recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
    }

    public static bool SamePath(string a, string b)
        => string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
