using System.Runtime.InteropServices;

namespace Overdesk.Platform.Windows;

/// <summary>
/// GPU/NPU engine loads and temperatures, read the way Task Manager does: the "GPU Engine" performance
/// counters (one instance per process and engine, e.g. "pid_12_luid_0x0_0x1A2B_phys_0_eng_0_engtype_3D").
/// NPUs report through the same counters; their adapter LUIDs are found with DXCore.
/// Temperatures: ACPI thermal zones for the CPU, NVML for NVIDIA GPUs. Every part fails soft (null).
/// </summary>
internal sealed unsafe class WinAccelerators
{
    private IntPtr _query;
    private IntPtr _engineCounter;
    private IntPtr _thermalCounter;
    private bool _unavailable;
    private HashSet<string>? _npuLuids;
    private IntPtr _nvmlDevice;
    private bool _nvmlTried;

    public sealed record Reading(double? Gpu3D, double? GpuCompute, double? Npu, double? CpuTemp, double? GpuTemp);

    public Reading Read()
    {
        if (_unavailable) return new Reading(null, null, null, null, null);
        try
        {
            if (_query == IntPtr.Zero)
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _unavailable = true; return Read(); }
                PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engineCounter);
                PdhAddEnglishCounter(_query, @"\Thermal Zone Information(*)\Temperature", IntPtr.Zero, out _thermalCounter);
                _npuLuids = NpuLuids();
                PdhCollectQueryData(_query);
                return new Reading(null, null, null, null, NvidiaTemperature()); // rate counters need two samples
            }

            if (PdhCollectQueryData(_query) != 0) return new Reading(null, null, null, null, null);

            double gpu3D = 0, compute = 0, npu = 0;
            var engines = Values(_engineCounter);
            foreach (var (name, value) in engines)
            {
                var isNpu = _npuLuids is { Count: > 0 } luids && luids.Any(l => name.Contains(l, StringComparison.OrdinalIgnoreCase));
                if (isNpu) npu += value;
                else if (name.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) gpu3D += value;
                else if (name.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("engtype_Cuda", StringComparison.OrdinalIgnoreCase)) compute += value;
            }

            // Thermal zones report Kelvin.
            var zones = Values(_thermalCounter).Select(v => v.Value - 273.15).Where(c => c is > 5 and < 125).ToList();

            return new Reading(
                engines.Count > 0 ? Math.Clamp(gpu3D, 0, 100) : null,
                engines.Count > 0 ? Math.Clamp(compute, 0, 100) : null,
                _npuLuids is { Count: > 0 } ? Math.Clamp(npu, 0, 100) : null,
                zones.Count > 0 ? zones.Max() : null,
                NvidiaTemperature());
        }
        catch (Exception)
        {
            _unavailable = true;
            return new Reading(null, null, null, null, null);
        }
    }

    private static List<(string Name, double Value)> Values(IntPtr counter)
    {
        var result = new List<(string, double)>();
        if (counter == IntPtr.Zero) return result;
        uint size = 0, count = 0;
        if (PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, ref count, IntPtr.Zero) != PdhMoreData || size == 0)
            return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, ref count, buffer) != 0) return result;
            // PDH_FMT_COUNTERVALUE_ITEM_W: { LPWSTR name; { DWORD status; double value; } }
            var itemSize = IntPtr.Size == 8 ? 24 : 16;
            var valueOffset = IntPtr.Size == 8 ? 16 : 8;
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * itemSize;
                if (Marshal.ReadInt32(item, IntPtr.Size) != 0) continue;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                result.Add((name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, valueOffset))));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    // ---------------------------------------------------------------- NPU discovery through DXCore

    private static readonly Guid IidAdapterFactory = new("78ee5945-c36e-4b13-a669-005dd11c0f06");
    private static readonly Guid IidAdapterList = new("526c7776-40e9-459b-b711-f32ad76dfc28");
    private static readonly Guid IidAdapter = new("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
    private static readonly Guid HardwareTypeNpu = new("d46140c4-add7-451b-9e56-06fe8c3b58ed");
    private const uint PropertyInstanceLuid = 0;

    /// <summary>LUIDs of NPU adapters formatted like the counter instance names ("luid_0x00000000_0x0000ABCD").</summary>
    private static HashSet<string> NpuLuids()
    {
        var luids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!NativeLibrary.TryLoad("dxcore.dll", out var lib)) return luids;
            var create = (delegate* unmanaged[Stdcall]<Guid*, IntPtr*, int>)NativeLibrary.GetExport(lib, "DXCoreCreateAdapterFactory");
            var iid = IidAdapterFactory;
            IntPtr factory;
            if (create(&iid, &factory) < 0 || factory == IntPtr.Zero) return luids;
            try
            {
                // IDXCoreAdapterFactory::CreateAdapterList is vtable slot 3.
                var vtbl = *(IntPtr**)factory;
                var createList = (delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, Guid*, IntPtr*, int>)vtbl[3];
                var filter = HardwareTypeNpu;
                var listIid = IidAdapterList;
                IntPtr list;
                if (createList(factory, 1, &filter, &listIid, &list) < 0 || list == IntPtr.Zero) return luids;
                try
                {
                    var listVtbl = *(IntPtr**)list;
                    var getAdapter = (delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)listVtbl[3];
                    var getCount = (delegate* unmanaged[Stdcall]<IntPtr, uint>)listVtbl[4];
                    var adapterIid = IidAdapter;
                    for (uint i = 0; i < getCount(list); i++)
                    {
                        IntPtr adapter;
                        if (getAdapter(list, i, &adapterIid, &adapter) < 0 || adapter == IntPtr.Zero) continue;
                        try
                        {
                            // IDXCoreAdapter::GetProperty is vtable slot 6.
                            var adapterVtbl = *(IntPtr**)adapter;
                            var getProperty = (delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, void*, int>)adapterVtbl[6];
                            long luid = 0;
                            if (getProperty(adapter, PropertyInstanceLuid, (nuint)sizeof(long), &luid) >= 0)
                                luids.Add($"luid_0x{(uint)(luid >> 32):X8}_0x{(uint)luid:X8}");
                        }
                        finally { Release(adapter); }
                    }
                }
                finally { Release(list); }
            }
            finally { Release(factory); }
        }
        catch (Exception) { /* no DXCore (older Windows): no NPU gauge */ }
        return luids;
    }

    private static void Release(IntPtr unknown)
    {
        var vtbl = *(IntPtr**)unknown;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2])(unknown);
    }

    // ---------------------------------------------------------------- NVIDIA temperature through NVML

    private double? NvidiaTemperature()
    {
        try
        {
            if (!_nvmlTried)
            {
                _nvmlTried = true;
                if (!NativeLibrary.TryLoad("nvml.dll", out var nvml)) return null;
                var init = (delegate* unmanaged<int>)NativeLibrary.GetExport(nvml, "nvmlInit_v2");
                var byIndex = (delegate* unmanaged<uint, IntPtr*, int>)NativeLibrary.GetExport(nvml, "nvmlDeviceGetHandleByIndex_v2");
                _nvmlTemperature = (delegate* unmanaged<IntPtr, int, uint*, int>)NativeLibrary.GetExport(nvml, "nvmlDeviceGetTemperature");
                IntPtr device;
                if (init() != 0 || byIndex(0, &device) != 0) return null;
                _nvmlDevice = device;
            }
            if (_nvmlDevice == IntPtr.Zero || _nvmlTemperature == null) return null;
            uint temp;
            return _nvmlTemperature(_nvmlDevice, 0, &temp) == 0 ? temp : null;
        }
        catch (Exception)
        {
            _nvmlDevice = IntPtr.Zero;
            return null;
        }
    }

    private delegate* unmanaged<IntPtr, int, uint*, int> _nvmlTemperature;

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr items);
}
