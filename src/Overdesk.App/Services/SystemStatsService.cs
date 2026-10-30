using System.Diagnostics;
using System.Net.NetworkInformation;
using Avalonia.Threading;
using Overdesk.Core.Platform;

namespace Overdesk.App.Services;

/// <summary>How a metric is drawn in the dock.</summary>
internal enum MetricKind { Chart, Bar, Text }

/// <summary>One gauge the user can switch on in the dock.</summary>
internal sealed record MetricInfo(string Id, string Label, MetricKind Kind, string Description, bool WindowsOnly = false);

/// <summary>The current reading of a metric, ready to display. Null fraction/history when not applicable.</summary>
internal sealed record MetricReading(string Value, double? Fraction = null, IReadOnlyList<double>? History = null, double HistoryMax = 100);

/// <summary>
/// Samples the machine once a second on a background thread: CPU, GPU, memory, swap and battery from the
/// platform layer; network, disk, uptime and process count through .NET (the same on every OS).
/// Keeps a short history for the charts and scans listening ports on demand.
/// </summary>
internal sealed class SystemStatsService : IDisposable
{
    public const int HistoryLength = 60;

    public static readonly IReadOnlyList<MetricInfo> Catalog = new[]
    {
        new MetricInfo("cpu", "CPU", MetricKind.Chart, "Processor load"),
        new MetricInfo("gpu", "GPU", MetricKind.Chart, "Graphics processor load"),
        new MetricInfo("gpuai", "GPU AI / compute", MetricKind.Chart, "CUDA, DirectML and other AI work on the GPU", WindowsOnly: true),
        new MetricInfo("npu", "NPU", MetricKind.Chart, "Neural processing unit (AI chip) load", WindowsOnly: true),
        new MetricInfo("ram", "Memory", MetricKind.Chart, "Memory in use"),
        new MetricInfo("net", "Network", MetricKind.Chart, "Download and upload speed"),
        new MetricInfo("disk", "Disk", MetricKind.Bar, "Space used on the system disk"),
        new MetricInfo("swap", "Swap", MetricKind.Bar, "Memory moved to disk"),
        new MetricInfo("battery", "Battery", MetricKind.Bar, "Charge level"),
        new MetricInfo("uptime", "Uptime", MetricKind.Text, "Time since the computer started"),
        new MetricInfo("procs", "Processes", MetricKind.Text, "Programs and services running"),
    };

    private readonly ISystemMonitor _monitor;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, List<double>> _history = new();
    private (long Rx, long Tx, DateTime At)? _lastNet;
    private (double Down, double Up) _netRate;
    private (long Used, long Total)? _disk;
    private int _processes;
    private bool _scanningPorts;
    private int _ticks;

    public SystemStatsService(ISystemMonitor monitor)
    {
        _monitor = monitor;
        _ = LoopAsync();
    }

    public event Action? Sampled;
    public event Action? PortsChanged;

    public SystemLoad? Latest { get; private set; }
    public IReadOnlyList<ListeningPort> Ports { get; private set; } = Array.Empty<ListeningPort>();

    /// <summary>Set by the UI: only scan ports while someone can see them.</summary>
    public bool WatchPorts { get; set; }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var sample = await Task.Run(SampleAll);
            if (sample is not null)
            {
                Latest = sample;
                Sampled?.Invoke();
            }
            if (WatchPorts && _ticks++ % 4 == 0) _ = RefreshPortsAsync();
            try { await Task.Delay(1000, _stop.Token); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Runs on a worker thread.</summary>
    private SystemLoad? SampleAll()
    {
        SystemLoad? load;
        try { load = _monitor.Sample(); } catch (Exception) { load = null; }

        SampleNetwork();
        if (_ticks % 10 == 0) SampleDisk();
        if (_ticks % 5 == 0) SampleProcesses();

        Dispatcher.UIThread.Post(() =>
        {
            if (load is null) return;
            Push("cpu", load.CpuPercent);
            Push("ram", load.MemoryPercent);
            if (load.GpuPercent is { } gpu) Push("gpu", gpu);
            if (load.GpuComputePercent is { } compute) Push("gpuai", compute);
            if (load.NpuPercent is { } npu) Push("npu", npu);
            Push("net", _netRate.Down);
        });
        return load;
    }

    private void SampleNetwork()
    {
        try
        {
            long rx = 0, tx = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
            var now = DateTime.UtcNow;
            if (_lastNet is { } last)
            {
                var seconds = Math.Max((now - last.At).TotalSeconds, 0.1);
                _netRate = (Math.Max(rx - last.Rx, 0) / seconds, Math.Max(tx - last.Tx, 0) / seconds);
            }
            _lastNet = (rx, tx, now);
        }
        catch (Exception) { /* not supported here */ }
    }

    private void SampleDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) is { Length: > 0 } r ? r : "/";
            // On macOS the user's data lives on the Data volume, which is what "free space" means to people.
            if (OperatingSystem.IsMacOS() && Directory.Exists("/System/Volumes/Data")) root = "/System/Volumes/Data";
            var drive = new DriveInfo(root);
            _disk = (drive.TotalSize - drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception) { _disk = null; }
    }

    private void SampleProcesses()
    {
        try
        {
            var all = Process.GetProcesses();
            _processes = all.Length;
            foreach (var p in all) p.Dispose();
        }
        catch (Exception) { }
    }

    private void Push(string id, double value)
    {
        if (!_history.TryGetValue(id, out var list)) _history[id] = list = new List<double>();
        // Start with a flat line across the whole chart instead of a lonely dot on the right.
        if (list.Count == 0)
            for (var i = 0; i < HistoryLength - 1; i++) list.Add(value);
        list.Add(value);
        if (list.Count > HistoryLength) list.RemoveAt(0);
    }

    public IReadOnlyList<double> History(string id)
        => _history.TryGetValue(id, out var list) ? list : Array.Empty<double>();

    /// <summary>The display value of a metric, or null when this machine cannot measure it.</summary>
    public MetricReading? Read(string id)
    {
        var load = Latest;
        switch (id)
        {
            case "cpu" when load is not null:
                return new MetricReading($"{load.CpuPercent:0}%{Degrees(load.CpuTemperature)}", load.CpuPercent / 100, History("cpu"));
            case "gpu" when load?.GpuPercent is { } gpu:
                return new MetricReading($"{gpu:0}%{Degrees(load.GpuTemperature)}", gpu / 100, History("gpu"));
            case "gpuai" when load?.GpuComputePercent is { } compute:
                return new MetricReading($"{compute:0}%", compute / 100, History("gpuai"));
            case "npu" when load?.NpuPercent is { } npu:
                return new MetricReading($"{npu:0}%", npu / 100, History("npu"));
            case "ram" when load is not null:
                return new MetricReading($"{load.MemoryPercent:0}%  ·  {Gb(load.MemoryUsedBytes)} / {Gb(load.MemoryTotalBytes)} GB",
                    load.MemoryPercent / 100, History("ram"));
            case "net":
                var history = History("net");
                var max = Math.Max(history.Count == 0 ? 0 : history.Max(), 64 * 1024);
                return new MetricReading($"↓ {Rate(_netRate.Down)}   ↑ {Rate(_netRate.Up)}", null, history, max);
            case "disk" when _disk is { Total: > 0 } d:
                return new MetricReading($"{Gb(d.Total - d.Used)} GB free of {Gb(d.Total)}", d.Used / (double)d.Total);
            case "swap" when load?.SwapTotalBytes is > 0 and var total:
                var used = load.SwapUsedBytes ?? 0;
                return new MetricReading($"{Gb(used)} / {Gb(total)} GB", used / (double)total);
            case "swap" when load?.SwapTotalBytes is not null:
                return new MetricReading("Not in use", 0);
            case "battery" when load?.BatteryPercent is { } battery:
                return new MetricReading($"{battery:0}%{(load.BatteryCharging == true ? "  ·  charging" : "")}", battery / 100);
            case "uptime":
                var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
                return new MetricReading(up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m" : $"{up.Hours}h {up.Minutes}m");
            case "procs" when _processes > 0:
                return new MetricReading(_processes.ToString());
            default:
                return null;
        }
    }

    private static string Degrees(double? celsius) => celsius is { } c ? $"  ·  {c:0}°C" : "";

    private static string Gb(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.0");

    internal static string Rate(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => $"{bytesPerSecond / 1024 / 1024:0.0} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:0} KB/s",
        _ => $"{bytesPerSecond:0} B/s",
    };

    public async Task RefreshPortsAsync()
    {
        if (_scanningPorts) return;
        _scanningPorts = true;
        try
        {
            var ports = await Task.Run(() => _monitor.GetListeningPortsAsync(_stop.Token));
            var changed = ports.Count != Ports.Count || ports.Zip(Ports).Any(p => p.First != p.Second);
            Ports = ports;
            if (changed) PortsChanged?.Invoke();
        }
        catch (Exception) { /* lsof/netstat unavailable: keep the old list */ }
        finally { _scanningPorts = false; }
    }

    public void Dispose() => _stop.Cancel();
}
