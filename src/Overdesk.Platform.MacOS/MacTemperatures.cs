using System.Runtime.InteropServices;
using System.Text;

namespace Overdesk.Platform.MacOS;

/// <summary>
/// Temperatures from the IOKit HID thermal sensors (the same source tools like Stats use; no extra permission needed).
/// Apple Silicon names its die sensors "PMU tdie*"; some chips also have "pACC/eACC MTR" (CPU clusters) and
/// "GPU MTR" sensors. The GPU sits on the same die, so the die temperature stands in when there is no GPU sensor.
/// </summary>
internal sealed class MacTemperatures
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const long EventTypeTemperature = 15;
    private const int TemperatureField = 15 << 16;

    private IntPtr _client;
    private bool _unavailable;

    public (double? Cpu, double? Gpu) Read()
    {
        if (_unavailable) return (null, null);
        try
        {
            if (_client == IntPtr.Zero && !Open()) return (null, null);
            var services = IOHIDEventSystemClientCopyServices(_client);
            if (services == IntPtr.Zero) return (null, null);
            var cpuCluster = new List<double>();
            var die = new List<double>();
            var gpu = new List<double>();
            try
            {
                var count = Native.CFArrayGetCount(services);
                for (nint i = 0; i < count; i++)
                {
                    var service = Native.CFArrayGetValueAtIndex(services, i);
                    var name = Product(service);
                    if (name is null) continue;
                    var evt = IOHIDServiceClientCopyEvent(service, EventTypeTemperature, 0, 0);
                    if (evt == IntPtr.Zero) continue;
                    var value = IOHIDEventGetFloatValue(evt, TemperatureField);
                    Native.CFRelease(evt);
                    if (value is <= 0 or > 150) continue;

                    if (name.Contains("ACC MTR", StringComparison.Ordinal)) cpuCluster.Add(value);
                    else if (name.Contains("GPU MTR", StringComparison.Ordinal)) gpu.Add(value);
                    else if (name.StartsWith("PMU tdie", StringComparison.Ordinal)) die.Add(value);
                }
            }
            finally
            {
                Native.CFRelease(services);
            }

            double? cpuTemp = cpuCluster.Count > 0 ? cpuCluster.Average() : die.Count > 0 ? die.Average() : null;
            double? gpuTemp = gpu.Count > 0 ? gpu.Average() : die.Count > 0 ? die.Max() : null;
            return (cpuTemp, gpuTemp);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            return (null, null);
        }
    }

    private bool Open()
    {
        _client = IOHIDEventSystemClientCreate(IntPtr.Zero);
        if (_client == IntPtr.Zero) { _unavailable = true; return false; }

        // Match Apple's vendor thermal sensors: PrimaryUsagePage 0xff00, PrimaryUsage 5.
        var lib = NativeLibrary.Load(Native.CoreFoundationPath);
        var keyCallbacks = NativeLibrary.GetExport(lib, "kCFTypeDictionaryKeyCallBacks");
        var valueCallbacks = NativeLibrary.GetExport(lib, "kCFTypeDictionaryValueCallBacks");
        var keys = new[] { Cf("PrimaryUsagePage"), Cf("PrimaryUsage") };
        var page = 0xff00;
        var usage = 5;
        var values = new[] { CFNumberCreate(IntPtr.Zero, 3, ref page), CFNumberCreate(IntPtr.Zero, 3, ref usage) };
        var matching = Native.CFDictionaryCreate(IntPtr.Zero, keys, values, 2, keyCallbacks, valueCallbacks);
        IOHIDEventSystemClientSetMatching(_client, matching);
        Native.CFRelease(matching);
        foreach (var k in keys) Native.CFRelease(k);
        foreach (var v in values) Native.CFRelease(v);
        return true;
    }

    private static IntPtr Cf(string s) => Native.CFStringCreateWithCString(IntPtr.Zero, s, Native.CFStringEncodingUTF8);

    private static readonly IntPtr ProductKey = Cf("Product");

    private static string? Product(IntPtr service)
    {
        var value = IOHIDServiceClientCopyProperty(service, ProductKey);
        if (value == IntPtr.Zero) return null;
        try
        {
            var buffer = new byte[128];
            return CFStringGetCString(value, buffer, buffer.Length, Native.CFStringEncodingUTF8)
                ? Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0) is var n and >= 0 ? n : buffer.Length)
                : null;
        }
        finally
        {
            Native.CFRelease(value);
        }
    }

    [DllImport(IOKit)] private static extern IntPtr IOHIDEventSystemClientCreate(IntPtr allocator);
    [DllImport(IOKit)] private static extern int IOHIDEventSystemClientSetMatching(IntPtr client, IntPtr matching);
    [DllImport(IOKit)] private static extern IntPtr IOHIDEventSystemClientCopyServices(IntPtr client);
    [DllImport(IOKit)] private static extern IntPtr IOHIDServiceClientCopyProperty(IntPtr service, IntPtr key);
    [DllImport(IOKit)] private static extern IntPtr IOHIDServiceClientCopyEvent(IntPtr service, long type, int options, long timestamp);
    [DllImport(IOKit)] private static extern double IOHIDEventGetFloatValue(IntPtr evt, int field);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFNumberCreate(IntPtr allocator, int type, ref int value);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFStringGetCString(IntPtr str, byte[] buffer, nint size, uint encoding);
}
