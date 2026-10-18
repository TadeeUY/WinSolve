using System.Buffers.Binary;
using System.Text;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>A driver loaded when the PC crashed: name, where it was in memory and its size.</summary>
public sealed record DumpModule(string Name, ulong Base, uint Size)
{
    public bool Contains(ulong address) => address >= Base && address - Base < Size;
}

/// <summary>What a blue screen's crash dump says.</summary>
public sealed record CrashDump(
    string File,
    DateTime Time,
    uint BugCheckCode,
    ulong[] Parameters,
    int DumpType,
    IReadOnlyList<DumpModule> Modules,
    string? SuspectModule)
{
    public string CodeText => $"0x{BugCheckCode:X8}";
    public string Name => CrashDumps.Name(BugCheckCode);
}

/// <summary>
/// Reads the blue screen dumps Windows saves in %SystemRoot%\Minidump (and MEMORY.DMP) without a
/// debugger: the date, the stop code and its parameters from the dump header, and the list of
/// loaded drivers of a small (triage) dump. For stop codes whose parameters include the address of
/// the faulting code, the driver loaded at that address is named as the probable cause. This is the
/// same first step WinDbg's !analyze takes; without symbols it can't walk the stack, so a crash in
/// the Windows kernel itself is reported as such instead of guessing.
/// </summary>
public static class CrashDumps
{
    // 64-bit kernel dump header (DUMP_HEADER64), always 0x2000 bytes.
    private const int Header64Size = 0x2000;
    private const int OffBugCheck = 0x38, OffParams = 0x40, OffDumpType = 0xF98, OffSystemTime = 0xFA8;
    // 32-bit header (DUMP_HEADER32), 0x1000 bytes.
    private const int OffBugCheck32 = 0x20, OffParams32 = 0x24;

    // Triage dump block right after the 64-bit header (TRIAGE_DUMP); its offsets are file offsets.
    private const int TriageDriverListOffset = 0x30, TriageDriverCount = 0x34, TriageStringPoolOffset = 0x38, TriageStringPoolSize = 0x3C;
    // One driver: DUMP_DRIVER_ENTRY64 = name offset (4) + padding (4) + KLDR_DATA_TABLE_ENTRY64 (0x88).
    private const int DriverEntrySize = 0x90, EntryDllBase = 0x08 + 0x30, EntrySizeOfImage = 0x08 + 0x40;

    public const int TriageDumpType = 4;

    /// <summary>Folder Windows writes small dumps to.</summary>
    public static string MinidumpFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");

    /// <summary>The most recent dumps, newest first. Unreadable files are skipped (and logged).</summary>
    public static List<CrashDump> ReadRecent(int max = 20)
    {
        var result = new List<CrashDump>();
        try
        {
            var dir = new DirectoryInfo(MinidumpFolder);
            if (!dir.Exists) return result;
            foreach (var f in dir.EnumerateFiles("*.dmp", SafePath.NoLinks(recursive: false)).OrderByDescending(f => f.LastWriteTimeUtc).Take(max))
            {
                try
                {
                    if (Read(f.FullName) is { } dump) result.Add(dump);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Write($"Could not read the crash dump {f.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Write($"Could not list {MinidumpFolder}: {ex.Message}");
        }
        return result;
    }

    /// <summary>Reads one dump file; null when it isn't a kernel crash dump.</summary>
    public static CrashDump? Read(string path)
    {
        var info = new FileInfo(path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        // Small dumps are a few hundred KB; a full MEMORY.DMP can be gigabytes, of which only the
        // header (and a triage block, if any) is needed.
        var length = (int)Math.Min(fs.Length, 4 << 20);
        var data = new byte[length];
        fs.ReadExactly(data, 0, length);
        return Parse(data, info.Name, info.LastWriteTime);
    }

    /// <summary>Parses the start of a dump. <paramref name="fileTime"/> is used when the header has no valid time.</summary>
    public static CrashDump? Parse(ReadOnlySpan<byte> data, string name, DateTime fileTime)
    {
        if (data.Length < 0x1000 || !data[..4].SequenceEqual("PAGE"u8)) return null;
        if (data[4..8].SequenceEqual("DU64"u8))
        {
            if (data.Length < Header64Size) return null;
            var code = BinaryPrimitives.ReadUInt32LittleEndian(data[OffBugCheck..]);
            var parameters = new ulong[4];
            for (var i = 0; i < 4; i++) parameters[i] = BinaryPrimitives.ReadUInt64LittleEndian(data[(OffParams + i * 8)..]);
            var dumpType = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[OffDumpType..]);
            var time = FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(data[OffSystemTime..])) ?? fileTime;
            var modules = dumpType == TriageDumpType ? ReadDrivers(data) : [];
            return new CrashDump(name, time, code, parameters, dumpType, modules, Suspect(code, parameters, modules));
        }
        if (data[4..8].SequenceEqual("DUMP"u8))
        {
            var code = BinaryPrimitives.ReadUInt32LittleEndian(data[OffBugCheck32..]);
            var parameters = new ulong[4];
            for (var i = 0; i < 4; i++) parameters[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(OffParams32 + i * 4)..]);
            return new CrashDump(name, fileTime, code, parameters, 0, [], null);
        }
        return null;
    }

    private static DateTime? FromFileTime(long ticks)
    {
        if (ticks <= 0) return null;
        try
        {
            var t = DateTime.FromFileTime(ticks);
            // Only a plausible crash time is used (a damaged header falls back to the file's date).
            return t.Year >= 2010 && t <= DateTime.Now.AddDays(2) ? t : null;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Loaded drivers of a triage dump. Anything out of bounds or implausible ends the list.</summary>
    internal static List<DumpModule> ReadDrivers(ReadOnlySpan<byte> data)
    {
        var list = new List<DumpModule>();
        var triage = data[Header64Size..];
        if (triage.Length < 0x40) return list;
        var listOffset = BinaryPrimitives.ReadUInt32LittleEndian(triage[TriageDriverListOffset..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(triage[TriageDriverCount..]);
        if (count is 0 or > 2048 || listOffset < Header64Size || listOffset + (long)count * DriverEntrySize > data.Length) return list;
        for (var i = 0; i < count; i++)
        {
            var entry = data[(int)(listOffset + i * DriverEntrySize)..];
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var dllBase = BinaryPrimitives.ReadUInt64LittleEndian(entry[EntryDllBase..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[EntrySizeOfImage..]);
            var name = ReadDumpString(data, nameOffset);
            if (name is null || size == 0 || dllBase < 0xFFFF_8000_0000_0000UL) break;
            list.Add(new DumpModule(name, dllBase, size));
        }
        return list;
    }

    /// <summary>A DUMP_STRING: a 32-bit length in characters, then UTF-16 text.</summary>
    private static string? ReadDumpString(ReadOnlySpan<byte> data, uint offset)
    {
        if (offset == 0 || offset + 4L > data.Length) return null;
        var chars = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)offset..]);
        if (chars is 0 or > 260 || offset + 4L + chars * 2L > data.Length) return null;
        var text = Encoding.Unicode.GetString(data.Slice((int)offset + 4, (int)chars * 2));
        return text.All(c => c >= ' ' && c != '\\' && c != '/') ? text : null;
    }

    /// <summary>
    /// The parameter that holds the address of the code that crashed, for the stop codes that have
    /// one (from Microsoft's bug check reference); null otherwise.
    /// </summary>
    internal static int? FaultAddressParameter(uint code) => code switch
    {
        0x0A or 0xD1 or 0xC5 => 3,         // IRQL_NOT_LESS_OR_EQUAL, DRIVER_IRQL_NOT_LESS_OR_EQUAL, DRIVER_CORRUPTED_EXPOOL: P4
        0x1E or 0x7E or 0x3B or 0x116 => 1, // KMODE_EXCEPTION, SYSTEM_THREAD_EXCEPTION, SYSTEM_SERVICE_EXCEPTION, VIDEO_TDR_FAILURE: P2
        0x50 or 0xD5 or 0xD6 => 2,          // PAGE_FAULT_IN_NONPAGED_AREA, DRIVER_PAGE_FAULT_*: P3
        0xFC => 0,                          // ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY: P1
        _ => null,
    };

    private static readonly string[] KernelModules = ["ntoskrnl.exe", "ntkrnlmp.exe", "ntkrnlpa.exe", "ntkrpamp.exe", "hal.dll"];

    /// <summary>The driver loaded at the faulting address, or null when the dump doesn't say.</summary>
    internal static string? Suspect(uint code, ulong[] parameters, IReadOnlyList<DumpModule> modules)
    {
        if (FaultAddressParameter(code) is not { } p || modules.Count == 0) return null;
        var address = parameters[p];
        return modules.FirstOrDefault(m => m.Contains(address))?.Name;
    }

    /// <summary>The Windows kernel itself: the real cause is usually another driver that the dump can't name without symbols.</summary>
    public static bool IsKernel(string? module) => module is not null && KernelModules.Contains(module, StringComparer.OrdinalIgnoreCase);

    /// <summary>The stop code's name (as shown on the blue screen).</summary>
    public static string Name(uint code) => code switch
    {
        0x0A => "IRQL_NOT_LESS_OR_EQUAL",
        0x1A => "MEMORY_MANAGEMENT",
        0x1E => "KMODE_EXCEPTION_NOT_HANDLED",
        0x3B => "SYSTEM_SERVICE_EXCEPTION",
        0x4E => "PFN_LIST_CORRUPT",
        0x50 => "PAGE_FAULT_IN_NONPAGED_AREA",
        0x7A => "KERNEL_DATA_INPAGE_ERROR",
        0x7B => "INACCESSIBLE_BOOT_DEVICE",
        0x7E => "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED",
        0x7F => "UNEXPECTED_KERNEL_MODE_TRAP",
        0x9F => "DRIVER_POWER_STATE_FAILURE",
        0xBE => "ATTEMPTED_WRITE_TO_READONLY_MEMORY",
        0xC2 => "BAD_POOL_CALLER",
        0xC5 => "DRIVER_CORRUPTED_EXPOOL",
        0xD1 => "DRIVER_IRQL_NOT_LESS_OR_EQUAL",
        0xD5 => "DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL",
        0xD6 => "DRIVER_PAGE_FAULT_BEYOND_END_OF_ALLOCATION",
        0xEA => "THREAD_STUCK_IN_DEVICE_DRIVER",
        0xEF => "CRITICAL_PROCESS_DIED",
        0xF4 => "CRITICAL_OBJECT_TERMINATION",
        0xFC => "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY",
        0x101 => "CLOCK_WATCHDOG_TIMEOUT",
        0x109 => "CRITICAL_STRUCTURE_CORRUPTION",
        0x116 => "VIDEO_TDR_FAILURE",
        0x117 => "VIDEO_TDR_TIMEOUT_DETECTED",
        0x119 => "VIDEO_SCHEDULER_INTERNAL_ERROR",
        0x124 => "WHEA_UNCORRECTABLE_ERROR",
        0x133 => "DPC_WATCHDOG_VIOLATION",
        0x139 => "KERNEL_SECURITY_CHECK_FAILURE",
        0x13A => "KERNEL_MODE_HEAP_CORRUPTION",
        0x154 => "UNEXPECTED_STORE_EXCEPTION",
        0x161 => "LIVE_SYSTEM_DUMP",
        0x1A8 => "BUGCODE_NDIS_DRIVER_LIVE_DUMP",
        _ => "",
    };

    /// <summary>A plain-language hint for common stop codes (what usually causes them).</summary>
    public static string Hint(CrashDump d) => d.BugCheckCode switch
    {
        _ when d.SuspectModule is { } m && !IsKernel(m)
            => $"The crash happened inside {m}. Update or reinstall the driver or program it belongs to.",
        0x116 or 0x117 or 0x119 => "The graphics driver stopped responding. Do a clean install of the graphics driver (Drivers page) and check GPU temperatures.",
        0x124 => "The processor or motherboard reported a hardware error. Check temperatures, remove overclocking and update the BIOS.",
        0x1A or 0x4E or 0x50 or 0x139 or 0x13A => "Often bad RAM or a faulty driver. Run the Windows Memory Diagnostic (Cleanup & repair) and update drivers.",
        0x7A or 0x154 or 0x7B => "Problem reading the disk. Check the disk's health and run a file system repair (Hardware > Disks).",
        0x9F => "A device didn't handle sleep or power changes. Update chipset, network and graphics drivers.",
        0x133 or 0x101 => "Something kept a processor busy too long, usually a storage or chipset driver, or the BIOS. Update them.",
        0xEF or 0xF4 => "A critical Windows process stopped. Repair the system files (Cleanup & repair > SFC and DISM).",
        _ => "Update drivers and Windows. If it keeps happening, the crash dump can be opened with WinDbg for a full analysis.",
    };
}
