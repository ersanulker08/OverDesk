using System.Diagnostics;

namespace Overdesk.AI;

/// <summary>Finds command-line tools the way the user's terminal would (their login shell's PATH).</summary>
public static class CliTools
{
    private static readonly Lazy<string?> LoginPath = new(() =>
        OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATH") : Run(Shell(), "-l", "-c", "printf %s \"$PATH\""));

    private static readonly Dictionary<string, string?> Found = new();

    /// <summary>The user's PATH as their terminal has it (GUI apps on macOS start with a much shorter one).</summary>
    public static string? Path => LoginPath.Value;

    /// <summary>Absolute path of a command, or null when it is not installed.</summary>
    public static string? Find(string command)
    {
        lock (Found)
        {
            if (Found.TryGetValue(command, out var known)) return known;
            var path = OperatingSystem.IsWindows()
                ? Run("where.exe", command)?.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
                    .FirstOrDefault(l => l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || l.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                : Run(Shell(), "-l", "-c", $"command -v {command}")?.Trim();
            if (path is { Length: 0 } || (path is not null && !File.Exists(path))) path = null;
            Found[command] = path;
            return path;
        }
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
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception) { return null; }
    }
}
