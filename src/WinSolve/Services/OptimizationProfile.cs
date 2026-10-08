using WinSolve.Core;

namespace WinSolve.Services;

public enum DeviceKind { PC, Laptop }

public enum OptimizationLevel { Light, Balanced, Maximum }

/// <summary>Hardware facts that decide what to optimize.</summary>
public sealed class HardwareProfile
{
    public bool IsLaptop { get; init; }
    public bool SystemOnSsd { get; init; }
    public bool HasHdd { get; init; }
    public double RamGb { get; init; }
    public int CpuThreads { get; init; }
    public string GpuVendor { get; init; } = "";
    public bool HasDedicatedGpu { get; init; }
    public bool IsWindows11 { get; init; }

    public bool LowRam => RamGb <= 8.5;

    public string Describe()
    {
        string T(string s) => Localization.Loc.T(s);
        var parts = new List<string>
        {
            T(IsLaptop ? "Laptop" : "Desktop PC"),
            T($"{RamGb:0} GB RAM"),
            T($"{CpuThreads} threads"),
            T(SystemOnSsd ? "Windows on SSD" : "Windows on HDD") + (HasHdd && SystemOnSsd ? " + HDD" : ""),
            T(HasDedicatedGpu ? $"dedicated {GpuVendor} GPU" : $"integrated {GpuVendor} GPU").Replace("  ", " "),
        };
        return string.Join(", ", parts);
    }

    private static HardwareProfile? _cached;

    public static HardwareProfile Detect()
    {
        if (_cached is not null) return _cached;

        // SMBIOS chassis types that mean portable.
        int[] laptopChassis = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];
        var chassis = Wmi.Query("SELECT ChassisTypes FROM Win32_SystemEnclosure")
            .SelectMany(c => c.Get<ushort[]>("ChassisTypes") ?? []).Select(x => (int)x);
        var hasBattery = Wmi.Query("SELECT Name FROM Win32_Battery").Count > 0;

        var ram = Wmi.Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem").Select(c => c.Get<long>("TotalPhysicalMemory")).FirstOrDefault();
        var threads = Environment.ProcessorCount;

        var gpus = Wmi.Query("SELECT Name, AdapterCompatibility FROM Win32_VideoController")
            .Select(g => $"{g.Str("AdapterCompatibility")} {g.Str("Name")}").ToList();
        var dedicated = gpus.FirstOrDefault(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || (g.Contains("Radeon", StringComparison.OrdinalIgnoreCase) && !g.Contains("Graphics", StringComparison.OrdinalIgnoreCase))
            || g.Contains("Arc", StringComparison.OrdinalIgnoreCase));
        var vendor = (dedicated ?? gpus.FirstOrDefault() ?? "") switch
        {
            var s when s.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) => "NVIDIA",
            var s when s.Contains("AMD", StringComparison.OrdinalIgnoreCase) || s.Contains("Radeon", StringComparison.OrdinalIgnoreCase) => "AMD",
            var s when s.Contains("Intel", StringComparison.OrdinalIgnoreCase) => "Intel",
            _ => "",
        };

        // System disk media type (MSFT_PhysicalDisk.MediaType: 3 = HDD, 4 = SSD).
        var disks = Wmi.Query("SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage");
        var systemDiskNumber = Wmi.Query(
                "SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '" + Environment.SystemDirectory[0] + "'",
                @"root\Microsoft\Windows\Storage")
            .Select(p => p.Get<uint>("DiskNumber").ToString()).FirstOrDefault();
        var systemDisk = disks.FirstOrDefault(d => d.Str("DeviceId") == systemDiskNumber);
        var systemSsd = systemDisk is null || systemDisk.Get<ushort>("MediaType") != 3;

        _cached = new HardwareProfile
        {
            IsLaptop = hasBattery || chassis.Any(c => laptopChassis.Contains(c)),
            SystemOnSsd = systemSsd,
            HasHdd = disks.Any(d => d.Get<ushort>("MediaType") == 3),
            RamGb = ram / 1024d / 1024 / 1024,
            CpuThreads = threads,
            GpuVendor = vendor,
            HasDedicatedGpu = dedicated is not null,
            IsWindows11 = Environment.OSVersion.Version.Build >= 22000,
        };
        return _cached;
    }
}

/// <summary>What will be done, and why.</summary>
public sealed class OptimizationPlan
{
    public List<SystemTask> Tasks { get; } = [];
    public List<Tweak> Tweaks { get; } = [];
    public List<string> Reasons { get; } = [];
}

/// <summary>
/// Builds an optimization plan from the device type, the chosen level
/// and the detected hardware.
/// </summary>
public static class OptimizationPlanner
{
    public static OptimizationPlan Build(DeviceKind device, OptimizationLevel level, HardwareProfile hw)
    {
        var plan = new OptimizationPlan();
        var taskIds = new List<string>();
        var tweakIds = new List<string>();

        void T(params string[] ids) => taskIds.AddRange(ids);
        void W(params string[] ids) => tweakIds.AddRange(ids);

        // ── Baseline (every level) ──
        T("clean-temp-user", "clean-temp-windows", "clean-error-reports", "net-flush-dns", "sec-defender-update");
        W("priv-advertising-id", "priv-suggestions", "priv-tailored", "ui-file-extensions", "ui-sticky-keys", "sys-long-paths");

        if (level >= OptimizationLevel.Balanced)
        {
            T("clean-wu-cache", "clean-delivery-opt", "clean-thumbnails", "perf-optimize-drives", "repair-time-sync", "repair-rescan-devices");
            W("priv-telemetry", "priv-activity-history", "priv-recall", "perf-game-mode", "perf-menu-delay", "perf-startup-delay",
              "sys-no-auto-reboot", "ui-end-task");
            plan.Reasons.Add(hw.SystemOnSsd
                ? "SSD detected: TRIM is used instead of defragmentation."
                : "Hard disk detected: the system drive will be defragmented.");

            if (hw.LowRam)
            {
                W("perf-background-apps");
                plan.Reasons.Add($"Only {hw.RamGb:0} GB of RAM: background apps are blocked to free memory.");
            }
        }

        if (level == OptimizationLevel.Maximum)
        {
            T("clean-component-store", "clean-browser-cache");
            W("perf-game-dvr", "perf-network-throttling", "priv-bing-search", "priv-copilot");

            if (hw.LowRam || !hw.SystemOnSsd)
            {
                W("perf-visual-effects");
                plan.Reasons.Add("Limited hardware: animations and visual effects are turned off.");
            }

            if (hw.HasDedicatedGpu && hw.GpuVendor is "NVIDIA" or "AMD")
            {
                W("perf-hags");
                plan.Reasons.Add($"Dedicated {hw.GpuVendor} GPU: hardware-accelerated GPU scheduling is enabled.");
            }

            if (device == DeviceKind.PC)
            {
                T("perf-ultimate-power-plan");
                W("perf-fast-startup-off", "sys-hibernation-off");
                plan.Reasons.Add("Desktop: Ultimate Performance power plan; hibernation and Fast Startup are disabled.");
            }
            else
            {
                T("perf-laptop-best-performance");
                plan.Reasons.Add("Laptop: Best performance power mode on the Balanced plan. " +
                                 "Hibernation stays on so work isn't lost if the battery runs out.");
            }
        }
        else if (device == DeviceKind.Laptop)
        {
            plan.Reasons.Add("Laptop: the power plan is left alone to protect battery life.");
        }
        else if (level == OptimizationLevel.Balanced)
        {
            T("perf-high-power-plan");
            plan.Reasons.Add("Desktop: the High performance power plan is enabled.");
        }

        plan.Tasks.AddRange(TaskCatalog.All.Where(t => taskIds.Contains(t.Id)));
        plan.Tweaks.AddRange(TweakCatalog.All.Where(t => tweakIds.Contains(t.Id)));
        return plan;
    }
}
