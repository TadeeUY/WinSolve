using WinSolve.Services;
using Xunit;
using Xunit.Abstractions;

namespace WinSolve.Tests;

public class SensorTests
{
    [Fact]
    public void A_thermal_zone_stuck_on_one_value_while_the_load_changes_is_not_a_CPU_sensor()
    {
        var f = new ThermalZoneFilter();
        for (var i = 0; i < 40; i++) f.Add(27.85, i % 2 == 0 ? 3 : 95);
        Assert.True(f.IsFrozen);
    }

    [Fact]
    public void A_thermal_zone_that_moves_is_kept()
    {
        var f = new ThermalZoneFilter();
        for (var i = 0; i < 40; i++) f.Add(45 + (i % 5), i % 2 == 0 ? 3 : 95);
        Assert.False(f.IsFrozen);
    }

    [Fact]
    public void An_idle_PC_is_not_judged_too_early()
    {
        var f = new ThermalZoneFilter();
        for (var i = 0; i < 10; i++) f.Add(40, i % 2 == 0 ? 0 : 100);
        Assert.False(f.IsFrozen); // not enough samples
        f = new ThermalZoneFilter();
        for (var i = 0; i < 100; i++) f.Add(40, 5);
        Assert.False(f.IsFrozen); // a steady value under a steady load proves nothing
    }

    [Fact]
    public void The_package_sensor_is_preferred_on_Intel()
    {
        var t = CpuTemperature.PickCpuSensor(
        [
            ("CPU Core #1", "/intelcpu/0/temperature/0", 61),
            ("CPU Core #2", "/intelcpu/0/temperature/1", 64),
            ("CPU Package", "/intelcpu/0/temperature/8", 66),
            ("GPU Core", "/gpu-nvidia/0/temperature/0", 70),
        ]);
        Assert.Equal(66, t);
    }

    [Fact]
    public void Tctl_is_used_on_AMD()
        => Assert.Equal(72, CpuTemperature.PickCpuSensor([("Core (Tctl/Tdie)", "/amdcpu/0/temperature/2", 72), ("CCD1 (Tdie)", "/amdcpu/0/temperature/3", 69)]));

    [Fact]
    public void Without_a_package_sensor_the_hottest_core_counts()
        => Assert.Equal(58, CpuTemperature.PickCpuSensor([("Core #0", "/intelcpu/0/temperature/0", 51), ("Core #1", "/intelcpu/0/temperature/1", 58)]));

    [Fact]
    public void Non_CPU_or_impossible_values_are_ignored()
    {
        Assert.Null(CpuTemperature.PickCpuSensor([("GPU Core", "/gpu-amd/0/temperature/0", 60)]));
        Assert.Null(CpuTemperature.PickCpuSensor([("CPU Package", "/intelcpu/0/temperature/8", 0), ("Core #0", "/intelcpu/0/temperature/0", 255)]));
    }
}

[Trait("Category", "E2E")]
public class PerformanceE2ETests(ITestOutputHelper output)
{
    [E2EFact]
    public async Task Scanning_the_Windows_folder_is_fast_enough()
    {
        var progress = new DiskScanner.Progress();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var r = await DiskScanner.ScanAsync(windows, progress, default);
        output.WriteLine($"{windows}: {progress.Files:N0} files, {progress.Bytes / 1e9:0.0} GB in {r.Elapsed.TotalSeconds:0.0} s ({progress.Files / Math.Max(0.1, r.Elapsed.TotalSeconds):N0} files/s), {r.Inaccessible} folders inaccessible");
        Assert.True(progress.Files > 10_000);
        Assert.True(r.Elapsed < TimeSpan.FromMinutes(3), $"took {r.Elapsed}");
    }

    [E2EFact]
    public async Task Scan_speed_by_folder()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var dir in new[] { Path.Combine(windows, "System32"), Path.Combine(windows, "WinSxS"), Path.Combine(windows, "System32"), @"C:\Program Files" })
        {
            var progress = new DiskScanner.Progress();
            var r = await DiskScanner.ScanAsync(dir, progress, default);
            output.WriteLine($"{dir}: {progress.Files:N0} files in {r.Elapsed.TotalSeconds:0.0} s ({progress.Files / Math.Max(0.1, r.Elapsed.TotalSeconds):N0} files/s)");
        }
    }

    [E2EFact]
    public void CPU_temperature_reports_its_source()
    {
        var reading = CpuTemperature.Read(10);
        output.WriteLine($"CPU temperature: {reading.Celsius?.ToString("0.0") ?? "none"} from {reading.Source}");
        Assert.True(reading.Celsius is null || reading.Celsius is > 0 and < 125);
    }
}
