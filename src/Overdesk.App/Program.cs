using Avalonia;
using System;
#if DEBUG
using Avalonia.Headless;
#endif

namespace Overdesk.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => Platform(AppBuilder.Configure<App>())
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    private static AppBuilder Platform(AppBuilder builder)
    {
#if DEBUG
        // Development snapshots without a display: Skia draws real pixels, nothing is shown.
        if (Environment.GetEnvironmentVariable("OVERDESK_HEADLESS") == "1")
            return builder.UseSkia().UseHeadless(new Avalonia.Headless.AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
#endif
        return builder.UsePlatformDetect();
    }
}
