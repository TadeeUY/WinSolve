using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

public class ActivityLogTests
{
    [Fact]
    public void First_summary_is_a_week_after_install()
    {
        var installed = new DateTime(2026, 10, 1, 15, 30, 0);
        Assert.Equal(new DateTime(2026, 10, 8), ActivityLog.NextDue(installed, null));
    }

    [Fact]
    public void Next_summary_follows_the_install_weekday()
    {
        var installed = new DateTime(2026, 10, 1);
        // Seen on day 10 (a few days late): the next one is still on the install weekday.
        Assert.Equal(new DateTime(2026, 10, 15), ActivityLog.NextDue(installed, new DateTime(2026, 10, 10)));
        Assert.Equal(new DateTime(2026, 10, 22), ActivityLog.NextDue(installed, new DateTime(2026, 10, 15, 9, 0, 0)));
    }

    [Fact]
    public void Summary_counts_alerts_and_freed_space()
    {
        var t = new DateTime(2026, 10, 5);
        var s = ActivityLog.Summarize(
        [
            new ActivityEntry { Time = t, Kind = "alert", Detail = "Critical" },
            new ActivityEntry { Time = t, Kind = "alert", Detail = "Warning" },
            new ActivityEntry { Time = t, Kind = "cleaned", Bytes = 1000 },
            new ActivityEntry { Time = t, Kind = "maintenance", Bytes = 500 },
        ], t.AddDays(-7), t.AddDays(1));
        Assert.Equal(2, s.Alerts);
        Assert.Equal(1, s.CriticalAlerts);
        Assert.Equal(1500, s.BytesFreed);
        Assert.Equal(1, s.Cleanups);
        Assert.Equal(1, s.MaintenanceRuns);
    }
}
