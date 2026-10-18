using System.Buffers.Binary;
using System.Text;
using WinSolve.Services;
using Xunit;
using Xunit.Abstractions;

namespace WinSolve.Tests;

public class CrashDumpTests
{
    /// <summary>A small (triage) 64-bit dump laid out like the ones in C:\Windows\Minidump.</summary>
    internal static byte[] FakeMinidump(uint code, ulong[] parameters, DateTime time, (string Name, ulong Base, uint Size)[] drivers, int dumpType = 4)
    {
        var data = new byte[0x10000];
        "PAGEDU64"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x38), code);
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x40 + i * 8), parameters[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0xF98), (uint)dumpType);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(0xFA8), time.ToFileTime());

        const int listOffset = 0x3000, stringPool = 0x8000;
        var triage = data.AsSpan(0x2000);
        BinaryPrimitives.WriteUInt32LittleEndian(triage[0x30..], listOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(triage[0x34..], (uint)drivers.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(triage[0x38..], stringPool);
        var str = stringPool;
        for (var i = 0; i < drivers.Length; i++)
        {
            var entry = data.AsSpan(listOffset + i * 0x90);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)str);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[(0x08 + 0x30)..], drivers[i].Base);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[(0x08 + 0x40)..], drivers[i].Size);
            var name = Encoding.Unicode.GetBytes(drivers[i].Name);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(str), (uint)drivers[i].Name.Length);
            name.CopyTo(data, str + 4);
            str += 4 + name.Length + 2;
        }
        return data;
    }

    private static readonly (string, ulong, uint)[] Drivers =
    [
        ("ntoskrnl.exe", 0xFFFFF800_10000000, 0x01000000),
        ("nvlddmkm.sys", 0xFFFFF800_30000000, 0x02000000),
        ("rt640x64.sys", 0xFFFFF800_40000000, 0x00100000),
    ];

    private static readonly DateTime When = new(2026, 10, 12, 21, 30, 5, DateTimeKind.Local);

    [Fact]
    public void Reads_the_stop_code_parameters_and_time()
    {
        var d = CrashDumps.Parse(FakeMinidump(0x7E, [0xFFFFFFFFC0000005, 0xFFFFF800_30001234, 1, 2], When, Drivers), "101226-1234-01.dmp", DateTime.MinValue)!;
        Assert.Equal(0x7Eu, d.BugCheckCode);
        Assert.Equal("0x0000007E", d.CodeText);
        Assert.Equal("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", d.Name);
        Assert.Equal(0xFFFFF800_30001234UL, d.Parameters[1]);
        Assert.Equal(When, d.Time);
        Assert.Equal(3, d.Modules.Count);
    }

    [Theory]
    [InlineData(0x7Eu, 1, "nvlddmkm.sys")]   // exception address in P2
    [InlineData(0xD1u, 3, "rt640x64.sys")]   // faulting instruction in P4
    [InlineData(0x50u, 2, "nvlddmkm.sys")]   // instruction address in P3
    [InlineData(0x116u, 1, "nvlddmkm.sys")]  // the display driver in P2
    public void Names_the_driver_at_the_faulting_address(uint code, int parameter, string expected)
    {
        var p = new ulong[4];
        p[parameter] = expected == "rt640x64.sys" ? 0xFFFFF800_40000100 : 0xFFFFF800_30000500;
        var d = CrashDumps.Parse(FakeMinidump(code, p, When, Drivers), "x.dmp", DateTime.MinValue)!;
        Assert.Equal(expected, d.SuspectModule);
        Assert.Contains(expected, CrashDumps.Hint(d));
    }

    [Fact]
    public void A_crash_inside_the_kernel_is_not_blamed_on_it()
    {
        var d = CrashDumps.Parse(FakeMinidump(0x7E, [0, 0xFFFFF800_10000100, 0, 0], When, Drivers), "x.dmp", DateTime.MinValue)!;
        Assert.Equal("ntoskrnl.exe", d.SuspectModule);
        Assert.True(CrashDumps.IsKernel(d.SuspectModule));
        Assert.DoesNotContain("ntoskrnl", CrashDumps.Hint(d));
    }

    [Fact]
    public void Stop_codes_without_an_address_name_no_driver()
    {
        var d = CrashDumps.Parse(FakeMinidump(0x124, [0, 0xFFFFF800_30000500, 0, 0], When, Drivers), "x.dmp", DateTime.MinValue)!;
        Assert.Null(d.SuspectModule);
        Assert.Contains("hardware error", CrashDumps.Hint(d));
    }

    [Fact]
    public void An_address_outside_every_driver_names_none()
    {
        var d = CrashDumps.Parse(FakeMinidump(0xD1, [0, 0, 0, 0xFFFFF800_90000000], When, Drivers), "x.dmp", DateTime.MinValue)!;
        Assert.Null(d.SuspectModule);
    }

    [Fact]
    public void A_damaged_time_falls_back_to_the_file_date()
    {
        var data = FakeMinidump(0x7E, new ulong[4], When, Drivers);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(0xFA8), 0);
        var fileTime = new DateTime(2026, 1, 2, 3, 4, 5);
        Assert.Equal(fileTime, CrashDumps.Parse(data, "x.dmp", fileTime)!.Time);
    }

    [Fact]
    public void A_full_memory_dump_has_no_driver_list_but_still_the_stop_code()
    {
        var d = CrashDumps.Parse(FakeMinidump(0x9F, [3, 0, 0, 0], When, Drivers, dumpType: 2), "MEMORY.DMP", DateTime.MinValue)!;
        Assert.Equal(0x9Fu, d.BugCheckCode);
        Assert.Empty(d.Modules);
        Assert.Null(d.SuspectModule);
    }

    [Fact]
    public void Implausible_driver_lists_are_ignored_instead_of_misread()
    {
        var data = FakeMinidump(0x7E, new ulong[4], When, Drivers);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x2000 + 0x34), 100_000); // count out of range
        Assert.Empty(CrashDumps.Parse(data, "x.dmp", DateTime.MinValue)!.Modules);

        data = FakeMinidump(0x7E, new ulong[4], When, Drivers);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x3000 + 0x38), 0x1000); // user-mode address: not a driver
        Assert.Empty(CrashDumps.Parse(data, "x.dmp", DateTime.MinValue)!.Modules);
    }

    [Theory]
    [InlineData("MDMP")]      // an application's dump, not a blue screen
    [InlineData("PAGEXXXX")]
    [InlineData("")]
    public void Files_that_are_not_kernel_dumps_are_skipped(string start)
    {
        var data = new byte[0x3000];
        Encoding.ASCII.GetBytes(start).CopyTo(data, 0);
        Assert.Null(CrashDumps.Parse(data, "x.dmp", DateTime.MinValue));
    }

    [Fact]
    public void Truncated_files_are_skipped()
        => Assert.Null(CrashDumps.Parse("PAGEDU64"u8.ToArray(), "x.dmp", DateTime.MinValue));

    [Fact]
    public void A_32_bit_dump_gives_its_stop_code()
    {
        var data = new byte[0x1000];
        "PAGEDUMP"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x20), 0xD1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x24 + 12), 0x8BADF00D);
        var d = CrashDumps.Parse(data, "old.dmp", When)!;
        Assert.Equal(0xD1u, d.BugCheckCode);
        Assert.Equal(0x8BADF00DUL, d.Parameters[3]);
    }
}

[Trait("Category", "E2E")]
public class CrashDumpE2ETests(ITestOutputHelper output)
{
    /// <summary>
    /// A real kernel dump from this Windows: the workflow asks Windows for a live kernel dump
    /// (stop code 0x161 LIVE_SYSTEM_DUMP, no crash) and passes its path.
    /// </summary>
    [E2EFact]
    public void A_real_live_kernel_dump_is_read()
    {
        var path = Environment.GetEnvironmentVariable("WINSOLVE_E2E_LIVEDUMP");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            output.WriteLine("No live dump was produced on this runner; header check skipped.");
            return;
        }
        var d = CrashDumps.Read(path);
        Assert.NotNull(d);
        output.WriteLine($"{d.File}: {d.CodeText} {d.Name} type {d.DumpType} at {d.Time:g}, {d.Modules.Count} drivers");
        Assert.Equal(0x161u, d.BugCheckCode);
        Assert.True(Math.Abs((DateTime.Now - d.Time).TotalHours) < 3, $"time {d.Time}");
    }

    [E2EFact]
    public void The_minidump_folder_can_be_read()
    {
        var dumps = CrashDumps.ReadRecent();
        foreach (var d in dumps) output.WriteLine($"{d.File}: {d.CodeText} {d.Name} {d.SuspectModule}");
        Assert.NotNull(dumps);
    }
}
