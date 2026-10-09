using WinSolve.Core;

namespace WinSolve.Services;

public sealed record OptimizeSummary(int TasksOk, int TasksFailed, int TweaksApplied, long FreedBytes, bool RebootRecommended, bool ExplorerRestarted);

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
            : TweakCatalog.All.Where(t => ids.Contains(t.Id));
    }

    public static async Task<OptimizeSummary> RunAsync(Action<string> log, Action<int, int> progress, CancellationToken ct)
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

        if (AppSettings.Current.CreateRestorePoint)
        {
            await TaskCatalog.CreateRestorePoint(ctx, "WinSolve - before optimization");
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

        if (explorer)
        {
            await TaskCatalog.ExplorerRestart(ctx);
        }

        log("Measuring the system after optimizing...");
        run.After = await OptimizationHistory.TakeAsync();
        run.FreedBytes = ctx.FreedBytes;
        OptimizationHistory.Add(run);

        return new OptimizeSummary(ok, failed, applied, ctx.FreedBytes, ctx.RebootRecommended, explorer);
    }
}
