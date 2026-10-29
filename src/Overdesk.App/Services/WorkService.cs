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

    public WorkService(string folder, AppSettings settings)
    {
        Settings = settings;
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

    /// <summary>A work should open in its own tab / in the tools panel.</summary>
    public event Action<Work>? TabRequested;
    public event Action<Work>? PanelRequested;

    /// <summary>The dirty works were just written to disk.</summary>
    public event Action? Saved;

    public void OpenInTab(Work work) => TabRequested?.Invoke(work);
    public void OpenInPanel(Work work) => PanelRequested?.Invoke(work);

    public void Flush()
    {
        if (!Library.HasDirty) return;
        Library.FlushDirty();
        Saved?.Invoke();
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
