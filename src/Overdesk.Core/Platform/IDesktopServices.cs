namespace Overdesk.Core.Platform;

/// <summary>Small desktop integrations that differ per OS.</summary>
public interface IDesktopServices
{
    /// <summary>A system notification (shown even when Overdesk is in the background). False when unsupported.</summary>
    bool Notify(string title, string text);

    /// <summary>Let the user select an area of the screen; the picture goes to the clipboard.</summary>
    void ScreenshotToClipboard();

    /// <summary>Unread badges the OS shows for running apps (e.g. "3" on Mail), keyed by app name. Empty when unknown.</summary>
    IReadOnlyDictionary<string, string> AppBadges();
}

public sealed class NoDesktopServices : IDesktopServices
{
    public bool Notify(string title, string text) => false;
    public void ScreenshotToClipboard() { }
    public IReadOnlyDictionary<string, string> AppBadges() => new Dictionary<string, string>();
}
