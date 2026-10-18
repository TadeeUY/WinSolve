using System.Text.Json;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed class ActivityEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    /// <summary>"cleaned" (Bytes freed), "alert" (Detail = severity) or "maintenance".</summary>
    public string Kind { get; set; } = "";
    public long Bytes { get; set; }
    public string Detail { get; set; } = "";
}

public sealed record WeeklySummary(DateTime From, DateTime To, int Alerts, int CriticalAlerts, long BytesFreed, int Cleanups, int MaintenanceRuns);

/// <summary>
/// What WinSolve did and saw, for the weekly summary on Home. Kept 90 days in
/// %ProgramData%\WinSolve\activity.json (administrators-only), no file names.
/// </summary>
public static class ActivityLog
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(DataLocation.UserFolder, "activity.json");

    public static void Cleaned(long bytes, string source)
    {
        if (bytes > 0) Add(new ActivityEntry { Kind = "cleaned", Bytes = bytes, Detail = source });
    }

    public static void Alert(IssueSeverity severity) => Add(new ActivityEntry { Kind = "alert", Detail = severity.ToString() });

    public static void Maintenance(long bytes) => Add(new ActivityEntry { Kind = "maintenance", Bytes = bytes });

    private static void Add(ActivityEntry entry)
    {
        try
        {
            lock (Gate)
            {
                var list = Load();
                list.Add(entry);
                list.RemoveAll(e => e.Time < DateTime.Now.AddDays(-90));
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(list));
                File.Move(tmp, FilePath, overwrite: true);
            }
        }
        catch (Exception ex) { Logger.Write($"Could not record activity: {ex.Message}"); }
    }

    private static List<ActivityEntry> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<ActivityEntry>>(File.ReadAllText(FilePath)) ?? [] : []; }
        catch { return []; }
    }

    public static WeeklySummary Summary(DateTime from, DateTime to)
    {
        List<ActivityEntry> week;
        lock (Gate) week = Load().Where(e => e.Time >= from && e.Time < to).ToList();
        return Summarize(week, from, to);
    }

    internal static WeeklySummary Summarize(List<ActivityEntry> week, DateTime from, DateTime to) => new(
        from, to,
        Alerts: week.Count(e => e.Kind == "alert"),
        CriticalAlerts: week.Count(e => e.Kind == "alert" && e.Detail == nameof(IssueSeverity.Critical)),
        BytesFreed: week.Where(e => e.Kind is "cleaned" or "maintenance").Sum(e => e.Bytes),
        Cleanups: week.Count(e => e.Kind == "cleaned"),
        MaintenanceRuns: week.Count(e => e.Kind == "maintenance"));

    /// <summary>
    /// When the next weekly summary is due: every 7 days counted from the day WinSolve was
    /// installed (first started), never more than once per week.
    /// </summary>
    internal static DateTime NextDue(DateTime installed, DateTime? lastShown)
    {
        var due = installed.Date.AddDays(7);
        if (lastShown is { } last)
            while (due <= last) due = due.AddDays(7);
        return due;
    }
}
