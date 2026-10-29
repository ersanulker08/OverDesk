using System.Diagnostics;
using Overdesk.Core.Platform;
using static Overdesk.Platform.Windows.NativeMethods;

namespace Overdesk.Platform.Windows;

/// <summary>Windows layer: start the program, wait for its main window, re-parent it.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class Win32WindowEmbedder : IWindowEmbedder
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(20);

    public string PlatformName => "Windows";
    public IReadOnlyList<string> ProgramPatterns => new[] { "*.exe", "*.lnk" };
    public bool HasPermissions => true;
    public string? PermissionHelp => null;
    public void RequestPermissions() { }

    /// <summary>Child windows live inside Overdesk, so full screen needs no special handling on Windows.</summary>
    public void PrepareOwnerWindow(IntPtr ownerHandle) { }

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

        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(programPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(programPath) ?? "",
            }) ?? throw new EmbeddingException("The program could not be started.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new EmbeddingException($"The program could not be started: {ex.Message}", ex);
        }

        try
        {
            var deadline = DateTime.UtcNow + WindowTimeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellation.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new EmbeddingException("The program exited before showing a window (it may hand over to another process).");

                var hwnd = FindMainWindow(process);
                if (hwnd != IntPtr.Zero)
                    return new Win32EmbeddedWindow(process, hwnd, ownerHandle);

                await Task.Delay(150, cancellation);
            }
            throw new EmbeddingException("The program did not show a window in time.");
        }
        catch
        {
            // Never leave a half-started program running (cancelled tab, timeout, early exit).
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
            throw;
        }
    }

    private static IntPtr FindMainWindow(Process process)
    {
        process.Refresh();
        if (process.MainWindowHandle != IntPtr.Zero)
            return process.MainWindowHandle;

        IntPtr found = IntPtr.Zero;
        var pid = (uint)process.Id;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid && IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero)
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
