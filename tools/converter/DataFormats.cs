using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Overdesk.Tool.Converter;

/// <summary>
/// Tables and trees: JSON, CSV, TSV and XML into each other. A table is a list of rows (objects with the same kind of
/// keys); nested values in a table cell are written as JSON text.
/// </summary>
public static class DataFormats
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Convert(string text, string from, string to)
    {
        from = from.ToLowerInvariant();
        to = to.ToLowerInvariant();
        JsonNode? tree = from switch
        {
            "json" => JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }),
            "csv" => FromTable(ParseDelimited(text, ',')),
            "tsv" => FromTable(ParseDelimited(text, '\t')),
            "xml" => FromXml(XDocument.Parse(text).Root!),
            _ => throw new InvalidOperationException($"Cannot read {from}."),
        };
        return to switch
        {
            "json" => tree?.ToJsonString(Pretty) ?? "null",
            "csv" => WriteDelimited(ToTable(tree), ','),
            "tsv" => WriteDelimited(ToTable(tree), '\t'),
            "xml" => ToXml(tree),
            _ => throw new InvalidOperationException($"Cannot write {to}."),
        };
    }

    // ---------------------------------------------------------------- tables

    /// <summary>RFC 4180 style: quotes around fields with the delimiter, quotes or line breaks; "" inside quotes.</summary>
    public static List<List<string>> ParseDelimited(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0) quoted = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = new List<string>();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    private static JsonArray FromTable(List<List<string>> rows)
    {
        var result = new JsonArray();
        if (rows.Count == 0) return result;
        var headers = rows[0].Select((h, i) => h.Trim().Length > 0 ? h.Trim() : $"column{i + 1}").ToList();
        foreach (var row in rows.Skip(1))
        {
            var item = new JsonObject();
            for (var i = 0; i < headers.Count; i++) item[headers[i]] = Typed(i < row.Count ? row[i] : "");
            result.Add(item);
        }
        return result;
    }

    /// <summary>Numbers, true/false and empty cells become real JSON values; everything else stays text.</summary>
    private static JsonNode? Typed(string value)
    {
        if (value.Length == 0) return null;
        if (value is "true" or "false") return JsonValue.Create(value == "true");
        // Leading zeros (zip codes, ids) stay text.
        if (!(value.Length > 1 && value[0] == '0' && value[1] != '.')
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && !double.IsInfinity(number))
            return value.Contains('.') || value.Contains('e') || value.Contains('E') ? JsonValue.Create(number) : long.TryParse(value, out var whole) ? JsonValue.Create(whole) : JsonValue.Create(number);
        return JsonValue.Create(value);
    }

    /// <summary>Rows from JSON: an array of objects (or an object holding one), a single object, or an array of values.</summary>
    private static List<JsonObject> ToTable(JsonNode? tree)
    {
        if (tree is JsonObject single)
        {
            var arrays = single.Where(p => p.Value is JsonArray).ToList();
            tree = arrays.Count == 1 ? arrays[0].Value : new JsonArray(single.DeepClone());
        }
        if (tree is not JsonArray array) throw new InvalidOperationException("To make a table, the JSON must be a list of objects.");
        return array.Select(item => item as JsonObject ?? new JsonObject { ["value"] = item?.DeepClone() }).ToList();
    }

    private static string WriteDelimited(List<JsonObject> rows, char delimiter)
    {
        var columns = new List<string>();
        foreach (var row in rows)
            foreach (var key in row.Select(p => p.Key))
                if (!columns.Contains(key)) columns.Add(key);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(delimiter, columns.Select(c => Escape(c, delimiter))));
        foreach (var row in rows)
            sb.AppendLine(string.Join(delimiter, columns.Select(c => Escape(Cell(row[c]), delimiter))));
        return sb.ToString();
    }

    private static string Cell(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => node.ToJsonString(),
    };

    private static string Escape(string value, char delimiter)
        => value.IndexOfAny(new[] { delimiter, '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    // ---------------------------------------------------------------- XML

    /// <summary>Elements become objects; attributes become "@name"; repeated children become arrays; text-only elements become values.</summary>
    private static JsonNode? FromXml(XElement element)
    {
        var hasChildren = element.HasElements;
        if (!hasChildren && !element.HasAttributes) return Typed(element.Value);
        var obj = new JsonObject();
        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration)) obj["@" + attribute.Name.LocalName] = Typed(attribute.Value);
        foreach (var group in element.Elements().GroupBy(e => e.Name.LocalName))
        {
            var items = group.Select(FromXml).ToList();
            obj[group.Key] = items.Count == 1 ? items[0] : new JsonArray(items.ToArray());
        }
        if (!hasChildren && element.Value.Length > 0) obj["#text"] = Typed(element.Value);
        return obj;
    }

    private static string ToXml(JsonNode? tree)
    {
        var root = new XElement("root");
        Fill(root, tree);
        // A single top-level object with one key becomes the root itself.
        if (tree is JsonObject { Count: 1 } only && only.First().Value is JsonObject) root = (XElement)root.FirstNode!;
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).Declaration + "\n" + root.ToString();
    }

    private static void Fill(XElement element, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key.StartsWith('@')) { element.SetAttributeValue(Name(key[1..]), Cell(value)); continue; }
                    if (key == "#text") { element.Add(Cell(value)); continue; }
                    if (value is JsonArray items)
                        foreach (var item in items) { var child = new XElement(Name(key)); Fill(child, item); element.Add(child); }
                    else { var child = new XElement(Name(key)); Fill(child, value); element.Add(child); }
                }
                break;
            case JsonArray array:
                foreach (var item in array) { var child = new XElement("item"); Fill(child, item); element.Add(child); }
                break;
            case null:
                break;
            default:
                element.Value = Cell(node);
                break;
        }
    }

    /// <summary>A valid XML element name for any JSON key.</summary>
    private static string Name(string key)
    {
        var name = XmlConvert.EncodeLocalName(key.Length > 0 ? key : "item");
        return name;
    }
}
