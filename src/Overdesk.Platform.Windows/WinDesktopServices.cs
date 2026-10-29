using System.Diagnostics;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.Windows;

/// <summary>Screenshots through the Snipping Tool's screen-clip mode. Notifications and badges stay inside Overdesk.</summary>
public sealed class WinDesktopServices : IDesktopServices
{
    public bool Notify(string title, string text) => false;

    public void ScreenshotToClipboard()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "ms-screenclip:") { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }

    public IReadOnlyDictionary<string, string> AppBadges() => new Dictionary<string, string>();
}
