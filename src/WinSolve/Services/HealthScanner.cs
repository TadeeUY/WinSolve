using WinSolve.Core;

namespace WinSolve.Services;

public enum IssueSeverity { Info, Warning, Critical }

/// <summary>A detected problem and, when possible, its fix.</summary>
public sealed class Issue
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public IssueSeverity Severity { get; init; }

    /// <summary>Catalog tasks that fix it.</summary>
    public string[] FixTaskIds { get; init; } = [];

    /// <summary>Alternative action: an app page or a Settings URI.</summary>
    public string? NavigateTo { get; init; }
    public string FixLabel { get; init; } = "Fix";
}

public sealed class ScanResult
{
    public SystemSummary System { get; init; } = new();
    public List<Issue> Issues { get; } = [];
    public List<DiskInfo> Disks { get; init; } = [];
    public List<ProblemDevice> Devices { get; init; } = [];
    public ActivationStatus Activation { get; init; } = new();
    public long JunkBytes { get; init; }

    /// <summary>0-100 score for the dashboard.</summary>
    public int Score => Math.Max(0, 100 - Issues.Sum(i => i.Severity switch
    {
        IssueSeverity.Critical => 25,
        IssueSeverity.Warning => 10,
        _ => 3,
    }));
}

/// <summary>Full health check: finds problems and proposes fixes.</summary>
public static class HealthScanner
{
    public static async Task<ScanResult> ScanAsync(Action<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Invoke("Reading system information...");
        var system = await Task.Run(SystemInfoService.Collect, ct);

        progress?.Invoke("Checking drive health...");
        var disks = await DiskHealthService.ScanAsync(ct);

        progress?.Invoke("Looking for device errors...");
        var devices = await Task.Run(HardwareService.GetProblemDevices, ct);

        progress?.Invoke("Checking activation...");
        var activation = await Task.Run(ActivationService.GetStatus, ct);

        progress?.Invoke("Measuring junk files...");
        var junk = await Task.Run(EstimateJunk, ct);

        progress?.Invoke("Reviewing critical events...");
        var events = await Task.Run(() => HardwareService.GetCriticalEvents(14), ct);

        var result = new ScanResult { System = system, Disks = disks, Devices = devices, Activation = activation, JunkBytes = junk };
        var issues = result.Issues;

        // ── Drives ──
        foreach (var d in disks.Where(d => d.Health == HealthLevel.Bad))
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Title = $"Drive in bad health: {d.Model}",
                Detail = string.Join(" ", d.Findings) + " Back up your data as soon as possible.",
                NavigateTo = "hardware", FixLabel = "Repair",
            });
        foreach (var d in disks.Where(d => d.Health == HealthLevel.Caution))
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Title = $"Drive needs attention: {d.Model}",
                Detail = string.Join(" ", d.Findings),
                NavigateTo = "hardware", FixLabel = "Repair",
            });

        foreach (var drive in system.Drives.Where(d => d.FreePercent < 10))
            issues.Add(new Issue
            {
                Severity = drive.FreePercent < 5 ? IssueSeverity.Critical : IssueSeverity.Warning,
                Title = $"Low disk space on {drive.Name}",
                Detail = $"{Format.Bytes(drive.Free)} free ({drive.FreePercent:0}%). Below 10% Windows slows down and updates fail.",
                FixTaskIds = ["clean-temp-user", "clean-temp-windows", "clean-wu-cache", "clean-delivery-opt", "clean-error-reports", "clean-recycle-bin"],
                FixLabel = "Free up space",
            });

        // ── Devices ──
        foreach (var dev in devices)
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Title = $"Device error: {dev.Name}",
                Detail = $"Code {dev.ErrorCode}: {dev.Explanation}",
                NavigateTo = "hardware", FixLabel = "Repair",
            });

        // ── Event log ──
        var bsods = events.Count(e => e.EventId == 1001);
        if (bsods > 0)
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Title = $"{bsods} blue screen(s) in the last 2 weeks",
                Detail = "Usually caused by drivers, RAM or the drive. Check the events under Hardware and repair the system files.",
                FixTaskIds = ["repair-system-files"], FixLabel = "Repair system files",
            });
        var whea = events.Count(e => e.Source == "Microsoft-Windows-WHEA-Logger");
        if (whea > 0)
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Title = $"{whea} hardware error(s) (WHEA)",
                Detail = "The CPU, RAM or PCIe bus reported errors. Common causes: unstable overclock, high temperatures or a failing component.",
                NavigateTo = "hardware", FixLabel = "View events",
            });
        var crashes = events.Count(e => e.EventId == 41 && e.Source == "Microsoft-Windows-Kernel-Power");
        if (crashes >= 2)
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Title = $"{crashes} unexpected shutdowns",
                Detail = "The PC turned off or restarted without shutting down Windows. Check the power supply and temperatures.",
                NavigateTo = "hardware", FixLabel = "View events",
            });
        var gpu = events.Count(e => e.EventId == 4101);
        if (gpu > 0)
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Title = "The display driver was reset",
                Detail = $"{gpu} time(s) in the last 2 weeks. A clean reinstall of the graphics driver usually fixes it.",
                NavigateTo = "drivers", FixLabel = "Open Drivers",
            });

        // ── System ──
        if (activation.LicenseStatus is not 1 and not -1)
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Title = "Windows is not activated",
                Detail = activation.FirmwareKey is not null
                    ? "This PC has a Windows key stored in its firmware. It can be activated with one click."
                    : activation.StatusText,
                NavigateTo = "activation", FixLabel = "Activate",
            });

        if (SystemInfoService.IsRebootPending())
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Info,
                Title = "Restart pending",
                Detail = "Windows has updates or changes waiting for a restart.",
            });

        if (system.Uptime.TotalDays >= 7)
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Info,
                Title = $"No restart in {Format.Duration(system.Uptime)}",
                Detail = "Restarting regularly frees memory and finishes updates. Shutting down with Fast Startup enabled does not count.",
            });

        if (junk > 1L << 30)
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Info,
                Title = $"{Format.Bytes(junk)} of junk files",
                Detail = "Temporary files, the update cache and error reports.",
                FixTaskIds = ["clean-temp-user", "clean-temp-windows", "clean-wu-cache", "clean-error-reports", "clean-thumbnails"],
                FixLabel = "Clean up",
            });

        if (system.RamTotal > 0 && system.RamFree * 100.0 / system.RamTotal < 10)
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Info,
                Title = "Memory almost full",
                Detail = $"{Format.Bytes(system.RamFree)} free of {Format.Bytes(system.RamTotal)}. Review your startup programs.",
                NavigateTo = "startup", FixLabel = "Startup programs",
            });

        var pendingTweaks = TweakCatalog.All.Count(t => t.Recommended && !t.SafeIsApplied());
        if (pendingTweaks > 0)
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Info,
                Title = $"{pendingTweaks} recommended tweaks not applied",
                Detail = "Privacy, performance and usability settings. Review them under Tweaks.",
                NavigateTo = "tweaks", FixLabel = "Review",
            });

        return result;
    }

    private static long EstimateJunk()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var local = InteractiveUser.LocalAppData;
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return FileCleaner.DirectorySize(InteractiveUser.TempPath)
               + FileCleaner.DirectorySize(Path.Combine(win, "Temp"))
               + FileCleaner.DirectorySize(Path.Combine(win, "SoftwareDistribution", "Download"))
               + FileCleaner.DirectorySize(Path.Combine(programData, "Microsoft", "Windows", "WER"))
               + FileCleaner.DirectorySize(Path.Combine(local, "Microsoft", "Windows", "WER"))
               + FileCleaner.RecycleBinSize();
    }
}
