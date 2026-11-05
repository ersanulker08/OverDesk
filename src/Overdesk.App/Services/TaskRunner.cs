using System.Diagnostics;
using System.Text;
using Avalonia.Threading;

namespace Overdesk.App.Services;

/// <summary>A shell command OverDesk started (a dev server, a build, a git pull...), with its live output.</summary>
internal sealed class RunningTask
{
    private readonly StringBuilder _output = new();
    private readonly object _lock = new();

    public required string Name { get; init; }
    public required string Command { get; init; }
    public required string Folder { get; init; }
    public string? ProjectId { get; init; }
    public DateTime Started { get; } = DateTime.Now;
    public Process? Process { get; set; }
    public bool IsRunning { get; set; } = true;
    public int? ExitCode { get; set; }

    public string Output
    {
        get { lock (_lock) return _output.ToString(); }
    }

    public void Append(string line)
    {
        lock (_lock)
        {
            _output.AppendLine(line);
            // Keep the last ~200 KB: long-running dev servers print forever.
            if (_output.Length > 200_000) _output.Remove(0, _output.Length - 150_000);
        }
    }
}

/// <summary>Starts shell commands in a folder, captures their output and stops them (with their child processes).</summary>
internal sealed class TaskRunner
{
    public List<RunningTask> Tasks { get; } = new();

    /// <summary>Raised on the UI thread when a task starts, ends or is removed.</summary>
    public event Action? Changed;

    /// <summary>Raised on the UI thread when a task prints (throttled, with a trailing call so the last lines always show).</summary>
    public event Action? OutputChanged;

    private readonly object _notifyLock = new();
    private DateTime _lastNotify;
    private bool _outputPending;

    public RunningTask Start(string name, string command, string folder, string? projectId = null)
    {
        var task = new RunningTask { Name = name, Command = command, Folder = folder, ProjectId = projectId };
        var info = OperatingSystem.IsWindows()
            ? CmdStartInfo(command)
            // A login shell so the user's PATH (node, cargo, dotnet, homebrew...) is there, like in their terminal.
            : new ProcessStartInfo(Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } sh ? sh : "/bin/zsh") { ArgumentList = { "-l", "-c", command } };
        info.WorkingDirectory = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.Environment["FORCE_COLOR"] = "0";
        info.Environment["NO_COLOR"] = "1";

        task.Append($"$ {command}");
        try
        {
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { task.Append(StripAnsi(e.Data)); NotifyOutput(); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { task.Append(StripAnsi(e.Data)); NotifyOutput(); } };
            process.Exited += (_, _) =>
            {
                // Let the redirected output finish arriving before the "finished" line and IsRunning = false.
                try { process.WaitForExit(); } catch (Exception) { }
                task.IsRunning = false;
                try { task.ExitCode = process.ExitCode; } catch (InvalidOperationException) { }
                task.Append(task.ExitCode == 0 ? "✓ finished" : $"✗ exited with code {task.ExitCode}");
                NotifyChanged();
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            task.Process = process;
        }
        catch (Exception ex)
        {
            task.IsRunning = false;
            task.ExitCode = -1;
            task.Append("✗ could not start: " + ex.Message);
        }
        Tasks.Insert(0, task);
        NotifyChanged();
        return task;
    }

    /// <summary>
    /// cmd.exe with the command line verbatim. ArgumentList would escape inner quotes as \" (which cmd does not
    /// understand); with /s, cmd removes just the outer pair of quotes added here.
    /// </summary>
    private static ProcessStartInfo CmdStartInfo(string command) => new("cmd.exe") { Arguments = $"/d /s /c \"{command}\"" };

    public void Stop(RunningTask task)
    {
        try { if (task.Process is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone */ }
    }

    public void Remove(RunningTask task)
    {
        Stop(task);
        Tasks.Remove(task);
        NotifyChanged();
    }

    public void StopAll()
    {
        foreach (var t in Tasks.Where(t => t.IsRunning)) Stop(t);
    }

    /// <summary>Run a command to completion and return its output (for git, docker...). Null when it could not run.</summary>
    public static async Task<string?> CaptureAsync(string command, string folder, CancellationToken cancellation = default)
    {
        var info = OperatingSystem.IsWindows()
            ? CmdStartInfo(command)
            : new ProcessStartInfo(Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } sh ? sh : "/bin/zsh") { ArgumentList = { "-l", "-c", command } };
        info.WorkingDirectory = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        try
        {
            using var p = Process.Start(info);
            if (p is null) return null;
            var output = await p.StandardOutput.ReadToEndAsync(cancellation);
            await p.WaitForExitAsync(cancellation);
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void NotifyChanged() => Dispatcher.UIThread.Post(() =>
    {
        Changed?.Invoke();
        OutputChanged?.Invoke();
    });

    private void NotifyOutput()
    {
        TimeSpan wait;
        lock (_notifyLock)
        {
            if (_outputPending) return;
            wait = TimeSpan.FromMilliseconds(250) - (DateTime.UtcNow - _lastNotify);
            _outputPending = true;
        }
        Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(FireOutput, wait > TimeSpan.Zero ? wait : TimeSpan.Zero));
    }

    private void FireOutput()
    {
        lock (_notifyLock)
        {
            _outputPending = false;
            _lastNotify = DateTime.UtcNow;
        }
        OutputChanged?.Invoke();
    }

    private static string StripAnsi(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"\x1B\[[0-9;?]*[ -/]*[@-~]", "");
}
