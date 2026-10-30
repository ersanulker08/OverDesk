using System.Text.Json.Nodes;
using Overdesk.Core.Plugins;

namespace Overdesk.Tests;

/// <summary>The bundled tools' AI tools: valid schemas, exact results.</summary>
public class PluginAiToolTests
{
    private static IReadOnlyList<PluginAiTool> All() =>
        new Overdesk.Tool.DevKit.DevKitPlugin().AiTools.Concat(new Overdesk.Tool.Converter.ConverterPlugin().AiTools).ToList();

    [Fact]
    public void Every_schema_is_a_json_object_with_properties()
    {
        foreach (var tool in All())
        {
            var schema = JsonNode.Parse(tool.Parameters) as JsonObject;
            Assert.NotNull(schema);
            Assert.Equal("object", schema!["type"]!.GetValue<string>());
            Assert.NotNull(schema["properties"]);
            Assert.Matches("^[a-z_]+$", tool.Name);
        }
    }

    [Fact]
    public async Task DevKit_hashes_exactly()
    {
        var devkit = All().Single(t => t.Name == "devkit");
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            await devkit.Run("""{"utility":"SHA-256","input":"hello"}""", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => devkit.Run("""{"utility":"nope"}""", CancellationToken.None));
    }

    [Fact]
    public async Task Files_are_hashed()
    {
        using var dir = new TempFolder();
        var path = dir.Write("a.txt", "hello");
        var result = await All().Single(t => t.Name == "hash_file").Run(new JsonObject { ["path"] = path }.ToJsonString(), CancellationToken.None);
        Assert.Contains("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", result);
        Assert.Contains("5d41402abc4b2a76b9719d911017c592", result);
    }
}
