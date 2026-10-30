using System.Diagnostics;
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
public sealed class Win32WindowEmbedder : IWindowEmbedder
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(25);

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
        if (!File.Exists(programPath))
            throw new EmbeddingException($"Program not found: {programPath}");

        // A dropped Start menu / desktop shortcut stands for the program it starts.
        if (programPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            programPath = Shortcuts.Resolve(programPath)
                          ?? throw new EmbeddingException("This shortcut does not point to a program.");

        var exeName = Path.GetFileNameWithoutExtension(programPath);
        var before = TopWindows().Select(w => w.Hwnd).ToHashSet();
        var started = DateTime.Now;

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(programPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(programPath) ?? "",
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new EmbeddingException($"The program could not be started: {ex.Message}", ex);
        }

        var startedPid = process is { HasExited: false } ? (uint)process.Id : 0u;
        try
        {
            IntPtr candidate = IntPtr.Zero;
            var deadline = DateTime.UtcNow + WindowTimeout;
            var adoptAt = DateTime.UtcNow + AdoptAfter;
            var splashUntil = DateTime.UtcNow + SplashGrace;
            while (DateTime.UtcNow < deadline)
            {
                cancellation.ThrowIfCancellationRequested();

                var windows = TopWindows();
                var fresh = Pick(windows.Where(w => !before.Contains(w.Hwnd)), startedPid, exeName, started,
                                 captionOnly: DateTime.UtcNow < splashUntil);

                // The same window on two polls in a row: it is not a flash (splash, "loading…" popup).
                if (fresh != IntPtr.Zero && fresh == candidate)
                    return Embed(fresh, ownerHandle, process, owned: true);
                candidate = fresh;

                if (fresh == IntPtr.Zero && DateTime.UtcNow >= adoptAt
                    && Pick(windows.Where(w => before.Contains(w.Hwnd)), 0, exeName, null, captionOnly: true) is var old
                    && old != IntPtr.Zero)
                {
                    // No new window: the program was already running and only brought its window forward.
                    return Embed(old, ownerHandle, process, owned: false);
                }

                await Task.Delay(150, cancellation);
            }
            throw new EmbeddingException(process is { HasExited: true }
                ? "The program exited without showing a window."
                : "The program did not show a window in time.");
        }
        catch
        {
            // Never leave a half-started program running (cancelled tab, timeout).
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process?.Dispose();
            throw;
        }
    }

    private static Win32EmbeddedWindow Embed(IntPtr hwnd, IntPtr owner, Process? started, bool owned)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        Process windowProcess;
        try { windowProcess = Process.GetProcessById((int)pid); }
        catch (ArgumentException) { throw new EmbeddingException("The program's window disappeared."); }

        // Kill the process we started only when the window really is its own.
        var ownProcess = started is not null && !started.HasExited && started.Id == (int)pid;
        if (!ownProcess) started?.Dispose();
        return new Win32EmbeddedWindow(windowProcess, hwnd, owner, owned, killOnClose: owned && ownProcess);
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
    private static IntPtr Pick(IEnumerable<TopWindow> windows, uint startedPid, string exeName, DateTime? startedAt, bool captionOnly)
    {
        IntPtr best = IntPtr.Zero;
        var bestScore = 0;
        foreach (var w in windows)
        {
            if (captionOnly && !w.Caption) continue;
            var score = 0;
            if (startedPid != 0 && w.Pid == startedPid) score = 4;
            else if (ProcessPath(w.Pid) is { } path
                     && string.Equals(Path.GetFileNameWithoutExtension(path), exeName, StringComparison.OrdinalIgnoreCase)) score = 3;
            else if (startedAt is not null && w.Class == "ApplicationFrameWindow") score = 2;
            else if (startedAt is { } since && StartedAfter(w.Pid, since)) score = 1;
            if (score > bestScore) { best = w.Hwnd; bestScore = score; }
        }
        return best;
    }

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
