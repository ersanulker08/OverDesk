using System.Text.Json.Nodes;
using Overdesk.AI;

namespace Overdesk.Tests;

public class ToolboxTests
{
    private int _runs;

    private AiToolbox Box() => new(new[]
    {
        new AiTool
        {
            Name = "danger", Description = "Needs a yes.", Schema = AiTool.Object(), Approval = _ => "Do the dangerous thing?",
            Run = (_, _) => { _runs++; return Task.FromResult(new AiToolResult("done")); },
        },
        new AiTool
        {
            Name = "boom", Description = "Throws.", Schema = AiTool.Object(),
            Run = (_, _) => throw new InvalidOperationException("kaput"),
        },
    });

    [Fact]
    public async Task Actions_needing_approval_are_refused_without_an_approver()
    {
        var result = await Box().RunAsync("danger", new JsonObject(), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal(0, _runs);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task The_users_answer_decides(bool allow, int runs)
    {
        var box = Box();
        string? asked = null;
        box.Approve = (question, _) => { asked = question; return Task.FromResult(allow); };
        var result = await box.RunAsync("danger", new JsonObject(), CancellationToken.None);
        Assert.Equal("Do the dangerous thing?", asked);
        Assert.Equal(!allow, result.IsError);
        Assert.Equal(runs, _runs);
    }

    [Fact]
    public async Task Exceptions_become_error_results_and_are_reported()
    {
        var box = Box();
        var failures = new List<string>();
        box.Failed += failures.Add;
        var result = await box.RunAsync("boom", null, CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal("kaput", result.Text);
        Assert.Single(failures);
    }

    [Fact]
    public async Task Unknown_tools_are_errors()
    {
        Assert.True((await Box().RunAsync("nope", null, CancellationToken.None)).IsError);
    }
}

public class AiMemoryTests
{
    [Fact]
    public void Notes_are_kept_per_project_and_survive_a_reload()
    {
        using var dir = new TempFolder();
        var memory = new AiMemory(dir.Path);
        memory.Add(null, "Prefers Turkish replies");
        var note = memory.Add("/work/game", "Uses Unity 6");
        memory.Add("/work/game", "Uses Unity 6"); // duplicates are ignored

        var again = new AiMemory(dir.Path);
        Assert.Single(again.Notes(null));
        Assert.Single(again.Notes("/work/game"));
        Assert.Empty(again.Notes("/work/other"));
        var prompt = again.PromptSection("/work/game");
        Assert.Contains("Prefers Turkish replies", prompt);
        Assert.Contains($"[{note.Id}] Uses Unity 6", prompt);

        Assert.True(again.Remove("/work/game", note.Id));
        Assert.Empty(again.Notes("/work/game"));
    }

    [Fact]
    public async Task The_tool_remembers_and_forgets()
    {
        using var dir = new TempFolder();
        var memory = new AiMemory(dir.Path);
        var box = new AiToolbox(new[] { memory.Tool(() => "/p") });
        var saved = await box.RunAsync("memory", new JsonObject { ["action"] = "remember", ["text"] = "Tabs over spaces", ["scope"] = "project" }, CancellationToken.None);
        Assert.False(saved.IsError);
        var id = memory.Notes("/p").Single().Id;
        var forgot = await box.RunAsync("memory", new JsonObject { ["action"] = "forget", ["id"] = id }, CancellationToken.None);
        Assert.False(forgot.IsError);
        Assert.Empty(memory.Notes("/p"));
    }
}
