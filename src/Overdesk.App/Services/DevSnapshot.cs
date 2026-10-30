using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Overdesk.App.Theme;

namespace Overdesk.App.Services;

/// <summary>
/// Development aid: with OVERDESK_SNAPSHOT=&lt;folder&gt; the app renders its main screens to PNG files and quits.
/// OVERDESK_SNAPSHOT_OPEN=&lt;path;path&gt; also opens those programs as tabs first. Lets UI changes be checked
/// without screen-recording permission.
/// </summary>
internal static class DevSnapshot
{
    public static async Task RunSplashAsync(SplashWindow splash, string dir)
    {
        Directory.CreateDirectory(dir);
        await WaitUntil(() => splash.Elapsed >= 0.9);
        Save(splash, Path.Combine(dir, "splash-1.png"));
        await WaitUntil(() => splash.Elapsed >= 2.3);
        Save(splash, Path.Combine(dir, "splash-2.png"));
    }

    public static async Task RunMainAsync(MainWindow main, string dir)
    {
        try { await RunMainCoreAsync(main, dir); }
        catch (Exception ex) { Console.Error.WriteLine("SNAPSHOT FAILED: " + ex); }
    }

    private static async Task RunMainCoreAsync(MainWindow main, string dir)
    {
        Directory.CreateDirectory(dir);
        App.RenderLogo(512).Save(Path.Combine(dir, "logo.png"), new PngBitmapEncoderOptions());

        // OVERDESK_SNAPSHOT_AI_PROMPT="..." sends one real message with the current provider (OVERDESK_SNAPSHOT_AI_MODEL picks
        // the model), waits for the answer, prints what the AI did and saves ai-live.png. Spends real tokens.
        // OVERDESK_SNAPSHOT_WEB_TEXT=<url> opens a web tab and prints the text the AI's read_web_page gets.
        if (Environment.GetEnvironmentVariable("OVERDESK_SNAPSHOT_WEB_TEXT") is { Length: > 0 } url)
        {
            Console.WriteLine("WEB TEXT: " + await main.DevReadWebAsync(url));
            main.Close();
            return;
        }

        if (Environment.GetEnvironmentVariable("OVERDESK_SNAPSHOT_AI_PROMPT") is { Length: > 0 } prompt)
        {
            Console.WriteLine(await main.DevAskAiAndWaitAsync(prompt, Environment.GetEnvironmentVariable("OVERDESK_SNAPSHOT_AI_MODEL")));
            await Task.Delay(800);
            Save(main, Path.Combine(dir, "ai-live.png"));
            main.Close();
            return;
        }

        if (Environment.GetEnvironmentVariable("OVERDESK_SNAPSHOT_WORKS") == "1")
        {
            await RunWorksAsync(main, dir);
            main.Close();
            return;
        }

        await Task.Delay(3500);
        Save(main, Path.Combine(dir, "home.png"));
        main.DevOpenPlayer();
        await Task.Delay(900);
        Save(main, Path.Combine(dir, "player.png"));

        main.DevOpenSettings();
        await Task.Delay(700);
        Save(main, Path.Combine(dir, "settings.png"));
        main.DevShowSettingsSection("AI");
        await Task.Delay(700);
        Save(main, Path.Combine(dir, "settings-ai.png"));
        main.DevShowSettingsSection("Data");
        await Task.Delay(700);
        Save(main, Path.Combine(dir, "settings-data.png"));
        main.DevCloseSettings();
        await Task.Delay(400);

        main.DevOpenPalette();
        await Task.Delay(700);
        Save(main, Path.Combine(dir, "palette.png"));
        main.DevClosePalette();
        await Task.Delay(400);

        main.DevOpenPicker();
        await Task.Delay(1500);
        Save(main, Path.Combine(dir, "picker.png"));
        main.DevClosePicker();
        await Task.Delay(400);

        var open = Environment.GetEnvironmentVariable("OVERDESK_SNAPSHOT_OPEN");
        if (open is { Length: > 0 })
        {
            foreach (var path in open.Split(';', StringSplitOptions.RemoveEmptyEntries)) main.DevOpenProgram(path);
            await Task.Delay(2500);
            Save(main, Path.Combine(dir, "tabs.png"));
            main.DevFakeRunning();
            await Task.Delay(600);
            Save(main, Path.Combine(dir, "stage.png"));
            main.DevOpenSettings();
            await Task.Delay(900);
            Save(main, Path.Combine(dir, "settings-tab.png"));
            main.DevCloseSettings();
            await Task.Delay(300);
            main.DevShowHome();
            await Task.Delay(900);
        }

        main.DevHomeSearch("plan my week");
        await Task.Delay(500);
        Save(main, Path.Combine(dir, "home-ask.png"));
        main.DevHomeSearch("");

        // No real question here: the default provider may be the user's own Claude account.
        main.DevOpenAi(sheet: false, rail: false);
        await Task.Delay(1500);
        Save(main, Path.Combine(dir, "ai.png"));
        main.DevOpenAi(sheet: false, rail: true);
        await Task.Delay(600);
        Save(main, Path.Combine(dir, "ai-history.png"));
        main.DevOpenAi(sheet: true, rail: false);
        await Task.Delay(2500);
        Save(main, Path.Combine(dir, "ai-models.png"));
        main.DevOpenAi(sheet: false, rail: false);
        main.DevAiDemo(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..")));
        await Task.Delay(600);
        Save(main, Path.Combine(dir, "ai-actions.png"));
        main.DevAiError();
        await Task.Delay(600);
        Save(main, Path.Combine(dir, "ai-error.png"));
        main.DevAiWorking();
        await Task.Delay(600);
        Save(main, Path.Combine(dir, "ai-working.png"));
        main.DevCloseTools();
        await Task.Delay(300);

        // OVERDESK_SNAPSHOT_TOOL=<plugin id> opens that tool from the plugins folder (checks the plugin loader).
        if (Environment.GetEnvironmentVariable("OVERDESK_SNAPSHOT_TOOL") is { Length: > 0 } toolId)
        {
            if (main.DevOpenTool(toolId) is { } problem) Console.Error.WriteLine($"TOOL {toolId}: {problem}");
            await Task.Delay(800);
            Save(main, Path.Combine(dir, "tool.png"));
            // OVERDESK_SNAPSHOT_TOOL_FILES="a;b" hands files to the tool as if they were dropped on it.
            if (Environment.GetEnvironmentVariable("OVERDESK_SNAPSHOT_TOOL_FILES") is { Length: > 0 } files)
            {
                main.DevDropOnTool(files.Split(';', StringSplitOptions.RemoveEmptyEntries));
                await Task.Delay(800);
                Save(main, Path.Combine(dir, "tool-files.png"));
            }
            main.DevCloseTools();
            await Task.Delay(300);
        }

        main.DevScrollDock(820);
        await Task.Delay(800);
        Save(main, Path.Combine(dir, "dock-dev.png"));
        main.DevScrollDock(0);

        main.DevShowWorkspaces();
        await Task.Delay(700);
        Save(main, Path.Combine(dir, "workspaces.png"));

        main.DevOpenWeb("github");
        await Task.Delay(5000);
        Save(main, Path.Combine(dir, "web.png"));
        main.DevShowHome();
        await Task.Delay(500);

        var original = AccentThemes.Current.Name;
        AccentThemes.Apply("Ultraviolet");
        await Task.Delay(900);
        Save(main, Path.Combine(dir, "home-ultraviolet.png"));
        AccentThemes.Apply("Cyber");
        await Task.Delay(500);
        Save(main, Path.Combine(dir, "home-cyber.png"));
        AccentThemes.Apply(original);

        main.Close();
    }

    /// <summary>OVERDESK_SNAPSHOT_WORKS=1: seeds sample works and renders the Works screens only.</summary>
    private static async Task RunWorksAsync(MainWindow main, string dir)
    {
        await Task.Delay(2500);
        main.DevSeedWorks();
        main.DevShowWorksTab(gallery: false);
        await Task.Delay(1200);
        Save(main, Path.Combine(dir, "w-hub.png"));
        main.DevShowWorksTab(gallery: true);
        await Task.Delay(900);
        Save(main, Path.Combine(dir, "w-gallery.png"));
        main.DevOpenWork(0, drawer: false);
        await Task.Delay(1000);
        Save(main, Path.Combine(dir, "w-tab.png"));
        main.DevOpenWork(2, drawer: true);
        await Task.Delay(1000);
        Save(main, Path.Combine(dir, "w-tab-settings.png"));
        main.DevOpenWork(4, drawer: false);
        await Task.Delay(1000);
        Save(main, Path.Combine(dir, "w-tab-todo.png"));
        main.DevOpenWorksList();
        await Task.Delay(900);
        Save(main, Path.Combine(dir, "w-panel-list.png"));
        main.DevOpenWorkInPanel(1);
        await Task.Delay(900);
        Save(main, Path.Combine(dir, "w-panel-editor.png"));
        main.DevOpenSettings();
        await Task.Delay(900);
        main.DevShowSettingsSection("Works");
        await Task.Delay(1200);
        Save(main, Path.Combine(dir, "w-settings.png"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        while (!condition()) await Task.Delay(30);
    }

    private static void Save(TopLevel top, string path)
    {
        Dispatcher.UIThread.RunJobs();
        if (OperatingSystem.IsMacOS() && top is Window window && Environment.GetEnvironmentVariable("OVERDESK_SNAPSHOT_SCREEN") == "1")
        {
            // Real screen pixels (includes OS window chrome); needs Screen Recording permission.
            var p = window.Position;
            var region = $"{p.X},{p.Y},{(int)window.Bounds.Width},{(int)window.Bounds.Height}";
            System.Diagnostics.Process.Start("screencapture", new[] { "-x", "-R", region, path })?.WaitForExit();
            return;
        }

        var scale = top.RenderScaling;
        var size = new PixelSize((int)(top.Bounds.Width * scale), (int)(top.Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(top);
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }
}
