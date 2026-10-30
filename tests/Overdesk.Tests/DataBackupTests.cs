using System.IO.Compression;
using Overdesk.Core.Settings;

namespace Overdesk.Tests;

public class DataBackupTests
{
    [Fact]
    public void A_backup_restores_at_the_next_start_and_keeps_the_old_data()
    {
        using var source = new TempFolder();
        source.Write("settings.json", "{\"AccentTheme\":\"Cyber\"}");
        source.Write("works/a.json", "{\"Id\":\"a\"}");
        source.Write("ai-memory/global.json", "{}");
        source.Write("ai-workspace/junk.txt", "not backed up");
        var zip = Path.Combine(source.Path, "backup.zip");
        Assert.Equal(3, DataBackup.Create(source.Path, zip));

        using var target = new TempFolder();
        target.Write("settings.json", "{\"AccentTheme\":\"Crimson\"}");
        target.Write("works/old.json", "{}");
        Assert.Equal(3, DataBackup.Stage(zip, target.Path));
        Assert.True(DataBackup.HasPending(target.Path));

        var settings = new SettingsStore(target.Path).Load(); // applies the staged backup first
        Assert.Equal("Cyber", settings.AccentTheme);
        Assert.True(File.Exists(Path.Combine(target.Path, "works", "a.json")));
        Assert.False(File.Exists(Path.Combine(target.Path, "works", "old.json")));
        Assert.True(File.Exists(Path.Combine(target.Path, ".before-restore", "works", "old.json")));
        Assert.False(DataBackup.HasPending(target.Path));
    }

    [Fact]
    public void Zips_that_are_not_backups_or_climb_out_are_refused()
    {
        using var dir = new TempFolder();
        var other = Path.Combine(dir.Path, "other.zip");
        using (var zip = ZipFile.Open(other, ZipArchiveMode.Create)) zip.CreateEntry("readme.txt");
        Assert.Throws<InvalidDataException>(() => DataBackup.Stage(other, Path.Combine(dir.Path, "data")));

        var evil = Path.Combine(dir.Path, "evil.zip");
        using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            zip.CreateEntry("settings.json");
            zip.CreateEntry("works/../../escaped.txt");
        }
        Assert.Throws<InvalidDataException>(() => DataBackup.Stage(evil, Path.Combine(dir.Path, "data")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "escaped.txt")));
    }
}
