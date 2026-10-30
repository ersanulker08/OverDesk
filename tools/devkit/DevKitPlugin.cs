using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Overdesk.Core.Plugins;

namespace Overdesk.Tool.DevKit;

/// <summary>
/// Entry point named by <c>typeName</c> in overdesk.plugin.json. The AI gets the same utilities, so hashes, Base64,
/// UUIDs and timestamps are computed exactly instead of guessed.
/// </summary>
public sealed class DevKitPlugin : IToolPlugin, IAiToolSource
{
    public object CreateView() => new DevKitView();

    public IReadOnlyList<PluginAiTool> AiTools { get; } = new[]
    {
        new PluginAiTool(
            "devkit",
            "Exact developer utilities (never compute these yourself): " + string.Join(", ", Converters.All.Select(c => c.Name)) +
            ". Give the utility's name and the input text. For the hash of a file, use hash_file.",
            $$"""
            {"type":"object","properties":{
              "utility":{"type":"string","enum":[{{string.Join(",", Converters.All.Select(c => $"\"{c.Name}\""))}}]},
              "input":{"type":"string","description":"The text to work on (empty is fine for UUID and Unix time)."}
            },"required":["utility"]}
            """,
            (json, _) =>
            {
                var args = JsonNode.Parse(json);
                var name = args?["utility"]?.ToString() ?? "";
                var converter = Converters.All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidOperationException($"No utility is called \"{name}\".");
                return Task.FromResult(converter.Run(args?["input"]?.ToString() ?? ""));
            })
        {
            Label = json => $"DevKit: {JsonNode.Parse(json)?["utility"]}",
        },
        new PluginAiTool(
            "hash_file",
            "SHA-256 (and MD5) of a file on this computer, e.g. to check a download against its published checksum.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Absolute path of the file."}},"required":["path"]}""",
            async (json, cancellation) =>
            {
                var path = JsonNode.Parse(json)?["path"]?.ToString() ?? "";
                if (!File.Exists(path)) throw new FileNotFoundException("No such file.", path);
                await using var stream = File.OpenRead(path);
                var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellation));
                stream.Position = 0;
                var md5 = Convert.ToHexStringLower(await MD5.HashDataAsync(stream, cancellation));
                return $"{Path.GetFileName(path)} ({new FileInfo(path).Length:N0} bytes)\nSHA-256  {sha}\nMD5      {md5}";
            })
        {
            Label = json => $"Hashing {Path.GetFileName(JsonNode.Parse(json)?["path"]?.ToString() ?? "a file")}",
        },
    };
}
