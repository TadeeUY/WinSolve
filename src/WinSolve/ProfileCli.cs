using System.Runtime.InteropServices;
using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve;

/// <summary>
/// <c>WinSolve.exe --apply-profile &lt;file&gt; [--silent] [--dry-run] [--accept-warnings] [--no-restore-point]</c>:
/// applies a profile without opening the app. Output goes to the console it was started from
/// (use an administrator console and <c>start /wait</c> in cmd) and everything is written to the log.
/// </summary>
internal static class ProfileCli
{
    public const int Ok = 0, SomeFailed = 1, InvalidProfile = 2, AlreadyRunning = 3, Cancelled = 4, WarningsNotAccepted = 5;

    public sealed record Options(string? Path, bool Silent, bool DryRun, bool AcceptWarnings, bool NoRestorePoint, bool Help);

    public static bool IsProfileCommand(string[] args)
        => args.Any(a => a.Equals("--apply-profile", StringComparison.OrdinalIgnoreCase));

    internal static Options Parse(string[] args)
    {
        bool Has(params string[] names) => args.Any(a => names.Any(n => a.Equals(n, StringComparison.OrdinalIgnoreCase)));
        var i = Array.FindIndex(args, a => a.Equals("--apply-profile", StringComparison.OrdinalIgnoreCase));
        var path = i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
        return new Options(path, Has("--silent", "/silent", "-s"), Has("--dry-run"), Has("--accept-warnings"), Has("--no-restore-point"), Has("--help", "-h", "/?"));
    }

    private const string Usage = """
        Usage: WinSolve.exe --apply-profile <file.winsolve> [options]
          --silent            Don't ask for confirmation (for scripts).
          --dry-run           Show what the profile would change and change nothing.
          --accept-warnings   Apply tweaks that have a warning (they are refused otherwise).
          --no-restore-point  Don't create a System Restore point first.
        Exit codes: 0 done, 1 some steps failed, 2 invalid profile, 3 WinSolve is already running,
                    4 cancelled, 5 warnings not accepted.
        """;

    public static int Run(string[] args)
    {
        AttachToConsole();
        var o = Parse(args);
        void Out(string line)
        {
            Console.WriteLine(Loc.T(line));
        }
        if (o.Help || o.Path is null)
        {
            Out(Usage);
            return o.Help ? Ok : InvalidProfile;
        }

        // The app (or another profile) must not change the same settings at the same time.
        using var mutex = new Mutex(true, @"Local\WinSolve.SingleInstance", out var isNew);
        if (!isNew)
        {
            Out("WinSolve is already running. Close it (also from the notification area) and try again.");
            return AlreadyRunning;
        }

        Logger.Write($"Profile command: {string.Join(' ', args)}");
        ProfilePlan plan;
        try
        {
            var profile = ProfileService.Load(Path.GetFullPath(o.Path));
            plan = ProfileService.Check(profile, TweakCatalog.All, TaskCatalog.All);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            Out(ex.Message);
            Logger.Write("[profile] " + ex.Message);
            return InvalidProfile;
        }

        Out($"Profile: {plan.Profile.Name ?? Path.GetFileName(o.Path)}");
        foreach (var issue in plan.Issues) Out((issue.IsError ? "ERROR: " : "Note: ") + issue.Message);
        if (plan.HasErrors)
        {
            Out("Nothing was changed.");
            Logger.Write("[profile] Not applied: " + string.Join(" / ", plan.Issues.Where(i => i.IsError).Select(i => i.Message)));
            return InvalidProfile;
        }
        foreach (var line in ProfileService.Describe(plan)) Out(line);
        if (plan.Warnings.Count > 0)
        {
            Out("Warnings:");
            foreach (var w in plan.Warnings) Out("  - " + w);
        }
        if (o.DryRun)
        {
            Out("Dry run: nothing was changed.");
            return Ok;
        }
        if (plan.Warnings.Count > 0 && !o.AcceptWarnings)
        {
            Out("Some tweaks have warnings. Run again with --accept-warnings to apply them. Nothing was changed.");
            return WarningsNotAccepted;
        }
        if (!o.Silent)
        {
            var text = Loc.T("Apply this profile?") + "\n\n" + string.Join("\n", ProfileService.Describe(plan).Where(l => !l.StartsWith(' ')).Take(25).Select(Loc.T));
            if (Loc.Show(null, text, "WinSolve", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                Out("Cancelled. Nothing was changed.");
                return Cancelled;
            }
        }

        try
        {
            var result = ProfileService.ApplyAsync(plan, createRestorePoint: !o.NoRestorePoint, warningsAccepted: o.AcceptWarnings, Out, CancellationToken.None)
                .GetAwaiter().GetResult();
            return result.Failed > 0 ? SomeFailed : Ok;
        }
        catch (Exception ex)
        {
            Out("ERROR: " + ex.Message);
            Logger.Write($"[profile] {ex}");
            return SomeFailed;
        }
    }

    // WinSolve is a Windows app, so it has no console of its own: write to the one it was started
    // from, unless the output is already redirected (to a file or a pipe).
    private static void AttachToConsole()
    {
        try
        {
            if (Console.IsOutputRedirected) return;
            if (AttachConsole(-1 /* ATTACH_PARENT_PROCESS */))
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.WriteLine();
            }
        }
        catch { /* no console: the log still has everything */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
