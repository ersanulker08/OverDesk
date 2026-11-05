using System.Diagnostics;
using Overdesk.Core.Dev;

namespace Overdesk.Tests;

/// <summary>TaskRunner against real shells: a login shell on macOS/Linux, cmd.exe with the command line verbatim on Windows.</summary>
[Collection("login shell")]
public class TaskRunnerTests
{
    private static async Task WaitUntil(Func<bool> condition, int seconds = 15)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed.TotalSeconds > seconds) throw new TimeoutException("the condition did not come true in time");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task A_task_runs_in_a_login_shell_and_streams_its_output_with_a_trailing_notification()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempFolder();
        var runner = new TaskRunner();
        var changes = 0;
        var outputSeenAtLastNotification = "";
        runner.Changed += () => changes++;
        runner.OutputChanged += () => outputSeenAtLastNotification = runner.Tasks[0].Output;

        // zsh marks a login shell with "l" in $-, bash only through shopt; the second line arrives after the throttle window.
        var task = runner.Start("probe", "if [ -n \"$ZSH_VERSION\" ]; then case $- in *l*) echo login;; *) echo plain;; esac; else shopt -q login_shell && echo login || echo plain; fi; pwd; sleep 0.4; printf '\\033[32mlast\\033[0m\\n'", dir.Path);
        Assert.Same(task, runner.Tasks[0]);
        Assert.True(task.IsRunning);
        await WaitUntil(() => !task.IsRunning);
        await WaitUntil(() => outputSeenAtLastNotification.Contains("✓ finished"));

        var lines = task.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("$ " + task.Command, lines[0]);
        Assert.Equal("login", lines[1]);
        Assert.Equal(Path.GetFileName(dir.Path), Path.GetFileName(lines[2]));
        Assert.Equal("last", lines[3]);
        Assert.Equal("✓ finished", lines[4]);
        Assert.Equal(0, task.ExitCode);
        Assert.True(changes >= 2, "Changed fires on start and on exit");
    }

    [Fact]
    public async Task A_failing_task_reports_its_exit_code()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempFolder();
        var task = new TaskRunner().Start("fail", "echo oops >&2; exit 3", dir.Path);
        await WaitUntil(() => !task.IsRunning);
        Assert.Equal(3, task.ExitCode);
        Assert.Contains("oops", task.Output);
        Assert.Contains("✗ exited with code 3", task.Output);
    }

    [Fact]
    public async Task Stop_kills_the_whole_process_tree()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempFolder();
        var runner = new TaskRunner();
        var task = runner.Start("server", "sleep 300 & echo CHILD=$!; wait $!", dir.Path);
        await WaitUntil(() => task.Output.Contains("\nCHILD="));
        var child = int.Parse(task.Output.Split("\nCHILD=")[1].Split('\n')[0]);
        Assert.False(Process.GetProcessById(child).HasExited);

        runner.Stop(task);
        await WaitUntil(() => !task.IsRunning);
        await WaitUntil(() =>
        {
            try { return Process.GetProcessById(child).HasExited; }
            catch (ArgumentException) { return true; }
        });
        Assert.NotEqual(0, task.ExitCode);
        Assert.Contains("✗ exited with code", task.Output);
        runner.Remove(task);
        Assert.Empty(runner.Tasks);
    }

    [Fact]
    public async Task Capture_returns_the_output_of_a_successful_command_and_null_otherwise()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempFolder();
        dir.Write("note.txt", "x");
        Assert.Equal("note.txt\n", await TaskRunner.CaptureAsync("ls", dir.Path));
        Assert.Null(await TaskRunner.CaptureAsync("exit 2", dir.Path));
        // A missing folder falls back to the home folder instead of failing to start.
        Assert.NotNull(await TaskRunner.CaptureAsync("pwd", Path.Combine(dir.Path, "missing")));
    }

    [Fact]
    public async Task Windows_passes_the_command_line_to_cmd_verbatim_including_inner_quotes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempFolder();
        dir.Write("a b.txt", "x");
        // ArgumentList would turn the inner quotes into \" and cmd would look for a file called \"a; verbatim they survive.
        var listed = await TaskRunner.CaptureAsync("dir /b \"a b.txt\" && echo \"quoted & kept\"", dir.Path);
        Assert.NotNull(listed);
        Assert.Contains("a b.txt", listed);
        Assert.Contains("\"quoted & kept\"", listed);
        Assert.Null(await TaskRunner.CaptureAsync("exit 2", dir.Path));

        var task = new TaskRunner().Start("probe", "echo hello & cd", dir.Path);
        await WaitUntil(() => !task.IsRunning);
        Assert.Equal(0, task.ExitCode);
        Assert.Contains("hello", task.Output);
        Assert.Contains("✓ finished", task.Output);
    }
}
