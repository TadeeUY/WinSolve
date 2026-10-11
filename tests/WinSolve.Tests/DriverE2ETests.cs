using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using WinSolve.Core;
using WinSolve.Services;
using Xunit;
using Xunit.Abstractions;

namespace WinSolve.Tests;

/// <summary>
/// Runs only on a throw-away Windows machine (the "Windows end-to-end" workflow sets
/// WINSOLVE_E2E=1): these tests change the real driver store and machine settings.
/// </summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WINSOLVE_E2E") != "1")
            Skip = "End-to-end test: runs only in the Windows end-to-end workflow.";
    }
}

[Trait("Category", "E2E")]
public class DriverE2ETests(ITestOutputHelper output)
{
    // Two versions of the same test driver package, staged by the workflow before the tests.
    private const string TestInf = "winsolvetest.inf";

    private static List<(string Inf, string Original, string Version)> StagedTestPackages()
    {
        var r = ProcessRunner.PowerShellAsync(
            "Get-WindowsDriver -Online | Where-Object { $_.OriginalFileName -like '*\\winsolvetest.inf' } | ForEach-Object { \"$($_.Driver)|$($_.OriginalFileName)|$($_.Version)\" }")
            .GetAwaiter().GetResult();
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|')).Where(p => p.Length == 3).Select(p => (p[0], p[1], p[2])).ToList();
    }

    [E2EFact]
    public async Task Old_drivers_never_include_one_a_device_uses()
    {
        var old = await DriverStoreCleaner.FindAsync();
        var inUse = Wmi.Query("SELECT InfName FROM Win32_PnPSignedDriver").Select(d => d.Str("InfName")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        output.WriteLine($"{inUse.Count} driver packages in use, {old.Count} old package(s) offered:");
        foreach (var d in old) output.WriteLine($"  {d.Inf} {d.OriginalName} {d.Version} (newer: {d.NewerVersion})");
        Assert.NotEmpty(inUse);
        Assert.DoesNotContain(old, d => inUse.Contains(d.Inf));
        Assert.All(old, d => Assert.True(Version.Parse(d.NewerVersion) > d.Version));
    }

    [E2EFact]
    public async Task Older_copy_of_a_driver_is_found_and_removed_and_the_newest_stays()
    {
        var staged = StagedTestPackages();
        foreach (var p in staged) output.WriteLine($"Staged: {p.Inf} {p.Version} {p.Original}");
        Assert.True(staged.Count >= 2, "The workflow should have staged two versions of the test driver.");
        var newest = staged.MaxBy(p => Version.Parse(p.Version));

        var old = (await DriverStoreCleaner.FindAsync()).Where(d => d.OriginalName.Equals(TestInf, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(staged.Count - 1, old.Count);
        Assert.DoesNotContain(old, d => d.Inf.Equals(newest.Inf, StringComparison.OrdinalIgnoreCase));

        await DriverStoreCleaner.DeleteAsync(old, output.WriteLine, default);

        var left = StagedTestPackages();
        Assert.Single(left);
        Assert.Equal(newest.Inf, left[0].Inf, StringComparer.OrdinalIgnoreCase);
        Assert.Empty((await DriverStoreCleaner.FindAsync()).Where(d => d.OriginalName.Equals(TestInf, StringComparison.OrdinalIgnoreCase)));
    }

    [E2EFact]
    public async Task Driver_backup_exports_the_third_party_drivers()
    {
        var folder = await DriverService.BackupDriversAsync(output.WriteLine, default);
        try
        {
            var infs = Directory.GetFiles(folder, "*.inf", SearchOption.AllDirectories);
            output.WriteLine($"{infs.Length} INF file(s) in {folder}");
            Assert.Contains(infs, f => Path.GetFileName(f).Equals(TestInf, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [E2EFact]
    public void Graphics_cards_are_listed_without_errors()
    {
        var gpus = DriverService.GetGpus();
        foreach (var g in gpus) output.WriteLine($"{g.Name} | vendor {g.Vendor} | driver {g.WindowsDriverVersion} ({g.FriendlyVersion})");
        Assert.NotNull(gpus);
    }

    [E2EFact]
    public void Windows_Update_driver_pause_is_restored_exactly()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching";
        var block = typeof(DriverService).GetMethod("BlockWindowsUpdateDrivers", BindingFlags.NonPublic | BindingFlags.Static)!;
        var original = Reg.Get(RegistryHive.LocalMachine, key, "SearchOrderConfig");
        try
        {
            // A value the user had set: paused while installing, then put back.
            Reg.Set(RegistryHive.LocalMachine, key, "SearchOrderConfig", 1, RegistryValueKind.DWord);
            block.Invoke(null, [(Action<string>)output.WriteLine]);
            Assert.Equal(0, Reg.Get(RegistryHive.LocalMachine, key, "SearchOrderConfig"));
            // A second pause (crash before restoring) must not overwrite the saved value.
            block.Invoke(null, [(Action<string>)output.WriteLine]);
            DriverService.RestoreWindowsUpdateDrivers(output.WriteLine);
            Assert.Equal(1, Reg.Get(RegistryHive.LocalMachine, key, "SearchOrderConfig"));

            // No value at all: restoring removes it again instead of leaving a 0.
            Reg.Delete(RegistryHive.LocalMachine, key, "SearchOrderConfig");
            block.Invoke(null, [(Action<string>)output.WriteLine]);
            DriverService.RestoreWindowsUpdateDrivers(output.WriteLine);
            Assert.Null(Reg.Get(RegistryHive.LocalMachine, key, "SearchOrderConfig"));
        }
        finally
        {
            if (original is int v) Reg.Set(RegistryHive.LocalMachine, key, "SearchOrderConfig", v, RegistryValueKind.DWord);
            else Reg.Delete(RegistryHive.LocalMachine, key, "SearchOrderConfig");
        }
    }

    [E2EFact]
    public async Task Update_check_finds_the_published_release_with_its_checksum()
    {
        // CheckAsync only returns newer versions (this build is the newest) and the GitHub API
        // may rate-limit shared CI machines, so only log it, then verify a published checksum.
        try
        {
            var update = await UpdateService.CheckAsync();
            output.WriteLine(update is null ? "No newer release than this build (expected)." : $"Newer release: {update.Tag}");
        }
        catch (Exception ex) { output.WriteLine("Update check: " + ex.Message); }
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinSolve-tests");
        const string baseUrl = "https://github.com/TadeeUY/WinSolve/releases/download/v0.13.1/";
        var expected = (await http.GetStringAsync(baseUrl + "WinSolveSetup.exe.sha256")).Split(' ')[0].Trim();
        var bytes = await http.GetByteArrayAsync(baseUrl + "WinSolveSetup.exe");
        Assert.Equal(expected, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), ignoreCase: true);
    }
}
