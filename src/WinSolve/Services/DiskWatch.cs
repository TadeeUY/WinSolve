using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>What a drive looked like the last time it was checked.</summary>
public sealed class DiskBaseline
{
    public HealthLevel Health { get; set; }
    public int? LifeLeft { get; set; }

    /// <summary>Life left at <see cref="Since"/>, to notice a drive wearing out unusually fast.</summary>
    public int? LifeAtSince { get; set; }
    public DateTime Since { get; set; }
}

/// <summary>Alerts when a drive's health gets worse, its life runs low or it runs hot.</summary>
public sealed partial class ErrorMonitor
{
    private System.Threading.Timer? _diskTimer;
    private int _checkingDisks;
    private readonly Dictionary<string, DateTime> _lastHeatAlert = [];

    private async Task CheckDisksAsync()
    {
        if (Interlocked.Exchange(ref _checkingDisks, 1) == 1) return;
        try
        {
            var disks = await DiskHealthService.ScanAsync();
            var baselines = AppSettings.Current.DiskBaselines;
            foreach (var d in disks)
            {
                var key = $"{d.Model}|{d.Serial}";
                baselines.TryGetValue(key, out var before);
                foreach (var alert in DiskAlerts(d, before, DateTime.Now))
                {
                    // A hot drive stays hot for a while: remind at most every 6 hours.
                    if (alert.Kind == "disk-heat")
                    {
                        if (_lastHeatAlert.TryGetValue(key, out var last) && DateTime.Now - last < TimeSpan.FromHours(6)) continue;
                        _lastHeatAlert[key] = DateTime.Now;
                    }
                    Raise(alert);
                }
                baselines[key] = NextBaseline(d, before, DateTime.Now);
            }
            AppSettings.Current.Save();
        }
        catch (Exception ex)
        {
            Logger.Write($"Error checking drive health: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _checkingDisks, 0);
        }
    }

    internal static DiskBaseline NextBaseline(DiskInfo d, DiskBaseline? before, DateTime now)
    {
        var life = LifeLeft(d);
        // The "wearing fast" window restarts every 30 days.
        var restart = before is null || before.LifeAtSince is null || now - before.Since > TimeSpan.FromDays(30);
        return new DiskBaseline
        {
            Health = d.Health,
            LifeLeft = life,
            LifeAtSince = restart ? life : before!.LifeAtSince,
            Since = restart ? now : before!.Since,
        };
    }

    private static int? LifeLeft(DiskInfo d) => d.WearPercent is { } w ? Math.Clamp(100 - w, 0, 100) : null;

    /// <summary>Highest safe temperature: the drive's own limit when it reports one.</summary>
    internal static int TemperatureLimit(DiskInfo d)
    {
        if (d.TemperatureMaxC is { } max and >= 50 and <= 100) return max;
        if (d.MediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase)) return 55;
        return d.BusType.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? 75 : 70;
    }

    internal static List<Alert> DiskAlerts(DiskInfo d, DiskBaseline? before, DateTime now)
    {
        var alerts = new List<Alert>();
        var name = string.IsNullOrWhiteSpace(d.Model) ? "A drive" : d.Model;
        var life = LifeLeft(d);

        // Health got worse since the last check (not on the first reading: that's the Hardware page's job).
        if (before is not null && d.Health > before.Health && d.Health is HealthLevel.Caution or HealthLevel.Bad)
        {
            alerts.Add(new Alert
            {
                Kind = "disk-health", DedupeKey = d.Model + d.Serial,
                Severity = d.Health == HealthLevel.Bad ? IssueSeverity.Critical : IssueSeverity.Warning,
                Title = d.Health == HealthLevel.Bad ? $"{name} may fail soon" : $"{name} shows signs of wear",
                Detail = (d.Findings.FirstOrDefault() ?? "Its S.M.A.R.T. health got worse.") + "\nBack up your important files.",
                NavigateTo = "hardware",
            });
        }

        // Life left crossed 20% or 10%.
        if (life is { } l && before?.LifeLeft is { } prev)
        {
            foreach (var threshold in new[] { 10, 20 })
            {
                if (prev > threshold && l <= threshold)
                {
                    alerts.Add(new Alert
                    {
                        Kind = "disk-life", DedupeKey = d.Model + d.Serial + threshold,
                        Severity = threshold == 10 ? IssueSeverity.Critical : IssueSeverity.Warning,
                        Title = $"{name}: {l}% life left",
                        Detail = "The SSD is close to the end of its rated writes. Back up your files and plan to replace it.",
                        NavigateTo = "hardware",
                    });
                    break;
                }
            }
        }

        // Unusually fast wear: 5% or more of its life in under 30 days.
        if (life is { } now5 && before?.LifeAtSince is { } start && start - now5 >= 5 && now - before.Since <= TimeSpan.FromDays(30))
        {
            alerts.Add(new Alert
            {
                Kind = "disk-wear", DedupeKey = d.Model + d.Serial,
                Title = $"{name} is wearing out fast",
                Detail = $"It used {start - now5}% of its life in {Math.Max(1, (int)(now - before.Since).TotalDays)} days. Something may be writing to it constantly (a swap file, a download or a backup loop).",
                NavigateTo = "hardware",
            });
        }

        if (d.TemperatureC is { } t && t >= TemperatureLimit(d))
        {
            alerts.Add(new Alert
            {
                Kind = "disk-heat", DedupeKey = d.Model + d.Serial,
                Title = $"{name} is running hot ({t} °C)",
                Detail = "High temperatures shorten a drive's life and slow it down. Check the airflow around it, or add a heatsink to an NVMe SSD.",
                NavigateTo = "hardware",
            });
        }
        return alerts;
    }
}
