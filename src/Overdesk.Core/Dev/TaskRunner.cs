using System.Diagnostics;
using System.Text;

namespace Overdesk.Core.Dev;

/// <summary>A shell command OverDesk started (a dev server, a build, a git pull...), with its live output.</summary>
public sealed class RunningTask
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
public sealed class TaskRunner
{
    private readonly Action<Action> _post;

    /// <param name="post">Runs event handlers where the caller wants them (the UI thread); by default they run on the reporting thread.</param>
    public TaskRunner(Action<Action>? post = null) => _post = post ?? (action => action());

    public List<RunningTask> Tasks { get; } = new();

    /// <summary>Raised through the post callback when a task starts, ends or is removed.</summary>
    public event Action? Changed;

    /// <summary>Raised through the post callback when a task prints (throttled, with a trailing call so the last lines always show).</summary>
    public event Action? OutputChanged;

    private readonly object _notifyLock = new();
    private DateTime _lastNotify;
    private bool _outputPending;

    public RunningTask Start(string name, string command, string folder, string? projectId = null)
    {
        var task = new RunningTask { Name = name, Command = command, Folder = folder, ProjectId = projectId };
        var info = StartInfo(command, folder);
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
        const int KeepFinished = 40;
        for (var i = Tasks.Count - 1; i >= 0 && Tasks.Count(t => !t.IsRunning) > KeepFinished; i--)
            if (!Tasks[i].IsRunning) { Tasks[i].Process?.Dispose(); Tasks.RemoveAt(i); }
        NotifyChanged();
        return task;
    }

    /// <summary>
    /// Windows: cmd.exe with the command line verbatim. ArgumentList would escape inner quotes as \" (which cmd does not
    /// understand); with /s, cmd removes just the outer pair of quotes added here. Elsewhere a login shell, so the user's
    /// PATH (node, cargo, dotnet, homebrew...) is there, like in their terminal.
    /// </summary>
    private static ProcessStartInfo StartInfo(string command, string folder)
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"{command}\"" }
            : new ProcessStartInfo(Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } sh ? sh : "/bin/zsh") { ArgumentList = { "-l", "-c", command } };
        info.WorkingDirectory = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        return info;
    }

    public void Stop(RunningTask task)
    {
        try { if (task.Process is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone */ }
    }

    public void Remove(RunningTask task)
    {
        Stop(task);
        Tasks.Remove(task);
        if (!task.IsRunning) task.Process?.Dispose();
        NotifyChanged();
    }

    public void StopAll()
    {
        foreach (var t in Tasks.Where(t => t.IsRunning)) Stop(t);
    }

    /// <summary>Run a command to completion and return its output (for git, docker...). Null when it could not run.</summary>
    public static async Task<string?> CaptureAsync(string command, string folder, CancellationToken cancellation = default)
    {
        var info = StartInfo(command, folder);
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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string?>> ExecutablePaths = new();

    /// <summary>Where the user's shell finds a program (one login shell per name, then cached). Null when it is not installed.</summary>
    public static Task<string?> FindExecutableAsync(string name) => ExecutablePaths.GetOrAdd(name, async n =>
    {
        var output = await CaptureAsync(OperatingSystem.IsWindows() ? $"where {n}" : $"command -v {n}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var path = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return path is { Length: > 0 } && File.Exists(path) ? path : null;
    });

    /// <summary>Run an already resolved program directly (no shell), for the polls that repeat every few seconds.</summary>
    public static async Task<string?> CaptureAsync(string executable, IEnumerable<string> arguments, string folder, CancellationToken cancellation = default)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
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

    private void NotifyChanged() => _post(() =>
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
        _ = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero).ContinueWith(_ => _post(FireOutput), TaskScheduler.Default);
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
