using System.IO.Compression;

namespace Overdesk.Core.Settings;

/// <summary>
/// One zip with everything the user made in OverDesk: settings, works, AI chats, AI memory and added plugins.
/// API keys stay in the system keychain and are not included. A restore is staged and applied at the next start,
/// before anything is loaded, so the running app cannot write the old data back over it.
/// </summary>
public static class DataBackup
{
    /// <summary>What goes into a backup: files and folders inside the data folder.</summary>
    public static readonly string[] Items = { "settings.json", "works", "chats", "ai-memory", "plugins" };

    private const string PendingFolder = ".restore-pending";

    public static int Create(string dataFolder, string zipPath)
    {
        var temp = zipPath + ".tmp";
        File.Delete(temp);
        var count = 0;
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var item in Items)
            {
                var path = Path.Combine(dataFolder, item);
                if (File.Exists(path))
                {
                    zip.CreateEntryFromFile(path, item);
                    count++;
                }
                else if (Directory.Exists(path))
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        zip.CreateEntryFromFile(file, Path.GetRelativePath(dataFolder, file).Replace('\\', '/'));
                        count++;
                    }
            }
        }
        File.Move(temp, zipPath, overwrite: true);
        return count;
    }

    /// <summary>Check the zip and stage it; it replaces the current data at the next start. Returns the number of files.</summary>
    public static int Stage(string zipPath, string dataFolder)
    {
        var pending = Path.Combine(dataFolder, PendingFolder);
        if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
        Directory.CreateDirectory(pending);
        var root = Path.GetFullPath(pending) + Path.DirectorySeparatorChar;
        var count = 0;
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.All(e => e.FullName != "settings.json"))
            throw new InvalidDataException("This is not an OverDesk backup (it has no settings.json).");
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var top = entry.FullName.Split('/')[0];
            if (!Items.Contains(top)) continue;
            var target = Path.GetFullPath(Path.Combine(pending, entry.FullName));
            // Entries must not climb out of the folder ("zip slip").
            if (!target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException($"Unsafe path in the backup: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            count++;
        }
        return count;
    }

    public static bool HasPending(string dataFolder) => Directory.Exists(Path.Combine(dataFolder, PendingFolder));

    /// <summary>At start: replace the backed-up items with the staged ones (the old ones are kept in .before-restore).</summary>
    public static void ApplyPending(string dataFolder)
    {
        var pending = Path.Combine(dataFolder, PendingFolder);
        if (!Directory.Exists(pending)) return;
        var before = Path.Combine(dataFolder, ".before-restore");
        if (Directory.Exists(before)) Directory.Delete(before, recursive: true);
        Directory.CreateDirectory(before);
        foreach (var item in Items)
        {
            var current = Path.Combine(dataFolder, item);
            var staged = Path.Combine(pending, item);
            if (!File.Exists(staged) && !Directory.Exists(staged)) continue;
            if (File.Exists(current)) File.Move(current, Path.Combine(before, item));
            else if (Directory.Exists(current)) Directory.Move(current, Path.Combine(before, item));
            if (File.Exists(staged)) File.Move(staged, current);
            else Directory.Move(staged, current);
        }
        Directory.Delete(pending, recursive: true);
    }
}
