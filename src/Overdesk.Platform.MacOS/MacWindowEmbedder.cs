using System.Diagnostics;
using System.Runtime.InteropServices;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.MacOS;

/// <summary>macOS layer: launch the app, then track its window through the Accessibility API.</summary>
public sealed class MacWindowEmbedder : IWindowEmbedder
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(20);

    public string PlatformName => "macOS";
    public IReadOnlyList<string> ProgramPatterns => new[] { "*.app" };

    public bool HasPermissions => Native.AXIsProcessTrusted();

    public string? PermissionHelp => HasPermissions
        ? null
        : "OverDesk needs Accessibility permission to place other programs inside its window. " +
          "Enable it in System Settings → Privacy & Security → Accessibility, then reopen the tab.";

    public void RequestPermissions()
    {
        // Ask macOS to show its prompt, which also adds this app to the Accessibility list.
        var promptKey = Native.ReadGlobal(Native.AppServicesPath, "kAXTrustedCheckOptionPrompt");
        var yes = Native.ReadGlobal(Native.CoreFoundationPath, "kCFBooleanTrue");
        var options = Native.CFDictionaryCreate(IntPtr.Zero, new[] { promptKey }, new[] { yes }, 1, IntPtr.Zero, IntPtr.Zero);
        try { Native.AXIsProcessTrustedWithOptions(options); }
        finally { Native.CFRelease(options); }

        Process.Start(new ProcessStartInfo("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")
        {
            UseShellExecute = false,
        })?.Dispose();
    }

    public bool? CanMirror => WindowCapture.Available;

    public void RequestMirrorPermission()
    {
        WindowCapture.RequestPermission();
        Process.Start(new ProcessStartInfo("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")
        {
            UseShellExecute = false,
        })?.Dispose();
    }

    public void PrepareOwnerWindow(IntPtr ownerHandle)
    {
        if (ownerHandle == IntPtr.Zero) return;
        const nuint fullScreenPrimary = 1 << 7, fullScreenNone = 1 << 9;
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            // The handle may be the NSView or the NSWindow depending on the UI toolkit version.
            var window = ObjC.RespondsTo(ownerHandle, "collectionBehavior") ? ownerHandle : ObjC.Call(ownerHandle, "window");
            if (window == IntPtr.Zero || !ObjC.RespondsTo(window, "setCollectionBehavior:")) return;
            var behavior = ObjC.CallNUInt(window, "collectionBehavior");
            ObjC.Call(window, "setCollectionBehavior:", (behavior & ~fullScreenPrimary) | fullScreenNone);
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    public async Task<IEmbeddedWindow> LaunchAsync(string programPath, IntPtr ownerHandle, CancellationToken cancellation)
    {
        if (!HasPermissions)
            throw new EmbeddingException(PermissionHelp!);

        var trimmed = BundleRoot(programPath.TrimEnd('/'));
        if (!Directory.Exists(trimmed) && !File.Exists(trimmed))
            throw new EmbeddingException($"Program not found: {programPath}");

        Process process;
        bool startedByUs;
        if (trimmed.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            (process, startedByUs) = await LaunchBundleAsync(trimmed, cancellation);
        else
        {
            process = StartExecutable(trimmed);
            startedByUs = true;
        }

        var ax = new AccessibilityWindow(process.Id);
        try
        {
            var started = DateTime.UtcNow;
            var deadline = started + WindowTimeout;
            var reopened = false;
            while (DateTime.UtcNow < deadline)
            {
                cancellation.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new EmbeddingException("The program exited before showing a window.");
                if (ax.TryAcquireWindow())
                    return new MacEmbeddedWindow(process, ax, startedByUs);

                // A running app with no window open (e.g. Safari after closing its last window): ask for one, once.
                if (!startedByUs && !reopened && DateTime.UtcNow - started > TimeSpan.FromSeconds(1))
                {
                    reopened = true;
                    await RunOpenAsync(trimmed, cancellation);
                }
                await Task.Delay(150, cancellation);
            }
            throw new EmbeddingException("The program did not show a main window in time. If it opened a dialog first, finish it and press Try again.");
        }
        catch
        {
            ax.Dispose();
            // Only quit what we started ourselves: an app that was already running is left alone.
            if (startedByUs)
            {
                try { if (!process.HasExited && !RunningApps.Terminate(process.Id)) process.Kill(); }
                catch (InvalidOperationException) { }
            }
            process.Dispose();
            throw;
        }
    }

    /// <summary>A path picked inside an .app bundle (e.g. Contents/MacOS/x) stands for the bundle itself.</summary>
    private static string BundleRoot(string path)
    {
        var index = path.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? path : path[..(index + 4)];
    }

    private static Process StartExecutable(string path)
        => Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })
           ?? throw new EmbeddingException("The program could not be started.");

    private static async Task<(Process Process, bool StartedByUs)> LaunchBundleAsync(string bundle, CancellationToken cancellation)
    {
        var bundleId = ReadPlistValue(bundle, "CFBundleIdentifier")
                       ?? throw new EmbeddingException("This is not a valid application bundle (no CFBundleIdentifier).");

        // Adopt an instance that is already running instead of starting a second one.
        if (RunningApps.Pids(bundleId).FirstOrDefault() is > 0 and var running)
        {
            try { return (Process.GetProcessById(running), false); }
            catch (ArgumentException) { /* exited in between: launch below */ }
        }

        await RunOpenAsync(bundle, cancellation);

        // `open` gives no pid; ask AppKit which process now runs this bundle.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            if (RunningApps.Pids(bundleId).FirstOrDefault() is > 0 and var pid)
            {
                try { return (Process.GetProcessById(pid), true); }
                catch (ArgumentException) { }
            }
            await Task.Delay(150, cancellation);
        }
        throw new EmbeddingException("The application did not start.");
    }

    private static async Task RunOpenAsync(string bundle, CancellationToken cancellation)
    {
        using var open = Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-a", bundle }, UseShellExecute = false })
                         ?? throw new EmbeddingException("Could not run 'open'.");
        await open.WaitForExitAsync(cancellation);
        if (open.ExitCode != 0)
            throw new EmbeddingException($"macOS could not open the application (exit code {open.ExitCode}).");
    }

    private static string? ReadPlistValue(string bundle, string key)
    {
        var plist = Path.Combine(bundle, "Contents", "Info.plist");
        if (!File.Exists(plist)) return null;
        var info = new ProcessStartInfo("/usr/libexec/PlistBuddy")
        {
            ArgumentList = { "-c", $"Print :{key}", plist },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(info);
        if (p is null) return null;
        var value = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return p.ExitCode == 0 && value.Length > 0 ? value : null;
    }
}
