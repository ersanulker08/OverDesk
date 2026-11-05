using Overdesk.Core.Plugins;
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

    [Theory]
    [InlineData(2, 1, 3, 0)]
    [InlineData(0, -1, 3, 2)]
    [InlineData(-1, 1, 3, 0)]
    [InlineData(-1, -1, 3, 2)]
    public void CycleWrapsBothWaysAndStartsFromNothing(int current, int step, int count, int expected)
        => Assert.Equal(expected, TabManager.Cycle(current, step, count));

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
        var library = new WorkLibrary(System.IO.Path.Combine(temp.Path, "works"));
        var work = library.Create(WorkTemplates.All[0]);
        library.Touch(work);

        library.Delete(work);
        library.FlushDirty();

        Assert.False(library.HasDirty);
        Assert.Empty(Directory.GetFiles(library.Folder, "*.json"));
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

public class WebUrlTests
{
    [Theory]
    [InlineData("github.com", "https://github.com/")]
    [InlineData("localhost:5173", "http://localhost:5173/")]
    [InlineData("127.0.0.1:8000/docs", "http://127.0.0.1:8000/docs")]
    [InlineData("192.168.1.20:8080", "http://192.168.1.20:8080/")]
    [InlineData("[::1]:3000", "http://[::1]:3000/")]
    [InlineData("8.8.8.8", "https://8.8.8.8/")]
    [InlineData("https://localhost:5173", "https://localhost:5173/")]
    public void NormalizeChoosesTheScheme(string typed, string expected) => Assert.Equal(expected, WebUrl.Normalize(typed));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://example.com")]
    public void NormalizeRefusesWhatIsNoWebAddress(string typed) => Assert.Null(WebUrl.Normalize(typed));

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("localhost:5173/app", true)]
    [InlineData("192.168.1.20:8080", true)]
    [InlineData("[::1]:3000", true)]
    [InlineData("github.com", true)]
    [InlineData("https://intranet/wiki", true)]
    [InlineData("hello", false)]
    [InlineData("how to center a div", false)]
    [InlineData("what is a.b", false)]
    [InlineData("notes/readme.md", false)]
    [InlineData("", false)]
    public void IsAddressTellsAddressesFromSearches(string typed, bool expected) => Assert.Equal(expected, WebUrl.IsAddress(typed));

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("app.localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.31.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("nas", true)]
    [InlineData("printer.local", true)]
    [InlineData("github.com", false)]
    [InlineData("8.8.8.8", false)]
    public void IsPrivateHostStaysOffPublicNames(string host, bool expected) => Assert.Equal(expected, WebUrl.IsPrivateHost(host));
}

public class PluginRegistryTests
{
    private const string Good = """{ "id": "a.good", "name": "Good", "run": "process", "entry": "bin/good" }""";

    private static PluginRegistry Discover(TempFolder folder)
    {
        var registry = new PluginRegistry();
        registry.Discover(new[] { folder.Path });
        return registry;
    }

    [Fact]
    public void BrokenManifestsAreReportedWithTheirPathAndNeverStopTheOthers()
    {
        using var folder = new TempFolder();
        folder.Write("a-good/overdesk.plugin.json", Good);
        var invalid = Path.GetFullPath(folder.Write("b-invalid/overdesk.plugin.json", "{ not json"));
        var incomplete = Path.GetFullPath(folder.Write("c-incomplete/overdesk.plugin.json", """{ "id": "c", "name": "C", "run": "inProcess", "assembly": "c.dll" }"""));
        var duplicate = Path.GetFullPath(folder.Write("d-duplicate/overdesk.plugin.json", Good));

        var registry = Discover(folder);

        Assert.Equal("a.good", Assert.Single(registry.Tools).Id);
        Assert.Equal(3, registry.Problems.Count);
        Assert.StartsWith(invalid, registry.Problems[0]);
        Assert.Contains("typeName", registry.Problems[1]);
        Assert.StartsWith(incomplete, registry.Problems[1]);
        Assert.Contains("duplicate id", registry.Problems[2]);
        Assert.StartsWith(duplicate, registry.Problems[2]);
    }

    [Fact]
    public void AiOnAProcessPluginIsReportedButTheToolStays()
    {
        using var folder = new TempFolder();
        folder.Write("tool/overdesk.plugin.json", """{ "id": "a.tool", "name": "Tool", "run": "process", "entry": "bin/tool", "ai": true }""");

        var registry = Discover(folder);

        Assert.Single(registry.Tools);
        Assert.Contains("\"ai\"", Assert.Single(registry.Problems));
    }
}
