namespace Overdesk.Core.Tabs;

public enum TabStatus { Starting, Running, Exited, Failed }

/// <summary>A program hosted by Overdesk as a tab in the top bar.</summary>
public sealed class HostedTab : ObservableObject
{
    private string _title;
    private TabStatus _status = TabStatus.Starting;
    private string? _message;
    private bool _isActive;
    private bool _isBusy;
    private string? _badge;

    /// <summary>Path of Overdesk's own settings page when it is open as a tab.</summary>
    public const string SettingsPath = "overdesk://settings";

    /// <summary>Prefix of the path of a work opened as a tab: "overdesk://work/&lt;id&gt;".</summary>
    public const string WorkPrefix = "overdesk://work/";

    public static string WorkPath(string workId) => WorkPrefix + workId;

    public HostedTab(string programPath, string? title = null)
    {
        ProgramPath = programPath;
        IsInternal = programPath.StartsWith("overdesk://", StringComparison.Ordinal);
        _title = title ?? (IsInternal ? "OverDesk" : ProgramNames.FromPath(programPath));
        if (IsInternal) _status = TabStatus.Running; // a page of Overdesk itself: nothing to launch
    }

    /// <summary>True for Overdesk's own pages (settings, works), false for hosted programs.</summary>
    public bool IsInternal { get; }

    public bool IsSettings => ProgramPath == SettingsPath;

    /// <summary>True when the tab shows one of the user's works.</summary>
    public bool IsWork => ProgramPath.StartsWith(WorkPrefix, StringComparison.Ordinal);

    public string? WorkId => IsWork ? ProgramPath[WorkPrefix.Length..] : null;

    public Guid Id { get; } = Guid.NewGuid();
    public string ProgramPath { get; }

    public string Title { get => _title; set => Set(ref _title, value); }
    public TabStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value)) OnPropertyChanged(nameof(HasProblem));
        }
    }

    /// <summary>Error or hint shown in the content area (null when all is fine).</summary>
    public string? Message { get => _message; set => Set(ref _message, value); }

    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    /// <summary>Unread count or similar the OS shows for the program ("3", "•"), or null.</summary>
    public string? Badge { get => _badge; set => Set(ref _badge, value); }

    /// <summary>True while the program is being launched and its window is being looked for.</summary>
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    /// <summary>True when the last launch failed or the program has exited.</summary>
    public bool HasProblem => _status is TabStatus.Failed or TabStatus.Exited;
}

/// <summary>
/// The name a program is shown with. macOS apps are named by their bundle; on Windows the Start menu shortcut's name
/// ("Google Chrome") is registered by the program catalog, and other .exe files use their file description.
/// </summary>
public static class ProgramNames
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remember the display name of a program path (the catalog does this for Start menu shortcuts).</summary>
    public static void Register(string path, string name)
    {
        if (!string.IsNullOrWhiteSpace(name)) Names[path.TrimEnd('/', '\\')] = name.Trim();
    }

    public static string FromPath(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        if (Names.TryGetValue(trimmed, out var known)) return known;
        var name = Path.GetFileNameWithoutExtension(trimmed);
        if (string.IsNullOrWhiteSpace(name)) return trimmed;
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = Describe(trimmed) ?? name;
        Names[trimmed] = name;
        return name;
    }

    /// <summary>An .exe's own description ("Google Chrome" for chrome.exe), when it has a short one.</summary>
    private static string? Describe(string exe)
    {
        try
        {
            if (!File.Exists(exe)) return null;
            var description = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim();
            return description is { Length: > 1 and <= 40 } ? description : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
