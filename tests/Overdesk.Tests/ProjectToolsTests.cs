using System.Text.Json.Nodes;
using Overdesk.AI;

namespace Overdesk.Tests;

public class ProjectToolsTests
{
    private static AiToolbox Box(string folder, bool edits = true) => new(ProjectTools.Create(folder, edits));

    private static Task<AiToolResult> Run(AiToolbox box, string tool, object args)
        => box.RunAsync(tool, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args)) as JsonObject, CancellationToken.None);

    [Fact]
    public void Read_only_folder_has_no_write_tools()
    {
        using var dir = new TempFolder();
        var names = ProjectTools.Create(dir.Path, allowEdits: false).Select(t => t.Name).ToList();
        Assert.Contains("read_file", names);
        Assert.DoesNotContain("write_file", names);
        Assert.DoesNotContain("edit_file", names);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    public async Task Paths_outside_the_project_are_refused(string path)
    {
        using var dir = new TempFolder();
        var box = Box(dir.Path);
        var read = await Run(box, "read_file", new { path });
        Assert.True(read.IsError);
        var write = await Run(box, "write_file", new { path, content = "x" });
        Assert.True(write.IsError);
    }

    [Fact]
    public async Task A_sibling_folder_with_the_same_prefix_is_outside()
    {
        using var dir = new TempFolder();
        var sibling = dir.Path + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            var result = await Run(Box(dir.Path), "write_file", new { path = sibling + "/x.txt", content = "x" });
            Assert.True(result.IsError);
            Assert.False(File.Exists(Path.Combine(sibling, "x.txt")));
        }
        finally { Directory.Delete(sibling, true); }
    }

    [Fact]
    public async Task Git_folder_is_never_written()
    {
        using var dir = new TempFolder();
        var result = await Run(Box(dir.Path), "write_file", new { path = ".git/config", content = "x" });
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Write_then_read_round_trips_with_line_numbers()
    {
        using var dir = new TempFolder();
        var box = Box(dir.Path);
        Assert.False((await Run(box, "write_file", new { path = "src/a.txt", content = "one\ntwo\nthree" })).IsError);
        var read = await Run(box, "read_file", new { path = "src/a.txt", offset = 2, limit = 1 });
        Assert.Contains("2  two", read.Text);
        Assert.DoesNotContain("three", read.Text.Split('\n')[0]);
    }

    [Fact]
    public async Task Edit_needs_a_unique_match_unless_replace_all()
    {
        using var dir = new TempFolder();
        dir.Write("a.cs", "var x = 1;\nvar x = 1;\n");
        var box = Box(dir.Path);
        var twice = await Run(box, "edit_file", new { path = "a.cs", old_text = "var x = 1;", new_text = "var y = 2;" });
        Assert.True(twice.IsError);
        var all = await Run(box, "edit_file", new { path = "a.cs", old_text = "var x = 1;", new_text = "var y = 2;", replace_all = true });
        Assert.False(all.IsError);
        Assert.Equal("var y = 2;\nvar y = 2;\n", File.ReadAllText(Path.Combine(dir.Path, "a.cs")));
    }

    [Fact]
    public async Task Edit_matches_text_in_files_with_windows_line_endings()
    {
        using var dir = new TempFolder();
        dir.Write("w.txt", "alpha\r\nbeta\r\ngamma\r\n");
        var result = await Run(Box(dir.Path), "edit_file", new { path = "w.txt", old_text = "alpha\nbeta", new_text = "alpha\nBETA" });
        Assert.False(result.IsError, result.Text);
        Assert.Equal("alpha\r\nBETA\r\ngamma\r\n", File.ReadAllText(Path.Combine(dir.Path, "w.txt")));
    }

    [Fact]
    public async Task Search_skips_dependency_folders()
    {
        using var dir = new TempFolder();
        dir.Write("src/app.js", "const needle = 1;");
        dir.Write("node_modules/lib/index.js", "const needle = 2;");
        var result = await Run(Box(dir.Path), "search_files", new { pattern = "needle" });
        Assert.Contains("src/app.js:1", result.Text);
        Assert.DoesNotContain("node_modules", result.Text);
    }

    [Fact]
    public async Task Binary_files_are_not_read()
    {
        using var dir = new TempFolder();
        File.WriteAllBytes(Path.Combine(dir.Path, "b.bin"), new byte[] { 1, 0, 2, 3 });
        Assert.True((await Run(Box(dir.Path), "read_file", new { path = "b.bin" })).IsError);
    }

    [Fact]
    public void Overview_names_the_stack_and_readme()
    {
        using var dir = new TempFolder();
        dir.Write("package.json", "{\"name\":\"demo\",\"scripts\":{\"dev\":\"vite\"}}");
        dir.Write("README.md", "# Demo app\nIt does things.");
        dir.Write("src/main.ts", "console.log(1)");
        var overview = ProjectTools.Overview(dir.Path);
        Assert.Contains("package.json", overview);
        Assert.Contains("Demo app", overview);
        Assert.Contains("scripts: dev", overview);
        Assert.Contains("src/", overview);
    }

    [Fact]
    public async Task Images_come_back_as_pictures_with_their_type()
    {
        using var dir = new TempFolder();
        File.WriteAllBytes(Path.Combine(dir.Path, "shot.jpg"), new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10 });
        var result = await Run(Box(dir.Path), "read_file", new { path = "shot.jpg" });
        Assert.False(result.IsError);
        Assert.NotNull(result.Image);
        Assert.Equal("image/jpeg", AiImages.MediaType(result.Image!));
        Assert.Equal("image/png", AiImages.MediaType(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }));
        Assert.Equal("image/webp", AiImages.MediaType(System.Text.Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ")));
    }
}
