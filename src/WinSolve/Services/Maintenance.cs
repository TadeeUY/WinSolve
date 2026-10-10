using System.Security;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>
/// Scheduled background maintenance: a Task Scheduler task starts "WinSolve.exe --maintenance",
/// which runs a light, safe cleanup without showing any window and records the result.
/// </summary>
public static class Maintenance
{
    private const string TaskName = "WinSolve Maintenance";

    public static readonly string[] Schedules = ["Off", "Daily", "Weekly", "Monthly"];

    /// <summary>Safe tasks only: nothing here changes settings or needs a restart.</summary>
    public static readonly string[] TaskIds =
    [
        // Not the Windows Update cache: unattended at night it could delete updates that are
        // downloaded but not yet installed, while Windows' own maintenance is using them.
        "clean-temp-user", "clean-temp-windows", "clean-error-reports",
        "clean-delivery-opt", "clean-thumbnails", "net-flush-dns", "sec-defender-update",
    ];

    public static async Task<bool> ApplyScheduleAsync(string schedule)
    {
        if (schedule == "Off")
        {
            await ProcessRunner.RunAsync("schtasks.exe", $"/delete /f /tn \"{TaskName}\"");
            Logger.Write("Scheduled maintenance turned off.");
            return true;
        }

        // Same rule as Start with Windows: the task runs elevated without a prompt.
        if (!AutoStart.IsAllowed)
        {
            Logger.Write("Scheduled maintenance refused: WinSolve is not installed in Program Files.");
            return false;
        }

        var exe = SecurityElement.Escape(Environment.ProcessPath ?? Application.ExecutablePath);
        var start = DateTime.Today.AddDays(1).AddHours(3).ToString("s");
        var trigger = schedule switch
        {
            "Daily" => "<ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>",
            "Weekly" => "<ScheduleByWeek><WeeksInterval>1</WeeksInterval><DaysOfWeek><Sunday /></DaysOfWeek></ScheduleByWeek>",
            _ => "<ScheduleByMonth><DaysOfMonth><Day>1</Day></DaysOfMonth><Months><January /><February /><March /><April /><May /><June /><July /><August /><September /><October /><November /><December /></Months></ScheduleByMonth>",
        };
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>WinSolve automatic cleanup</Description></RegistrationInfo>
              <Triggers>
                <CalendarTrigger><StartBoundary>{start}</StartBoundary><Enabled>true</Enabled>{trigger}</CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <StartWhenAvailable>true</StartWhenAvailable>
                <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{exe}</Command><Arguments>--maintenance</Arguments></Exec>
              </Actions>
            </Task>
            """;

        var file = Path.Combine(SafePath.CreateAdminOnlyFolder("Tasks"), "maintenance.xml");
        await File.WriteAllTextAsync(file, xml, System.Text.Encoding.Unicode);
        var r = await ProcessRunner.RunAsync("schtasks.exe", $"/create /f /tn \"{TaskName}\" /xml \"{file}\"");
        try { Directory.Delete(Path.GetDirectoryName(file)!, true); } catch { }
        Logger.Write($"Scheduled maintenance set to {schedule}: {r.Output.Trim()}");
        if (r.Success)
        {
            AppSettings.Current.MaintenanceTaskFormat = CurrentTaskFormat;
            AppSettings.Current.Save();
        }
        return r.Success;
    }

    // 2: runs as SYSTEM. Before, the task ran as whoever turned it on and only while that account
    // was signed in, so it never ran when WinSolve had been elevated with another admin account.
    private const int CurrentTaskFormat = 2;

    /// <summary>Re-creates a task made by an older version in the current format.</summary>
    public static async Task UpgradeTaskAsync()
    {
        var s = AppSettings.Current;
        if (Edition.IsPortable || s.MaintenanceSchedule == "Off" || s.MaintenanceTaskFormat >= CurrentTaskFormat || !Admin.IsElevated) return;
        if (!await ApplyScheduleAsync(s.MaintenanceSchedule))
            Logger.Write("Could not update the scheduled maintenance task.");
    }

    /// <summary>Entry point for "--maintenance": runs headless and writes everything to the log.</summary>
    public static async Task RunAsync()
    {
        Logger.Write("Scheduled maintenance started.");
        var ctx = new TaskContext(line => Logger.Write("[maintenance] " + line), CancellationToken.None);
        var run = new OptimizationRun { Profile = "Scheduled maintenance", IsMaintenance = true, Before = await OptimizationHistory.TakeAsync() };
        foreach (var id in TaskIds)
        {
            if (TaskCatalog.Find(id) is not { } task) continue;
            try { await task.Run(ctx); }
            catch (Exception ex) { Logger.Write($"[maintenance] {task.Title} failed: {ex.Message}"); }
        }
        run.After = await OptimizationHistory.TakeAsync();
        run.FreedBytes = ctx.FreedBytes;
        OptimizationHistory.Add(run);
        Logger.Write($"Scheduled maintenance finished. Freed {Format.Bytes(ctx.FreedBytes)}.");
    }
}
