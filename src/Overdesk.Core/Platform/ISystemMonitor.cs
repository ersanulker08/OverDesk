namespace Overdesk.Core.Platform;

/// <summary>CPU and memory use of the whole machine, plus the extras a platform can report (null when it cannot).</summary>
public sealed record SystemLoad(double CpuPercent, double MemoryPercent, long MemoryUsedBytes, long MemoryTotalBytes)
{
    public double? GpuPercent { get; init; }

    /// <summary>Degrees Celsius; null when the machine does not expose a sensor.</summary>
    public double? CpuTemperature { get; init; }
    public double? GpuTemperature { get; init; }

    /// <summary>GPU compute engines (CUDA/DirectML/AI workloads), separate from 3D rendering. Windows only for now.</summary>
    public double? GpuComputePercent { get; init; }

    /// <summary>Neural processing unit load. Windows only for now.</summary>
    public double? NpuPercent { get; init; }
    public long? SwapUsedBytes { get; init; }
    public long? SwapTotalBytes { get; init; }

    /// <summary>Null on machines without a battery.</summary>
    public double? BatteryPercent { get; init; }
    public bool? BatteryCharging { get; init; }
}

/// <summary>A TCP port some local process is listening on (a dev server, a database...).</summary>
public sealed record ListeningPort(int Port, string Address, int ProcessId, string ProcessName);

/// <summary>Machine-wide numbers for the developer widgets.</summary>
public interface ISystemMonitor
{
    /// <summary>
    /// CPU use since the previous call (the first call may report 0), memory, and the optional extras.
    /// May block for a few milliseconds; call it off the UI thread.
    /// </summary>
    SystemLoad? Sample();

    /// <summary>TCP ports in the LISTEN state, one entry per port.</summary>
    Task<IReadOnlyList<ListeningPort>> GetListeningPortsAsync(CancellationToken cancellation);
}

/// <summary>Used where no monitor exists.</summary>
public sealed class NoSystemMonitor : ISystemMonitor
{
    public SystemLoad? Sample() => null;
    public Task<IReadOnlyList<ListeningPort>> GetListeningPortsAsync(CancellationToken cancellation)
        => Task.FromResult<IReadOnlyList<ListeningPort>>(Array.Empty<ListeningPort>());
}
