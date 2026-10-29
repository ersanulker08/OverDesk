using Overdesk.Core.Platform;
#if WINDOWS
using Overdesk.Platform.Windows;
#else
using Overdesk.Platform.MacOS;
#endif

namespace Overdesk.App.Services;

/// <summary>
/// The only place that knows which OS layer exists. Everything else uses the Core interfaces.
/// Windows builds reference only the Windows layer, macOS builds only the macOS layer (see the .csproj).
/// </summary>
internal static class PlatformFactory
{
#if WINDOWS
    public static IWindowEmbedder CreateEmbedder() => new Win32WindowEmbedder();
    public static IProgramCatalog CreateCatalog() => new Win32ProgramCatalog();
    public static IMediaController CreateMedia() => new WinMediaController();
    public static ISystemMonitor CreateSystemMonitor() => new Win32SystemMonitor();
    public static IWebViewFactory CreateWebViews() => new WinWebViewFactory();
    public static ISecretStore CreateSecrets() => new WinCredentials();
    public static IDesktopServices CreateDesktop() => new WinDesktopServices();
#else
    public static IWindowEmbedder CreateEmbedder()
        => OperatingSystem.IsMacOS() ? new MacWindowEmbedder() : new UnsupportedWindowEmbedder();

    public static IProgramCatalog CreateCatalog()
        => OperatingSystem.IsMacOS() ? new MacProgramCatalog() : new EmptyProgramCatalog();

    public static IMediaController CreateMedia()
        => OperatingSystem.IsMacOS() ? new MacMediaController() : new NoMediaController();

    public static ISystemMonitor CreateSystemMonitor()
        => OperatingSystem.IsMacOS() ? new MacSystemMonitor() : new NoSystemMonitor();

    public static IWebViewFactory CreateWebViews()
        => OperatingSystem.IsMacOS() ? new MacWebViewFactory() : new NoWebViewFactory();

    public static ISecretStore CreateSecrets()
        => OperatingSystem.IsMacOS() ? new MacKeychain() : new MemorySecretStore();

    public static IDesktopServices CreateDesktop()
        => OperatingSystem.IsMacOS() ? new MacDesktopServices() : new NoDesktopServices();
#endif
}
