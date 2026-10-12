using System.Diagnostics.Eventing.Reader;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed record ProblemDevice(string Name, string Class, string InstanceId, int ErrorCode, string Explanation);

public sealed record BatteryInfo(string Name, int ChargePercent, long? DesignCapacity, long? FullChargeCapacity)
{
    public int? HealthPercent => DesignCapacity is > 0 && FullChargeCapacity is > 0
        ? (int)Math.Min(100, Math.Round(FullChargeCapacity.Value * 100.0 / DesignCapacity.Value))
        : null;
}

public sealed record MemoryModule(string Slot, string Manufacturer, string PartNumber, long CapacityBytes, int SpeedMhz);

public sealed record CriticalEvent(DateTime Time, string Source, int EventId, string Summary);

public static class HardwareService
{
    /// <summary>Device Manager problem codes (CM_PROB_*) in plain words.</summary>
    private static readonly Dictionary<int, string> ErrorCodes = new()
    {
        [1] = "The device is not configured correctly. Reinstall the driver.",
        [3] = "The driver is corrupted or the system is low on memory.",
        [10] = "The device cannot start. Update or reinstall the driver.",
        [12] = "Not enough free resources (resource conflict).",
        [14] = "The computer must be restarted.",
        [18] = "The drivers must be reinstalled.",
        [19] = "Its registry configuration is corrupted.",
        [21] = "Windows is removing the device.",
        [22] = "The device is disabled.",
        [24] = "The device is missing or not working properly.",
        [28] = "No driver is installed for this device.",
        [29] = "Disabled by the firmware (check BIOS/UEFI settings).",
        [31] = "Windows cannot load the required drivers.",
        [32] = "The driver service is disabled.",
        [33] = "Windows cannot determine the required resources.",
        [34] = "The device must be configured manually.",
        [35] = "The firmware lacks required information (update the BIOS).",
        [37] = "The driver returned a failure during initialization.",
        [38] = "A previous instance of the driver is still in memory. Restart.",
        [39] = "The driver is corrupted or missing.",
        [40] = "Service information is missing from the registry.",
        [41] = "The driver loaded but cannot find the hardware.",
        [43] = "Windows stopped the device because it reported problems (common with failing GPUs/USB devices).",
        [45] = "The device is not connected.",
        [47] = "Prepared for safe removal. Unplug it and plug it back in.",
        [48] = "The driver was blocked because of known issues.",
        [49] = "The system registry hive exceeded its size limit.",
        [52] = "Windows cannot verify the driver's digital signature.",
    };

    public static string ExplainCode(int code) => ErrorCodes.GetValueOrDefault(code, $"Device error code {code}.");

    public static List<ProblemDevice> GetProblemDevices()
    {
        var list = new List<ProblemDevice>();
        foreach (var d in Wmi.Query("SELECT Name, PNPClass, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
        {
            var code = d.Get<int>("ConfigManagerErrorCode");
            // 22 (deliberately disabled) and 45 (unplugged) are not real errors.
            if (code is 22 or 45) continue;
            list.Add(new ProblemDevice(
                d.Str("Name") is { Length: > 0 } n ? n : "Unknown device",
                d.Str("PNPClass"),
                d.Str("PNPDeviceID"),
                code,
                ExplainCode(code)));
        }
        return list;
    }

    // ───────────── Safe automatic repair ─────────────

    public enum DeviceRepairAction { None, RestartOnly, RestartThenReinstall }

    public sealed record DeviceRepairPlan(ProblemDevice Device, DeviceRepairAction Action);

    /// <summary>
    /// Problem codes that restarting or reinstalling the device can fix. Others are left alone:
    /// disabled (22, 29, 32), unplugged (45, 47), pending restart or removal (14, 21), blocked or
    /// unsigned drivers (48, 52), resource conflicts (12)...
    /// </summary>
    public static readonly IReadOnlySet<int> RepairableCodes = new HashSet<int> { 1, 3, 10, 18, 19, 24, 28, 31, 37, 39, 40, 41, 43 };

    /// <summary>
    /// Device classes that are never removed, only restarted: removing a network adapter loses its
    /// settings, and removing storage, input, system, display or USB controller devices can leave
    /// the PC without a disk, keyboard, mouse, screen or USB ports until the next restart.
    /// </summary>
    public static readonly IReadOnlySet<string> RestartOnlyClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Net", "SCSIAdapter", "HDC", "HDCController", "DiskDrive", "Volume", "VolumeSnapshot", "Keyboard", "Mouse", "HIDClass",
        "System", "Processor", "Computer", "Battery", "Display", "USB", "SoftwareDevice", "Firmware", "SecurityDevices",
    };

    /// <summary>What the automatic repair may do for a device with this class and problem code.</summary>
    public static DeviceRepairAction RepairActionFor(string deviceClass, int errorCode)
    {
        if (!RepairableCodes.Contains(errorCode)) return DeviceRepairAction.None;
        return RestartOnlyClasses.Contains(deviceClass ?? "") ? DeviceRepairAction.RestartOnly : DeviceRepairAction.RestartThenReinstall;
    }

    /// <summary>The devices the automatic repair may act on, and how.</summary>
    public static List<DeviceRepairPlan> PlanRepairs(IEnumerable<ProblemDevice> devices)
        => devices.Select(d => new DeviceRepairPlan(d, RepairActionFor(d.Class, d.ErrorCode)))
            .Where(p => p.Action != DeviceRepairAction.None)
            .ToList();

    /// <summary>One line per device for the confirmation: what will happen to it.</summary>
    public static string DescribePlan(DeviceRepairPlan p) => p.Action == DeviceRepairAction.RestartThenReinstall
        ? $"• {p.Device.Name} (code {p.Device.ErrorCode}): restart; if it still fails, remove it so Windows reinstalls it"
        : $"• {p.Device.Name} (code {p.Device.ErrorCode}): restart only";

    /// <summary>Restarts a device (disable + enable) with pnputil.</summary>
    public static Task<ProcessResult> RestartDeviceAsync(string instanceId, Action<string> log, CancellationToken ct = default)
        => ProcessRunner.RunAsync("pnputil.exe", $"/restart-device \"{CheckId(instanceId)}\"", log, ct);

    /// <summary>Device instance IDs come from drivers; refuse anything that could break out of quotes.</summary>
    private static string CheckId(string instanceId)
        => instanceId.IndexOfAny(['"', '\r', '\n', '\0']) >= 0 ? throw new ArgumentException("Invalid device instance ID.") : instanceId;

    /// <summary>Removes the device so Windows reinstalls it on the next hardware scan.</summary>
    /// <remarks>Callers decide with <see cref="RepairActionFor"/> whether removing is safe.</remarks>
    public static async Task ReinstallDeviceAsync(string instanceId, Action<string> log, CancellationToken ct = default)
    {
        await ProcessRunner.RunAsync("pnputil.exe", $"/remove-device \"{CheckId(instanceId)}\"", log, ct);
        await ProcessRunner.RunAsync("pnputil.exe", "/scan-devices", log, ct);
    }

    public static List<BatteryInfo> GetBatteries()
    {
        var result = new List<BatteryInfo>();
        var batteries = Wmi.Query("SELECT Name, EstimatedChargeRemaining FROM Win32_Battery");
        if (batteries.Count == 0) return result;

        var design = Wmi.Query("SELECT DesignedCapacity FROM BatteryStaticData", @"root\wmi");
        var full = Wmi.Query("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", @"root\wmi");

        for (int i = 0; i < batteries.Count; i++)
        {
            var b = batteries[i];
            long? dc = i < design.Count ? design[i].Get<long>("DesignedCapacity") : null;
            long? fc = i < full.Count ? full[i].Get<long>("FullChargedCapacity") : null;
            result.Add(new BatteryInfo(b.Str("Name"), b.Get<int>("EstimatedChargeRemaining"), dc, fc));
        }
        return result;
    }

    public static List<MemoryModule> GetMemory()
        => Wmi.Query("SELECT DeviceLocator, Manufacturer, PartNumber, Capacity, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory")
            .Select(m => new MemoryModule(
                m.Str("DeviceLocator"),
                m.Str("Manufacturer"),
                m.Str("PartNumber"),
                m.Get<long>("Capacity"),
                m.Get<int>("ConfiguredClockSpeed") is > 0 and var s ? s : m.Get<int>("Speed")))
            .ToList();

    /// <summary>
    /// Critical events from the last days: blue screens, unexpected shutdowns,
    /// hardware errors (WHEA) and disk errors.
    /// </summary>
    public static List<CriticalEvent> GetCriticalEvents(int days = 30)
    {
        var events = new List<CriticalEvent>();
        var ms = (long)TimeSpan.FromDays(days).TotalMilliseconds;
        var query = $"""
            <QueryList><Query Id="0" Path="System">
              <Select Path="System">*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) &lt;= {ms}]]]</Select>
              <Select Path="System">*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41) and TimeCreated[timediff(@SystemTime) &lt;= {ms}]]]</Select>
              <Select Path="System">*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting' or @Name='BugCheck'] and (EventID=1001) and TimeCreated[timediff(@SystemTime) &lt;= {ms}]]]</Select>
              <Select Path="System">*[System[Provider[@Name='disk' or @Name='Disk' or @Name='Ntfs' or @Name='storahci' or @Name='stornvme'] and (Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) &lt;= {ms}]]]</Select>
              <Select Path="System">*[System[Provider[@Name='Display'] and (EventID=4101) and TimeCreated[timediff(@SystemTime) &lt;= {ms}]]]</Select>
            </Query></QueryList>
            """;

        try
        {
            // Capped per kind, not overall: hundreds of errors from a flaky USB disk must not
            // push an older blue screen (1001) or power loss (41) out of the list.
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query) { ReverseDirection = true });
            var perKind = new Dictionary<string, int>();
            var read = 0;
            for (var rec = reader.ReadEvent(); rec is not null && read < 5000; rec = reader.ReadEvent(), read++)
            {
                using (rec)
                {
                    var kind = rec.Id is 1001 or 41 ? rec.Id.ToString() : rec.ProviderName;
                    var n = perKind.GetValueOrDefault(kind);
                    if (n >= 60) continue;
                    perKind[kind] = n + 1;
                    events.Add(new CriticalEvent(rec.TimeCreated ?? DateTime.MinValue, rec.ProviderName, rec.Id, Summarize(rec)));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not read the event log: {ex.Message}");
        }
        return events;
    }

    private static string Summarize(EventRecord rec) => (rec.ProviderName, rec.Id) switch
    {
        ("Microsoft-Windows-Kernel-Power", 41) => "Unexpected shutdown (power loss, hang or forced restart).",
        (_, 1001) when rec.ProviderName is "BugCheck" or "Microsoft-Windows-WER-SystemErrorReporting"
            => "Blue screen (BSOD). " + FirstLine(rec),
        ("Microsoft-Windows-WHEA-Logger", _) => "Hardware error reported by the CPU/motherboard (WHEA). " + FirstLine(rec),
        ("Display", 4101) => "The display driver stopped responding and recovered.",
        _ => FirstLine(rec),
    };

    private static string FirstLine(EventRecord rec)
    {
        try
        {
            var text = rec.FormatDescription() ?? "";
            var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            return line.Length > 220 ? line[..220] + "…" : line;
        }
        catch
        {
            return $"Event {rec.Id}";
        }
    }
}
