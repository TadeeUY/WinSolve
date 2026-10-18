using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>Where a CPU temperature reading came from.</summary>
public enum CpuTempSource { None, LibreHardwareMonitor, OpenHardwareMonitor, AcpiThermalZone }

public readonly record struct CpuTempReading(double? Celsius, CpuTempSource Source);

/// <summary>
/// CPU temperature without installing a kernel driver (the drivers that read the CPU sensors
/// directly, such as WinRing0, are on Microsoft's vulnerable-driver blocklist and flagged by
/// antivirus software). In order of accuracy:
/// <list type="number">
/// <item>LibreHardwareMonitor or OpenHardwareMonitor, if the user runs one: they publish the real
/// package / Tctl sensor over WMI.</item>
/// <item>The ACPI thermal zone the firmware reports. It is often the motherboard rather than the
/// CPU, and many PCs report a fixed placeholder; a value that never moves while the CPU load changes
/// a lot is treated as "not reported" instead of being shown as a temperature.</item>
/// </list>
/// </summary>
public static class CpuTemperature
{
    private static readonly object Gate = new();
    private static DateTime _nextNamespaceCheck = DateTime.MinValue;
    private static string? _monitorNamespace;
    private static readonly ThermalZoneFilter Filter = new();

    public static CpuTempReading Read(double cpuPercent)
    {
        if (MonitorNamespace() is { } ns && ReadHardwareMonitor(ns) is { } c)
            return new(c, ns.EndsWith("LibreHardwareMonitor", StringComparison.Ordinal) ? CpuTempSource.LibreHardwareMonitor : CpuTempSource.OpenHardwareMonitor);

        // ACPI thermal zones report Kelvin.
        var zones = Wmi.Query("SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation")
            .Select(z => z.Get<uint>("Temperature")).Where(k => k > 273 && k < 400).ToList();
        if (zones.Count == 0) return new(null, CpuTempSource.None);
        var celsius = zones.Max() - 273.15;
        lock (Gate)
        {
            Filter.Add(celsius, cpuPercent);
            return Filter.IsFrozen ? new(null, CpuTempSource.None) : new(celsius, CpuTempSource.AcpiThermalZone);
        }
    }

    /// <summary>The WMI namespace of a running hardware monitor (checked every few minutes, it can be started later).</summary>
    private static string? MonitorNamespace()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow < _nextNamespaceCheck) return _monitorNamespace;
            _nextNamespaceCheck = DateTime.UtcNow.AddMinutes(2);
            var names = Wmi.Query("SELECT Name FROM __NAMESPACE", "root").Select(n => n.Str("Name")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _monitorNamespace = names.Contains("LibreHardwareMonitor") ? @"root\LibreHardwareMonitor"
                : names.Contains("OpenHardwareMonitor") ? @"root\OpenHardwareMonitor" : null;
            return _monitorNamespace;
        }
    }

    private static double? ReadHardwareMonitor(string ns)
    {
        var sensors = Wmi.Query("SELECT Name, Identifier, Value FROM Sensor WHERE SensorType = 'Temperature'", ns)
            .Select(s => (Name: s.Str("Name"), Id: s.Str("Identifier"), Value: (double)s.Get<float>("Value")))
            .ToList();
        return PickCpuSensor(sensors);
    }

    /// <summary>The CPU package (Intel) or Tctl/Tdie (AMD) sensor, else the hottest core.</summary>
    internal static double? PickCpuSensor(IReadOnlyList<(string Name, string Id, double Value)> sensors)
    {
        var cpu = sensors.Where(s => (s.Id.Contains("/intelcpu/", StringComparison.OrdinalIgnoreCase) || s.Id.Contains("/amdcpu/", StringComparison.OrdinalIgnoreCase))
                                     && s.Value is > 0 and < 125).ToList();
        if (cpu.Count == 0) return null;
        var main = cpu.FirstOrDefault(s => s.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase)
                                           || s.Name.StartsWith("Core (Tctl", StringComparison.OrdinalIgnoreCase)
                                           || s.Name.Equals("Core (Tdie)", StringComparison.OrdinalIgnoreCase));
        if (main.Name is not null) return main.Value;
        var cores = cpu.Where(s => s.Name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase) || s.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase)).ToList();
        return cores.Count > 0 ? cores.Max(s => s.Value) : null;
    }
}

/// <summary>
/// Spots a firmware thermal zone that reports a placeholder: after enough samples, if the value has
/// stayed within half a degree while the CPU load varied by 40 points or more, it isn't a CPU sensor.
/// </summary>
public sealed class ThermalZoneFilter
{
    private const int MinSamples = 30;
    private double _minTemp = double.MaxValue, _maxTemp = double.MinValue, _minLoad = double.MaxValue, _maxLoad = double.MinValue;
    private int _samples;

    public void Add(double celsius, double cpuPercent)
    {
        _samples++;
        _minTemp = Math.Min(_minTemp, celsius);
        _maxTemp = Math.Max(_maxTemp, celsius);
        _minLoad = Math.Min(_minLoad, cpuPercent);
        _maxLoad = Math.Max(_maxLoad, cpuPercent);
    }

    /// <summary>The zone never moved although the load did: don't show it as the CPU temperature.</summary>
    public bool IsFrozen => _samples >= MinSamples && _maxTemp - _minTemp < 0.5 && _maxLoad - _minLoad >= 40;
}
