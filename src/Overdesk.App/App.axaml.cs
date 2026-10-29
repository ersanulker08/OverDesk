using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Overdesk.App.Controls;
using Overdesk.App.Services;
using Overdesk.App.Theme;
using Overdesk.Core.Settings;

namespace Overdesk.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // OVERDESK_DATA_DIR lets development runs use a throwaway settings folder.
            var store = new SettingsStore(Environment.GetEnvironmentVariable("OVERDESK_DATA_DIR") is { Length: > 0 } dir
                ? dir
                : SettingsStore.DefaultDirectory());
            var settings = store.Load();
            AccentThemes.Apply(settings.AccentTheme);

            var catalog = PlatformFactory.CreateCatalog();
            IconCache.Catalog = catalog;
            var icon = CreateAppIcon();
            using (var png = new MemoryStream())
            {
                RenderLogo(512).Save(png, new PngBitmapEncoderOptions());
                PlatformFactory.CreateDesktop().SetAppIcon(png.ToArray());
            }

            var main = new MainWindow(store, catalog) { Icon = icon };
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var snapshotDir = Environment.GetEnvironmentVariable("OVERDESK_SNAPSHOT");

            if (settings.ShowSplash)
            {
                var splash = new SplashWindow { Icon = icon, Topmost = true };
                desktop.MainWindow = splash;
                splash.Completed += () =>
                {
                    desktop.MainWindow = main;
                    main.Show();
                    main.PlayEntrance();
                    splash.FadeAway();
                    if (snapshotDir is { Length: > 0 }) _ = DevSnapshot.RunMainAsync(main, snapshotDir);
                };
                if (snapshotDir is { Length: > 0 }) _ = DevSnapshot.RunSplashAsync(splash, snapshotDir);
            }
            else
            {
                desktop.MainWindow = main;
                if (snapshotDir is { Length: > 0 }) main.Opened += (_, _) => _ = DevSnapshot.RunMainAsync(main, snapshotDir);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The window/dock icon is the logo itself, rendered on a dark rounded tile.</summary>
    private static WindowIcon CreateAppIcon() => new(RenderLogo(256));

    internal static Bitmap RenderLogo(int pixels)
    {
        var accent = AccentThemes.Current;
        var logo = new LogoMark
        {
            Width = pixels, Height = pixels, AppIconBackdrop = true, Glow = 0.8,
            Accent = accent.Primary, Accent2 = accent.Secondary,
        };
        logo.Measure(new Size(pixels, pixels));
        logo.Arrange(new Rect(0, 0, pixels, pixels));
        var bitmap = new RenderTargetBitmap(new PixelSize(pixels, pixels));
        bitmap.Render(logo);
        return bitmap;
    }
}