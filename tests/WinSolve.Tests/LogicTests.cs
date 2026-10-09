using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

public class UninstallCommandTests
{
    [Fact]
    public void Quoted_executable_is_split_from_arguments()
    {
        var c = AppsService.SplitCommand("\"C:\\Program Files\\App\\uninstall.exe\" /S /x");
        Assert.Equal(("C:\\Program Files\\App\\uninstall.exe", "/S /x"), c);
    }

    [Fact]
    public void Unquoted_command_falls_back_to_first_token()
    {
        var c = AppsService.SplitCommand("C:\\NoSuchDir\\un.exe --remove");
        Assert.Equal(("C:\\NoSuchDir\\un.exe", "--remove"), c);
    }

    [Fact]
    public void Shell_metacharacters_stay_inside_the_arguments()
    {
        // Run without cmd.exe, "& calc" is just an argument to the uninstaller, never a second command.
        var c = AppsService.SplitCommand("\"C:\\App\\u.exe\" & calc.exe");
        Assert.Equal("C:\\App\\u.exe", c!.Value.Exe);
        Assert.Equal("& calc.exe", c.Value.Args);
    }

    [Fact]
    public void Empty_or_broken_commands_are_rejected()
    {
        Assert.Null(AppsService.SplitCommand(""));
        Assert.Null(AppsService.SplitCommand("\"unterminated"));
    }
}

public class OptimizationPlannerTests
{
    private static HardwareProfile Hw(bool laptop = false, bool ssd = true, double ram = 16, string gpu = "NVIDIA", bool dedicated = true) => new()
    {
        IsLaptop = laptop, SystemOnSsd = ssd, RamGb = ram, CpuThreads = 8, GpuVendor = gpu, HasDedicatedGpu = dedicated,
    };

    private static HashSet<string> Ids(OptimizationPlan p) => [.. p.Tasks.Select(t => t.Id), .. p.Tweaks.Select(t => t.Id)];

    [Fact]
    public void Light_never_touches_power_settings()
    {
        var ids = Ids(OptimizationPlanner.Build(DeviceKind.PC, OptimizationLevel.Light, Hw()));
        Assert.DoesNotContain(ids, id => id.Contains("power-plan") || id.Contains("hibernation"));
    }

    [Fact]
    public void Maximum_on_laptop_keeps_hibernation_and_uses_power_mode()
    {
        var ids = Ids(OptimizationPlanner.Build(DeviceKind.Laptop, OptimizationLevel.Maximum, Hw(laptop: true)));
        Assert.DoesNotContain("sys-hibernation-off", ids);
        Assert.DoesNotContain("perf-ultimate-power-plan", ids);
        Assert.Contains("perf-laptop-best-performance", ids);
    }

    [Fact]
    public void Maximum_on_desktop_uses_ultimate_plan()
    {
        var ids = Ids(OptimizationPlanner.Build(DeviceKind.PC, OptimizationLevel.Maximum, Hw()));
        Assert.Contains("perf-ultimate-power-plan", ids);
        Assert.Contains("sys-hibernation-off", ids);
    }

    [Fact]
    public void Hags_only_for_dedicated_nvidia_or_amd()
    {
        Assert.Contains("perf-hags", Ids(OptimizationPlanner.Build(DeviceKind.PC, OptimizationLevel.Maximum, Hw(gpu: "AMD"))));
        Assert.DoesNotContain("perf-hags", Ids(OptimizationPlanner.Build(DeviceKind.PC, OptimizationLevel.Maximum, Hw(gpu: "Intel", dedicated: false))));
    }

    [Fact]
    public void Low_ram_blocks_background_apps()
    {
        Assert.Contains("perf-background-apps", Ids(OptimizationPlanner.Build(DeviceKind.PC, OptimizationLevel.Balanced, Hw(ram: 8))));
        Assert.DoesNotContain("perf-background-apps", Ids(OptimizationPlanner.Build(DeviceKind.PC, OptimizationLevel.Balanced, Hw(ram: 32))));
    }

    [Fact]
    public void Every_planned_id_exists_in_the_catalogs()
    {
        foreach (var device in Enum.GetValues<DeviceKind>())
        foreach (var level in Enum.GetValues<OptimizationLevel>())
        {
            var plan = OptimizationPlanner.Build(device, level, Hw(laptop: device == DeviceKind.Laptop, ssd: false, ram: 4));
            Assert.All(plan.Tasks, t => Assert.NotNull(TaskCatalog.Find(t.Id)));
            Assert.NotEmpty(plan.Tasks);
        }
    }

    [Fact]
    public void Maintenance_only_uses_existing_safe_tasks()
    {
        foreach (var id in Maintenance.TaskIds)
        {
            var task = TaskCatalog.Find(id);
            Assert.NotNull(task);
            Assert.False(task!.NeedsReboot);
        }
    }
}

public class DriverTests
{
    [Theory]
    [InlineData("32.0.15.6636", "566.36")]
    [InlineData("31.0.15.5222", "552.22")]
    [InlineData("27.21.14.5671", "456.71")]
    public void Nvidia_version_from_windows_version(string windows, string expected)
        => Assert.Equal(expected, DriverService.NvidiaVersion(windows));

    [Fact]
    public void Newer_version_comparison()
    {
        Assert.True(DriverService.IsNewer("566.36", "560.94"));
        Assert.False(DriverService.IsNewer("560.94", "560.94"));
    }

    [Fact]
    public void Vendor_signers_are_exact_names()
    {
        Assert.Equal(["NVIDIA Corporation"], DriverService.TrustedSigners(GpuVendor.Nvidia));
        Assert.Empty(DriverService.TrustedSigners(GpuVendor.Unknown));
    }
}

public class SmartParsingTests
{
    [Fact]
    public void Ata_attributes_are_parsed_and_evaluated()
    {
        var raw = new byte[512];
        var thr = new byte[512];
        void Attr(int slot, byte id, byte cur, byte worst, long rawValue, byte threshold)
        {
            var off = 2 + slot * 12;
            raw[off] = id; raw[off + 3] = cur; raw[off + 4] = worst;
            for (int b = 0; b < 6; b++) raw[off + 5 + b] = (byte)(rawValue >> (8 * b));
            thr[off] = id; thr[off + 1] = threshold;
        }
        Attr(0, 0x05, 100, 100, 12, 10);     // 12 reallocated sectors
        Attr(1, 0x09, 90, 90, 12345, 0);     // power-on hours
        Attr(2, 0xC2, 64, 50, 36, 0);        // temperature 36 °C

        var disk = new DiskInfo { Model = "Test" };
        DiskHealthService.ParseAttributes(raw, thr, disk);
        DiskHealthService.Evaluate(disk);

        Assert.Equal(3, disk.Attributes.Count);
        Assert.Equal(12345, disk.PowerOnHours);
        Assert.Equal(36, disk.TemperatureC);
        Assert.Equal(HealthLevel.Caution, disk.Health); // reallocated sectors > 0
    }

    [Fact]
    public void Attribute_below_threshold_is_bad()
    {
        var raw = new byte[512];
        var thr = new byte[512];
        raw[2] = 0x05; raw[5] = 5; raw[6] = 5;   // current 5
        thr[2] = 0x05; thr[3] = 10;              // threshold 10
        var disk = new DiskInfo();
        DiskHealthService.ParseAttributes(raw, thr, disk);
        DiskHealthService.Evaluate(disk);
        Assert.Equal(HealthLevel.Bad, disk.Health);
    }
}

public class LocalizationTests
{
    [Fact]
    public void Spanish_exact_template_and_nested_translations()
    {
        var previous = AppSettings.Current.Language;
        try
        {
            AppSettings.Current.Language = "es";
            Loc.Reset();
            Assert.Equal("Limpieza y reparación", Loc.T("Cleanup & repair"));
            Assert.Equal("3 de 28 ajustes activos", Loc.T("3 of 28 tweaks on"));
            Assert.Equal("Optimizando: completado", Loc.T("Optimizing: completed"));
            Assert.Equal("  Explorador reiniciado.", Loc.T("  Explorer restarted.")); // indentation is kept
            Assert.Equal("C:\\Some\\Unknown path", Loc.T("C:\\Some\\Unknown path")); // unknown text is left alone
            Assert.Equal("Se ejecutará:\n\n- X\n\n¿Continuar?", Loc.T("The following will run:\n\n- X\n\nContinue?"));
        }
        finally
        {
            AppSettings.Current.Language = previous;
            Loc.Reset();
        }
    }

    [Fact]
    public void English_is_returned_unchanged()
    {
        var previous = AppSettings.Current.Language;
        AppSettings.Current.Language = "en";
        try { Assert.Equal("3 of 28 tweaks on", Loc.T("3 of 28 tweaks on")); }
        finally { AppSettings.Current.Language = previous; }
    }

    [Fact]
    public void Every_template_target_has_matching_placeholders()
    {
        foreach (var (key, value) in Spanish.Table)
        {
            var keyCount = System.Text.RegularExpressions.Regex.Matches(key, @"\{\d+\}").Count;
            var valueCount = System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+\}").Count;
            Assert.True(keyCount == valueCount, $"Placeholder mismatch in '{key}'");
        }
    }
}

public class TweakCatalogTests
{
    [Fact]
    public void Catalog_loads_with_unique_ids_and_groups()
    {
        var all = TweakCatalog.All;
        Assert.NotEmpty(all);
        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct().Count());
        Assert.Contains(all, t => t.Group == TweakGroup.Essential);
        Assert.Contains(all, t => t.Group == TweakGroup.Advanced);
        Assert.Contains(all, t => t.Group == TweakGroup.Preference);
    }

    [Fact]
    public void Presets_only_reference_existing_non_preference_tweaks()
    {
        foreach (var id in TweakCatalog.StandardPreset.Concat(TweakCatalog.MinimalPreset))
        {
            var t = TweakCatalog.Find(id);
            Assert.NotNull(t);
            Assert.NotEqual(TweakGroup.Preference, t!.Group);
        }
    }

    [Fact]
    public void Actions_are_never_reported_as_applied()
    {
        foreach (var t in TweakCatalog.All.Where(t => t.IsAction))
            Assert.False(t.SafeIsApplied());
    }

    [Fact]
    public void Dns_providers_have_valid_addresses()
    {
        foreach (var p in DnsService.Providers.Skip(1))
        {
            Assert.NotEmpty(p.IPv4);
            Assert.All(p.IPv4.Concat(p.IPv6), a => Assert.True(System.Net.IPAddress.TryParse(a, out _), a));
        }
    }
}

public class DiskRepairTests
{
    [Fact]
    public void Advice_escalates_with_bad_sectors()
    {
        var good = new DiskInfo { Health = HealthLevel.Good, MediaType = "HDD" };
        Assert.Equal(HealthLevel.Good, DiskRepairService.Advice(good).Level);

        var pending = new DiskInfo { Health = HealthLevel.Caution };
        pending.Attributes.Add(new SmartAttribute(0xC5, "Current pending sectors", 100, 100, 0, 8));
        Assert.Equal(HealthLevel.Caution, DiskRepairService.Advice(pending).Level);
        Assert.Contains("pending: 8", DiskRepairService.Advice(pending).Text);

        var failing = new DiskInfo { Health = HealthLevel.Bad };
        Assert.Equal(HealthLevel.Bad, DiskRepairService.Advice(failing).Level);
    }
}
