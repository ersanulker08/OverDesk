using System.Text;
using System.Text.Json.Nodes;
using Overdesk.Core.Plugins;

namespace Overdesk.Tool.Converter;

/// <summary>Entry point named by <c>typeName</c> in overdesk.plugin.json. Also lets the AI convert files.</summary>
public sealed class ConverterPlugin : IToolPlugin, IAiToolSource
{
    public object CreateView() => new ConverterView();

    public IReadOnlyList<PluginAiTool> AiTools { get; } = new[]
    {
        new PluginAiTool(
            "convert_files",
            "Convert files to another format with OverDesk's Converter: images (PNG, JPG, WebP; HEIC, TIFF, PDF on macOS; optional resize), " +
            "data (JSON, CSV, TSV, XML), Markdown to HTML or Word, documents (DOCX, RTF, ODT, HTML, TXT on macOS), audio and video. " +
            "Results are written next to the originals (never overwriting). Call without \"to\" to see which formats each file can become.",
            """
            {"type":"object","properties":{
              "paths":{"type":"array","items":{"type":"string"},"description":"Absolute paths of the files."},
              "to":{"type":"string","description":"Target extension, e.g. png, jpg, webp, csv, json, html, docx, mp3, mp4."},
              "max_size":{"type":"integer","description":"Images: longest edge in pixels."},
              "quality":{"type":"integer","description":"JPG / WebP / HEIC quality 1-100 (default 90)."},
              "output_folder":{"type":"string","description":"Folder for the results; default next to each original."}
            },"required":["paths"]}
            """,
            RunAsync)
        {
            Label = json =>
            {
                var args = JsonNode.Parse(json);
                var count = (args?["paths"] as JsonArray)?.Count ?? 0;
                var files = count == 1 ? Path.GetFileName(args!["paths"]![0]!.ToString()) : $"{count} files";
                return args?["to"]?.ToString() is { Length: > 0 } to ? $"Converting {files} to {to.ToUpperInvariant()}" : $"Checking formats for {files}";
            },
        },
    };

    private static async Task<string> RunAsync(string json, CancellationToken cancellation)
    {
        var args = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        var paths = (args["paths"] as JsonArray)?.Select(p => p?.ToString() ?? "").Where(p => p.Length > 0).ToList() ?? new List<string>();
        if (paths.Count == 0) throw new InvalidOperationException("No files given.");
        var to = args["to"]?.ToString();
        var sb = new StringBuilder();
        if (string.IsNullOrWhiteSpace(to))
        {
            foreach (var path in paths)
                sb.AppendLine($"{path}: {(File.Exists(path) ? string.Join(", ", Engine.TargetsFor(path)) is { Length: > 0 } t ? t : "no conversion available" : "file not found")}");
            return sb.ToString();
        }
        var options = new ConvertOptions
        {
            Quality = args["quality"] is JsonValue q && int.TryParse(q.ToString(), out var quality) ? quality : 90,
            MaxSize = args["max_size"] is JsonValue m && int.TryParse(m.ToString(), out var max) ? max : null,
            OutputFolder = args["output_folder"]?.ToString() is { Length: > 0 } folder ? folder : null,
        };
        if (options.OutputFolder is { } output) Directory.CreateDirectory(output);
        var failures = 0;
        foreach (var path in paths)
        {
            try { sb.AppendLine($"{path} → {await Engine.ConvertAsync(path, to, options, null, cancellation)}"); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                sb.AppendLine($"{path}: failed: {ex.Message}");
            }
        }
        if (failures == paths.Count) throw new InvalidOperationException(sb.ToString());
        return sb.ToString();
    }
}
