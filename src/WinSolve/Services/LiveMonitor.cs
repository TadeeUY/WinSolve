using System.Runtime.InteropServices;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed class GpuSensor
{
    public required string Name { get; init; }
    public double? TemperatureC { get; set; }
    public int? FanRpm { get; set; }
}

public sealed class LiveSample
{
    public double CpuPercent { get; set; }
    public double? CpuGhz { get; set; }
    public double? CpuTempC { get; set; }
    public double RamUsedPercent { get; set; }
    public long RamUsedBytes { get; set; }
    public long RamTotalBytes { get; set; }
    public double? GpuPercent { get; set; }
    public double DiskPercent { get; set; }
    public double NetMbps { get; set; }
    public List<GpuSensor> Gpus { get; } = [];
}

/// <summary>
/// Live sensors without third-party kernel drivers:
/// CPU/RAM/disk/network/GPU load from WMI performance classes (language-independent),
/// GPU temperature from D3DKMT (the same source Task Manager uses) with NVML as fallback,
/// and CPU temperature from the ACPI thermal zone when the firmware reports one.
/// </summary>
public static class LiveMonitor
{
    private static double? _baseMhz;

    private static string Adapter(string engineName)
    {
        var m = System.Text.RegularExpressions.Regex.Match(engineName, @"luid_0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+");
        return m.Success ? m.Value : "";
    }

    public static LiveSample Sample()
    {
        var s = new LiveSample();

        foreach (var p in Wmi.Query("SELECT Name, PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name = '_Total'"))
            s.CpuPercent = p.Get<ulong>("PercentProcessorTime");

        _baseMhz ??= Wmi.Query("SELECT MaxClockSpeed FROM Win32_Processor").Select(c => (double?)c.Get<uint>("MaxClockSpeed")).FirstOrDefault();
        foreach (var p in Wmi.Query("SELECT Name, PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name = '_Total'"))
        {
            var perf = p.Get<ulong>("PercentProcessorPerformance");
            if (perf > 0 && _baseMhz is > 0) s.CpuGhz = _baseMhz.Value * perf / 100.0 / 1000.0;
        }

        // ACPI thermal zones report Kelvin; many desktops expose none or a fixed value.
        var zones = Wmi.Query("SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation")
            .Select(z => z.Get<uint>("Temperature")).Where(k => k > 273 && k < 400).ToList();
        if (zones.Count > 0) s.CpuTempC = zones.Max() - 273.15;

        foreach (var os in Wmi.Query("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
        {
            s.RamTotalBytes = os.Get<long>("TotalVisibleMemorySize") * 1024;
            s.RamUsedBytes = s.RamTotalBytes - os.Get<long>("FreePhysicalMemory") * 1024;
            s.RamUsedPercent = s.RamTotalBytes > 0 ? s.RamUsedBytes * 100.0 / s.RamTotalBytes : 0;
        }

        var engines = Wmi.Query("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
        if (engines.Count > 0)
        {
            // Task Manager shows the busiest engine type; 3D is the usual one.
            // Per adapter (luid) and engine type, so an iGPU and a dGPU aren't added together.
            s.GpuPercent = Math.Min(100, engines
                .GroupBy(e => (Adapter(e.Str("Name")), EngineType(e.Str("Name"))))
                .Select(g => g.Sum(e => (double)e.Get<ulong>("UtilizationPercentage")))
                .DefaultIfEmpty(0).Max());
        }

        foreach (var d in Wmi.Query("SELECT PercentDiskTime FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name = '_Total'"))
            s.DiskPercent = Math.Min(100, d.Get<ulong>("PercentDiskTime"));

        s.NetMbps = Wmi.Query("SELECT BytesTotalPersec FROM Win32_PerfFormattedData_Tcpip_NetworkInterface")
            .Sum(n => (double)n.Get<ulong>("BytesTotalPersec")) * 8 / 1_000_000;

        s.Gpus.AddRange(GpuSensors());
        return s;
    }

    private static string EngineType(string name)
    {
        var i = name.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? name : name[(i + 8)..];
    }

    private static List<GpuSensor> GpuSensors()
    {
        var list = new List<GpuSensor>();
        try { list.AddRange(D3dkmt.Read()); } catch (Exception ex) { Logger.Write($"D3DKMT sensors unavailable: {ex.Message}"); }
        if (list.All(g => g.TemperatureC is null))
        {
            try
            {
                var nv = Nvml.Read();
                if (nv.Count > 0) return nv;
            }
            catch { /* no NVIDIA driver */ }
        }
        return list;
    }

    // ═══════════════ D3DKMT (gdi32): adapter performance data, WDDM 2.4+ ═══════════════

    private static class D3dkmt
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ADAPTERINFO
        {
            public uint hAdapter;
            public LUID AdapterLuid;
            public uint NumOfSources;
            public int bPrecisePresentRegionsPreferred;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }

        [StructLayout(LayoutKind.Sequential)]
        private struct QUERYADAPTERINFO
        {
            public uint hAdapter;
            public int Type;
            public IntPtr pPrivateDriverData;
            public uint PrivateDriverDataSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ADAPTER_PERFDATA
        {
            public uint PhysicalAdapterIndex;
            public ulong MemoryFrequency;
            public ulong MaxMemoryFrequency;
            public ulong MaxMemoryFrequencyOC;
            public ulong MemoryBandwidth;
            public ulong PCIEBandwidth;
            public uint FanRPM;
            public uint Power;
            public uint Temperature;   // deci-Celsius
            public byte PowerStateOverride;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CLOSEADAPTER { public uint hAdapter; }

        private const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8;
        private const int KMTQAITYPE_ADAPTERPERFDATA = 62;

        [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref ENUMADAPTERS2 data);
        [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QUERYADAPTERINFO data);
        [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER data);

        public static List<GpuSensor> Read()
        {
            var result = new List<GpuSensor>();
            var e = new ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return result;

            var size = Marshal.SizeOf<ADAPTERINFO>();
            e.pAdapters = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
            try
            {
                if (D3DKMTEnumAdapters2(ref e) != 0) return result;
                for (int i = 0; i < e.NumAdapters; i++)
                {
                    var info = Marshal.PtrToStructure<ADAPTERINFO>(e.pAdapters + i * size);
                    try
                    {
                        var name = QueryName(info.hAdapter);
                        if (name is null || name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) continue;
                        var perf = QueryPerf(info.hAdapter);
                        result.Add(new GpuSensor
                        {
                            Name = name,
                            TemperatureC = perf is { Temperature: > 0 and < 1500 } p ? p.Temperature / 10.0 : null,
                            FanRpm = perf is { FanRPM: > 0 and < 20000 } f ? (int)f.FanRPM : null,
                        });
                    }
                    finally
                    {
                        var close = new CLOSEADAPTER { hAdapter = info.hAdapter };
                        D3DKMTCloseAdapter(ref close);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(e.pAdapters);
            }
            // Same physical GPU can be listed once per output; keep one entry per name.
            return result.GroupBy(g => g.Name).Select(g => g.OrderByDescending(x => x.TemperatureC ?? 0).First()).ToList();
        }

        private static string? QueryName(uint adapter)
        {
            const int chars = 260 * 4; // AdapterString, BiosString, DacType, ChipType
            var buffer = Marshal.AllocHGlobal(chars * 2);
            try
            {
                var q = new QUERYADAPTERINFO { hAdapter = adapter, Type = KMTQAITYPE_ADAPTERREGISTRYINFO, pPrivateDriverData = buffer, PrivateDriverDataSize = chars * 2 };
                return D3DKMTQueryAdapterInfo(ref q) == 0 ? Marshal.PtrToStringUni(buffer)?.Trim() : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static ADAPTER_PERFDATA? QueryPerf(uint adapter)
        {
            var size = Marshal.SizeOf<ADAPTER_PERFDATA>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(new ADAPTER_PERFDATA(), buffer, false);
                var q = new QUERYADAPTERINFO { hAdapter = adapter, Type = KMTQAITYPE_ADAPTERPERFDATA, pPrivateDriverData = buffer, PrivateDriverDataSize = (uint)size };
                return D3DKMTQueryAdapterInfo(ref q) == 0 ? Marshal.PtrToStructure<ADAPTER_PERFDATA>(buffer) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    // ═══════════════ NVML (nvml.dll from the NVIDIA driver) ═══════════════

    private static class Nvml
    {
        private static bool _initialized;

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] private static extern int Init();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")] private static extern int GetCount(out uint count);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int GetHandle(uint index, out IntPtr device);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] private static extern int GetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)]
        private static extern int GetName(IntPtr device, [Out] byte[] name, uint length);

        public static List<GpuSensor> Read()
        {
            var list = new List<GpuSensor>();
            // nvml.dll is installed in System32 by NVIDIA DCH drivers; DLL search is restricted to System32.
            if (!File.Exists(Path.Combine(Environment.SystemDirectory, "nvml.dll"))) return list;
            if (!_initialized) _initialized = Init() == 0;
            if (!_initialized || GetCount(out var count) != 0) return list;
            for (uint i = 0; i < count; i++)
            {
                if (GetHandle(i, out var dev) != 0) continue;
                var nameBuf = new byte[96];
                var name = GetName(dev, nameBuf, (uint)nameBuf.Length) == 0
                    ? System.Text.Encoding.ASCII.GetString(nameBuf).TrimEnd('\0') : "NVIDIA GPU";
                list.Add(new GpuSensor
                {
                    Name = name,
                    TemperatureC = GetTemperature(dev, 0, out var t) == 0 ? t : null,
                });
            }
            return list;
        }
    }
}
