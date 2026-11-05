using System.Diagnostics;

namespace Overdesk.AI;

/// <summary>Finds command-line tools the way the user's terminal would (their login shell's PATH).</summary>
public static class CliTools
{
    private static readonly Lazy<string?> LoginPath = new(() =>
        OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATH") : Run(Shell(), "-l", "-c", "printf %s \"$PATH\""));

    private static readonly Dictionary<string, string> Found = new();

    /// <summary>When a command was last looked for in vain: checked again after a while, since it may get installed.</summary>
    private static readonly Dictionary<string, DateTime> Missing = new();
    private static readonly TimeSpan MissingFor = TimeSpan.FromSeconds(30);

    /// <summary>The user's PATH as their terminal has it (GUI apps on macOS start with a much shorter one).</summary>
    public static string? Path => LoginPath.Value;

    /// <summary>Absolute path of a command, or null when it is not installed.</summary>
    public static string? Find(string command)
    {
        lock (Found)
        {
            if (Found.TryGetValue(command, out var known)) return known;
            if (Missing.TryGetValue(command, out var when) && DateTime.UtcNow - when < MissingFor) return null;
            var path = OperatingSystem.IsWindows()
                ? Run("where.exe", command)?.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
                    .FirstOrDefault(l => l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || l.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                : Run(Shell(), "-l", "-c", $"command -v {command}")?.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Missing[command] = DateTime.UtcNow;
                return null;
            }
            Found[command] = path;
            return path;
        }
    }

    /// <summary>Look again now for commands that were missing (the user may have just installed one).</summary>
    public static void ForgetMissing()
    {
        lock (Found) Missing.Clear();
    }

    /// <summary>A start info for the tool with the user's PATH, so tools that call node/python find them.</summary>
    public static ProcessStartInfo StartInfo(string executable, string workingDirectory)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        if (Path is { Length: > 0 } path) info.Environment["PATH"] = path;
        info.Environment["NO_COLOR"] = "1";
        return info;
    }

    private static string Shell() => Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } s ? s : "/bin/zsh";

    private static string? Run(string file, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info);
            if (p is null) return null;
            // Read both pipes while waiting: a login shell that blocks or fills stderr must not hang the caller.
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                p.Kill(entireProcessTree: true);
                return null;
            }
            return p.ExitCode == 0 && output.Wait(1000) ? output.Result : null;
        }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// Images of one message as files in a CLI tool's workspace, where it can read them. They are deleted with this object,
/// and anything left behind by a crash goes when the next message is sent.
/// </summary>
internal sealed class ImageFiles : IDisposable
{
    private readonly string _workspace;

    private ImageFiles(string workspace) => _workspace = workspace;

    public List<string> Paths { get; } = new();

    public static async Task<ImageFiles> WriteAsync(string workspace, IReadOnlyList<byte[]>? images, CancellationToken cancellation)
    {
        var files = new ImageFiles(workspace);
        files.Dispose();
        foreach (var image in images ?? Array.Empty<byte[]>())
        {
            var path = Path.Combine(workspace, $"screenshot-{Guid.NewGuid():N}.png");
            await File.WriteAllBytesAsync(path, image, cancellation);
            files.Paths.Add(path);
        }
        return files;
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_workspace, "screenshot-*.png"))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
