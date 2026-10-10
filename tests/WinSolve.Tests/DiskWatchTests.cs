using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

public class DiskWatchTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0);

    private static DiskInfo Ssd(int wear, HealthLevel health = HealthLevel.Good, int? temp = 40) => new()
    {
        Model = "Test SSD", Serial = "1", MediaType = "SSD", BusType = "NVMe", WearPercent = wear, Health = health, TemperatureC = temp,
    };

    [Fact]
    public void First_reading_never_alerts_about_health_or_life()
    {
        var alerts = ErrorMonitor.DiskAlerts(Ssd(95, HealthLevel.Bad), null, Now);
        Assert.DoesNotContain(alerts, a => a.Kind is "disk-health" or "disk-life" or "disk-wear");
    }

    [Fact]
    public void Health_getting_worse_alerts()
    {
        var before = ErrorMonitor.NextBaseline(Ssd(10), null, Now.AddHours(-1));
        var alerts = ErrorMonitor.DiskAlerts(Ssd(10, HealthLevel.Caution), before, Now);
        Assert.Contains(alerts, a => a.Kind == "disk-health");
    }

    [Fact]
    public void Same_health_does_not_alert()
    {
        var before = ErrorMonitor.NextBaseline(Ssd(10, HealthLevel.Caution), null, Now.AddHours(-1));
        Assert.Empty(ErrorMonitor.DiskAlerts(Ssd(10, HealthLevel.Caution), before, Now));
    }

    [Fact]
    public void Crossing_twenty_percent_life_alerts_once()
    {
        var before = ErrorMonitor.NextBaseline(Ssd(79), null, Now.AddHours(-1)); // 21% left
        Assert.Contains(ErrorMonitor.DiskAlerts(Ssd(80), before, Now), a => a.Kind == "disk-life");
        var after = ErrorMonitor.NextBaseline(Ssd(80), before, Now);
        Assert.DoesNotContain(ErrorMonitor.DiskAlerts(Ssd(80), after, Now.AddHours(1)), a => a.Kind == "disk-life");
    }

    [Fact]
    public void Fast_wear_within_thirty_days_alerts()
    {
        var start = ErrorMonitor.NextBaseline(Ssd(10), null, Now.AddDays(-10));
        var mid = ErrorMonitor.NextBaseline(Ssd(12), start, Now.AddDays(-5));
        Assert.Equal(start.Since, mid.Since);
        Assert.Contains(ErrorMonitor.DiskAlerts(Ssd(15), mid, Now), a => a.Kind == "disk-wear");
    }

    [Fact]
    public void Wear_window_restarts_after_thirty_days()
    {
        var start = ErrorMonitor.NextBaseline(Ssd(10), null, Now.AddDays(-40));
        var next = ErrorMonitor.NextBaseline(Ssd(15), start, Now);
        Assert.Equal(Now, next.Since);
        Assert.Equal(85, next.LifeAtSince);
    }

    [Fact]
    public void Heat_uses_the_drive_limit_when_reported()
    {
        var hot = Ssd(10, temp: 72);
        Assert.Empty(ErrorMonitor.DiskAlerts(hot, null, Now)); // NVMe default limit is 75
        hot.TemperatureMaxC = 70;
        Assert.Contains(ErrorMonitor.DiskAlerts(hot, null, Now), a => a.Kind == "disk-heat");
        var hdd = new DiskInfo { Model = "HDD", MediaType = "HDD", BusType = "SATA", TemperatureC = 56 };
        Assert.Contains(ErrorMonitor.DiskAlerts(hdd, null, Now), a => a.Kind == "disk-heat");
    }
}
