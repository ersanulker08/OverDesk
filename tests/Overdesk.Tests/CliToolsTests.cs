using System.Diagnostics;
using Overdesk.AI;

namespace Overdesk.Tests;

/// <summary>Gemini CLI through a fake `gemini` script, and the login-shell probe through a fake $SHELL.</summary>
public class CliToolsTests
{
    private static string Script(TempFolder dir, string name, string body)
    {
        var path = dir.Write(name, "#!/bin/bash\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task<string> Run(GeminiCliProvider provider, string model, params ChatMessage[] messages)
    {
        var text = "";
        await foreach (var chunk in provider.StreamAsync(model, null, messages, null, CancellationToken.None)) text += chunk.Text;
        return text;
    }

    [Fact]
    public async Task Gemini_cli_gets_the_chosen_model_and_can_read_attached_images()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempFolder();
        var workspace = Path.Combine(dir.Path, "w");
        var exe = Script(dir, "gemini", $"""
            printf '%s\n' "$@" > '{dir.Path}/args.txt'
            cat > '{dir.Path}/stdin.txt'
            ls > '{dir.Path}/seen.txt'
            echo answer
            """);

        var text = await Run(new GeminiCliProvider(workspace, exe), "pro", new ChatMessage("user", "what is this?", new[] { new byte[] { 1, 2 } }));

        Assert.Equal("answer", text.Trim());
        Assert.Equal(new[] { "-m", "pro" }, File.ReadAllLines(Path.Combine(dir.Path, "args.txt")));
        var image = File.ReadAllLines(Path.Combine(dir.Path, "seen.txt")).Single(f => f.StartsWith("screenshot-"));
        Assert.Contains(Path.Combine(workspace, image), File.ReadAllText(Path.Combine(dir.Path, "stdin.txt")));
        Assert.Empty(Directory.GetFiles(workspace, "screenshot-*"));
    }

    [Fact]
    public async Task Gemini_cli_default_model_passes_no_flag_and_offers_the_others()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempFolder();
        var provider = new GeminiCliProvider(Path.Combine(dir.Path, "w"), Script(dir, "gemini", $"""printf '%s\n' "$@" > '{dir.Path}/args.txt'; cat > /dev/null"""));

        await Run(provider, "default", new ChatMessage("user", "hi"));

        Assert.Empty(File.ReadAllText(Path.Combine(dir.Path, "args.txt")).Trim());
        Assert.Equal(new[] { "default", "pro", "flash" }, (await provider.ListModelsAsync(CancellationToken.None)).Select(m => m.Id));
    }

    [Fact]
    public void Probing_the_login_shell_survives_a_hang_and_sees_a_later_install()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempFolder();
        var marker = Path.Combine(dir.Path, "installed");
        var tool = dir.Write("fake-tool", "");
        var hang = Path.Combine(dir.Path, "hang");
        var shell = Script(dir, "shell", $"""
            head -c 300000 /dev/zero >&2
            if [ -e '{hang}' ]; then sleep 30; fi
            if [ -e '{marker}' ]; then echo '{tool}'; else exit 1; fi
            """);
        var command = "overdesk-test-" + Guid.NewGuid().ToString("N");
        var saved = Environment.GetEnvironmentVariable("SHELL");
        Environment.SetEnvironmentVariable("SHELL", shell);
        try
        {
            File.WriteAllText(hang, "");
            var clock = Stopwatch.StartNew();
            Assert.Null(CliTools.Find(command));
            Assert.InRange(clock.Elapsed.TotalSeconds, 4, 12);

            File.Delete(hang);
            File.WriteAllText(marker, "");
            Assert.Null(CliTools.Find(command));
            CliTools.ForgetMissing();
            Assert.Equal(tool, CliTools.Find(command));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHELL", saved);
        }
    }
}
