using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Overdesk.Tool.Converter;

/// <summary>
/// The rest of the data formats: TOML, INI, .env, Java .properties, JSON Lines and Apple property lists both ways, and
/// tables written as Markdown, an HTML page or SQL. Everything goes through the same JSON tree as the others.
/// </summary>
public static partial class DataFormats
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static JsonNode? ReadMore(string text, string from) => from switch
    {
        "toml" => new Toml(text).Read(),
        "ini" or "cfg" or "conf" => ReadIni(text),
        "env" => ReadKeyValues(text, env: true),
        "properties" => ReadKeyValues(text, env: false),
        "jsonl" or "ndjson" => new JsonArray(text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)).ToArray()),
        "plist" => ReadPlist(XDocument.Parse(text).Root!.Elements().FirstOrDefault()),
        _ => throw new InvalidOperationException($"Cannot read {from}."),
    };

    private static string WriteMore(JsonNode? tree, string to) => to switch
    {
        "toml" => WriteToml(tree),
        "ini" => WriteIni(tree),
        "env" => WriteKeyValues(tree, env: true),
        "properties" => WriteKeyValues(tree, env: false),
        "plist" => WritePlist(tree),
        _ => throw new InvalidOperationException($"Cannot write {to}."),
    };

    // ---------------------------------------------------------------- tables out

    private static string WriteTable(List<JsonObject> rows, string to)
    {
        var columns = new List<string>();
        foreach (var row in rows)
            foreach (var key in row.Select(p => p.Key))
                if (!columns.Contains(key)) columns.Add(key);
        var sb = new StringBuilder();
        switch (to)
        {
            case "jsonl":
                foreach (var row in rows) sb.Append(row.ToJsonString()).Append('\n');
                break;
            case "md":
                string Md(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
                sb.Append("| ").Append(string.Join(" | ", columns.Select(Md))).Append(" |\n");
                sb.Append('|').Append(string.Join('|', columns.Select(c => rows.All(r => r[c] is null or JsonValue && IsNumber(r[c])) ? " ---: " : " --- "))).Append("|\n");
                foreach (var row in rows) sb.Append("| ").Append(string.Join(" | ", columns.Select(c => Md(Cell(row[c]))))).Append(" |\n");
                break;
            case "html":
                sb.Append("<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>Table</title><style>")
                  .Append("body{font:14px system-ui,sans-serif;margin:24px;color:#222}table{border-collapse:collapse}")
                  .Append("th,td{border:1px solid #ddd;padding:6px 10px;text-align:left;vertical-align:top}th{background:#f4f4f6}tr:nth-child(even) td{background:#fafafa}")
                  .Append("</style></head><body>\n<table>\n<thead><tr>");
                foreach (var c in columns) sb.Append("<th>").Append(WebUtility.HtmlEncode(c)).Append("</th>");
                sb.Append("</tr></thead>\n<tbody>\n");
                foreach (var row in rows)
                {
                    sb.Append("<tr>");
                    foreach (var c in columns) sb.Append("<td>").Append(WebUtility.HtmlEncode(Cell(row[c]))).Append("</td>");
                    sb.Append("</tr>\n");
                }
                sb.Append("</tbody>\n</table>\n</body></html>\n");
                break;
            case "sql":
                string Id(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
                string Type(string c)
                {
                    var values = rows.Select(r => r[c]).Where(v => v is not null).ToList();
                    if (values.Count == 0) return "TEXT";
                    if (values.All(v => v is JsonValue j && j.GetValueKind() is JsonValueKind.True or JsonValueKind.False)) return "BOOLEAN";
                    if (values.All(v => v is JsonValue j && j.TryGetValue<long>(out _))) return "INTEGER";
                    return values.All(IsNumber) ? "REAL" : "TEXT";
                }
                string Literal(JsonNode? v) => v switch
                {
                    null => "NULL",
                    JsonValue j when j.GetValueKind() is JsonValueKind.True => "TRUE",
                    JsonValue j when j.GetValueKind() is JsonValueKind.False => "FALSE",
                    JsonValue j when IsNumber(j) => j.ToJsonString(),
                    _ => "'" + Cell(v).Replace("'", "''") + "'",
                };
                sb.Append("CREATE TABLE ").Append(Id("data")).Append(" (\n");
                sb.Append(string.Join(",\n", columns.Select(c => $"  {Id(c)} {Type(c)}"))).Append("\n);\n\n");
                foreach (var row in rows)
                    sb.Append("INSERT INTO ").Append(Id("data")).Append(" (").Append(string.Join(", ", columns.Select(Id)))
                      .Append(") VALUES (").Append(string.Join(", ", columns.Select(c => Literal(row[c])))).Append(");\n");
                break;
        }
        return sb.ToString();
    }

    private static bool IsNumber(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number;

    // ---------------------------------------------------------------- INI

    private static JsonObject ReadIni(string text)
    {
        var root = new JsonObject();
        var section = root;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                section = root[name] as JsonObject ?? new JsonObject();
                root[name] = section;
                continue;
            }
            var split = line.IndexOfAny(new[] { '=', ':' });
            if (split <= 0) continue;
            var value = line[(split + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'') value = value[1..^1];
            section[line[..split].Trim()] = Typed(value);
        }
        return root;
    }

    private static string WriteIni(JsonNode? tree)
    {
        if (tree is not JsonObject obj) obj = new JsonObject { ["items"] = tree?.DeepClone() };
        var sb = new StringBuilder();
        string Value(JsonNode? v) => v is JsonValue ? Cell(v) : v?.ToJsonString() ?? "";
        foreach (var (key, value) in obj.Where(p => p.Value is not JsonObject)) sb.Append(key).Append(" = ").Append(Value(value)).Append('\n');
        foreach (var (key, value) in obj.Where(p => p.Value is JsonObject))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append('[').Append(key).Append("]\n");
            foreach (var (k, v) in (JsonObject)value!) sb.Append(k).Append(" = ").Append(Value(v)).Append('\n');
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- .env and .properties

    private static JsonObject ReadKeyValues(string text, bool env)
    {
        var root = new JsonObject();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || (!env && line[0] == '!')) continue;
            if (env && line.StartsWith("export ")) line = line[7..].TrimStart();
            var split = env ? line.IndexOf('=') : line.IndexOfAny(new[] { '=', ':' });
            if (split <= 0) continue;
            var key = line[..split].Trim();
            var value = line[(split + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = Regex.Unescape(value[1..^1]);
            else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value[1..^1];
            else if (env && value.IndexOf(" #", StringComparison.Ordinal) is var hash and > 0) value = value[..hash].TrimEnd();
            root[key] = JsonValue.Create(value);
        }
        return root;
    }

    /// <summary>Nested keys are joined: DATABASE_HOST for .env, database.host for .properties.</summary>
    private static string WriteKeyValues(JsonNode? tree, bool env)
    {
        var sb = new StringBuilder();
        void Walk(JsonNode? node, string prefix)
        {
            if (node is JsonObject obj)
            {
                foreach (var (k, v) in obj) Walk(v, prefix.Length == 0 ? k : prefix + (env ? "_" : ".") + k);
                return;
            }
            if (node is JsonArray array && array.All(i => i is JsonObject or JsonArray))
            {
                for (var i = 0; i < array.Count; i++) Walk(array[i], prefix + (env ? "_" : ".") + i);
                return;
            }
            var key = env ? Regex.Replace(prefix.ToUpperInvariant(), "[^A-Z0-9_]", "_") : prefix;
            var value = node is JsonValue or null ? Cell(node) : node.ToJsonString();
            if (env && value.IndexOfAny(new[] { ' ', '#', '"', '\'', '\n', '$', '=' }) >= 0)
                value = "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
            if (!env) value = value.Replace("\\", "\\\\").Replace("\n", "\\n");
            sb.Append(key.Length > 0 ? key : "VALUE").Append('=').Append(value).Append('\n');
        }
        Walk(tree, "");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- property lists

    private static JsonNode? ReadPlist(XElement? element)
    {
        if (element is null) return null;
        switch (element.Name.LocalName)
        {
            case "dict":
                var obj = new JsonObject();
                string? key = null;
                foreach (var child in element.Elements())
                {
                    if (child.Name.LocalName == "key") key = child.Value;
                    else if (key is not null) { obj[key] = ReadPlist(child); key = null; }
                }
                return obj;
            case "array": return new JsonArray(element.Elements().Select(ReadPlist).ToArray());
            case "integer": return long.TryParse(element.Value, NumberStyles.Integer, Inv, out var l) ? JsonValue.Create(l) : JsonValue.Create(element.Value);
            case "real": return double.TryParse(element.Value, NumberStyles.Float, Inv, out var d) ? JsonValue.Create(d) : JsonValue.Create(element.Value);
            case "true": return JsonValue.Create(true);
            case "false": return JsonValue.Create(false);
            default: return JsonValue.Create(element.Value.Trim());
        }
    }

    private static string WritePlist(JsonNode? tree)
    {
        static XElement Element(JsonNode? node) => node switch
        {
            JsonObject obj => new XElement("dict", obj.Where(p => p.Value is not null).SelectMany(p => new[] { new XElement("key", p.Key), Element(p.Value) })),
            JsonArray array => new XElement("array", array.Select(Element)),
            JsonValue v when v.GetValueKind() == JsonValueKind.True => new XElement("true"),
            JsonValue v when v.GetValueKind() == JsonValueKind.False => new XElement("false"),
            JsonValue v when v.TryGetValue<long>(out var l) => new XElement("integer", l),
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => new XElement("real", v.ToJsonString()),
            null => new XElement("string"),
            _ => new XElement("string", Cell(node)),
        };
        var plist = new XElement("plist", new XAttribute("version", "1.0"), Element(tree));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" + plist + "\n";
    }

    // ---------------------------------------------------------------- TOML

    private static string WriteToml(JsonNode? tree)
    {
        if (tree is not JsonObject root) root = new JsonObject { ["items"] = tree?.DeepClone() };
        var sb = new StringBuilder();
        static string Key(string k) => Regex.IsMatch(k, "^[A-Za-z0-9_-]+$") ? k : Str(k);
        static string Str(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
        static string Inline(JsonNode? node) => node switch
        {
            JsonObject obj => "{ " + string.Join(", ", obj.Where(p => p.Value is not null).Select(p => $"{Key(p.Key)} = {Inline(p.Value)}")) + " }",
            JsonArray array => "[" + string.Join(", ", array.Where(i => i is not null).Select(Inline)) + "]",
            JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number => v.ToJsonString(),
            _ => Str(Cell(node)),
        };
        static bool IsTableArray(JsonNode? n) => n is JsonArray { Count: > 0 } a && a.All(i => i is JsonObject);

        void Table(JsonObject obj, string path)
        {
            foreach (var (k, v) in obj)
                if (v is not null and not JsonObject && !IsTableArray(v)) sb.Append(Key(k)).Append(" = ").Append(Inline(v)).Append('\n');
            foreach (var (k, v) in obj)
            {
                var full = path.Length == 0 ? Key(k) : path + "." + Key(k);
                if (v is JsonObject child)
                {
                    sb.Append('\n').Append('[').Append(full).Append("]\n");
                    Table(child, full);
                }
                else if (IsTableArray(v))
                    foreach (var item in (JsonArray)v!)
                    {
                        sb.Append('\n').Append("[[").Append(full).Append("]]\n");
                        Table((JsonObject)item!, full);
                    }
            }
        }
        Table(root, "");
        return sb.ToString().TrimStart('\n');
    }

    /// <summary>A TOML 1.0 reader: tables, arrays of tables, dotted keys, all string kinds, numbers, dates (kept as text), inline tables.</summary>
    private sealed class Toml(string text)
    {
        private int _i;

        public JsonObject Read()
        {
            var root = new JsonObject();
            var current = root;
            while (true)
            {
                SkipBlank();
                if (_i >= text.Length) return root;
                if (text[_i] == '[')
                {
                    var array = _i + 1 < text.Length && text[_i + 1] == '[';
                    _i += array ? 2 : 1;
                    var path = Keys();
                    Expect(']');
                    if (array) Expect(']');
                    var parent = root;
                    for (var k = 0; k < path.Count - 1; k++) parent = Child(parent, path[k]);
                    var last = path[^1];
                    if (array)
                    {
                        var list = parent[last] as JsonArray ?? new JsonArray();
                        parent[last] = list;
                        current = new JsonObject();
                        list.Add(current);
                    }
                    else current = Child(parent, last);
                    EndOfLine();
                    continue;
                }
                KeyValue(current);
                EndOfLine();
            }
        }

        private static JsonObject Child(JsonObject parent, string key)
        {
            switch (parent[key])
            {
                case JsonObject obj: return obj;
                case JsonArray { Count: > 0 } list when list[^1] is JsonObject last: return last;
                default:
                    var created = new JsonObject();
                    parent[key] = created;
                    return created;
            }
        }

        private void KeyValue(JsonObject table)
        {
            var path = Keys();
            Expect('=');
            var target = table;
            for (var k = 0; k < path.Count - 1; k++) target = Child(target, path[k]);
            target[path[^1]] = Value();
        }

        private List<string> Keys()
        {
            var keys = new List<string>();
            while (true)
            {
                SkipSpaces();
                if (_i >= text.Length) throw Error("a key");
                var c = text[_i];
                if (c is '"' or '\'') keys.Add(String());
                else
                {
                    var start = _i;
                    while (_i < text.Length && (char.IsLetterOrDigit(text[_i]) || text[_i] is '_' or '-')) _i++;
                    if (_i == start) throw Error("a key");
                    keys.Add(text[start.._i]);
                }
                SkipSpaces();
                if (_i < text.Length && text[_i] == '.') { _i++; continue; }
                return keys;
            }
        }

        private JsonNode? Value()
        {
            SkipSpaces();
            if (_i >= text.Length) throw Error("a value");
            var c = text[_i];
            if (c is '"' or '\'') return JsonValue.Create(String());
            if (c == '[')
            {
                _i++;
                var array = new JsonArray();
                while (true)
                {
                    SkipBlank();
                    if (_i < text.Length && text[_i] == ']') { _i++; return array; }
                    array.Add(Value());
                    SkipBlank();
                    if (_i < text.Length && text[_i] == ',') { _i++; continue; }
                    SkipBlank();
                    Expect(']');
                    return array;
                }
            }
            if (c == '{')
            {
                _i++;
                var obj = new JsonObject();
                SkipSpaces();
                if (_i < text.Length && text[_i] == '}') { _i++; return obj; }
                while (true)
                {
                    KeyValue(obj);
                    SkipSpaces();
                    if (_i < text.Length && text[_i] == ',') { _i++; continue; }
                    Expect('}');
                    return obj;
                }
            }
            var start = _i;
            // Dates may hold one space ("1979-05-27 07:32:00").
            while (_i < text.Length && text[_i] is not (',' or ']' or '}' or '\n' or '\r' or '#')
                   && !(text[_i] == ' ' && !(_i + 1 < text.Length && char.IsDigit(text[_i + 1]) && _i - start == 10))) _i++;
            var token = text[start.._i].Trim();
            if (token == "true") return JsonValue.Create(true);
            if (token == "false") return JsonValue.Create(false);
            var clean = token.Replace("_", "");
            if (clean.StartsWith("0x")) return JsonValue.Create(System.Convert.ToInt64(clean[2..], 16));
            if (clean.StartsWith("0o")) return JsonValue.Create(System.Convert.ToInt64(clean[2..], 8));
            if (clean.StartsWith("0b")) return JsonValue.Create(System.Convert.ToInt64(clean[2..], 2));
            if (long.TryParse(clean, NumberStyles.AllowLeadingSign, Inv, out var whole)) return JsonValue.Create(whole);
            if (!clean.Contains(':') && clean.Count(ch => ch == '-') <= 1 + (clean.Contains("e-") ? 1 : 0)
                && double.TryParse(clean, NumberStyles.Float, Inv, out var number)) return JsonValue.Create(number);
            if (token.Length == 0) throw Error("a value");
            return JsonValue.Create(token); // dates, times, inf, nan
        }

        private string String()
        {
            var quote = text[_i];
            var multi = _i + 2 < text.Length && text[_i + 1] == quote && text[_i + 2] == quote;
            _i += multi ? 3 : 1;
            if (multi && _i < text.Length && text[_i] == '\n') _i++;
            else if (multi && _i + 1 < text.Length && text[_i] == '\r' && text[_i + 1] == '\n') _i += 2;
            var sb = new StringBuilder();
            while (true)
            {
                if (_i >= text.Length) throw Error("the end of a string");
                var c = text[_i];
                if (c == quote && (!multi || (_i + 2 < text.Length && text[_i + 1] == quote && text[_i + 2] == quote)))
                {
                    _i += multi ? 3 : 1;
                    return sb.ToString();
                }
                if (c == '\\' && quote == '"')
                {
                    var e = text[++_i];
                    _i++;
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u': sb.Append(char.ConvertFromUtf32(System.Convert.ToInt32(text.Substring(_i, 4), 16))); _i += 4; break;
                        case 'U': sb.Append(char.ConvertFromUtf32(System.Convert.ToInt32(text.Substring(_i, 8), 16))); _i += 8; break;
                        case '\n' or '\r' or ' ' or '\t' when multi:
                            while (_i < text.Length && char.IsWhiteSpace(text[_i])) _i++;
                            break;
                        default: sb.Append(e); break;
                    }
                    continue;
                }
                if (!multi && c == '\n') throw Error("the end of a string");
                sb.Append(c);
                _i++;
            }
        }

        private void SkipSpaces()
        {
            while (_i < text.Length && text[_i] is ' ' or '\t') _i++;
        }

        /// <summary>Spaces, line breaks and comments.</summary>
        private void SkipBlank()
        {
            while (_i < text.Length)
            {
                if (char.IsWhiteSpace(text[_i])) _i++;
                else if (text[_i] == '#') while (_i < text.Length && text[_i] != '\n') _i++;
                else return;
            }
        }

        private void EndOfLine()
        {
            SkipSpaces();
            if (_i < text.Length && text[_i] == '#') while (_i < text.Length && text[_i] != '\n') _i++;
            if (_i < text.Length && text[_i] is not ('\n' or '\r')) throw Error("a new line");
        }

        private void Expect(char c)
        {
            SkipSpaces();
            if (_i >= text.Length || text[_i] != c) throw Error($"'{c}'");
            _i++;
        }

        private FormatException Error(string expected)
        {
            var line = text.Take(Math.Min(_i, text.Length)).Count(ch => ch == '\n') + 1;
            return new FormatException($"TOML: expected {expected} on line {line}.");
        }
    }
}
