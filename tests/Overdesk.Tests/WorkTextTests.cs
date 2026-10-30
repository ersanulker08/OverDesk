using System.Text.Json.Nodes;
using Overdesk.Core.Works;

namespace Overdesk.Tests;

public class WorkTextTests
{
    [Fact]
    public void Blocks_parse_every_kind_and_tolerate_loose_values()
    {
        var json = JsonNode.Parse("""
            [
              {"kind": "heading", "text": "Plan", "level": 1},
              {"kind": "checkbox", "text": "Buy milk", "checked": "true"},
              {"kind": "field", "text": "Name", "value": "Aria"},
              {"kind": "number", "text": "Gold", "number": "12.5"},
              {"kind": "toggle", "text": "hasKey", "checked": true},
              {"kind": "nonsense", "text": "falls back to text"},
              "plain string row"
            ]
            """)!.AsArray();
        var rows = WorkText.ParseBlocks(json);
        Assert.Equal(new[] { BlockKind.Heading, BlockKind.Todo, BlockKind.Field, BlockKind.Number, BlockKind.Toggle, BlockKind.Text, BlockKind.Text },
            rows.Select(r => r.Kind));
        Assert.Equal(1, rows[0].Level);
        Assert.True(rows[1].Checked);
        Assert.Equal("Aria", rows[2].Value);
        Assert.Equal(12.5, rows[3].Number);
        Assert.Equal("plain string row", rows[6].Text);
    }

    [Fact]
    public void Numeric_kind_strings_are_not_accepted()
    {
        Assert.Null(WorkText.ParseKind("42"));
    }

    [Fact]
    public void Describe_shows_short_ids_that_find_the_row_again()
    {
        var work = WorkTemplates.Find("todo").Create();
        var text = WorkText.Describe(work);
        var line = text.Split('\n')[1];
        var id = line[1..(1 + WorkText.ShortId)];
        Assert.Same(work.Blocks[0], WorkText.FindBlock(work, $"[{id}]"));
    }

    [Fact]
    public void Too_short_ids_match_nothing()
    {
        var work = WorkTemplates.Find("todo").Create();
        Assert.Null(WorkText.FindBlock(work, work.Blocks[0].Id[..2]));
    }

    [Fact]
    public void Library_round_trips_through_disk()
    {
        using var dir = new TempFolder();
        var library = new WorkLibrary(dir.Path);
        var work = library.Create(WorkTemplates.Find("character"));
        work.Title = "Aria";
        library.Touch(work);
        library.FlushDirty();

        var again = new WorkLibrary(dir.Path);
        again.Load();
        Assert.Equal("Aria", again.Find(work.Id)!.Title);
        Assert.Equal(work.Blocks.Count, again.Find(work.Id)!.Blocks.Count);
    }
}

public class WorkMarkdownTests
{
    [Fact]
    public void Markdown_becomes_rows()
    {
        var rows = WorkText.FromMarkdown("### You\nPlan it\n\n### AI\n- [x] book flights\n- pack\n> remember\n---\n```\ncode\n\nmore\n```\nlast line");
        Assert.Equal(new[] { BlockKind.Heading, BlockKind.Text, BlockKind.Heading, BlockKind.Todo, BlockKind.Bullet, BlockKind.Quote, BlockKind.Divider, BlockKind.Text, BlockKind.Text },
            rows.Select(r => r.Kind));
        Assert.True(rows[3].Checked);
        Assert.Equal("```\ncode\n\nmore\n```", rows[7].Text);
    }
}

public class ProgramNameTests
{
    [Fact]
    public void Registered_names_win_and_others_fall_back_to_the_file_name()
    {
        Overdesk.Core.Tabs.ProgramNames.Register(@"C:\Apps\chrome.exe", "Google Chrome");
        Assert.Equal("Google Chrome", Overdesk.Core.Tabs.ProgramNames.FromPath(@"C:\Apps\chrome.exe"));
        Assert.Equal("Calculator", Overdesk.Core.Tabs.ProgramNames.FromPath("/System/Applications/Calculator.app/"));
        Assert.Equal("tool", Overdesk.Core.Tabs.ProgramNames.FromPath("/nowhere/tool.exe"));
    }
}
