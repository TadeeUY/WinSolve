using System.Text.Json;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed class VolumeInfo
{
    public string Letter { get; set; } = "";
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public long Size { get; set; }
    public long Free { get; set; }
    public string Health { get; set; } = "";

    public bool IsSystem => string.Equals(Letter + ":", Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    public bool SupportsOnlineScan => FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// File system repair and bad sector handling with CHKDSK.
///
/// What can and cannot be repaired:
///  - Logical errors (corrupted file records, indexes, lost clusters) are fixed by /f.
///  - Physical bad sectors cannot be fixed. /r reads every sector, recovers readable data,
///    marks unreadable clusters as bad so Windows never uses them again, and makes the drive
///    remap pending sectors to its spare area. A drive whose bad sector count keeps growing
///    is failing and must be replaced.
/// </summary>
public static class DiskRepairService
{
    private sealed class PsVolume
    {
        public string? Letter { get; set; }
        public string? Label { get; set; }
        public string? FileSystem { get; set; }
        public long Size { get; set; }
        public long Free { get; set; }
        public string? Health { get; set; }
    }

    public static async Task<List<VolumeInfo>> GetVolumesAsync(int diskNumber)
    {
        var r = await ProcessRunner.PowerShellAsync($$"""
            $out = Get-Partition -DiskNumber {{diskNumber}} -ErrorAction SilentlyContinue | Where-Object DriveLetter | ForEach-Object {
                $v = $_ | Get-Volume
                [pscustomobject]@{ Letter = "$($_.DriveLetter)"; Label = $v.FileSystemLabel; FileSystem = $v.FileSystemType; Size = [int64]$v.Size; Free = [int64]$v.SizeRemaining; Health = "$($v.HealthStatus)" }
            }
            ConvertTo-Json -InputObject @($out) -Compress
            """);
        var start = r.Output.IndexOf('[');
        if (start < 0) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<PsVolume>>(r.Output[start..], new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [])
                .Where(v => v.Letter is { Length: 1 } l && char.IsLetter(l[0]))
                .Select(v => new VolumeInfo
                {
                    Letter = v.Letter!.ToUpperInvariant(), Label = v.Label ?? "", FileSystem = v.FileSystem ?? "",
                    Size = v.Size, Free = v.Free, Health = v.Health ?? "",
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not read volumes of disk {diskNumber}: {ex.Message}");
            return [];
        }
    }

    private static string Drive(VolumeInfo v) => v.Letter + ":";

    /// <summary>Read-only check. NTFS/ReFS are scanned online without locking the drive.</summary>
    public static async Task ScanAsync(VolumeInfo v, Action<string> log, CancellationToken ct)
    {
        log($"Checking {Drive(v)} (read-only)...");
        var args = v.SupportsOnlineScan ? $"{Drive(v)} /scan" : Drive(v);
        var r = await ProcessRunner.RunAsync("chkdsk.exe", args, log, ct);
        log(r.ExitCode switch
        {
            0 => "No errors were found.",
            1 => "Errors were found and fixed.",
            2 => "Cleanup was done, no errors found.",
            _ => "Errors were found. Use 'Fix file system errors' to repair them.",
        });
    }

    /// <summary>
    /// Fixes file system errors. The system drive is in use, so the repair is scheduled for
    /// the next restart; other drives are briefly dismounted (open files on them are closed).
    /// </summary>
    public static async Task<bool> FixAsync(VolumeInfo v, Action<string> log, CancellationToken ct)
    {
        if (v.IsSystem)
        {
            log($"{Drive(v)} is the Windows drive: the repair will run on the next restart.");
            await ProcessRunner.CmdAsync($"echo Y| chkdsk {Drive(v)} /f", log, ct);
            return true; // restart needed
        }
        log($"Repairing {Drive(v)}. The drive is dismounted for a moment; programs using it may lose open files.");
        await ProcessRunner.RunAsync("chkdsk.exe", $"{Drive(v)} /f /x", log, ct);
        return false;
    }

    /// <summary>
    /// Surface scan: finds bad sectors, recovers readable data and isolates the bad areas.
    /// Takes hours on large hard disks.
    /// </summary>
    public static async Task<bool> RepairBadSectorsAsync(VolumeInfo v, Action<string> log, CancellationToken ct)
    {
        if (v.IsSystem)
        {
            log($"{Drive(v)} is the Windows drive: the surface scan will run on the next restart (it can take hours, do not turn the PC off).");
            await ProcessRunner.CmdAsync($"echo Y| chkdsk {Drive(v)} /r", log, ct);
            return true;
        }
        log($"Scanning every sector of {Drive(v)}. This can take hours on large hard disks; keep the PC on.");
        await ProcessRunner.RunAsync("chkdsk.exe", $"{Drive(v)} /r /x", log, ct);
        log("Finished. Refresh S.M.A.R.T. to see the reallocated / pending sector counts now.");
        return false;
    }

    /// <summary>Plain-language advice for a drive, based on its S.M.A.R.T. data.</summary>
    public static (string Text, HealthLevel Level) Advice(DiskInfo d)
    {
        long Raw(byte id) => d.Attributes.FirstOrDefault(a => a.Id == id)?.Raw ?? 0;
        var reallocated = Raw(0x05);
        var pending = Raw(0xC5);
        var uncorrectable = Raw(0xC6);
        var isSsd = d.MediaType == "SSD";

        if (d.Health == HealthLevel.Bad)
            return ("This drive is failing. Copy your important files to another drive first: a full sector scan puts extra stress on a dying drive. " +
                    "Repair can isolate bad sectors so Windows stops using them, but it cannot fix the hardware. Plan to replace the drive.", HealthLevel.Bad);

        if (pending > 0 || uncorrectable > 0 || reallocated > 0)
            return ($"This drive has bad or unstable sectors (reallocated: {reallocated}, pending: {pending}, uncorrectable: {uncorrectable}). " +
                    "1) Back up your files. 2) Run 'Scan & repair bad sectors' to recover what can be read and isolate the bad areas; pending sectors are usually remapped or cleared. " +
                    "3) Refresh S.M.A.R.T. afterwards and over the next days: if the numbers keep rising, replace the drive.", HealthLevel.Caution);

        if (d.Health == HealthLevel.Caution)
            return ("The drive reports a warning. Back up your files, then run 'Fix file system errors'. Check the findings above for the cause (temperature, wear...).", HealthLevel.Caution);

        return (isSsd
            ? "No problems reported. Run 'Check' if you see file errors. A sector scan is rarely useful on SSDs: they remap bad blocks themselves."
            : "No problems reported. Run 'Check' if you see file errors or after a power cut.", HealthLevel.Good);
    }
}
