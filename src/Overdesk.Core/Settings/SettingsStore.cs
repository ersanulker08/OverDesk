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

    public AppSettings Load()
    {
        // A backup restored last time is applied now, before anything reads the data folder.
        try { DataBackup.ApplyPending(Directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* keep the current data */ }
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Keep the unreadable file around for inspection and start fresh.
            try { File.Move(FilePath, FilePath + ".broken", overwrite: true); } catch (IOException) { }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
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
