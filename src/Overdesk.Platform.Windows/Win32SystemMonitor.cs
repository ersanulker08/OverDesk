using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Overdesk.Core.Platform;

namespace Overdesk.Platform.Windows;

/// <summary>CPU through GetSystemTimes, memory through GlobalMemoryStatusEx, listening ports through GetExtendedTcpTable.</summary>
public sealed class Win32SystemMonitor : ISystemMonitor
{
    private const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;

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

    public Task<IReadOnlyList<ListeningPort>> GetListeningPortsAsync(CancellationToken cancellation)
    {
        var ports = new Dictionary<int, ListeningPort>();
        ReadListeners(AfInet, ports);
        ReadListeners(AfInet6, ports); // dual-stack servers (Node's listen(3000)) only show up here
        return Task.FromResult<IReadOnlyList<ListeningPort>>(ports.Values.OrderBy(p => p.Port).ToList());
    }

    private static void ReadListeners(int family, Dictionary<int, ListeningPort> ports)
    {
        byte[]? table = null;
        var size = 0;
        uint result;
        while ((result = GetExtendedTcpTable(table, ref size, false, family, TcpTableOwnerPidListener, 0)) != 0)
        {
            if (result != ErrorInsufficientBuffer) return;
            table = new byte[size]; // the table can grow between the calls, so this repeats until it fits
        }
        if (table is null) return;

        // MIB_TCPROW_OWNER_PID and MIB_TCP6ROW_OWNER_PID, which follow a 4-byte row count.
        var (rowSize, addressAt, addressSize, portAt, pidAt) = family == AfInet6 ? (56, 0, 16, 20, 52) : (24, 4, 4, 8, 20);
        var count = BinaryPrimitives.ReadInt32LittleEndian(table);
        for (var i = 0; i < count; i++)
        {
            var row = table.AsSpan(4 + i * rowSize, rowSize);
            var port = BinaryPrimitives.ReadUInt16BigEndian(row[portAt..]);
            if (ports.ContainsKey(port)) continue;
            var pid = BinaryPrimitives.ReadInt32LittleEndian(row[pidAt..]);
            ports[port] = new ListeningPort(port, new IPAddress(row.Slice(addressAt, addressSize)).ToString(), pid, ProcessName(pid));
        }
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

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(byte[]? table, ref int size, bool sorted, int family, int tableClass, uint reserved);
}
