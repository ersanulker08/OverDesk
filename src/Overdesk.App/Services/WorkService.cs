using Avalonia.Media;
using Avalonia.Threading;
using Overdesk.Core.Settings;
using Overdesk.Core.Works;

namespace Overdesk.App.Services;

/// <summary>
/// Glue between the works on disk and the three places that show them (home hub, side panel, tab):
/// debounced autosave, "open here" requests and the fonts a work resolves to.
/// </summary>
internal sealed class WorkService
{
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private (string Title, string Body, double Size) _defaults;

    public WorkService(string folder, AppSettings settings)
    {
        Settings = settings;
        _defaults = (settings.WorkTitleFont, settings.WorkBodyFont, settings.WorkFontSize);
        Library = new WorkLibrary(folder);
        Library.Load();
        Library.Changed += (work, _) =>
        {
            if (work is null) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Flush();
        };
    }

    public AppSettings Settings { get; }
    public WorkLibrary Library { get; }

    /// <summary>A work should open in its own tab (focusTitle: it was just made, so the title box takes the keyboard) / in the tools panel.</summary>
    public event Action<Work, bool>? TabRequested;
    public event Action<Work>? PanelRequested;

    /// <summary>The user or the AI deleted a work; a copy went to the trash file.</summary>
    public event Action<Work, string>? Deleted;

    /// <summary>The default fonts or size in Settings changed; works without their own keep up.</summary>
    public event Action? DefaultsChanged;

    /// <summary>The dirty works were just written to disk.</summary>
    public event Action? Saved;

    /// <summary>Writing to disk failed (message); the work stays dirty and the save is retried.</summary>
    public event Action<string>? SaveFailed;

    /// <summary>The user asked the AI to do something with a work (instruction), or to talk about it (null).</summary>
    public event Action<Work, string?>? AiRequested;

    public void AskAi(Work work, string? instruction) => AiRequested?.Invoke(work, instruction);

    /// <summary>The user wants the AI to make a new work from a description.</summary>
    public event Action? AiCreateRequested;

    public void CreateWithAi() => AiCreateRequested?.Invoke();

    public void OpenInTab(Work work, bool focusTitle = false) => TabRequested?.Invoke(work, focusTitle);
    public void OpenInPanel(Work work) => PanelRequested?.Invoke(work);

    /// <summary>Delete with a trash copy; null when the copy could not be written (the work stays).</summary>
    public string? Delete(Work work)
    {
        if (Library.Delete(work) is not { } trash) return null;
        Deleted?.Invoke(work, trash);
        return trash;
    }

    /// <summary>Called on any settings change; the views only hear about it when the work defaults moved.</summary>
    public void NotifyDefaultsChanged()
    {
        var now = (Settings.WorkTitleFont, Settings.WorkBodyFont, Settings.WorkFontSize);
        if (now == _defaults) return;
        _defaults = now;
        DefaultsChanged?.Invoke();
    }

    public void Flush()
    {
        if (!Library.HasDirty) return;
        if (Library.FlushDirty())
        {
            Saved?.Invoke();
            return;
        }
        SaveFailed?.Invoke(Library.LastSaveError ?? "the disk is not writable");
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public string TitleFontName(Work work) => work.TitleFont ?? Settings.WorkTitleFont;
    public string BodyFontName(Work work) => work.BodyFont ?? Settings.WorkBodyFont;
    public FontFamily TitleFont(Work work) => WorkFonts.Resolve(TitleFontName(work));
    public FontFamily BodyFont(Work work) => WorkFonts.Resolve(BodyFontName(work));
    public double FontSize(Work work) => work.FontSize > 0 ? work.FontSize : Math.Clamp(Settings.WorkFontSize, 11, 32);

    public static string Ago(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 2) return "yesterday";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} days ago";
        return time.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }
}
