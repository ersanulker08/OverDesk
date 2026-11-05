using System.Text.Json;

namespace Overdesk.Core.Settings;

/// <summary>Reads and writes <see cref="AppSettings"/> as JSON. A broken file never stops the app.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public SettingsStore(string directory) => Directory = directory;

    public string Directory { get; }
    public string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>Default per-user folder: %APPDATA%\Overdesk on Windows, ~/Library/Application Support/Overdesk on macOS.</summary>
    public static string DefaultDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Overdesk");

    /// <summary>Set when settings.json exists but could not be read (locked, not corrupt); saving is skipped so it is not overwritten.</summary>
    public bool ReadFailed { get; private set; }

    /// <summary>The error from a restore that could not be applied at start, if any.</summary>
    public string? RestoreError { get; private set; }

    public AppSettings Load()
    {
        // A backup restored last time is applied now, before anything reads the data folder.
        try { DataBackup.ApplyPending(Directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RestoreError = ex.Message; }
        ReadFailed = false;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(FilePath)) return new AppSettings();
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch (FileNotFoundException)
            {
                return new AppSettings();
            }
            catch (JsonException)
            {
                // Keep the unreadable file around for inspection and start fresh.
                try { File.Move(FilePath, FilePath + ".broken", overwrite: true); } catch (IOException) { }
                return new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Probably locked for a moment (sync, antivirus): the file is fine, so never rename or overwrite it.
                if (attempt < 4) { Thread.Sleep(150); continue; }
                ReadFailed = true;
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        if (ReadFailed) return;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to save must not crash the app.
        }
    }
}
