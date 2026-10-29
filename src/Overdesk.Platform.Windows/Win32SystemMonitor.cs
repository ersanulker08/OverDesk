using System.Diagnostics;
using System.Runtime.InteropServices;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.Windows;

/// <summary>CPU through GetSystemTimes, memory through GlobalMemoryStatusEx, listening ports through netstat.</summary>
public sealed class Win32SystemMonitor : ISystemMonitor
{
    private (ulong Idle, ulong Kernel, ulong User)? _last;
    private readonly WinAccelerators _accelerators = new();

    public SystemLoad? Sample()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return null;
        var now = (Idle: ToULong(idleTime), Kernel: ToULong(kernelTime), User: ToULong(userTime));
        var cpu = 0.0;
        if (_last is { } last)
        {
            // Kernel time includes idle time.
            double idle = now.Idle - last.Idle, total = now.Kernel - last.Kernel + (now.User - last.User);
            cpu = total > 0 ? (total - idle) / total * 100 : 0;
        }
        _last = now;

        var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref memory)) return null;
        var used = (long)(memory.ullTotalPhys - memory.ullAvailPhys);

        // The page file total includes physical memory; what is left over is the swap part.
        var swapTotal = (long)memory.ullTotalPageFile - (long)memory.ullTotalPhys;
        var committed = (long)(memory.ullTotalPageFile - memory.ullAvailPageFile);
        var swapUsed = Math.Clamp(committed - used, 0, Math.Max(swapTotal, 0));

        double? battery = null;
        bool? charging = null;
        if (GetSystemPowerStatus(out var power) && (power.BatteryFlag & 128) == 0 && power.BatteryLifePercent <= 100)
        {
            battery = power.BatteryLifePercent;
            charging = power.ACLineStatus == 1;
        }

        var accel = _accelerators.Read();
        return new SystemLoad(cpu, memory.dwMemoryLoad, used, (long)memory.ullTotalPhys)
        {
            GpuPercent = accel.Gpu3D,
            GpuComputePercent = accel.GpuCompute,
            NpuPercent = accel.Npu,
            CpuTemperature = accel.CpuTemp,
            GpuTemperature = accel.GpuTemp,
            SwapUsedBytes = swapTotal > 0 ? swapUsed : null,
            SwapTotalBytes = swapTotal > 0 ? swapTotal : null,
            BatteryPercent = battery,
            BatteryCharging = charging,
        };
    }

    public async Task<IReadOnlyList<ListeningPort>> GetListeningPortsAsync(CancellationToken cancellation)
    {
        var info = new ProcessStartInfo("netstat", "-ano -p TCP")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info);
        if (process is null) return Array.Empty<ListeningPort>();
        var output = await process.StandardOutput.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);

        // "  TCP    0.0.0.0:3000    0.0.0.0:0    LISTENING    1234"
        var ports = new Dictionary<int, ListeningPort>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0] != "TCP" || parts[3] != "LISTENING") continue;
            var colon = parts[1].LastIndexOf(':');
            if (colon < 0 || !int.TryParse(parts[1][(colon + 1)..], out var port) || ports.ContainsKey(port)) continue;
            int.TryParse(parts[4], out var pid);
            ports[port] = new ListeningPort(port, parts[1][..colon], pid, ProcessName(pid));
        }
        return ports.Values.OrderBy(p => p.Port).ToList();
    }

    private static string ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch (Exception) { return pid == 4 ? "System" : "?"; }
    }

    private static ulong ToULong(System.Runtime.InteropServices.ComTypes.FILETIME t)
        => ((ulong)(uint)t.dwHighDateTime << 32) | (uint)t.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernel, out System.Runtime.InteropServices.ComTypes.FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

}
