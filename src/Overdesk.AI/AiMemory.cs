using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Overdesk.AI;

/// <summary>One thing the AI was asked to remember.</summary>
public sealed record MemoryNote(string Id, string Text, DateTime Saved);

/// <summary>
/// What the AI remembers between chats: notes about the user (global) and notes about each project folder. Stored as
/// small JSON files in the data folder, shown in Settings → AI, and put into the system prompt of every chat.
/// </summary>
public sealed class AiMemory
{
    private const int MaxNotes = 60;
    private const int MaxPromptChars = 4000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _lock = new();

    public AiMemory(string folder) => Folder = folder;

    public string Folder { get; }

    /// <summary>Raised after notes were added or removed.</summary>
    public event Action? Changed;

    private string FileFor(string? project)
    {
        if (project is null) return Path.Combine(Folder, "global.json");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(project).TrimEnd('/', '\\').ToLowerInvariant())))[..16];
        return Path.Combine(Folder, $"project-{key}.json");
    }

    /// <summary>Notes for the user (project null) or for one project folder, oldest first.</summary>
    public IReadOnlyList<MemoryNote> Notes(string? project)
    {
        lock (_lock)
        {
            try
            {
                var file = FileFor(project);
                if (!File.Exists(file)) return Array.Empty<MemoryNote>();
                var node = JsonNode.Parse(File.ReadAllText(file));
                return node?["notes"]?.Deserialize<List<MemoryNote>>() ?? new List<MemoryNote>();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return Array.Empty<MemoryNote>();
            }
        }
    }

    public MemoryNote Add(string? project, string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException("Nothing to remember.");
        if (text.Length > 600) text = text[..600];
        MemoryNote note;
        lock (_lock)
        {
            var notes = Notes(project).ToList();
            var same = notes.FirstOrDefault(n => string.Equals(n.Text, text, StringComparison.OrdinalIgnoreCase));
            if (same is not null) return same;
            note = new MemoryNote(Guid.NewGuid().ToString("N")[..8], text, DateTime.Now);
            notes.Add(note);
            if (notes.Count > MaxNotes) notes.RemoveRange(0, notes.Count - MaxNotes);
            Save(project, notes);
        }
        Changed?.Invoke();
        return note;
    }

    public bool Remove(string? project, string id)
    {
        lock (_lock)
        {
            var notes = Notes(project).ToList();
            if (notes.RemoveAll(n => n.Id.Equals(id.Trim().Trim('[', ']'), StringComparison.OrdinalIgnoreCase)) == 0) return false;
            Save(project, notes);
        }
        Changed?.Invoke();
        return true;
    }

    public void Clear(string? project)
    {
        lock (_lock) Save(project, new List<MemoryNote>());
        Changed?.Invoke();
    }

    private void Save(string? project, List<MemoryNote> notes)
    {
        Directory.CreateDirectory(Folder);
        var node = new JsonObject { ["project"] = project, ["notes"] = JsonSerializer.SerializeToNode(notes) };
        var file = FileFor(project);
        File.WriteAllText(file + ".tmp", node.ToJsonString(Json));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    /// <summary>The notes as a system-prompt section, or empty when there are none.</summary>
    public string PromptSection(string? project)
    {
        var sb = new StringBuilder();
        void Add(string title, IReadOnlyList<MemoryNote> notes)
        {
            if (notes.Count == 0) return;
            sb.AppendLine(title);
            foreach (var n in notes) sb.AppendLine($"- [{n.Id}] {n.Text}");
        }
        Add("What you remember about the user (from earlier chats):", Notes(null));
        if (project is not null) Add($"What you remember about this project ({Path.GetFileName(project.TrimEnd('/', '\\'))}):", Notes(project));
        var text = sb.ToString();
        return text.Length > MaxPromptChars ? text[^MaxPromptChars..] : text;
    }

    /// <summary>The "memory" tool: remember, forget, or list.</summary>
    public AiTool Tool(Func<string?> project) => new()
    {
        Name = "memory",
        Description = "Your long-term memory across chats. Save short facts worth knowing next time: the user's preferences, " +
                      "names, decisions, project conventions (scope project, when a project folder is chosen) or about the user (scope user). " +
                      "Save when the user says \"remember\" or tells you something lasting; do not save passing details. Forget what is wrong or outdated.",
        Schema = AiTool.Object(("action", "string", "remember, forget or list.", true),
                               ("text", "string", "remember: the fact, one short sentence.", false),
                               ("id", "string", "forget: the note's id (in brackets in your memory list).", false),
                               ("scope", "string", "user (default) or project.", false)),
        Label = a => AiToolbox.Str(a, "action") switch
        {
            "remember" => $"Remembering: {AiToolbox.Str(a, "text")}",
            "forget" => "Forgetting a note",
            _ => "Looking at what I remember",
        },
        Run = (a, _) =>
        {
            var scope = AiToolbox.Str(a, "scope") == "project" ? project() : null;
            if (AiToolbox.Str(a, "scope") == "project" && scope is null)
                return Task.FromResult(AiToolResult.Error("No project folder is chosen; use scope user."));
            switch (AiToolbox.Str(a, "action"))
            {
                case "remember":
                    var note = Add(scope, AiToolbox.Str(a, "text") ?? "");
                    return Task.FromResult(new AiToolResult($"Saved as [{note.Id}]."));
                case "forget":
                    var id = AiToolbox.Str(a, "id") ?? "";
                    // The id may belong to either list.
                    var removed = Remove(scope, id) || Remove(scope is null ? project() : null, id);
                    return Task.FromResult(removed ? new AiToolResult("Forgotten.") : AiToolResult.Error("No note with that id."));
                default:
                    var all = PromptSection(project());
                    return Task.FromResult(new AiToolResult(all.Length > 0 ? all : "Nothing saved yet."));
            }
        },
    };
}
