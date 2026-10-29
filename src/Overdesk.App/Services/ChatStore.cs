using System.Text.Json;

namespace Overdesk.App.Services;

/// <summary>One saved conversation of the AI panel (text only: attached screenshots are not kept).</summary>
internal sealed class ChatThread
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New chat";
    public string? ProviderId { get; set; }
    public string? Model { get; set; }

    /// <summary>For providers that keep the conversation themselves (Claude Code): their session id.</summary>
    public string? SessionId { get; set; }
    public string? SessionProvider { get; set; }

    /// <summary>The folder that session runs in (Claude Code sessions belong to a folder).</summary>
    public string? SessionFolder { get; set; }

    /// <summary>Project folder the conversation was about.</summary>
    public string? Folder { get; set; }

    public DateTime Updated { get; set; } = DateTime.Now;

    /// <summary>The user chose "allow all" on an approval card: later actions in this conversation run without asking.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool AllowAll { get; set; }
    public List<AiTurn> Turns { get; set; } = new();
}

/// <summary>Conversations on disk, one JSON file each, in the settings folder ("chats").</summary>
internal sealed class ChatStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _folder;

    public ChatStore(string folder) => _folder = folder;

    public string Folder => _folder;

    public List<ChatThread> LoadAll()
    {
        var threads = new List<ChatThread>();
        if (!Directory.Exists(_folder)) return threads;
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<ChatThread>(File.ReadAllText(file), Json) is { Turns.Count: > 0 } thread) threads.Add(thread);
            }
            catch (Exception) { /* a damaged chat file is skipped, never fatal */ }
        }
        return threads.OrderByDescending(t => t.Updated).ToList();
    }

    public void Save(ChatThread thread)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var copy = new ChatThread
            {
                Id = thread.Id, Title = thread.Title, ProviderId = thread.ProviderId, Model = thread.Model,
                SessionId = thread.SessionId, SessionProvider = thread.SessionProvider, SessionFolder = thread.SessionFolder,
                Folder = thread.Folder, Updated = thread.Updated,
                Turns = thread.Turns.Where(t => !t.IsStreaming).Select(t => new AiTurn { Role = t.Role, Text = t.Text, Error = t.Error, Actions = t.Actions.ToList() }).ToList(),
            };
            File.WriteAllText(Path.Combine(_folder, thread.Id + ".json"), JsonSerializer.Serialize(copy, Json));
        }
        catch (Exception) { /* history is a convenience */ }
    }

    public void Delete(ChatThread thread)
    {
        try { File.Delete(Path.Combine(_folder, thread.Id + ".json")); } catch (Exception) { }
    }
}
