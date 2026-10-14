using WinSolve.Core;
using WinSolve.Services;
using Xunit;
using Xunit.Abstractions;

namespace WinSolve.Tests;

public class SafeModeTests
{
    [Theory]
    [InlineData("The entry was successfully copied to {1d3b6a2e-90f1-11ef-9c2a-a4bb6d5e7c01}.")]
    [InlineData("La entrada se copió correctamente en {1D3B6A2E-90F1-11EF-9C2A-A4BB6D5E7C01}.")]
    public void Entry_id_is_read_from_the_translated_bcdedit_message(string output)
        => Assert.Equal("{1d3b6a2e-90f1-11ef-9c2a-a4bb6d5e7c01}", SafeMode.ParseEntryId(output));

    [Theory]
    [InlineData("")]
    [InlineData("The boot configuration data store could not be opened. Access is denied.")]
    [InlineData("{current}")]
    public void No_entry_id_when_bcdedit_failed(string output) => Assert.Null(SafeMode.ParseEntryId(output));

    [Fact]
    public void Safe_mode_is_in_the_toolbox_and_translated()
    {
        var tool = WinSolve.UI.Toolbox.All.Single(t => t.Id == "safe-mode");
        Assert.Equal("Windows", tool.Category);
        Assert.True(Localization.Spanish.Table.ContainsKey(tool.Title));
        Assert.True(Localization.Spanish.Table.ContainsKey(tool.Description));
    }
}

[Trait("Category", "E2E")]
public class SafeModeE2ETests(ITestOutputHelper output)
{
    private static async Task<string> Bcd(string args) => (await ProcessRunner.RunAsync("bcdedit.exe", args)).Output;

    [E2EFact]
    public async Task One_time_safe_mode_start_is_prepared_and_taken_back()
    {
        var id = await SafeMode.PrepareAsync(networking: true, output.WriteLine, default);
        try
        {
            output.WriteLine(await Bcd($"/enum {id}"));
            var bootmgr = await Bcd("/enum {bootmgr} /v");
            output.WriteLine(bootmgr);
            Assert.Contains(SafeMode.EntryDescription, await Bcd($"/enum {id}"));
            Assert.Contains("Network", await Bcd($"/enum {id}"), StringComparison.OrdinalIgnoreCase);
            // Booted once through "bootsequence", never added to the menu or made the default.
            var lines = bootmgr.Split('\n').Select(l => l.Trim()).ToList();
            Assert.Contains(lines, l => l.StartsWith("bootsequence", StringComparison.OrdinalIgnoreCase) && l.Contains(id, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(lines, l => (l.StartsWith("displayorder", StringComparison.OrdinalIgnoreCase) || l.StartsWith("default", StringComparison.OrdinalIgnoreCase)) && l.Contains(id, StringComparison.OrdinalIgnoreCase));
            // The normal Windows entry is unchanged.
            Assert.DoesNotContain("safeboot", await Bcd("/enum {current}"), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await SafeMode.UndoAsync(output.WriteLine);
        }
        Assert.DoesNotContain(id, await Bcd("/enum all /v"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bootsequence", await Bcd("/enum {bootmgr} /v"), StringComparison.OrdinalIgnoreCase);
    }
}

[Trait("Category", "E2E")]
public class DiskRepairE2ETests
{
    [E2EFact]
    public async Task A_normal_drive_has_nothing_blocking_its_repair()
        => Assert.Null(await DiskRepairService.WriteBlockerAsync(new VolumeInfo { Letter = "C", FileSystem = "NTFS" }, default));

    [E2EFact]
    public async Task A_surface_scan_of_another_drive_can_be_scheduled_for_the_restart()
    {
        // What happens when a program holds a drive: the scan is moved to the next boot.
        const string key = @"SYSTEM\CurrentControlSet\Control\Session Manager";
        var before = (string[])Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key)!.GetValue("BootExecute")!;
        try
        {
            Assert.True(await DiskRepairService.ScheduleAtRestartAsync('T', surfaceScan: true, _ => { }, default));
            Assert.True(DiskRepairService.IsCheckScheduled('T', surfaceScan: true));
            var after = (string[])Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key)!.GetValue("BootExecute")!;
            Assert.Contains(@"autocheck autochk /r \??\T:", after);
            // Windows' own entries are all kept, after the new one.
            Assert.Equal([.. before], after.Where(e => !e.Contains(@"\??\T:")).ToArray());
            Assert.Equal(0, Array.FindIndex(after, e => e.Contains(@"\??\T:")));

            // "Cancel scheduled check" puts the list back exactly as it was.
            Assert.Equal(1, DiskRepairService.CancelScheduledCheck('T'));
            Assert.Equal(before, (string[])Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key)!.GetValue("BootExecute")!);
            Assert.False(DiskRepairService.IsCheckScheduled('T', surfaceScan: false));
        }
        finally
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key, writable: true)!;
            k.SetValue("BootExecute", before, Microsoft.Win32.RegistryValueKind.MultiString);
        }
    }
}

public class DiskRepairDeniedTests
{
    [Theory]
    [InlineData(1, "Acceso denegado.")]
    [InlineData(3, "Access Denied as you do not have sufficient privileges or the disk may be locked by another process.")]
    [InlineData(5, "Zugriff verweigert")]
    [InlineData(2, "Отказано в доступе.")]
    public void A_refused_drive_is_recognized(int exit, string output)
        => Assert.True(DiskRepairService.Denied(new ProcessResult(exit, output)));

    [Fact]
    public void A_real_check_is_not_taken_for_a_refusal()
    {
        var output = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"Stage line {i}"));
        Assert.False(DiskRepairService.Denied(new ProcessResult(1, output)));
        Assert.False(DiskRepairService.Denied(new ProcessResult(0, "")));
    }
}

public class BootCheckTests
{
    [Fact]
    public void Cancelling_removes_only_that_drives_check()
    {
        string[] list = ["autocheck autochk /r \\??\\E:", "autocheck autochk /q /v *", "autocheck autochk /p \\??\\D:"];
        Assert.Equal(["autocheck autochk /q /v *", "autocheck autochk /p \\??\\D:"], DiskRepairService.WithoutCheckOf(list, 'e'));
        Assert.Equal(list, DiskRepairService.WithoutCheckOf(list, 'F'));
    }

    [Theory]
    [InlineData("Checking file system on E:\r\nThe type of the file system is NTFS.", 'E', true)]
    [InlineData("Comprobando el sistema de archivos en E:\r\nEl tipo de sistema de archivos es NTFS.", 'E', true)]
    [InlineData("Checking file system on C:\r\nThe type of the file system is NTFS.", 'E', false)]
    [InlineData("Checking file system on C:\r\nVolume label is ONE.\r\nCHKDSK is verifying files (stage 1 of 3)...", 'E', false)]
    public void The_check_log_is_matched_to_its_drive(string text, char letter, bool expected)
        => Assert.Equal(expected, DiskRepairService.IsAboutDrive(text, letter));

    [Fact]
    public void Progress_lines_are_dropped_from_the_result()
    {
        var text = "Checking file system on E:\r\nProgress: 1 of 200 done; Stage: 0%\r\n\r\n\r\n   0 KB in bad sectors.\r\nProgress: 200 of 200 done; Stage: 100%";
        Assert.Equal("Checking file system on E:" + Environment.NewLine + Environment.NewLine + "   0 KB in bad sectors.", DiskRepairService.CleanCheckText(text));
    }
}

[Trait("Category", "E2E")]
public class BootCheckE2ETests(ITestOutputHelper output)
{
    [E2EFact]
    public async Task The_last_startup_check_can_be_read()
    {
        // The test machine may never have checked a drive at startup; reading must still work.
        var last = await DiskRepairService.LastBootCheckAsync('C');
        output.WriteLine(last is null ? "No startup check of C: recorded." : $"{last.When:g}\n{last.Text}");
        Assert.Null(await DiskRepairService.LastBootCheckAsync('Q'));
    }
}
