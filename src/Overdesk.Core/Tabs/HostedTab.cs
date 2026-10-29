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

    public HostedTab(string programPath, string? title = null)
    {
        ProgramPath = programPath;
        IsInternal = programPath.StartsWith("overdesk://", StringComparison.Ordinal);
        _title = title ?? (IsInternal ? "OverDesk" : ProgramNames.FromPath(programPath));
        if (IsInternal) _status = TabStatus.Running; // a page of Overdesk itself: nothing to launch
    }

    /// <summary>True for Overdesk's own pages (settings), false for hosted programs.</summary>
    public bool IsInternal { get; }

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

public static class ProgramNames
{
    public static string FromPath(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var name = Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }
}
