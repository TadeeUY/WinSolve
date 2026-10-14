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
}
