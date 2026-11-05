using System.IO.Compression;
using System.Text.Json;
using Overdesk.Core.Settings;
using Overdesk.Core.Tabs;
using Overdesk.Core.Text;
using Overdesk.Core.Works;

namespace Overdesk.Tests;

public class BackupSafetyTests
{
    private static string BrokenZip(TempFolder dir)
    {
        var zipPath = Path.Combine(dir.Path, "evil.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        zip.CreateEntry("settings.json");
        zip.CreateEntry("works/ok.json");
        zip.CreateEntry("works/../../escaped.txt");
        return zipPath;
    }

    [Fact]
    public void A_refused_zip_leaves_nothing_pending_and_settings_survive()
    {
        using var dir = new TempFolder();
        var data = Path.Combine(dir.Path, "data");
        var store = new SettingsStore(data);
        store.Save(new AppSettings { AccentTheme = "Cyber" });
        var zip = BrokenZip(dir);

        Assert.Throws<InvalidDataException>(() => DataBackup.Stage(zip, data));

        Assert.False(DataBackup.HasPending(data));
        Assert.False(Directory.Exists(Path.Combine(data, ".restore-staging")));
        Assert.Equal("Cyber", new SettingsStore(data).Load().AccentTheme);
        Assert.False(File.Exists(store.FilePath + ".broken"));
    }

    [Fact]
    public void A_refused_zip_keeps_an_earlier_staged_restore()
    {
        using var dir = new TempFolder();
        var data = Path.Combine(dir.Path, "data");
        var good = Path.Combine(dir.Path, "good.zip");
        using (var zip = ZipFile.Open(good, ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("settings.json").Open())) w.Write("{\"AccentTheme\":\"Toxic\"}");
        DataBackup.Stage(good, data);

        Assert.Throws<InvalidDataException>(() => DataBackup.Stage(BrokenZip(dir), data));

        Assert.True(DataBackup.HasPending(data));
        Assert.Equal("Toxic", new SettingsStore(data).Load().AccentTheme);
    }

    [Fact]
    public void Applying_twice_never_deletes_the_kept_originals()
    {
        using var dir = new TempFolder();
        var data = Path.Combine(dir.Path, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), "{\"AccentTheme\":\"Crimson\"}");
        var backup = Path.Combine(dir.Path, "b.zip");
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("settings.json").Open())) w.Write("{\"AccentTheme\":\"Cyber\"}");
        DataBackup.Stage(backup, data);

        DataBackup.ApplyPending(data);
        DataBackup.Stage(backup, data);
        DataBackup.ApplyPending(data);

        var kept = Directory.GetDirectories(data, ".before-restore-*");
        Assert.Equal(2, kept.Length);
        Assert.Contains(kept, k => File.ReadAllText(Path.Combine(k, "settings.json")).Contains("Crimson"));
    }
}

public class SettingsStoreLockTests
{
    [Fact]
    public void A_locked_settings_file_is_neither_renamed_nor_overwritten()
    {
        using var dir = new TempFolder();
        var store = new SettingsStore(dir.Path);
        File.WriteAllText(store.FilePath, JsonSerializer.Serialize(new AppSettings { AccentTheme = "Cyber" }));

        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var loaded = store.Load();
            store.Save(loaded);
            Assert.True(store.ReadFailed);
        }

        Assert.False(File.Exists(store.FilePath + ".broken"));
        Assert.Equal("Cyber", new SettingsStore(dir.Path).Load().AccentTheme);
    }
}

public class TextGapTests
{
    [Fact]
    public void Numbered_items_and_star_tasks_become_rows()
    {
        var rows = WorkText.FromMarkdown("1. Install\n2. Build\n* [ ] buy milk\n+ [X] done");
        Assert.Equal(new[] { BlockKind.Bullet, BlockKind.Bullet, BlockKind.Todo, BlockKind.Todo }, rows.Select(r => r.Kind));
        Assert.Equal("1. Install", rows[0].Text);
        Assert.Equal("buy milk", rows[2].Text);
        Assert.True(rows[3].Checked);
    }

    [Fact]
    public void Table_cells_and_list_items_do_not_run_together()
    {
        var text = Markdown.HtmlToText("<table><tr><td>Name</td><td>Price</td></tr></table><ul><li>one</li></ul>");
        Assert.Contains("Name\tPrice", text);
        Assert.Contains("- one", text);
    }
}

public class ReopenAndDuplicateTests
{
    [Fact]
    public void Closed_program_tabs_can_be_reopened_in_reverse_order_but_internal_pages_cannot()
    {
        var tabs = new TabManager();
        var a = tabs.Add("/apps/a.app");
        var settings = tabs.Add(HostedTab.SettingsPath);
        var b = tabs.Add("/apps/b.app");

        tabs.Close(b);
        tabs.Close(settings);
        tabs.Close(a);

        Assert.Equal(0, tabs.PopClosed()!.Index);
        Assert.Equal("/apps/b.app", tabs.PopClosed()!.ProgramPath);
        Assert.Null(tabs.PopClosed());
    }

    [Fact]
    public void Two_files_with_one_id_load_as_one_work()
    {
        using var dir = new TempFolder();
        var library = new WorkLibrary(dir.Path);
        var work = library.Create(WorkTemplates.All[0]);
        File.Copy(Path.Combine(dir.Path, work.Id + ".json"), Path.Combine(dir.Path, "Copy of work.json"));

        var reloaded = new WorkLibrary(dir.Path);
        reloaded.Load();

        Assert.Single(reloaded.Works);
        Assert.True(File.Exists(Path.Combine(dir.Path, work.Id + ".json")));
        reloaded.Delete(reloaded.Works[0]);
        var again = new WorkLibrary(dir.Path);
        again.Load();
        Assert.Empty(again.Works);
    }
}
