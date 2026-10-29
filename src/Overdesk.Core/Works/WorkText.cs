using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Overdesk.Core.Works;

/// <summary>
/// Works as text for the AI: a readable listing with short row ids, and rows built from the JSON the model sends.
/// </summary>
public static class WorkText
{
    /// <summary>Row kinds as the AI names them.</summary>
    public static readonly string[] Kinds = { "heading", "text", "todo", "toggle", "field", "number", "bullet", "quote", "divider" };

    /// <summary>Length of the row ids shown to the AI (a prefix of the full id).</summary>
    public const int ShortId = 8;

    public static string Describe(Work work)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {work.Title}  (id {work.Id}; template {work.Template}; color {work.Color}{(work.Pinned ? "; pinned" : "")})");
        foreach (var block in work.Blocks) sb.AppendLine(DescribeBlock(block));
        return sb.ToString();
    }

    public static string DescribeBlock(WorkBlock b)
    {
        var id = $"[{b.Id[..Math.Min(ShortId, b.Id.Length)]}]";
        return b.Kind switch
        {
            BlockKind.Heading => $"{id} heading{b.Level}: {b.Text}",
            BlockKind.Todo => $"{id} todo [{(b.Checked ? "x" : " ")}]: {b.Text}",
            BlockKind.Toggle => $"{id} toggle: {b.Text} = {(b.Checked ? "on" : "off")}",
            BlockKind.Field => $"{id} field: {b.Text} = {b.Value}",
            BlockKind.Number => $"{id} number: {b.Text} = {b.Number.ToString(CultureInfo.InvariantCulture)}",
            BlockKind.Divider => $"{id} divider",
            _ => $"{id} {b.Kind.ToString().ToLowerInvariant()}: {b.Text}",
        };
    }

    /// <summary>A row by its short id (brackets allowed); null when the id is too short to be safe or unknown.</summary>
    public static WorkBlock? FindBlock(Work work, string id)
    {
        id = id.Trim().Trim('[', ']');
        return id.Length < 4 ? null : work.Blocks.FirstOrDefault(b => b.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A row kind from the AI's word for it ("checkbox", "paragraph"...), or null.</summary>
    public static BlockKind? ParseKind(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "checkbox" or "task" or "todo" => BlockKind.Todo,
        "variable" or "bool" or "toggle" => BlockKind.Toggle,
        "paragraph" or "text" => BlockKind.Text,
        "list" or "bullet" => BlockKind.Bullet,
        "hr" or "divider" => BlockKind.Divider,
        "title" or "heading" => BlockKind.Heading,
        var other => Enum.TryParse<BlockKind>(other, ignoreCase: true, out var k) && Enum.IsDefined(k) ? k : null,
    };

    /// <summary>Rows from [{kind, text, value, checked, number, level}, ...]; plain strings become text rows.</summary>
    public static List<WorkBlock> ParseBlocks(JsonArray blocks)
    {
        var rows = new List<WorkBlock>();
        foreach (var node in blocks)
        {
            if (node is JsonValue plain)
            {
                rows.Add(new WorkBlock { Text = plain.ToString() });
                continue;
            }
            if (node is not JsonObject o) continue;
            var block = new WorkBlock
            {
                Kind = ParseKind(o["kind"]?.ToString() ?? "text") ?? BlockKind.Text,
                Text = o["text"]?.ToString() ?? "",
                Value = o["value"]?.ToString() ?? "",
                Checked = o["checked"] is JsonValue c && (c.TryGetValue<bool>(out var b) ? b : bool.TryParse(c.ToString(), out var p) && p),
                Level = Math.Clamp(o["level"] is JsonValue l && int.TryParse(l.ToString(), out var level) ? level : 2, 1, 3),
            };
            if (o["number"] is JsonValue n && double.TryParse(n.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) block.Number = number;
            rows.Add(block);
        }
        return rows;
    }
}
