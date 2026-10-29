using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.MacOS;

/// <summary>CPU/memory through Mach host statistics, listening ports through lsof.</summary>
public sealed class MacSystemMonitor : ISystemMonitor
{
    private const int HostCpuLoadInfo = 3;
    private const int HostVmInfo64 = 4;
    private const int CpuStateCount = 4;   // user, system, idle, nice
    private const int VmInfo64Count = 38;  // sizeof(vm_statistics64) / sizeof(natural_t)

    private static readonly Regex GpuUtilization = new("\"Device Utilization %\"\\s*=\\s*(\\d+)", RegexOptions.Compiled);
    private static readonly Regex BatteryLine = new("(\\d+)%;\\s*([a-zA-Z ]+);", RegexOptions.Compiled);

    private uint[]? _lastTicks;
    private readonly MacTemperatures _temperatures = new();
    private (DateTime At, double? Cpu, double? Gpu) _temps;
    private (DateTime At, double? Value) _gpu;
    private (DateTime At, double? Percent, bool? Charging) _battery = (DateTime.MinValue, null, null);

    public SystemLoad? Sample()
    {
        try
        {
            var host = mach_host_self();

            var ticks = new uint[CpuStateCount];
            var count = CpuStateCount;
            if (host_statistics(host, HostCpuLoadInfo, ticks, ref count) != 0) return null;
            var cpu = 0.0;
            if (_lastTicks is { } last)
            {
                double user = ticks[0] - last[0], system = ticks[1] - last[1], idle = ticks[2] - last[2], nice = ticks[3] - last[3];
                var all = user + system + idle + nice;
                cpu = all > 0 ? (user + system + nice) / all * 100 : 0;
            }
            _lastTicks = ticks;

            var vm = new uint[VmInfo64Count];
            count = VmInfo64Count;
            if (host_statistics64(host, HostVmInfo64, vm, ref count) != 0) return null;
            // Same formula as Activity Monitor's "Memory Used": app memory + wired + compressed.
            long Field(int byteOffset) => vm[byteOffset / 4];
            var wired = Field(12);
            var purgeable = Field(88);
            var compressed = Field(128);
            var internalPages = Field(140);
            var pageSize = (long)getpagesize();
            var used = (internalPages - purgeable + wired + compressed) * pageSize;
            var total = MemSize();
            var (swapUsed, swapTotal) = Swap();
            var (battery, charging) = Battery();
            return new SystemLoad(cpu, total > 0 ? used * 100.0 / total : 0, used, total)
            {
                GpuPercent = Gpu(),
                CpuTemperature = Temperatures().Cpu,
                GpuTemperature = Temperatures().Gpu,
                SwapUsedBytes = swapUsed,
                SwapTotalBytes = swapTotal,
                BatteryPercent = battery,
                BatteryCharging = charging,
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ListeningPort>> GetListeningPortsAsync(CancellationToken cancellation)
    {
        var info = new ProcessStartInfo("/usr/sbin/lsof")
        {
            ArgumentList = { "-nP", "-iTCP", "-sTCP:LISTEN", "-Fpcn" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(info);
        if (process is null) return Array.Empty<ListeningPort>();
        var output = await process.StandardOutput.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);

        // -F output: "p<pid>", "c<command>", then one "n<address:port>" per socket.
        var ports = new Dictionary<int, ListeningPort>();
        int pid = 0;
        var name = "";
        foreach (var line in output.Split('\n'))
        {
            if (line.Length < 2) continue;
            var value = line[1..];
            switch (line[0])
            {
                case 'p': int.TryParse(value, out pid); break;
                case 'c': name = value; break;
                case 'n':
                    var colon = value.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(value[(colon + 1)..], out var port) && !ports.ContainsKey(port))
                        ports[port] = new ListeningPort(port, value[..colon], pid, name);
                    break;
            }
        }
        return ports.Values.OrderBy(p => p.Port).ToList();
    }

    private (double? Cpu, double? Gpu) Temperatures()
    {
        if (DateTime.UtcNow - _temps.At > TimeSpan.FromSeconds(3))
        {
            var (cpu, gpu) = _temperatures.Read();
            _temps = (DateTime.UtcNow, cpu, gpu);
        }
        return (_temps.Cpu, _temps.Gpu);
    }

    /// <summary>GPU busy % from the IOAccelerator's performance statistics (Apple Silicon and most Intel GPUs).</summary>
    private double? Gpu()
    {
        if (DateTime.UtcNow - _gpu.At < TimeSpan.FromSeconds(2)) return _gpu.Value;
        double? value = null;
        var output = Run("/usr/sbin/ioreg", "-r", "-d", "1", "-w", "0", "-c", "IOAccelerator");
        var match = output is null ? null : GpuUtilization.Match(output);
        if (match is { Success: true }) value = Math.Clamp(double.Parse(match.Groups[1].Value), 0, 100);
        _gpu = (DateTime.UtcNow, value);
        return value;
    }

    /// <summary>Battery level and charging state from pmset; null on desktops.</summary>
    private (double? Percent, bool? Charging) Battery()
    {
        if (DateTime.UtcNow - _battery.At < TimeSpan.FromSeconds(30)) return (_battery.Percent, _battery.Charging);
        double? percent = null;
        bool? charging = null;
        var output = Run("/usr/bin/pmset", "-g", "batt");
        var match = output is null ? null : BatteryLine.Match(output);
        if (match is { Success: true })
        {
            percent = double.Parse(match.Groups[1].Value);
            var state = match.Groups[2].Value.Trim();
            charging = state is "charging" or "charged" or "finishing charge";
        }
        _battery = (DateTime.UtcNow, percent, charging);
        return (percent, charging);
    }

    private static (long? Used, long? Total) Swap()
    {
        // struct xsw_usage { u64 total; u64 avail; u64 used; u32 pagesize; boolean_t encrypted; }
        var buffer = new long[4];
        var size = (nint)32;
        if (sysctlbyname("vm.swapusage", buffer, ref size, IntPtr.Zero, 0) != 0) return (null, null);
        return (buffer[2], buffer[0]);
    }

    private static string? Run(string file, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return output;
        }
        catch (Exception) { return null; }
    }

    private static long MemSize()
    {
        long value = 0;
        var size = (nint)sizeof(long);
        return sysctlbyname("hw.memsize", ref value, ref size, IntPtr.Zero, 0) == 0 ? value : 0;
    }

    private const string LibSystem = "/usr/lib/libSystem.dylib";
    [DllImport(LibSystem)] private static extern uint mach_host_self();
    [DllImport(LibSystem)] private static extern int host_statistics(uint host, int flavor, [Out] uint[] info, ref int count);
    [DllImport(LibSystem)] private static extern int host_statistics64(uint host, int flavor, [Out] uint[] info, ref int count);
    [DllImport(LibSystem)] private static extern int getpagesize();
    [DllImport(LibSystem)] private static extern int sysctlbyname(string name, ref long value, ref nint size, IntPtr newValue, nint newSize);
    [DllImport(LibSystem)] private static extern int sysctlbyname(string name, [Out] long[] value, ref nint size, IntPtr newValue, nint newSize);
}
