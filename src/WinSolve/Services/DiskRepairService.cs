using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
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

    /// <summary>Hosts the page file, user profiles or Program Files: can't be dismounted safely.</summary>
    public bool InUse { get; set; }

    /// <summary>The repair has to wait for the next restart instead of dismounting the drive.</summary>
    public bool RepairAtRestart => IsSystem || InUse;
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
            var inUse = InUseLetters();
            return (JsonSerializer.Deserialize<List<PsVolume>>(r.Output[start..], new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [])
                .Where(v => v.Letter is { Length: 1 } l && char.IsLetter(l[0]))
                .Select(v => new VolumeInfo
                {
                    Letter = v.Letter!.ToUpperInvariant(), Label = v.Label ?? "", FileSystem = v.FileSystem ?? "",
                    Size = v.Size, Free = v.Free, Health = v.Health ?? "",
                    InUse = inUse.Contains(char.ToUpperInvariant(v.Letter![0])),
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

    // ───────────── Drives in use ─────────────

    /// <summary>
    /// Drive letters that Windows keeps busy: the Windows drive, drives with a page file, user
    /// profiles or Program Files. Dismounting them (/x) would break running programs, so their
    /// repair is scheduled for the next restart instead.
    /// </summary>
    public static HashSet<char> InUseLetters()
    {
        var paths = new List<string>
        {
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        try
        {
            if (Reg.Get(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles") is string[] pageFiles)
                paths.AddRange(pageFiles);
            foreach (var page in Wmi.Query("SELECT Name FROM Win32_PageFileUsage")) paths.Add(page.Str("Name"));
        }
        catch { /* best effort */ }
        try
        {
            using var profiles = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (profiles is not null)
                foreach (var sid in profiles.GetSubKeyNames())
                {
                    using var k = profiles.OpenSubKey(sid);
                    if (k?.GetValue("ProfileImagePath") is string path) paths.Add(Environment.ExpandEnvironmentVariables(path));
                }
        }
        catch { /* best effort */ }
        return DriveLetters(paths);
    }

    /// <summary>Drive letters of the given paths ("C:\pagefile.sys 0 0" → C). "?:\pagefile.sys" means the Windows drive.</summary>
    internal static HashSet<char> DriveLetters(IEnumerable<string?> paths)
    {
        var letters = new HashSet<char>();
        var system = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:")[0];
        foreach (var raw in paths)
        {
            var p = (raw ?? "").Trim().TrimStart('"');
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
            if (p.Length < 2 || p[1] != ':') continue;
            if (p[0] == '?') letters.Add(char.ToUpperInvariant(system));
            else if (char.IsAsciiLetter(p[0])) letters.Add(char.ToUpperInvariant(p[0]));
        }
        return letters;
    }

    // ───────────── Scheduling at boot ─────────────

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize, out uint outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    private const uint FsctlIsVolumeDirty = 0x00090078;

    /// <summary>Whether the volume's dirty bit is set (autochk then checks it at boot). Null if it can't be read.</summary>
    public static bool? IsVolumeDirty(char letter)
    {
        if (!char.IsAsciiLetter(letter)) return null;
        // No access rights needed for this control code; share everything so nothing is locked.
        using var h = CreateFile($@"\\.\{char.ToUpperInvariant(letter)}:", 0, 7, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        if (!DeviceIoControl(h, FsctlIsVolumeDirty, IntPtr.Zero, 0, out var flags, 4, out _, IntPtr.Zero)) return null;
        return (flags & 1) != 0; // VOLUME_IS_DIRTY
    }

    /// <summary>
    /// Whether a CHKDSK of the volume is scheduled in BootExecute (what "chkdsk X: /r" writes when
    /// it schedules). Language-independent, unlike chkdsk's or chkntfs's messages.
    /// </summary>
    public static bool IsCheckScheduled(char letter, bool surfaceScan)
    {
        try
        {
            if (Reg.Get(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager", "BootExecute") is not string[] entries) return false;
            return IsCheckScheduled(entries, letter, surfaceScan);
        }
        catch { return false; }
    }

    internal static bool IsCheckScheduled(IEnumerable<string> bootExecute, char letter, bool surfaceScan)
        => bootExecute.Any(e => e.Contains("autochk", StringComparison.OrdinalIgnoreCase)
                                && e.Contains($@"\??\{char.ToUpperInvariant(letter)}:", StringComparison.OrdinalIgnoreCase)
                                && (!surfaceScan || Regex.IsMatch(e, @"(^|\s)/r(\s|$)", RegexOptions.IgnoreCase)));

    /// <summary>The letter chkdsk accepts as "yes" in its (Y/N) question, read from its own prompt: Y, S (Spanish), J (German), O (French)...</summary>
    internal static char? YesLetter(string chkdskOutput)
    {
        var m = Regex.Matches(chkdskOutput, @"\((\p{L})\s*/\s*(\p{L})\)").LastOrDefault();
        return m is null ? null : m.Groups[1].Value[0];
    }

    /// <summary>
    /// Schedules a check of an in-use drive at the next restart and verifies it really is
    /// scheduled. /f: the dirty bit is set (autochk checks dirty volumes at boot on any language).
    /// /r: chkdsk is asked to schedule it, answering its question with the "yes" letter of this
    /// Windows language, and the schedule is read back from the registry.
    /// </summary>
    public static async Task<bool> ScheduleAtRestartAsync(char letter, bool surfaceScan, Action<string> log, CancellationToken ct)
    {
        letter = char.ToUpperInvariant(letter);
        if (!char.IsAsciiLetter(letter)) throw new ArgumentException("Invalid drive letter.");
        var drive = $"{letter}:";

        if (!surfaceScan)
        {
            var set = await ProcessRunner.RunAsync("fsutil.exe", $"dirty set {drive}", log, ct);
            // Read back from the volume itself; only if that's impossible, trust fsutil's exit code.
            var dirty = IsVolumeDirty(letter) ?? set.Success;
            log(dirty ? $"{drive} will be checked and repaired on the next restart."
                      : $"The repair of {drive} could not be scheduled. Nothing will run at restart.");
            return dirty;
        }

        var isWindowsDrive = letter == char.ToUpperInvariant((Path.GetPathRoot(Environment.SystemDirectory) ?? "C:")[0]);
        if (IsCheckScheduled(letter, surfaceScan: true))
        {
            log($"A surface scan of {drive} was already scheduled.");
        }
        else if (isWindowsDrive)
        {
            // The Windows drive can never be locked while Windows runs, so chkdsk only asks
            // whether to schedule it, "(Y/N)" in the Windows language. Asked once with no answer
            // (stdin is NUL, so it never waits) to read the letter, then answered with it.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var probe = await ProcessRunner.CmdAsync($"chkdsk {drive} /r < nul", null, timeout.Token);
            if (YesLetter(probe.Output) is { } y && char.IsLetter(y))
                await ProcessRunner.CmdAsync($"echo {y}| chkdsk {drive} /r", log, timeout.Token);
        }
        else
        {
            // Another drive would first be offered a forced dismount, which must not happen to a
            // drive with the page file or profiles: write the same boot entry chkdsk writes.
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager", writable: true);
                // Only a normal multi-string value is edited; anything else is left exactly as it is.
                if (key is null || !key.GetValueNames().Contains("BootExecute", StringComparer.OrdinalIgnoreCase)
                    || key.GetValueKind("BootExecute") != RegistryValueKind.MultiString
                    || key.GetValue("BootExecute", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string[] current)
                {
                    log("Windows' boot check list has an unexpected format, so WinSolve doesn't change it.");
                }
                else if (IsCheckScheduled(current, letter, surfaceScan: false))
                {
                    log($"Another check of {drive} is already scheduled for the next restart. Restart once to let it run, then schedule the surface scan again.");
                }
                else
                {
                    var entries = current.ToList();
                    var at = entries.FindIndex(e => e.Trim().Equals("autocheck autochk *", StringComparison.OrdinalIgnoreCase));
                    entries.Insert(at < 0 ? 0 : at, $@"autocheck autochk /r \??\{letter}:");
                    key.SetValue("BootExecute", entries.ToArray(), RegistryValueKind.MultiString);
                }
            }
            catch (Exception ex) { log($"Could not schedule the scan: {ex.Message}"); }
        }

        var scheduled = IsCheckScheduled(letter, surfaceScan: true);
        log(scheduled ? $"The surface scan of {drive} will run on the next restart (it can take hours, do not turn the PC off)."
                      : $"The surface scan of {drive} could not be scheduled. Nothing will run at restart.");
        return scheduled;
    }

    /// <summary>S.M.A.R.T. advice for the physical disk that holds a drive letter, or null if it can't be found.</summary>
    public static async Task<(DiskInfo Disk, string Text, HealthLevel Level)?> HealthOfDriveAsync(char letter, CancellationToken ct)
    {
        if (!char.IsAsciiLetter(letter)) return null;
        var r = await ProcessRunner.PowerShellAsync($"(Get-Partition -DriveLetter {letter} -ErrorAction Stop).DiskNumber", ct: ct);
        if (!int.TryParse(r.Output.Trim().Split('\n').LastOrDefault()?.Trim(), out var number)) return null;
        var disk = (await DiskHealthService.ScanAsync(ct)).FirstOrDefault(d => d.DiskNumber == number);
        if (disk is null) return null;
        var (text, level) = Advice(disk);
        return (disk, text, level);
    }

    /// <summary>
    /// Fixes file system errors. Drives Windows keeps in use (system, page file, profiles,
    /// Program Files) are repaired at the next restart; other drives are briefly dismounted
    /// (open files on them are closed). Returns true when a restart is needed.
    /// </summary>
    public static async Task<bool> FixAsync(VolumeInfo v, Action<string> log, CancellationToken ct)
    {
        if (v.RepairAtRestart)
        {
            log(v.IsSystem ? $"{Drive(v)} is the Windows drive: the repair will run on the next restart."
                           : $"{Drive(v)} is in use by Windows (page file, user profiles or programs): the repair will run on the next restart.");
            return await ScheduleAtRestartAsync(v.Letter[0], surfaceScan: false, log, ct);
        }
        if (await WriteBlockerAsync(v, ct) is { } blocked) { log(blocked); return false; }
        log($"Repairing {Drive(v)}. The drive is dismounted for a moment; programs using it may lose open files.");
        // stdin is NUL: if chkdsk asks anything, it gets "no" instead of waiting forever.
        var r = await ProcessRunner.CmdAsync($"chkdsk {Drive(v)} /f /x < nul", log, ct);
        log(Outcome(v, r, "Repair finished."));
        return false;
    }

    /// <summary>
    /// Surface scan: finds bad sectors, recovers readable data and isolates the bad areas.
    /// Takes hours on large hard disks. Returns true when a restart is needed.
    /// </summary>
    public static async Task<bool> RepairBadSectorsAsync(VolumeInfo v, Action<string> log, CancellationToken ct)
    {
        if (v.RepairAtRestart)
        {
            log(v.IsSystem ? $"{Drive(v)} is the Windows drive: the surface scan will run on the next restart."
                           : $"{Drive(v)} is in use by Windows (page file, user profiles or programs): the surface scan will run on the next restart.");
            return await ScheduleAtRestartAsync(v.Letter[0], surfaceScan: true, log, ct);
        }
        if (await WriteBlockerAsync(v, ct) is { } blocked) { log(blocked); return false; }
        log($"Scanning every sector of {Drive(v)}. This can take hours on large hard disks; keep the PC on.");
        var r = await ProcessRunner.CmdAsync($"chkdsk {Drive(v)} /r /x < nul", log, ct);
        log(Outcome(v, r, "Finished. Refresh S.M.A.R.T. to see the reallocated / pending sector counts now."));
        return false;
    }

    /// <summary>chkdsk's exit code 3 means it could not check or could not fix the drive.</summary>
    private static string Outcome(VolumeInfo v, ProcessResult r, string success)
        => r.ExitCode is 0 or 1 or 2
           && !r.Output.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
           && !r.Output.Contains("Acceso denegado", StringComparison.OrdinalIgnoreCase) ? success
            : $"chkdsk could not finish on {Drive(v)} (code {r.ExitCode}). Read the lines above: close the programs that use the drive and try again, or restart the PC first.";

    /// <summary>
    /// Why chkdsk would get "Access denied" on a drive, found before running it: locked by
    /// BitLocker, write-protected, or opened by a program that doesn't let it be locked.
    /// Null when the drive can be opened for repair.
    /// </summary>
    internal static async Task<string?> WriteBlockerAsync(VolumeInfo v, CancellationToken ct)
    {
        var drive = Drive(v);
        var r = await ProcessRunner.PowerShellAsync($$"""
            try {
              $e = Get-CimInstance -Namespace root\CIMV2\Security\MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume -Filter "DriveLetter='{{drive}}'" -ErrorAction Stop
              if ($e -and (Invoke-CimMethod -InputObject $e -MethodName GetLockStatus).LockStatus -eq 1) { 'STATE:LOCKED' }
            } catch { }
            $p = Get-Partition -DriveLetter '{{v.Letter}}' -ErrorAction SilentlyContinue
            if ($p -and ($p.IsReadOnly -or ($p | Get-Disk).IsReadOnly)) { 'STATE:READONLY' }
            """, null, ct);
        if (r.Output.Contains("STATE:LOCKED", StringComparison.Ordinal))
            return $"{drive} is locked by BitLocker, so Windows denies access to it. Unlock it first (open it in File Explorer and type its password or recovery key), then try again.";
        if (r.Output.Contains("STATE:READONLY", StringComparison.Ordinal))
            return $"{drive} is write-protected (read-only), so it can't be repaired. On a memory card or USB drive, check its lock switch.";

        using var h = CreateFile($@"\\.\{drive}", 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */, 3, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (!h.IsInvalid) return null;
        return Marshal.GetLastWin32Error() switch
        {
            5 => $"Windows denied access to {drive}. Usually a program protects it: an antivirus, backup or encryption software (like VeraCrypt), or the drive was encrypted on another PC. Close those programs or restart the PC, then try again.",
            19 => $"{drive} is write-protected (read-only), so it can't be repaired. On a memory card or USB drive, check its lock switch.",
            _ => null,
        };
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
