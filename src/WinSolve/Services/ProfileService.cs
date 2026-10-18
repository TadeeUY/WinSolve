using System.Text.Json;
using System.Text.Json.Serialization;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>
/// A WinSolve profile (.winsolve, JSON): the tweaks to apply or undo, a DNS preset and the
/// one-click optimization settings, so a technician can set up many PCs the same way, from the
/// app or with <c>WinSolve.exe --apply-profile file.winsolve</c>.
/// </summary>
public sealed class WinSolveProfile
{
    public const string FormatName = "winsolve-profile";
    public const int CurrentVersion = 1;
    public const string Extension = ".winsolve";

    /// <summary>Must be "winsolve-profile" in the file (filled in when saving).</summary>
    public string? Format { get; set; }

    /// <summary>Format version; must be in the file (filled in when saving).</summary>
    public int Version { get; set; }
    public string? Name { get; set; }
    public string? CreatedWith { get; set; }
    public DateTime? Created { get; set; }
    public ProfileTweaks? Tweaks { get; set; }
    public ProfileDns? Dns { get; set; }
    public ProfileOptimization? Optimization { get; set; }
}

public sealed class ProfileTweaks
{
    /// <summary>Tweak ids to turn on.</summary>
    public List<string> Apply { get; set; } = [];

    /// <summary>Tweak ids to undo.</summary>
    public List<string> Revert { get; set; } = [];
}

public sealed class ProfileDns
{
    /// <summary>A DNS preset name ("Cloudflare", "Google"...), or "Default (DHCP)" to put back the previous servers.</summary>
    public string Preset { get; set; } = "";
}

public sealed class ProfileOptimization
{
    /// <summary>"PC" or "Laptop"; null = detect.</summary>
    public string? Device { get; set; }

    /// <summary>"Light", "Balanced" or "Maximum".</summary>
    public string? Level { get; set; }

    public bool? CreateRestorePoint { get; set; }

    /// <summary>Use the custom lists below instead of the automatic plan.</summary>
    public bool? UseCustomList { get; set; }
    public List<string>? Tasks { get; set; }
    public List<string>? Tweaks { get; set; }

    /// <summary>Run the one-click optimization after applying the profile.</summary>
    public bool Run { get; set; }
}

public sealed record ProfileIssue(bool IsError, string Message);

/// <summary>A profile checked against this PC and WinSolve version, ready to show or apply.</summary>
public sealed class ProfilePlan
{
    public required WinSolveProfile Profile { get; init; }
    public List<ProfileIssue> Issues { get; } = [];
    public List<Tweak> Apply { get; } = [];
    public List<Tweak> Revert { get; } = [];
    public DnsProvider? Dns { get; set; }
    public DeviceKind? Device { get; set; }
    public OptimizationLevel? Level { get; set; }
    public List<string>? Tasks { get; set; }
    public List<string>? OneClickTweaks { get; set; }

    public bool HasErrors => Issues.Any(i => i.IsError);

    /// <summary>Tweaks and tasks whose warning has to be accepted (--accept-warnings or the confirmation in the app).</summary>
    public List<string> Warnings { get; } = [];
}

public sealed record ProfileResult(int Ok, int Failed, int Skipped);

public static class ProfileService
{
    /// <summary>A profile is a small text file; anything bigger is not one.</summary>
    public const long MaxFileBytes = 1 << 20;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Serialize(WinSolveProfile profile)
    {
        profile.Format = WinSolveProfile.FormatName;
        profile.Version = WinSolveProfile.CurrentVersion;
        return JsonSerializer.Serialize(profile, Json);
    }

    /// <summary>Reads a profile; throws <see cref="InvalidDataException"/> with a readable reason.</summary>
    public static WinSolveProfile Parse(string json)
    {
        WinSolveProfile? profile;
        try { profile = JsonSerializer.Deserialize<WinSolveProfile>(json, Json); }
        catch (JsonException ex) { throw new InvalidDataException($"The file is not a valid WinSolve profile (JSON error at line {(ex.LineNumber ?? 0) + 1})."); }
        if (profile is null || !string.Equals(profile.Format, WinSolveProfile.FormatName, StringComparison.Ordinal) || profile.Version < 1)
            throw new InvalidDataException("The file is not a WinSolve profile.");
        if (profile.Version > WinSolveProfile.CurrentVersion)
            throw new InvalidDataException($"This profile was made by a newer WinSolve (format {profile.Version}). Update WinSolve to use it.");
        return profile;
    }

    public static WinSolveProfile Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"The profile {path} doesn't exist.");
        if (info.Length > MaxFileBytes) throw new InvalidDataException("The file is too big to be a WinSolve profile.");
        return Parse(File.ReadAllText(path));
    }

    /// <summary>The current setup of this PC as a profile: tweaks that are on, the DNS preset and the optimization settings.</summary>
    public static async Task<WinSolveProfile> ExportAsync(string? name = null)
    {
        var s = AppSettings.Current;
        var dns = await DnsService.CurrentAsync();
        return new WinSolveProfile
        {
            Name = name,
            CreatedWith = UpdateService.CurrentVersion.ToString(3),
            Created = DateTime.Now,
            Tweaks = new ProfileTweaks
            {
                Apply = [.. TweakCatalog.All.Where(t => !t.IsAction && t.SafeIsAvailable() && t.SafeIsApplied()).Select(t => t.Id)],
            },
            Dns = DnsService.Providers.Any(p => p.Name == dns) ? new ProfileDns { Preset = dns } : null,
            Optimization = new ProfileOptimization
            {
                Device = s.Device?.ToString(),
                Level = s.Level.ToString(),
                CreateRestorePoint = s.CreateRestorePoint,
                UseCustomList = s.UseCustomOneClick,
                Tasks = s.OneClickTasks,
                Tweaks = s.OneClickTweaks,
            },
        };
    }

    /// <summary>
    /// Checks a profile against this PC: unknown ids and presets are errors (nothing is applied),
    /// tweaks that don't exist on this Windows are skipped, and risky ones are listed in
    /// <see cref="ProfilePlan.Warnings"/>.
    /// </summary>
    public static ProfilePlan Check(WinSolveProfile profile, IReadOnlyList<Tweak> tweaks, IReadOnlyList<SystemTask> tasks)
    {
        var plan = new ProfilePlan { Profile = profile };
        Tweak? Find(string id) => tweaks.FirstOrDefault(t => t.Id == id);

        void AddTweaks(List<string>? ids, List<Tweak> target, bool applying)
        {
            foreach (var id in (ids ?? []).Distinct(StringComparer.Ordinal))
            {
                if (Find(id) is not { } t) { plan.Issues.Add(new(true, $"Unknown tweak: {id}")); continue; }
                if (t.IsAction && !applying) { plan.Issues.Add(new(true, $"{id} is a one-off action and can't be undone.")); continue; }
                if (!t.SafeIsAvailable()) { plan.Issues.Add(new(false, $"Skipped, not available on this PC: {id}")); continue; }
                if (applying && t.Warning is not null) plan.Warnings.Add($"{t.Title}: {t.Warning}");
                if (applying && t.TouchesPolicies && ManagedPc.IsManaged) plan.Warnings.Add($"{t.Title}: this PC is managed by an organization, whose policies may override or conflict with it.");
                target.Add(t);
            }
        }
        AddTweaks(profile.Tweaks?.Apply, plan.Apply, applying: true);
        AddTweaks(profile.Tweaks?.Revert, plan.Revert, applying: false);
        foreach (var both in plan.Apply.Intersect(plan.Revert).ToList())
            plan.Issues.Add(new(true, $"{both.Id} is both applied and undone."));

        if (profile.Dns is { } dns && !string.IsNullOrWhiteSpace(dns.Preset))
        {
            plan.Dns = DnsService.Providers.FirstOrDefault(p => p.Name.Equals(dns.Preset.Trim(), StringComparison.OrdinalIgnoreCase));
            if (plan.Dns is null)
                plan.Issues.Add(new(true, $"Unknown DNS preset: {dns.Preset}. Use one of: {string.Join(", ", DnsService.Providers.Select(p => p.Name))}"));
        }

        if (profile.Optimization is { } o)
        {
            if (o.Device is not null)
            {
                if (Enum.TryParse<DeviceKind>(o.Device, ignoreCase: true, out var d)) plan.Device = d;
                else plan.Issues.Add(new(true, $"Unknown device type: {o.Device} (use PC or Laptop)."));
            }
            if (o.Level is not null)
            {
                if (Enum.TryParse<OptimizationLevel>(o.Level, ignoreCase: true, out var l) && Enum.IsDefined(l)) plan.Level = l;
                else plan.Issues.Add(new(true, $"Unknown optimization level: {o.Level} (use Light, Balanced or Maximum)."));
            }
            if (o.Tasks is not null)
            {
                foreach (var id in o.Tasks.Where(id => tasks.All(t => t.Id != id)))
                    plan.Issues.Add(new(true, $"Unknown task: {id}"));
                plan.Tasks = [.. o.Tasks.Distinct(StringComparer.Ordinal)];
            }
            if (o.Tweaks is not null)
            {
                foreach (var id in o.Tweaks.Where(id => tweaks.All(t => t.Id != id)))
                    plan.Issues.Add(new(true, $"Unknown tweak: {id}"));
                plan.OneClickTweaks = [.. o.Tweaks.Distinct(StringComparer.Ordinal)];
            }
        }

        if (plan.Apply.Count + plan.Revert.Count == 0 && plan.Dns is null && profile.Optimization is null)
            plan.Issues.Add(new(true, "The profile doesn't change anything."));
        return plan;
    }

    /// <summary>Everything the plan will do, one line each (the dry run, and the confirmation in the app).</summary>
    public static List<string> Describe(ProfilePlan plan)
    {
        var lines = new List<string>();
        foreach (var t in plan.Apply)
        {
            lines.Add($"Apply: {t.Title}" + (t.SafeIsApplied() ? " (already on)" : ""));
            lines.AddRange(t.Changes.Select(c => $"    {c.Target} -> {c.NewValue}"));
        }
        foreach (var t in plan.Revert)
            lines.Add($"Undo: {t.Title}" + (t.SafeCanUndo() ? "" : " (not applied, nothing to undo)"));
        if (plan.Dns is { } dns)
            lines.Add(dns.IsDefault ? "DNS: back to the previous servers / DHCP" : $"DNS servers: {dns.Name} ({string.Join(", ", dns.IPv4)})");
        if (plan.Profile.Optimization is { } o)
        {
            if (plan.Device is { } d) lines.Add($"One-click device type: {d}");
            if (plan.Level is { } l) lines.Add($"One-click level: {l}");
            if (o.CreateRestorePoint is { } rp) lines.Add($"Create a restore point before changes: {(rp ? "yes" : "no")}");
            if (o.UseCustomList is { } custom) lines.Add($"One-click uses the custom list: {(custom ? "yes" : "no")}");
            if (plan.Tasks is { } tasks) lines.Add($"Custom list tasks: {string.Join(", ", tasks)}");
            if (plan.OneClickTweaks is { } tw) lines.Add($"Custom list tweaks: {string.Join(", ", tw)}");
            if (o.Run) lines.Add("Then run the one-click optimization");
        }
        return lines;
    }

    /// <summary>
    /// Applies a checked plan: a restore point first (unless turned off), then the tweaks, the DNS
    /// preset and the optimization settings, and optionally the one-click optimization. Every step
    /// is logged; tweaks and DNS keep their usual backups, so each change can be undone in the app.
    /// </summary>
    public static async Task<ProfileResult> ApplyAsync(ProfilePlan plan, bool createRestorePoint, bool warningsAccepted, Action<string> log, CancellationToken ct)
    {
        if (plan.HasErrors) throw new InvalidOperationException("The profile has errors; nothing was applied.");
        if (plan.Warnings.Count > 0 && !warningsAccepted) throw new InvalidOperationException("The profile has tweaks with warnings that weren't accepted; nothing was applied.");
        void Log(string message)
        {
            Logger.Write("[profile] " + message);
            log(message);
        }
        int ok = 0, failed = 0, skipped = 0;
        var ctx = new TaskContext(Log, ct);
        var name = plan.Profile.Name ?? "unnamed";
        Log($"Applying profile '{name}'.");

        if (createRestorePoint && (plan.Apply.Count > 0 || plan.Revert.Count > 0 || plan.Dns is not null))
        {
            var point = await TaskCatalog.CreateRestorePointAsync(ctx, $"WinSolve - profile {name}");
            Log(point.Created ? "Restore point created." : "Could not create a restore point; continuing (each change keeps its own backup).");
        }

        foreach (var t in plan.Apply)
        {
            ct.ThrowIfCancellationRequested();
            if (!t.IsAction && t.SafeIsApplied()) { skipped++; Log($"  Already on: {t.Title}"); continue; }
            try
            {
                t.Apply();
                ok++;
                Log($"  OK  {t.Title}");
                foreach (var c in t.Changes) Log($"      {c.Target} -> {c.NewValue}");
                ctx.RebootRecommended |= t.NeedsReboot;
            }
            catch (Exception ex) { failed++; Log($"  ERROR  {t.Title}: {ex.Message}"); }
        }
        foreach (var t in plan.Revert)
        {
            ct.ThrowIfCancellationRequested();
            if (!t.SafeCanUndo()) { skipped++; Log($"  Not applied, nothing to undo: {t.Title}"); continue; }
            try { t.Revert(); ok++; Log($"  Undone  {t.Title}"); }
            catch (Exception ex) { failed++; Log($"  ERROR  {t.Title}: {ex.Message}"); }
        }

        if (plan.Dns is { } dns)
        {
            try
            {
                var adapters = (await DnsService.GetAdaptersAsync(ct)).Where(a => !a.Domain && !a.NoIp).ToList();
                if (adapters.Count == 0) { skipped++; Log("  DNS: no connected adapter whose DNS can be changed."); }
                else if (dns.IsDefault)
                {
                    var backup = DnsService.LoadBackup();
                    await DnsService.RestoreAsync([.. adapters.Select(a => DnsService.PlanDefault(a, backup.GetValueOrDefault(a.Guid)))], Log, ct);
                    ok++;
                }
                else
                {
                    await DnsService.ApplyAsync(dns, adapters, Log, ct);
                    ok++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failed++; Log($"  ERROR  DNS: {ex.Message}"); }
        }

        if (plan.Profile.Optimization is { } o)
        {
            var s = AppSettings.Current;
            if (plan.Device is { } d) s.Device = d;
            if (plan.Level is { } l) s.Level = l;
            if (o.CreateRestorePoint is { } rp) s.CreateRestorePoint = rp;
            if (o.UseCustomList is { } custom) s.UseCustomOneClick = custom;
            if (plan.Tasks is { } tasks) s.OneClickTasks = tasks;
            if (plan.OneClickTweaks is { } tw) s.OneClickTweaks = tw;
            s.Save();
            Log("  One-click settings saved.");
            if (o.Run)
            {
                // The profile's own restore point already covers this run.
                var restore = s.CreateRestorePoint;
                s.CreateRestorePoint = s.CreateRestorePoint && !createRestorePoint;
                try
                {
                    var summary = await OneClickOptimizer.RunAsync(Log, (_, _) => { }, ct, _ => true);
                    ok += summary.TasksOk + summary.TweaksApplied;
                    failed += summary.TasksFailed;
                    ctx.RebootRecommended |= summary.RebootRecommended;
                }
                finally
                {
                    s.CreateRestorePoint = restore;
                    s.Save();
                }
            }
        }

        if (ctx.RebootRecommended) Log("Restart the PC to finish applying some changes.");
        Log($"Profile '{name}' finished: {ok} done, {failed} failed, {skipped} skipped.");
        return new ProfileResult(ok, failed, skipped);
    }
}
