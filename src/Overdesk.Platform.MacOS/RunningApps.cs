namespace Overdesk.Platform.MacOS;

/// <summary>NSRunningApplication lookups: find and politely quit apps by bundle identifier.</summary>
internal static class RunningApps
{
    /// <summary>Process ids of running instances of the bundle, newest first.</summary>
    public static IReadOnlyList<int> Pids(string bundleId)
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var apps = ObjC.Call(ObjC.Class("NSRunningApplication"), "runningApplicationsWithBundleIdentifier:", ObjC.NSString(bundleId));
            if (apps == IntPtr.Zero) return Array.Empty<int>();
            var count = (int)ObjC.CallNUInt(apps, "count");
            var pids = new List<int>(count);
            for (var i = count - 1; i >= 0; i--)
            {
                var app = ObjC.Call(apps, "objectAtIndex:", (nuint)i);
                if (app != IntPtr.Zero && !ObjC.CallBool(app, "isTerminated")) pids.Add(ObjC.CallInt(app, "processIdentifier"));
            }
            return pids;
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>Process id of the app the user is working in right now.</summary>
    public static int FrontmostPid()
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var workspace = ObjC.Call(ObjC.Class("NSWorkspace"), "sharedWorkspace");
            var app = ObjC.Call(workspace, "frontmostApplication");
            return app == IntPtr.Zero ? 0 : ObjC.CallInt(app, "processIdentifier");
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>Ask the app to quit the normal way (like Cmd+Q, so it can offer to save). False when it is not running.</summary>
    public static bool Terminate(int pid)
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var app = ObjC.Call(ObjC.Class("NSRunningApplication"), "runningApplicationWithProcessIdentifier:", (nuint)pid);
            return app != IntPtr.Zero && ObjC.CallBool(app, "terminate");
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }
}
