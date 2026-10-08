using System.Text.Json;
using WinSolve.Core;

namespace WinSolve.Services;

public enum HealthLevel { Unknown, Good, Caution, Bad }

public sealed record SmartAttribute(byte Id, string Name, byte Current, byte Worst, byte Threshold, long Raw)
{
    /// <summary>Attribute at or below the manufacturer threshold.</summary>
    public bool Failing => Threshold > 0 && Current > 0 && Current <= Threshold;
}

public sealed class DiskInfo
{
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string BusType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string WindowsHealth { get; set; } = "";
    public bool? PredictFailure { get; set; }
    public int? TemperatureC { get; set; }
    public int? TemperatureMaxC { get; set; }
    public int? WearPercent { get; set; }
    public long? PowerOnHours { get; set; }
    public long? PowerCycles { get; set; }
    public long? ReadErrors { get; set; }
    public long? WriteErrors { get; set; }
    public List<SmartAttribute> Attributes { get; } = [];
    public HealthLevel Health { get; set; }
    public List<string> Findings { get; } = [];

    public string HealthText => Health switch
    {
        HealthLevel.Good => "Good",
        HealthLevel.Caution => "Caution",
        HealthLevel.Bad => "Bad",
        _ => "Unknown",
    };
}

/// <summary>
/// CrystalDiskInfo-style drive health: combines Storage Reliability Counters
/// (NVMe and SATA) with raw ATA SMART attributes read through WMI.
/// </summary>
public static class DiskHealthService
{
    private static readonly Dictionary<byte, string> AttributeNames = new()
    {
        [0x01] = "Raw read error rate",
        [0x03] = "Spin-up time",
        [0x04] = "Start/stop count",
        [0x05] = "Reallocated sectors",
        [0x07] = "Seek error rate",
        [0x09] = "Power-on hours",
        [0x0A] = "Spin retry count",
        [0x0C] = "Power cycle count",
        [0xAA] = "Available reserved space",
        [0xAB] = "Program fail count",
        [0xAC] = "Erase fail count",
        [0xAD] = "Wear level",
        [0xAE] = "Unexpected power loss count",
        [0xB1] = "Wear leveling count",
        [0xB3] = "Used reserved blocks",
        [0xB5] = "Program fail count",
        [0xB6] = "Erase fail count",
        [0xB7] = "SATA downshift errors",
        [0xB8] = "End-to-end errors",
        [0xBB] = "Reported uncorrectable errors",
        [0xBC] = "Command timeouts",
        [0xBE] = "Airflow temperature",
        [0xBF] = "G-sense error rate",
        [0xC0] = "Power-off retract count",
        [0xC1] = "Load/unload cycles",
        [0xC2] = "Temperature",
        [0xC3] = "Hardware ECC recovered",
        [0xC4] = "Reallocation events",
        [0xC5] = "Current pending sectors",
        [0xC6] = "Uncorrectable sectors",
        [0xC7] = "UltraDMA CRC errors (cable)",
        [0xE7] = "SSD life left",
        [0xE8] = "Available reserve",
        [0xE9] = "Media wearout indicator",
        [0xF1] = "Total LBAs written",
        [0xF2] = "Total LBAs read",
    };

    // Critical attributes: any raw value above zero is a bad sign.
    private static readonly HashSet<byte> CriticalRaw = [0x05, 0xC5, 0xC6, 0xBB, 0xB8];

    private sealed class PsDisk
    {
        public string? FriendlyName { get; set; }
        public string? SerialNumber { get; set; }
        public string? FirmwareVersion { get; set; }
        public string? MediaType { get; set; }
        public string? BusType { get; set; }
        public long Size { get; set; }
        public string? HealthStatus { get; set; }
        public string? DeviceId { get; set; }
        public int? Temperature { get; set; }
        public int? TemperatureMax { get; set; }
        public int? Wear { get; set; }
        public long? PowerOnHours { get; set; }
        public long? StartStopCycleCount { get; set; }
        public long? ReadErrorsUncorrected { get; set; }
        public long? WriteErrorsUncorrected { get; set; }
    }

    public static async Task<List<DiskInfo>> ScanAsync(CancellationToken ct = default)
    {
        var disks = new List<DiskInfo>();

        // 1) Physical disks + reliability counters (NVMe/SATA/USB).
        const string script = """
            $out = foreach ($d in Get-PhysicalDisk) {
                $r = $null
                try { $r = $d | Get-StorageReliabilityCounter -ErrorAction Stop } catch {}
                [pscustomobject]@{
                    FriendlyName = $d.FriendlyName; SerialNumber = "$($d.SerialNumber)".Trim(); FirmwareVersion = $d.FirmwareVersion
                    MediaType = "$($d.MediaType)"; BusType = "$($d.BusType)"; Size = [int64]$d.Size; HealthStatus = "$($d.HealthStatus)"
                    DeviceId = "$($d.DeviceId)"
                    Temperature = $r.Temperature; TemperatureMax = $r.TemperatureMax; Wear = $r.Wear; PowerOnHours = $r.PowerOnHours
                    StartStopCycleCount = $r.StartStopCycleCount; ReadErrorsUncorrected = $r.ReadErrorsUncorrected; WriteErrorsUncorrected = $r.WriteErrorsUncorrected
                }
            }
            ConvertTo-Json -InputObject @($out) -Compress
            """;

        var result = await ProcessRunner.PowerShellAsync(script, ct: ct);
        try
        {
            var json = result.Output.Trim();
            var start = json.IndexOf('[');
            if (start >= 0)
            {
                var items = JsonSerializer.Deserialize<List<PsDisk>>(json[start..], new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                foreach (var p in items)
                {
                    disks.Add(new DiskInfo
                    {
                        Model = p.FriendlyName ?? "Disk",
                        Serial = p.SerialNumber ?? "",
                        Firmware = p.FirmwareVersion ?? "",
                        MediaType = p.MediaType switch { "SSD" => "SSD", "HDD" => "HDD", "SCM" => "SCM", _ => "Unspecified" },
                        BusType = p.BusType ?? "",
                        SizeBytes = p.Size,
                        WindowsHealth = p.HealthStatus ?? "",
                        TemperatureC = p.Temperature is > 0 ? p.Temperature : null,
                        TemperatureMaxC = p.TemperatureMax is > 0 ? p.TemperatureMax : null,
                        WearPercent = p.Wear,
                        PowerOnHours = p.PowerOnHours,
                        PowerCycles = p.StartStopCycleCount,
                        ReadErrors = p.ReadErrorsUncorrected,
                        WriteErrors = p.WriteErrorsUncorrected,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not read physical disks: {ex.Message}");
        }

        // 2) Raw SMART attributes (only ATA/SATA expose these through WMI).
        AttachRawSmart(disks);

        foreach (var d in disks) Evaluate(d);
        return disks;
    }

    private static void AttachRawSmart(List<DiskInfo> disks)
    {
        var drives = Wmi.Query("SELECT Model, SerialNumber, PNPDeviceID FROM Win32_DiskDrive");
        var predict = Wmi.Query("SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus", @"root\wmi");
        var data = Wmi.Query("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData", @"root\wmi");
        var thresholds = Wmi.Query("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds", @"root\wmi");

        foreach (var drive in drives)
        {
            var pnp = drive.Str("PNPDeviceID");
            if (pnp.Length == 0) continue;

            bool Matches(System.Management.ManagementBaseObject o) =>
                o.Str("InstanceName").StartsWith(pnp, StringComparison.OrdinalIgnoreCase);

            var serial = drive.Str("SerialNumber");
            var model = drive.Str("Model");
            var disk = disks.FirstOrDefault(d => serial.Length > 0 && d.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase))
                       ?? disks.FirstOrDefault(d => d.Model.Equals(model, StringComparison.OrdinalIgnoreCase));
            if (disk is null) continue;

            var p = predict.FirstOrDefault(Matches);
            if (p is not null) disk.PredictFailure = p.Get<bool>("PredictFailure");

            var raw = data.FirstOrDefault(Matches)?.Get<byte[]>("VendorSpecific");
            var thr = thresholds.FirstOrDefault(Matches)?.Get<byte[]>("VendorSpecific");
            if (raw is null) continue;

            ParseAttributes(raw, thr, disk);
        }
    }

    /// <summary>
    /// ATA SMART layout: 2-byte revision, then 30 entries of 12 bytes:
    /// [id][flags x2][current][worst][raw x6][reserved].
    /// </summary>
    private static void ParseAttributes(byte[] raw, byte[]? thr, DiskInfo disk)
    {
        for (int i = 0; i < 30; i++)
        {
            int off = 2 + i * 12;
            if (off + 12 > raw.Length) break;
            byte id = raw[off];
            if (id == 0) continue;

            long rawValue = 0;
            for (int b = 5; b >= 0; b--) rawValue = (rawValue << 8) | raw[off + 5 + b];

            byte threshold = 0;
            if (thr is not null && off + 1 < thr.Length && thr[off] == id) threshold = thr[off + 1];

            var attr = new SmartAttribute(id, AttributeNames.GetValueOrDefault(id, "Vendor specific"),
                raw[off + 3], raw[off + 4], threshold, rawValue);
            disk.Attributes.Add(attr);

            switch (id)
            {
                case 0x09 when disk.PowerOnHours is null or 0:
                    disk.PowerOnHours = rawValue & 0xFFFFFFFF;
                    break;
                case 0x0C when disk.PowerCycles is null or 0:
                    disk.PowerCycles = rawValue & 0xFFFFFFFF;
                    break;
                case 0xC2 or 0xBE when disk.TemperatureC is null:
                    var t = (int)(rawValue & 0xFF);
                    if (t is > 0 and < 120) disk.TemperatureC = t;
                    break;
            }
        }
    }

    private static void Evaluate(DiskInfo d)
    {
        var level = HealthLevel.Good;

        void Raise(HealthLevel l, string finding)
        {
            if (l > level) level = l;
            d.Findings.Add(finding);
        }

        if (d.PredictFailure == true)
            Raise(HealthLevel.Bad, "SMART reports an imminent failure. Back up your data now.");

        if (d.WindowsHealth.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase))
            Raise(HealthLevel.Bad, "Windows reports this drive as unhealthy.");
        else if (d.WindowsHealth.Equals("Warning", StringComparison.OrdinalIgnoreCase))
            Raise(HealthLevel.Caution, "Windows reports a warning for this drive.");

        foreach (var a in d.Attributes)
        {
            if (a.Failing)
                Raise(HealthLevel.Bad, $"Attribute {a.Id:X2} ({a.Name}) is below the manufacturer threshold.");
            else if (CriticalRaw.Contains(a.Id) && a.Raw > 0)
                Raise(HealthLevel.Caution, $"{a.Name}: {a.Raw}. The drive has bad sectors or errors.");
        }

        if (d.WearPercent is >= 90)
            Raise(HealthLevel.Bad, $"SSD wear: {d.WearPercent}%. The drive is at the end of its rated life.");
        else if (d.WearPercent is >= 70)
            Raise(HealthLevel.Caution, $"SSD wear: {d.WearPercent}%.");

        if (d.ReadErrors is > 0 || d.WriteErrors is > 0)
            Raise(HealthLevel.Caution, $"Uncorrected errors: {d.ReadErrors ?? 0} read, {d.WriteErrors ?? 0} write.");

        if (d.TemperatureC is >= 70)
            Raise(HealthLevel.Caution, $"High temperature: {d.TemperatureC} °C. Check the cooling.");

        if (d.Attributes.Count == 0 && d.WearPercent is null && d.TemperatureC is null && d.PredictFailure is null
            && string.IsNullOrEmpty(d.WindowsHealth))
            level = HealthLevel.Unknown;

        d.Health = level;
    }
}
