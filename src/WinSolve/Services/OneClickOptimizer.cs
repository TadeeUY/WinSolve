using WinSolve.Core;

namespace WinSolve.Services;

/// <param name="ExplorerRestartSuggested">Some tweaks only fully show after Explorer restarts (the user is asked; it is never restarted automatically).</param>
/// <param name="ProtectionTurnedOnDrive">Drive on which System Protection had to be turned on for the restore point, or null.</param>
public sealed record OptimizeSummary(int TasksOk, int TasksFailed, int TweaksApplied, long FreedBytes, bool RebootRecommended,
    bool ExplorerRestartSuggested, string? ProtectionTurnedOnDrive = null);

/// <summary>One-click optimization: runs the tasks and tweaks of the selected profile.</summary>
public static class OneClickOptimizer
{
    public static DeviceKind CurrentDevice =>
        AppSettings.Current.Device ?? (HardwareProfile.Detect().IsLaptop ? DeviceKind.Laptop : DeviceKind.PC);

    /// <summary>Plan for the selected profile (desktop/laptop + level) and the detected hardware.</summary>
    public static OptimizationPlan CurrentPlan() =>
        OptimizationPlanner.Build(CurrentDevice, AppSettings.Current.Level, HardwareProfile.Detect());

    public static IEnumerable<SystemTask> SelectedTasks()
    {
        if (!AppSettings.Current.UseCustomOneClick) return CurrentPlan().Tasks;
        var ids = AppSettings.Current.OneClickTasks;
        return ids is null
            ? TaskCatalog.All.Where(t => t.Recommended)
            : TaskCatalog.All.Where(t => ids.Contains(t.Id));
    }

    public static IEnumerable<Tweak> SelectedTweaks()
    {
        if (!AppSettings.Current.UseCustomOneClick) return CurrentPlan().Tweaks;
        var ids = AppSettings.Current.OneClickTweaks;
        return ids is null
            ? TweakCatalog.All.Where(t => t.Recommended)
            : TweakCatalog.All.Where(t => ids.Contains(t.Id) && t.SafeIsAvailable());
    }

    /// <summary>
    /// Runs the optimization. When the restore point can't be created,
    /// <paramref name="continueWithoutRestorePoint"/> is asked (on the caller's side, e.g. the UI
    /// thread); answering no cancels the run before anything changes.
    /// </summary>
    public static async Task<OptimizeSummary> RunAsync(Action<string> log, Action<int, int> progress, CancellationToken ct,
        Func<string, bool>? continueWithoutRestorePoint = null)
    {
        log("Measuring the system before optimizing...");
        var run = new OptimizationRun
        {
            Profile = AppSettings.Current.UseCustomOneClick ? "Custom list" : $"{CurrentDevice}, {AppSettings.Current.Level}",
            Before = await OptimizationHistory.TakeAsync(),
        };

        var tasks = SelectedTasks().ToList();
        if (!AppSettings.Current.UseCustomOneClick)
        {
            log($"Profile: {CurrentDevice}, {AppSettings.Current.Level}. Hardware: {HardwareProfile.Detect().Describe()}");
            foreach (var reason in CurrentPlan().Reasons) log("  - " + reason);
        }
        var tweaks = SelectedTweaks().Where(t => !t.SafeIsApplied()).ToList();
        var ctx = new TaskContext(log, ct);
        int total = tasks.Count + (tweaks.Count > 0 ? 1 : 0) + (AppSettings.Current.CreateRestorePoint ? 1 : 0);
        int step = 0, ok = 0, failed = 0, applied = 0;
        bool explorer = false;
        string? protectionDrive = null;

        if (AppSettings.Current.CreateRestorePoint)
        {
            var point = await TaskCatalog.CreateRestorePointAsync(ctx, "WinSolve - before optimization");
            if (point.ProtectionTurnedOn) protectionDrive = point.Drive;
            if (!point.Created)
            {
                var proceed = continueWithoutRestorePoint?.Invoke("Could not create a restore point. Continue without it?") ?? true;
                if (!proceed)
                {
                    log("Stopped: nothing was changed because no restore point could be created.");
                    throw new OperationCanceledException();
                }
                log("Continuing without a restore point.");
            }
            progress(++step, total);
        }

        if (tweaks.Count > 0)
        {
            log($"Applying {tweaks.Count} tweaks...");
            foreach (var t in tweaks)
            {
                try
                {
                    t.Apply();
                    applied++;
                    log($"  OK  {t.Title}");
                    explorer |= t.NeedsExplorerRestart;
                    ctx.RebootRecommended |= t.NeedsReboot;
                }
                catch (Exception ex)
                {
                    failed++;
                    log($"  ERROR  {t.Title}: {ex.Message}");
                }
            }
            progress(++step, total);
        }

        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();
            log($"> {task.Title}");
            try
            {
                var errorsBefore = ctx.CommandErrors;
                await task.Run(ctx);
                if (ctx.CommandErrors > errorsBefore)
                {
                    failed++;
                    log($"  WARNING  {task.Title}: a command reported an error (see above).");
                }
                else ok++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                log($"  ERROR: {ex.Message}");
            }
            progress(++step, total);
        }

        // Never restart Explorer here (it closes open folder windows): tell it settings changed,
        // and the page offers a restart at the end.
        if (explorer) TaskCatalog.RefreshShell(ctx);

        log("Measuring the system after optimizing...");
        run.After = await OptimizationHistory.TakeAsync();
        run.FreedBytes = ctx.FreedBytes;
        ActivityLog.Cleaned(ctx.FreedBytes, "one-click");
        OptimizationHistory.Add(run);

        return new OptimizeSummary(ok, failed, applied, ctx.FreedBytes, ctx.RebootRecommended, explorer, protectionDrive);
    }
}
