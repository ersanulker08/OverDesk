using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Overdesk.Tool.Converter;

/// <summary>
/// YAML to and from JSON trees. Writes block style; reads the common subset: nested mappings and sequences by
/// indentation, plain / single / double quoted scalars, comments, inline [a, b] and {a: 1}, | and > blocks, and
/// several documents (--- separated) as a list. Anchors, tags and complex keys are not supported.
/// </summary>
public static class Yaml
{
    // ---------------------------------------------------------------- writing

    public static string Write(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteNode(sb, node, 0, inSequence: false);
        return sb.ToString().TrimEnd() + "\n";
    }

    private static void WriteNode(StringBuilder sb, JsonNode? node, int indent, bool inSequence)
    {
        var pad = new string(' ', indent);
        switch (node)
        {
            case JsonObject obj when obj.Count > 0:
                var first = true;
                foreach (var (key, value) in obj)
                {
                    // In a list, the first key sits on the "- " line.
                    if (!(inSequence && first)) sb.Append(pad);
                    first = false;
                    sb.Append(Key(key)).Append(':');
                    WriteValue(sb, value, indent);
                }
                break;
            case JsonArray array when array.Count > 0:
                foreach (var item in array)
                {
                    if (!(inSequence && ReferenceEquals(item, array[0]))) sb.Append(pad);
                    sb.Append("- ");
                    if (item is JsonObject { Count: > 0 } or JsonArray { Count: > 0 }) WriteNode(sb, item, indent + 2, inSequence: true);
                    else sb.Append(Scalar(item)).Append('\n');
                }
                break;
            default:
                sb.Append(Scalar(node)).Append('\n');
                break;
        }
    }

    private static void WriteValue(StringBuilder sb, JsonNode? value, int indent)
    {
        switch (value)
        {
            case JsonObject { Count: > 0 }:
                sb.Append('\n');
                WriteNode(sb, value, indent + 2, inSequence: false);
                break;
            case JsonArray { Count: > 0 }:
                sb.Append('\n');
                WriteNode(sb, value, indent + 2, inSequence: false);
                break;
            // Multi-line text as a block: "|" keeps one final line break, "|-" none (more than one: quoted instead).
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Contains('\n') && !s.EndsWith("\n\n") && s == s.TrimStart(' '):
                sb.Append(s.EndsWith('\n') ? " |\n" : " |-\n");
                foreach (var line in s.TrimEnd('\n').Split('\n')) sb.Append(new string(' ', indent + 2)).Append(line).Append('\n');
                break;
            default:
                sb.Append(' ').Append(Scalar(value)).Append('\n');
                break;
        }
    }

    private static string Key(string key) => NeedsQuotes(key) ? Quote(key) : key;

    private static string Scalar(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "{}",
        JsonArray => "[]",
        JsonValue v when v.TryGetValue<string>(out var s) => NeedsQuotes(s) ? Quote(s) : s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        _ => node.ToJsonString(),
    };

    /// <summary>Strings that YAML would read as something else (numbers, booleans, null) or that hold special characters.</summary>
    private static bool NeedsQuotes(string s)
        => s.Length == 0 || s != s.Trim() || s.Contains(": ") || s.Contains(" #") || s.EndsWith(':') || "-?:,[]{}#&*!|>'\"%@`".Contains(s[0])
           || s is "true" or "false" or "null" or "~" or "yes" or "no" or "on" or "off" or "True" or "False" or "Null" or "Yes" or "No"
           || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _) || s.Any(char.IsControl);

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

    // ---------------------------------------------------------------- reading

    private sealed record Line(int Indent, string Text, int Number);

    public static JsonNode? Read(string yaml)
    {
        var documents = new List<List<Line>> { new() };
        var raw = yaml.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < raw.Length; i++)
        {
            var text = raw[i];
            if (text.TrimEnd() == "---") { if (documents[^1].Count > 0) documents.Add(new List<Line>()); continue; }
            if (text.TrimEnd() == "...") continue;
            if (text.TrimStart().StartsWith('#') || text.Trim().Length == 0)
            {
                // Blank lines matter inside | blocks; keep them as empty text.
                if (text.Trim().Length == 0) documents[^1].Add(new Line(-1, "", i + 1));
                continue;
            }
            if (text.Contains('\t') && text.TrimStart() != text && text[..(text.Length - text.TrimStart().Length)].Contains('\t'))
                throw new FormatException($"Line {i + 1}: YAML does not allow tabs for indentation.");
            documents[^1].Add(new Line(text.Length - text.TrimStart().Length, text.TrimStart(), i + 1));
        }
        var parsed = documents.Where(d => d.Any(l => l.Indent >= 0)).Select(d =>
        {
            var position = 0;
            return ParseBlock(d, ref position, -1);
        }).ToList();
        return parsed.Count == 1 ? parsed[0] : new JsonArray(parsed.ToArray());
    }

    private static void SkipBlank(List<Line> lines, ref int i)
    {
        while (i < lines.Count && lines[i].Indent < 0) i++;
    }

    /// <summary>The node whose lines are indented more than <paramref name="parentIndent"/>.</summary>
    private static JsonNode? ParseBlock(List<Line> lines, ref int i, int parentIndent)
    {
        SkipBlank(lines, ref i);
        if (i >= lines.Count || lines[i].Indent <= parentIndent) return null;
        var indent = lines[i].Indent;
        if (IsSequenceItem(lines[i].Text)) return ParseSequence(lines, ref i, indent);
        if (SplitKey(lines[i].Text) is not null) return ParseMapping(lines, ref i, indent);
        // A lone scalar (possibly folded over several lines).
        var text = new List<string>();
        while (i < lines.Count && (lines[i].Indent < 0 || lines[i].Indent >= indent)) { if (lines[i].Indent >= 0) text.Add(lines[i].Text); i++; }
        return ParseScalar(string.Join(" ", text));
    }

    private static bool IsSequenceItem(string text) => text == "-" || text.StartsWith("- ", StringComparison.Ordinal);

    private static JsonArray ParseSequence(List<Line> lines, ref int i, int indent)
    {
        var array = new JsonArray();
        while (true)
        {
            SkipBlank(lines, ref i);
            if (i >= lines.Count || lines[i].Indent != indent || !IsSequenceItem(lines[i].Text)) break;
            var line = lines[i];
            var rest = line.Text.Length > 1 ? line.Text[2..].TrimStart() : "";
            if (rest.Length == 0)
            {
                i++;
                array.Add(ParseBlock(lines, ref i, indent));
                continue;
            }
            // "- key: value" starts a mapping whose keys line up with "key".
            var itemIndent = indent + (line.Text.Length - line.Text[1..].TrimStart().Length);
            if (IsSequenceItem(rest) || (SplitKey(rest) is not null && !rest.StartsWith('{') && !rest.StartsWith('[')))
            {
                lines[i] = new Line(itemIndent, rest, line.Number);
                array.Add(ParseBlock(lines, ref i, indent));
                continue;
            }
            i++;
            array.Add(ScalarOrBlock(rest, lines, ref i, indent));
        }
        return array;
    }

    private static JsonObject ParseMapping(List<Line> lines, ref int i, int indent)
    {
        var obj = new JsonObject();
        while (true)
        {
            SkipBlank(lines, ref i);
            if (i >= lines.Count || lines[i].Indent != indent) break;
            if (SplitKey(lines[i].Text) is not { } pair) throw new FormatException($"Line {lines[i].Number}: expected \"key: value\".");
            i++;
            JsonNode? value;
            if (pair.Value.Length == 0)
            {
                SkipBlank(lines, ref i);
                // Sequences may sit at the same indent as their key.
                value = i < lines.Count && lines[i].Indent == indent && IsSequenceItem(lines[i].Text)
                    ? ParseSequence(lines, ref i, indent)
                    : ParseBlock(lines, ref i, indent);
            }
            else value = ScalarOrBlock(pair.Value, lines, ref i, indent);
            obj[pair.Key] = value;
        }
        return obj;
    }

    /// <summary>A value after "key:" or "- ": a | or > block, an inline [..] / {..}, or a scalar.</summary>
    private static JsonNode? ScalarOrBlock(string text, List<Line> lines, ref int i, int indent)
    {
        text = StripComment(text);
        if (text is "|" or "|-" or "|+" or ">" or ">-" or ">+")
        {
            var block = new List<string>();
            int? blockIndent = null;
            while (i < lines.Count && (lines[i].Indent < 0 || lines[i].Indent > indent))
            {
                if (lines[i].Indent >= 0) blockIndent ??= lines[i].Indent;
                block.Add(lines[i].Indent < 0 ? "" : new string(' ', lines[i].Indent - (blockIndent ?? lines[i].Indent)) + lines[i].Text);
                i++;
            }
            while (block.Count > 0 && block[^1].Length == 0) block.RemoveAt(block.Count - 1);
            var joined = text[0] == '|' ? string.Join("\n", block) : string.Join(" ", block.Select(b => b.Length == 0 ? "\n" : b)).Replace(" \n ", "\n");
            return JsonValue.Create(text.EndsWith('-') ? joined : joined + "\n");
        }
        if (text.StartsWith('[') || text.StartsWith('{'))
        {
            // Inline collections may continue on the next lines.
            var buffer = new StringBuilder(text);
            while (!Balanced(buffer.ToString()) && i < lines.Count) buffer.Append(' ').Append(StripComment(lines[i++].Text));
            var position = 0;
            return ParseFlow(buffer.ToString(), ref position);
        }
        return ParseScalar(text);
    }

    private static bool Balanced(string text)
    {
        var depth = 0;
        char? quote = null;
        foreach (var c in text)
        {
            if (quote is not null) { if (c == quote) quote = null; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c is '[' or '{') depth++;
            else if (c is ']' or '}') depth--;
        }
        return depth <= 0;
    }

    private static JsonNode? ParseFlow(string text, ref int p)
    {
        while (p < text.Length && text[p] == ' ') p++;
        if (p >= text.Length) return null;
        if (text[p] == '[')
        {
            p++;
            var array = new JsonArray();
            while (true)
            {
                while (p < text.Length && text[p] is ' ' or ',') p++;
                if (p >= text.Length || text[p] == ']') { p++; return array; }
                array.Add(ParseFlow(text, ref p));
            }
        }
        if (text[p] == '{')
        {
            p++;
            var obj = new JsonObject();
            while (true)
            {
                while (p < text.Length && text[p] is ' ' or ',') p++;
                if (p >= text.Length || text[p] == '}') { p++; return obj; }
                var key = FlowToken(text, ref p, ':');
                p++; // ':'
                obj[Unquote(key)] = ParseFlow(text, ref p);
            }
        }
        return ParseScalar(FlowToken(text, ref p, ',', ']', '}'));
    }

    private static string FlowToken(string text, ref int p, params char[] stops)
    {
        var start = p;
        if (text[p] is '"' or '\'')
        {
            var quote = text[p++];
            while (p < text.Length && !(text[p] == quote && text[p - 1] != '\\')) p++;
            p++;
            return text[start..p];
        }
        while (p < text.Length && !stops.Contains(text[p])) p++;
        return text[start..p].Trim();
    }

    /// <summary>"key: value" (the key may be quoted); null when the line is not a mapping entry.</summary>
    private static (string Key, string Value)? SplitKey(string text)
    {
        if (text.StartsWith('"') || text.StartsWith('\''))
        {
            var end = text.IndexOf(text[0], 1);
            while (end > 0 && text[0] == '"' && text[end - 1] == '\\') end = text.IndexOf('"', end + 1);
            if (end > 0 && end + 1 < text.Length && text[end + 1] == ':' && (end + 2 == text.Length || text[end + 2] == ' '))
                return (Unquote(text[..(end + 1)]), text[(end + 2)..].Trim());
            return null;
        }
        if (text.StartsWith('[') || text.StartsWith('{')) return null;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '#' && i > 0 && text[i - 1] == ' ') return null;
            if (text[i] == ':' && (i + 1 == text.Length || text[i + 1] == ' '))
                return (text[..i].Trim(), text[(i + 1)..].Trim());
        }
        return null;
    }

    private static string StripComment(string text)
    {
        if (text.StartsWith('"') || text.StartsWith('\'')) return text;
        var hash = text.IndexOf(" #", StringComparison.Ordinal);
        return hash >= 0 ? text[..hash].TrimEnd() : text;
    }

    private static JsonNode? ParseScalar(string text)
    {
        text = StripComment(text).Trim();
        if (text.Length == 0 || text is "~" or "null" or "Null" or "NULL") return null;
        if (text.StartsWith('"') || text.StartsWith('\'')) return JsonValue.Create(Unquote(text));
        if (text is "true" or "True" or "TRUE" or "yes" or "Yes" or "on") return JsonValue.Create(true);
        if (text is "false" or "False" or "FALSE" or "no" or "No" or "off") return JsonValue.Create(false);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) && !(text.Length > 1 && text[0] == '0')) return JsonValue.Create(whole);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && text.Any(char.IsDigit) && !(text.Length > 1 && text[0] == '0' && text[1] != '.'))
            return JsonValue.Create(number);
        return JsonValue.Create(text);
    }

    private static string Unquote(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'') return text[1..^1].Replace("''", "'");
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            var sb = new StringBuilder();
            for (var i = 1; i < text.Length - 1; i++)
            {
                if (text[i] != '\\' || i + 1 >= text.Length - 1) { sb.Append(text[i]); continue; }
                var c = text[++i];
                sb.Append(c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => c });
            }
            return sb.ToString();
        }
        return text;
    }
}
