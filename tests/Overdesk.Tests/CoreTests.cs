using Overdesk.Core.Settings;
using Overdesk.Core.Tabs;
using Overdesk.Core.Works;

namespace Overdesk.Tests;

public class TabManagerTests
{
    [Fact]
    public void ClosingActiveTabActivatesLeftNeighbour()
    {
        var tabs = new TabManager();
        var a = tabs.Add("a");
        var b = tabs.Add("b");
        var c = tabs.Add("c");
        tabs.Activate(b);

        tabs.Close(b);

        Assert.Same(a, tabs.Active);
        Assert.True(a.IsActive);
        Assert.False(b.IsActive);
        Assert.Equal(new[] { a, c }, tabs.Tabs);
    }

    [Fact]
    public void ClosingFirstActiveTabActivatesNewFirst()
    {
        var tabs = new TabManager();
        var a = tabs.Add("a");
        var b = tabs.Add("b");
        tabs.Activate(a);

        tabs.Close(a);

        Assert.Same(b, tabs.Active);
    }

    [Fact]
    public void ClosingLastTabShowsHome()
    {
        var tabs = new TabManager();
        var a = tabs.Add("a");

        tabs.Close(a);
        tabs.Close(a);

        Assert.Null(tabs.Active);
        Assert.Empty(tabs.Tabs);
    }

    [Fact]
    public void ClosingBackgroundTabKeepsActive()
    {
        var tabs = new TabManager();
        var a = tabs.Add("a");
        var b = tabs.Add("b");
        tabs.Activate(a);

        tabs.Close(b);

        Assert.Same(a, tabs.Active);
    }

    [Fact]
    public void ActivateIgnoresForeignTab()
    {
        var tabs = new TabManager();
        var a = tabs.Add("a");

        tabs.Activate(new HostedTab("other", null));

        Assert.Same(a, tabs.Active);
    }

    [Fact]
    public void ActivateNextWrapsBothWays()
    {
        var tabs = new TabManager();
        var a = tabs.Add("a");
        tabs.Add("b");
        var c = tabs.Add("c");

        tabs.ActivateNext(1);
        Assert.Same(a, tabs.Active);
        tabs.ActivateNext(-1);
        Assert.Same(c, tabs.Active);
    }

    [Fact]
    public void ActivateNextDoesNothingOnHome()
    {
        var tabs = new TabManager();
        tabs.Add("a");
        tabs.ShowHome();

        tabs.ActivateNext(1);

        Assert.Null(tabs.Active);
    }

    [Fact]
    public void MoveClampsIndex()
    {
        var tabs = new TabManager();
        var a = tabs.Add("a");
        var b = tabs.Add("b");
        var c = tabs.Add("c");

        tabs.Move(a, 99);
        tabs.Move(c, -5);

        Assert.Equal(new[] { c, b, a }, tabs.Tabs);
    }

    [Fact]
    public void LoadKeepsOnlyOneActiveTab()
    {
        var tabs = new TabManager();
        var a = tabs.Add("a");
        var b = new HostedTab("b", null) { IsActive = true };

        tabs.Load(new[] { b }, b);

        Assert.False(a.IsActive);
        Assert.Same(b, tabs.Active);
        Assert.True(b.IsActive);
        Assert.Single(tabs.Tabs);
    }
}

public class ProgramHistoryTests
{
    [Fact]
    public void TouchMovesToFrontWithoutDuplicates()
    {
        var recent = new List<string> { "/a", "/b", "/c" };

        ProgramHistory.Touch(recent, "/b/");

        Assert.Equal(new[] { "/b/", "/a", "/c" }, recent);
    }

    [Fact]
    public void TouchCapsTheList()
    {
        var recent = new List<string>();
        for (var i = 0; i < ProgramHistory.MaxRecent + 5; i++) ProgramHistory.Touch(recent, "/p" + i);

        Assert.Equal(ProgramHistory.MaxRecent, recent.Count);
        Assert.Equal("/p" + (ProgramHistory.MaxRecent + 4), recent[0]);
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void RoundTripsSettings()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(System.IO.Path.Combine(temp.Path, "data"));

        store.Save(new AppSettings { AccentTheme = "Cyber", ToolsPanelWidth = 512 });
        var loaded = store.Load();

        Assert.Equal("Cyber", loaded.AccentTheme);
        Assert.Equal(512, loaded.ToolsPanelWidth);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        using var temp = new TempFolder();

        var loaded = new SettingsStore(temp.Path).Load();

        Assert.Equal(new AppSettings().AccentTheme, loaded.AccentTheme);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    public void BrokenFileIsKeptAndDefaultsReturned(string content)
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.Path);
        temp.Write("settings.json", content);

        var loaded = store.Load();

        Assert.Equal(new AppSettings().AccentTheme, loaded.AccentTheme);
        Assert.True(File.Exists(store.FilePath + ".broken"));
        Assert.False(File.Exists(store.FilePath));
    }
}

public class WorkLibraryTests
{
    [Fact]
    public void CreatedWorkSurvivesReload()
    {
        using var temp = new TempFolder();
        var library = new WorkLibrary(temp.Path);
        var work = library.Create(WorkTemplates.All[0]);

        var reloaded = new WorkLibrary(temp.Path);
        reloaded.Load();

        Assert.NotNull(reloaded.Find(work.Id));
    }

    [Fact]
    public void FlushWritesEditedWorkAndClearsDirty()
    {
        using var temp = new TempFolder();
        var library = new WorkLibrary(temp.Path);
        var work = library.Create(WorkTemplates.All[0]);
        work.Title = "Edited";

        library.Touch(work);
        Assert.True(library.HasDirty);
        library.FlushDirty();

        Assert.False(library.HasDirty);
        var reloaded = new WorkLibrary(temp.Path);
        reloaded.Load();
        Assert.Equal("Edited", reloaded.Find(work.Id)!.Title);
    }

    [Fact]
    public void FailedFlushStaysDirtyUntilTheDiskWorksAgain()
    {
        using var temp = new TempFolder();
        var folder = System.IO.Path.Combine(temp.Path, "works");
        var library = new WorkLibrary(folder);
        var work = library.Create(WorkTemplates.All[0]);
        work.Title = "Edited";
        library.Touch(work);

        // A file where the folder should be makes every write fail.
        Directory.Delete(folder, recursive: true);
        File.WriteAllText(folder, "blocker");
        library.FlushDirty();
        Assert.True(library.HasDirty);

        File.Delete(folder);
        library.FlushDirty();

        Assert.False(library.HasDirty);
        var reloaded = new WorkLibrary(folder);
        reloaded.Load();
        Assert.Equal("Edited", reloaded.Find(work.Id)!.Title);
    }

    [Fact]
    public void DeleteRemovesFileAndPendingSave()
    {
        using var temp = new TempFolder();
        var library = new WorkLibrary(temp.Path);
        var work = library.Create(WorkTemplates.All[0]);
        library.Touch(work);

        library.Delete(work);
        library.FlushDirty();

        Assert.False(library.HasDirty);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.json"));
    }

    [Fact]
    public void DamagedFileIsSkipped()
    {
        using var temp = new TempFolder();
        var library = new WorkLibrary(temp.Path);
        var work = library.Create(WorkTemplates.All[0]);
        temp.Write("bad.json", "{ nope");

        var reloaded = new WorkLibrary(temp.Path);
        reloaded.Load();

        Assert.Single(reloaded.Works);
        Assert.NotNull(reloaded.Find(work.Id));
    }
}
