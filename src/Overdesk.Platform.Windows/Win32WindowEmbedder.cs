using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Overdesk.Core.Platform;
using static Overdesk.Platform.Windows.NativeMethods;

namespace Overdesk.Platform.Windows;

/// <summary>
/// Windows layer: start the program, find the window it opens, re-parent it.
/// The window is not always owned by the process we started: launchers hand over to another process, single-instance
/// apps (Chrome, VS Code, Explorer) open their window in the copy that already runs, and Store apps live in
/// ApplicationFrameHost. So we look for any new top-level window that belongs to the program, and fall back to
/// adopting a window it already had open.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class Win32WindowEmbedder : IWindowEmbedder
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(25);

    /// <summary>How long a program that is still running gets (Photoshop or a game launcher on a slow disk).</summary>
    private static readonly TimeSpan SlowWindowTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long to wait for a new window before adopting one the program already had open.</summary>
    private static readonly TimeSpan AdoptAfter = TimeSpan.FromSeconds(3);

    /// <summary>Before this, only windows with a caption count (skips splash screens).</summary>
    private static readonly TimeSpan SplashGrace = TimeSpan.FromSeconds(6);

    public string PlatformName => "Windows";
    public IReadOnlyList<string> ProgramPatterns => new[] { "*.exe", "*.lnk" };
    public bool HasPermissions => true;
    public string? PermissionHelp => null;
    public void RequestPermissions() { }

    /// <summary>
    /// Call once at startup, before any window exists: lets programs with another DPI awareness than Overdesk
    /// (most older apps) be parented into it. Without this SetParent can fail or the program draws at the wrong size.
    /// </summary>
    public static void PrepareProcess()
    {
        try { SetThreadDpiHostingBehavior(DPI_HOSTING_BEHAVIOR_MIXED); }
        catch (EntryPointNotFoundException) { /* Windows 10 before 1803 */ }
    }

    /// <summary>The owner must clip its children, otherwise Overdesk's own drawing covers the hosted program.</summary>
    public void PrepareOwnerWindow(IntPtr ownerHandle)
    {
        if (ownerHandle == IntPtr.Zero) return;
        var style = Style(ownerHandle);
        if ((style & WS_CLIPCHILDREN) == 0) SetWindowLongPtr(ownerHandle, GWL_STYLE, new IntPtr(style | WS_CLIPCHILDREN));
    }

    /// <summary>Hosted windows are real children of Overdesk and never fall behind it: no mirroring needed.</summary>
    public bool? CanMirror => null;
    public void RequestMirrorPermission() { }

    public async Task<IEmbeddedWindow> LaunchAsync(string programPath, IntPtr ownerHandle, CancellationToken cancellation)
    {
        var launch = Describe(programPath);
        var before = TopWindows().Select(w => w.Hwnd).ToHashSet();
        var started = DateTime.Now;

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(launch.File, launch.Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = launch.Folder,
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new EmbeddingException($"The program could not be started: {ex.Message}", ex);
        }

        // For a Store app this is only explorer.exe passing the request on.
        if (launch.AppId is not null)
        {
            process?.Dispose();
            process = null;
        }

        var startedPid = process is { HasExited: false } ? (uint)process.Id : 0u;
        try
        {
            IntPtr candidate = IntPtr.Zero;
            var deadline = DateTime.UtcNow + WindowTimeout;
            var slowDeadline = DateTime.UtcNow + SlowWindowTimeout;
            var adoptAt = DateTime.UtcNow + AdoptAfter;
            var splashUntil = DateTime.UtcNow + SplashGrace;
            while (DateTime.UtcNow < deadline || (DateTime.UtcNow < slowDeadline && process is { HasExited: false }))
            {
                cancellation.ThrowIfCancellationRequested();

                var windows = TopWindows();
                var fresh = Pick(windows.Where(w => !before.Contains(w.Hwnd)), startedPid, launch, started,
                                 captionOnly: DateTime.UtcNow < splashUntil);

                // The same window on two polls in a row: it is not a flash (splash, "loading…" popup).
                if (fresh != IntPtr.Zero && fresh == candidate)
                    return Embed(fresh, ownerHandle, owned: true);
                candidate = fresh;

                if (fresh == IntPtr.Zero && DateTime.UtcNow >= adoptAt
                    && OpenWindow(windows.Where(w => before.Contains(w.Hwnd)).ToList(), launch) is var old
                    && old != IntPtr.Zero)
                {
                    // No new window: the program was already running and only brought its window forward.
                    return Embed(old, ownerHandle, owned: false);
                }

                await Task.Delay(150, cancellation);
            }
            throw new EmbeddingException(process is { HasExited: true }
                ? "The program exited without showing a window."
                : "The program did not show a window in time. It may still be running on its own.");
        }
        catch (OperationCanceledException)
        {
            // The tab was closed while the program was starting: do not leave it half-started.
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException) { /* gone, or elevated */ }
            throw;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>What to start and how to recognize the program's windows afterwards.</summary>
    private sealed record Launch(string File, string Arguments, string Folder, IReadOnlySet<string> Names, string? AppId);

    private static Launch Describe(string programPath)
    {
        if (StoreApps.IsStoreApp(programPath))
            return new Launch("explorer.exe", programPath, "", new HashSet<string>(), StoreApps.AppId(programPath));

        if (!File.Exists(programPath))
            throw new EmbeddingException($"Program not found: {programPath}");

        // A Start menu / desktop shortcut stands for the program it starts, with its arguments.
        var file = programPath;
        var arguments = "";
        string? folder = null;
        if (programPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var shortcut = Shortcuts.Resolve(programPath)
                           ?? throw new EmbeddingException("This shortcut does not point to a program.");
            (file, arguments, folder) = (shortcut.Target, shortcut.Arguments, shortcut.WorkingDirectory);
        }

        // "Update.exe --processStart Discord.exe": the windows belong to the program named in the arguments.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileNameWithoutExtension(file) };
        foreach (Match exe in ExeArgument().Matches(arguments)) names.Add(Path.GetFileNameWithoutExtension(exe.Value));

        return new Launch(file, arguments, folder ?? Path.GetDirectoryName(file) ?? "", names, null);
    }

    [GeneratedRegex(@"[^\s""\\/]+\.exe\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExeArgument();

    /// <summary>A window of the program that was already open before we started it.</summary>
    private static IntPtr OpenWindow(List<TopWindow> open, Launch launch)
    {
        var known = Pick(open, 0, launch, null, captionOnly: true);
        if (known != IntPtr.Zero || launch.AppId is null) return known;

        // A running UWP app sits in ApplicationFrameHost, which does not say whose frame it is; starting it raises the frame.
        var front = GetForegroundWindow();
        return open.Any(w => w.Hwnd == front && w.Class == "ApplicationFrameWindow") ? front : IntPtr.Zero;
    }

    private static Win32EmbeddedWindow Embed(IntPtr hwnd, IntPtr owner, bool owned)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        Process windowProcess;
        try { windowProcess = Process.GetProcessById((int)pid); }
        catch (ArgumentException) { throw new EmbeddingException("The program's window disappeared."); }

        try { return new Win32EmbeddedWindow(windowProcess, hwnd, owner, owned); }
        catch (EmbeddingException)
        {
            windowProcess.Dispose(); // refused (administrator program): it keeps running as a normal window
            throw;
        }
    }

    private readonly record struct TopWindow(IntPtr Hwnd, uint Pid, bool Caption, string Class);

    /// <summary>Visible, unowned, real application windows of other processes.</summary>
    private static List<TopWindow> TopWindows()
    {
        var list = new List<TopWindow>();
        var self = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self) return true;

            var style = Style(hwnd);
            var ex = ExStyle(hwnd);
            if ((style & WS_CHILD) != 0 || (ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;
            if (IsCloaked(hwnd) || GetWindowTextLengthW(hwnd) == 0) return true;
            if (!GetWindowRect(hwnd, out var r) || r.Right - r.Left < 160 || r.Bottom - r.Top < 100) return true;

            var cls = ClassName(hwnd);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true; // the desktop itself
            list.Add(new TopWindow(hwnd, pid, (style & WS_CAPTION) == WS_CAPTION || (style & WS_THICKFRAME) != 0, cls));
            return true;
        }, IntPtr.Zero);
        return list; // EnumWindows walks front to back, so earlier = more recently raised
    }

    /// <summary>
    /// The best window for the program among <paramref name="windows"/>: one of the process we started, then one of
    /// another copy of the same program, then (new windows only) a Store-app frame or a window of a process that
    /// started after we launched.
    /// </summary>
    private static IntPtr Pick(IEnumerable<TopWindow> windows, uint startedPid, Launch launch, DateTime? startedAt, bool captionOnly)
    {
        IntPtr best = IntPtr.Zero;
        var bestScore = 0;
        foreach (var w in windows)
        {
            if (captionOnly && !w.Caption) continue;
            var score = 0;
            if (startedPid != 0 && w.Pid == startedPid) score = 4;
            else if (BelongsTo(w.Pid, launch)) score = 3;
            else if (startedAt is not null && w.Class == "ApplicationFrameWindow") score = 2;
            else if (startedAt is { } since && StartedAfter(w.Pid, since)) score = 1;
            if (score > bestScore) { best = w.Hwnd; bestScore = score; }
        }
        return best;
    }

    private static bool BelongsTo(uint pid, Launch launch)
        => launch.AppId is { } appId
            ? string.Equals(ProcessAppId(pid), appId, StringComparison.OrdinalIgnoreCase)
            : ProcessPath(pid) is { } path && launch.Names.Contains(Path.GetFileNameWithoutExtension(path));

    private static bool StartedAfter(uint pid, DateTime since)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.StartTime >= since.AddSeconds(-1);
        }
        catch (Exception) { return false; } // gone, or elevated (no access)
    }
}
