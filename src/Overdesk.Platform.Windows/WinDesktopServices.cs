using System.Diagnostics;
using Microsoft.Win32;
using Overdesk.Core.Platform;
using Windows.UI.Notifications;

namespace Overdesk.Platform.Windows;

/// <summary>Screenshots through the Snipping Tool's screen-clip mode, notifications as toasts. Badges stay inside Overdesk.</summary>
public sealed class WinDesktopServices : IDesktopServices
{
    /// <summary>The identity toasts are shown under. Registered per user, so an unpackaged program can have one.</summary>
    private const string AppId = "OverDesk";

    private static readonly string IconFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Overdesk", "notification-icon.png");

    public bool Notify(string title, string text)
    {
        try
        {
            RegisterAppId();
            var toast = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var lines = toast.GetElementsByTagName("text");
            lines[0].AppendChild(toast.CreateTextNode(title));
            lines[1].AppendChild(toast.CreateTextNode(text));
            ToastNotificationManager.CreateToastNotifier(AppId).Show(new ToastNotification(toast));
            return true;
        }
        catch (Exception)
        {
            return false; // toasts unavailable (Server Core, policy)
        }
    }

    /// <summary>The window and .exe icons need nothing from this; toasts want an image file.</summary>
    public void SetAppIcon(byte[] png)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IconFile)!);
            File.WriteAllBytes(IconFile, png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A second copy of OverDesk may be writing it; toasts work without an icon too.
        }
    }

    private static void RegisterAppId()
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
        key.SetValue("DisplayName", "OverDesk");
        if (File.Exists(IconFile)) key.SetValue("IconUri", IconFile);
    }

    public void ScreenshotToClipboard()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "ms-screenclip:") { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }

    public IReadOnlyDictionary<string, string> AppBadges() => new Dictionary<string, string>();
}
