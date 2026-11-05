using Overdesk.Core.Works;

namespace Overdesk.Tests;

public class WorkTrashAndHistoryTests
{
    private static WorkLibrary NewLibrary(TempFolder temp) => new(System.IO.Path.Combine(temp.Path, "works"));

    [Fact]
    public void DeleteKeepsTheLatestUnsavedStateInTrashAndRestoreBringsItBack()
    {
        using var temp = new TempFolder();
        var library = NewLibrary(temp);
        var work = library.Create(WorkTemplates.All[0]);
        work.Title = "Unsaved title";
        library.Touch(work);

        var trashed = library.Delete(work);

        Assert.NotNull(trashed);
        Assert.Null(library.Find(work.Id));
        Assert.Empty(Directory.GetFiles(library.Folder, "*.json"));
        var listed = Assert.Single(library.ListTrash());
        Assert.Equal("Unsaved title", listed.Title);

        var restored = library.Restore(listed.File);

        Assert.NotNull(restored);
        Assert.Equal(work.Id, restored!.Id);
        Assert.Empty(library.ListTrash());
        var reloaded = NewLibrary(temp);
        reloaded.Load();
        Assert.Equal("Unsaved title", reloaded.Find(work.Id)!.Title);
    }

    [Fact]
    public void RestoreGivesANewIdWhenTheOldOneIsTaken()
    {
        using var temp = new TempFolder();
        var library = NewLibrary(temp);
        var work = library.Create(WorkTemplates.All[0]);
        var trashed = library.Delete(work)!;
        library.Create(WorkTemplates.All[0]).Id = work.Id;

        var restored = library.Restore(trashed);

        Assert.NotNull(restored);
        Assert.NotEqual(work.Id, restored!.Id);
    }

    [Fact]
    public void DeleteLeavesTheWorkWhenTrashCannotBeWritten()
    {
        using var temp = new TempFolder();
        var library = NewLibrary(temp);
        var work = library.Create(WorkTemplates.All[0]);
        File.WriteAllText(library.TrashFolder, "blocker");

        Assert.Null(library.Delete(work));
        Assert.NotNull(library.Find(work.Id));
        Assert.Single(Directory.GetFiles(library.Folder, "*.json"));
    }

    [Fact]
    public void FlushReportsFailureAndTheReason()
    {
        using var temp = new TempFolder();
        var library = NewLibrary(temp);
        var work = library.Create(WorkTemplates.All[0]);
        library.Touch(work);
        Directory.Delete(library.Folder, recursive: true);
        File.WriteAllText(library.Folder, "blocker");

        Assert.False(library.FlushDirty());
        Assert.NotNull(library.LastSaveError);

        File.Delete(library.Folder);
        Assert.True(library.FlushDirty());
        Assert.Null(library.LastSaveError);
    }

    [Fact]
    public void DamagedFilesAreListed()
    {
        using var temp = new TempFolder();
        var library = NewLibrary(temp);
        temp.Write("works/bad.json", "{ nope");
        library.Load();

        Assert.Single(library.LoadErrors);
    }

    [Fact]
    public void SearchFindsRowContent()
    {
        var work = new Work { Title = "Sheet" };
        work.Blocks.Add(new WorkBlock { Kind = BlockKind.Field, Text = "currentLocation", Value = "Village" });
        work.Blocks.Add(new WorkBlock { Kind = BlockKind.Number, Text = "gold", Number = 120 });

        Assert.True(work.ContainsText("village"));
        Assert.True(work.ContainsText("120"));
        Assert.False(work.ContainsText("castle"));
    }

    [Fact]
    public void LeavingAFieldKeepsItsValue()
    {
        var block = new WorkBlock { Kind = BlockKind.Field, Text = "Name", Value = "Aria Vale" };
        block.ConvertTo(BlockKind.Text);

        Assert.Equal("Name: Aria Vale", block.Text);
        Assert.Equal("", block.Value);
    }

    [Fact]
    public void HistoryRestoresRowsWithTheirIdsAndIsBounded()
    {
        var history = new BlockHistory();
        var rows = new List<WorkBlock> { new() { Text = "a" }, new() { Text = "b" } };
        var ids = rows.Select(r => r.Id).ToList();

        history.Push(rows);
        rows[0].Text = "changed";
        rows.RemoveAt(1);
        var undone = history.Pop()!;

        Assert.Equal(new[] { "a", "b" }, undone.Select(r => r.Text));
        Assert.Equal(ids, undone.Select(r => r.Id));
        Assert.Null(history.Pop());

        for (var i = 0; i < 80; i++) history.Push(rows);
        var count = 0;
        while (history.Pop() is not null) count++;
        Assert.Equal(50, count);
    }
}
