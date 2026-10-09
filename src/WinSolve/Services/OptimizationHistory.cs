using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed class SystemSnapshot
{
    public DateTime Time { get; set; } = DateTime.Now;
    public long SystemDriveFree { get; set; }
    public long RamUsed { get; set; }
    public int StartupApps { get; set; }
    public int Processes { get; set; }
    public int RunningServices { get; set; }
    public int TweaksOn { get; set; }

    /// <summary>Duration of the most recent boot (seconds), from the Diagnostics-Performance log.</summary>
    public double? BootSeconds { get; set; }
}

public sealed class OptimizationRun
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Profile { get; set; } = "";
    public SystemSnapshot Before { get; set; } = new();
    public SystemSnapshot? After { get; set; }
    public long FreedBytes { get; set; }

    /// <summary>First boot measured after this run (filled in on a later start).</summary>
    public double? BootSecondsAfter { get; set; }

    /// <summary>Scheduled background cleanup (not shown as "last optimization").</summary>
    public bool IsMaintenance { get; set; }
}

/// <summary>Before/after measurements of each optimization, kept in %ProgramData%\WinSolve\history.json.</summary>
public static class OptimizationHistory
{
    private static string FilePath => Path.Combine(SafePath.DataFolder, "history.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<OptimizationRun> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<OptimizationRun>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not read optimization history: {ex.Message}");
        }
        return [];
    }

    public static void Save(List<OptimizationRun> runs)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(runs.TakeLast(20).ToList(), Json));
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not save optimization history: {ex.Message}");
        }
    }

    public static void Add(OptimizationRun run)
    {
        var runs = Load();
        runs.Add(run);
        Save(runs);
    }

    /// <summary>Latest run, with the post-optimization boot time filled in once the PC has restarted.</summary>
    public static OptimizationRun? Latest()
    {
        var runs = Load();
        var last = runs.LastOrDefault(r => !r.IsMaintenance);
        // boot.Time is when that boot started (not when Windows logged it, minutes later), so a
        // boot that began before the optimization is never credited to it.
        if (last is { BootSecondsAfter: null } && LastBoot() is { } boot && boot.Time > last.Time)
        {
            last.BootSecondsAfter = boot.Seconds;
            Save(runs);
        }
        return last;
    }

    public static async Task<SystemSnapshot> TakeAsync()
    {
        var snap = await Task.Run(() =>
        {
            var s = new SystemSnapshot();
            var sysRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
            try { s.SystemDriveFree = new DriveInfo(sysRoot).AvailableFreeSpace; } catch { }
            foreach (var os in Wmi.Query("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                s.RamUsed = (os.Get<long>("TotalVisibleMemorySize") - os.Get<long>("FreePhysicalMemory")) * 1024;
            s.Processes = Process.GetProcesses().Length;
            s.RunningServices = Wmi.Query("SELECT Name FROM Win32_Service WHERE State = 'Running'").Count;
            s.TweaksOn = TweakCatalog.All.Count(t => t.SafeIsApplied());
            s.BootSeconds = LastBoot()?.Seconds;
            return s;
        });
        snap.StartupApps = (await StartupService.GetItemsAsync()).Count(i => i.Enabled);
        return snap;
    }

    /// <summary>Most recent boot duration (event 100 of Diagnostics-Performance).</summary>
    public static (DateTime Time, double Seconds)? LastBoot()
    {
        try
        {
            var query = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName,
                "*[System[(EventID=100)]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            using var rec = reader.ReadEvent();
            if (rec is null) return null;
            var xml = rec.ToXml();
            var m = Regex.Match(xml, @"<Data Name='BootTime'>(\d+)</Data>");
            if (!m.Success) return null;
            var started = Regex.Match(xml, @"<Data Name='BootStartTime'>([^<]+)</Data>");
            var time = started.Success && DateTime.TryParse(started.Groups[1].Value, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
                ? t.ToLocalTime()
                : rec.TimeCreated ?? DateTime.MinValue;
            return (time, double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) / 1000.0);
        }
        catch
        {
            return null;
        }
    }
}
