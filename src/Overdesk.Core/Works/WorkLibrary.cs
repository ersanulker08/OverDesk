using System.Text.Json;

namespace Overdesk.Core.Works;

/// <summary>
/// All works of the user, one JSON file each in the "works" folder. Edits mark a work dirty; the host saves
/// the dirty ones shortly after the last change (<see cref="FlushDirty"/>), so nothing is ever lost.
/// </summary>
public sealed class WorkLibrary
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly HashSet<string> _dirty = new();
    private readonly List<Work> _works = new();

    public WorkLibrary(string folder) => Folder = folder;

    public string Folder { get; }

    /// <summary>Raised on any change: (work, null) for edits, (null, null) when the list itself changed.</summary>
    public event Action<Work?, object?>? Changed;

    /// <summary>Newest edited first; pinned ones on top.</summary>
    public IReadOnlyList<Work> Works => _works
        .OrderByDescending(w => w.Pinned).ThenByDescending(w => w.Updated).ToList();

    public Work? Find(string? id) => _works.FirstOrDefault(w => w.Id == id);

    /// <summary>Folder beside the works folder that keeps deleted works (user deletes and AI deletes alike).</summary>
    public string TrashFolder => Path.Combine(Path.GetDirectoryName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? Folder, "works-trash");

    /// <summary>Files that could not be read by the last <see cref="Load"/>.</summary>
    public IReadOnlyList<string> LoadErrors { get; private set; } = Array.Empty<string>();

    /// <summary>Why the last write failed, or null when the last write worked.</summary>
    public string? LastSaveError { get; private set; }

    public void Load()
    {
        _works.Clear();
        var errors = new List<string>();
        LoadErrors = errors;
        if (!Directory.Exists(Folder)) return;
        var found = new Dictionary<string, (Work Work, string File)>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Work>(File.ReadAllText(file)) is not { } work || work.Id.Length == 0)
                {
                    errors.Add(file);
                    continue;
                }
                if (!found.TryGetValue(work.Id, out var other))
                {
                    found[work.Id] = (work, file);
                    continue;
                }
                // Two files claim one Id (a copied file): keep the newest, set the other aside.
                var own = Path.GetFileName(file) == work.Id + ".json";
                var keepNew = work.Updated > other.Work.Updated || (work.Updated == other.Work.Updated && own);
                var loser = keepNew ? other.File : file;
                try { File.Move(loser, loser + ".duplicate", overwrite: true); } catch (IOException) { }
                if (keepNew) found[work.Id] = (work, file);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A damaged file is left alone, never fatal.
                errors.Add(file);
            }
        }
        foreach (var (work, file) in found.Values)
        {
            _works.Add(work);
            // A file under another name is moved to the Id's own name so edits and deletes hit the same file.
            if (Path.GetFileName(file) != work.Id + ".json" && Write(work))
                try { File.Move(file, file + ".duplicate", overwrite: true); } catch (IOException) { }
        }
    }

    public Work Create(WorkTemplate template)
    {
        var work = template.Create();
        _works.Add(work);
        Write(work);
        Changed?.Invoke(null, null);
        return work;
    }

    public Work Duplicate(Work source)
    {
        var copy = source.Duplicate();
        _works.Add(copy);
        Write(copy);
        Changed?.Invoke(null, null);
        return copy;
    }

    /// <summary>
    /// Moves the work to the trash folder (its current state, even if not yet saved) and returns the trash file.
    /// Returns null, leaving the work in place, when the backup could not be written.
    /// </summary>
    public string? Delete(Work work)
    {
        string trashed;
        try
        {
            Directory.CreateDirectory(TrashFolder);
            trashed = Path.Combine(TrashFolder, $"{work.Id}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(trashed, JsonSerializer.Serialize(work, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        _works.Remove(work);
        _dirty.Remove(work.Id);
        try { File.Delete(PathOf(work)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke(null, null);
        return trashed;
    }

    /// <summary>Deleted works, newest first.</summary>
    public IReadOnlyList<TrashedWork> ListTrash()
    {
        var list = new List<TrashedWork>();
        if (!Directory.Exists(TrashFolder)) return list;
        foreach (var file in Directory.EnumerateFiles(TrashFolder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Work>(File.ReadAllText(file)) is { } work && work.Id.Length > 0)
                    list.Add(new TrashedWork(file, work.Title, File.GetLastWriteTime(file)));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return list.OrderByDescending(t => t.Deleted).ToList();
    }

    /// <summary>Brings a trashed work back (with a new id if its old one is taken) and removes the trash file.</summary>
    public Work? Restore(string trashFile)
    {
        try
        {
            if (JsonSerializer.Deserialize<Work>(File.ReadAllText(trashFile)) is not { } work || work.Id.Length == 0) return null;
            if (Find(work.Id) is not null) work.Id = Guid.NewGuid().ToString("N");
            if (!Write(work)) return null;
            _works.Add(work);
            try { File.Delete(trashFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Changed?.Invoke(null, null);
            return work;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Call after every edit. <paramref name="sender"/> is the view that made it (so it can skip refreshing itself).</summary>
    public void Touch(Work work, object? sender = null, bool listChanged = false)
    {
        work.Updated = DateTime.Now;
        _dirty.Add(work.Id);
        Changed?.Invoke(work, sender);
        if (listChanged) Changed?.Invoke(null, null);
    }

    public bool HasDirty => _dirty.Count > 0;

    /// <summary>Writes every dirty work; false when at least one could not be written (it stays dirty).</summary>
    public bool FlushDirty()
    {
        foreach (var id in _dirty.ToList())
            if (Find(id) is not { } work || Write(work)) _dirty.Remove(id);
        return _dirty.Count == 0;
    }

    private string PathOf(Work work) => Path.Combine(Folder, work.Id + ".json");

    private bool Write(Work work)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = PathOf(work) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(work, Json));
            File.Move(tmp, PathOf(work), overwrite: true);
            LastSaveError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep it dirty so the next flush tries again.
            _dirty.Add(work.Id);
            LastSaveError = ex.Message;
            return false;
        }
    }
}

/// <summary>A deleted work waiting in the trash folder.</summary>
public sealed record TrashedWork(string File, string Title, DateTime Deleted);
