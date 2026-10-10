using WinSolve.Core;
using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

/// <summary>
/// Run against the real Windows APIs on the CI runner (elevated). They only check that each call
/// works and returns sane values; the runner's state (no Wi-Fi, little history) varies.
/// </summary>
public class WindowsIntegrationTests
{
    [Fact]
    public void Pause_and_resume_Windows_Update()
    {
        if (!Admin.IsElevated) return;
        try
        {
            WindowsUpdateService.Pause(7);
            var until = WindowsUpdateService.PausedUntil();
            Assert.NotNull(until);
            Assert.InRange(until!.Value, DateTime.Now.AddDays(6.9), DateTime.Now.AddDays(7.1));

            WindowsUpdateService.Pause(100); // capped at 5 weeks
            Assert.InRange(WindowsUpdateService.PausedUntil()!.Value, DateTime.Now.AddDays(34.9), DateTime.Now.AddDays(35.1));
        }
        finally
        {
            WindowsUpdateService.Resume();
        }
        Assert.Null(WindowsUpdateService.PausedUntil());
    }

    [Fact]
    public void Windows_Update_history_can_be_read()
    {
        var history = WindowsUpdateService.GetHistory(50);
        Assert.All(history, h => Assert.False(string.IsNullOrWhiteSpace(h.Title)));
        Assert.All(history, h => Assert.True(h.Date > new DateTime(2000, 1, 1)));
    }

    [Fact]
    public void Driver_rollback_candidates_are_listed()
    {
        var devices = WindowsUpdateService.GetRollbackCandidates();
        Assert.All(devices, d => Assert.False(d.Provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Disk_benchmark_measures_and_cleans_up()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var before = Directory.GetFiles(root, "*.tmp").Length;
        var r = await DiskBench.RunAsync(root, 64, _ => { }, (_, _) => { }, CancellationToken.None);
        Assert.True(r.SeqReadMBs > 0 && r.SeqWriteMBs > 0);
        Assert.True(r.RandReadIops > 0 && r.RandWriteIops > 0);
        Assert.Equal(before, Directory.GetFiles(root, "*.tmp").Length);
    }

    [Fact]
    public void Memory_purge_reports_sane_numbers()
    {
        if (!Admin.IsElevated) return;
        var r = MemoryCleaner.PurgeStandbyList();
        Assert.True(r.Total > 0);
        Assert.InRange(r.FreeAfter, 1, r.Total);
        Assert.InRange(r.Freed, 0, r.Total);
    }

    [Fact]
    public void Wifi_passwords_read_or_explain()
    {
        // Servers usually have no WLAN service: that must be a clear error, not a crash.
        try { Assert.NotNull(WifiPasswords.Read()); }
        catch (Exception ex) { Assert.False(string.IsNullOrWhiteSpace(ex.Message)); }
    }

    [Fact]
    public void Context_menu_entries_are_listed()
        => Assert.All(ContextMenuItems.List(), e => Assert.False(string.IsNullOrEmpty(e.RegistryPath)));

    [Fact]
    public async Task Locked_file_is_found_with_this_process()
    {
        var file = Path.GetTempFileName();
        try
        {
            await using var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var procs = LockFinder.Find([file]);
            Assert.Contains(procs, p => p.Pid == Environment.ProcessId);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
