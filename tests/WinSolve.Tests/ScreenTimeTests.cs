using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

public class ScreenTimeTests
{
    // 2026-10-19 is a Monday.
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0);

    private static LockSchedule Night(params DayOfWeek[] days) => new()
    {
        Enabled = true, StartMinute = 23 * 60, EndMinute = 7 * 60, Days = days.Length > 0 ? [.. days] : [.. Enum.GetValues<DayOfWeek>()],
    };

    [Fact]
    public void Overnight_hours_cover_the_evening_and_the_next_morning()
    {
        var s = Night();
        Assert.False(s.IsActive(At(19, 22, 59)));
        Assert.True(s.IsActive(At(19, 23, 0)));
        Assert.True(s.IsActive(At(20, 3, 0)));
        Assert.True(s.IsActive(At(20, 6, 59)));
        Assert.False(s.IsActive(At(20, 7, 0)));
        Assert.Equal(At(20, 7), s.EndsAt(At(19, 23, 30)));
        Assert.Equal(At(20, 7), s.EndsAt(At(20, 2)));
    }

    [Fact]
    public void An_overnight_schedule_belongs_to_the_day_it_starts()
    {
        // Only on Friday night: Saturday morning is locked, Friday morning is not.
        var s = Night(DayOfWeek.Friday);
        Assert.True(s.IsActive(At(23, 23, 30)));   // Friday 23:30
        Assert.True(s.IsActive(At(24, 2)));        // Saturday 02:00
        Assert.False(s.IsActive(At(23, 2)));       // Friday 02:00 (Thursday night isn't on)
        Assert.False(s.IsActive(At(24, 23, 30)));  // Saturday 23:30
    }

    [Fact]
    public void Daytime_hours_and_disabled_or_empty_schedules()
    {
        var s = new LockSchedule { Enabled = true, StartMinute = 13 * 60, EndMinute = 14 * 60 + 30 };
        Assert.True(s.IsActive(At(19, 13)));
        Assert.True(s.IsActive(At(19, 14, 29)));
        Assert.False(s.IsActive(At(19, 14, 30)));
        Assert.False(s.IsActive(At(19, 12, 59)));
        Assert.False(new LockSchedule { Enabled = false }.IsActive(At(19, 23, 30)));
        Assert.False(new LockSchedule { Enabled = true, StartMinute = 600, EndMinute = 600 }.IsActive(At(19, 10)));
        Assert.False(new LockSchedule { Enabled = true, Days = [] }.IsActive(At(19, 23, 30)));
    }

    [Fact]
    public void Damaged_values_are_put_back_in_range()
    {
        var s = new LockSchedule { StartMinute = -5, EndMinute = 99999, RelockMinutes = 0, Days = null! };
        s.Normalize();
        Assert.Equal(0, s.StartMinute);
        Assert.Equal(24 * 60 - 1, s.EndMinute);
        Assert.Equal(1, s.RelockMinutes);
        Assert.Equal(7, s.Days.Count);
        s.Days = [DayOfWeek.Monday, DayOfWeek.Monday, (DayOfWeek)42];
        s.Normalize();
        Assert.Equal([DayOfWeek.Monday], s.Days);
    }

    [Fact]
    public void Usage_adds_up_per_day_with_the_lock_hours_part()
    {
        var data = new ScreenTimeData();
        data.Add(At(19, 22), 3600, lockHours: false);
        data.Add(At(19, 23, 30), 1800, lockHours: true);
        data.Add(At(20, 9), 600, lockHours: false);
        data.CountUnlock(At(19, 23, 40));
        data.CountUnlock(At(19, 23, 55));
        var days = data.LastDays(DateOnly.FromDateTime(At(20, 12)));
        Assert.Equal(7, days.Count);
        Assert.Equal(new DateOnly(2026, 10, 14), days[0].Date);
        Assert.Equal(TimeSpan.FromMinutes(90), days[5].Total);
        Assert.Equal(TimeSpan.FromMinutes(30), days[5].LockHours);
        Assert.Equal(2, days[5].Unlocks);
        Assert.Equal(TimeSpan.FromMinutes(10), days[6].Total);
        Assert.Equal(TimeSpan.Zero, days[0].Total);
    }

    [Fact]
    public void A_day_never_counts_more_than_24_hours()
    {
        var data = new ScreenTimeData();
        for (var i = 0; i < 30; i++) data.Add(At(19, 10), 3600, lockHours: true);
        var day = data.Find(new DateOnly(2026, 10, 19))!;
        Assert.Equal(24 * 3600, day.Seconds);
        Assert.Equal(24 * 3600, day.LockSeconds);
    }

    [Fact]
    public void Old_and_unreadable_days_are_dropped()
    {
        var data = new ScreenTimeData();
        data.Add(At(19, 10), 60, false);
        data.Days["2020-01-01"] = new DayUsage { Seconds = 5 };
        data.Days["garbage"] = new DayUsage { Seconds = 5 };
        data.Days["2027-01-01"] = new DayUsage { Seconds = 5 };
        data.Trim(new DateOnly(2026, 10, 19));
        Assert.Equal(["2026-10-19"], data.Days.Keys.ToArray());
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(59, "59m")]
    [InlineData(60, "1h 00m")]
    [InlineData(200, "3h 20m")]
    public void Durations_read_well(int minutes, string expected)
        => Assert.Equal(expected, ScreenTimeData.Format(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Lock_hours_warn_then_lock_and_lock_again_after_each_unlock()
    {
        var e = new LockEnforcer { Relock = TimeSpan.FromMinutes(10) };
        var t = At(19, 22, 59);
        Assert.Equal(LockEnforcer.Action.None, e.Tick(t, lockHours: false, unlocked: true));

        // The lock hours start while the PC is in use: a warning, then the lock a minute later.
        t = At(19, 23);
        Assert.Equal(LockEnforcer.Action.Warn, e.Tick(t, true, true));
        Assert.Equal(LockEnforcer.Action.None, e.Tick(t.AddSeconds(30), true, true));
        Assert.Equal(LockEnforcer.Action.Lock, e.Tick(t.AddMinutes(1), true, true));
        e.OnLocked();
        Assert.Equal(LockEnforcer.Action.None, e.Tick(t.AddMinutes(2), true, unlocked: false));

        // Unlocked at 23:05: it counts, warns at 23:14 and locks at 23:15.
        var unlock = At(19, 23, 5);
        Assert.True(e.OnUnlocked(unlock, lockHours: true));
        Assert.Equal(unlock.AddMinutes(10), e.LockAt);
        Assert.Equal(LockEnforcer.Action.None, e.Tick(unlock.AddMinutes(8), true, true));
        Assert.Equal(LockEnforcer.Action.Warn, e.Tick(unlock.AddMinutes(9), true, true));
        Assert.Equal(LockEnforcer.Action.None, e.Tick(unlock.AddMinutes(9.5), true, true));
        Assert.Equal(LockEnforcer.Action.Lock, e.Tick(unlock.AddMinutes(10), true, true));
    }

    [Fact]
    public void Outside_lock_hours_nothing_is_counted_or_locked()
    {
        var e = new LockEnforcer();
        Assert.False(e.OnUnlocked(At(19, 12), lockHours: false));
        Assert.Null(e.LockAt);
        Assert.Equal(LockEnforcer.Action.None, e.Tick(At(19, 12, 30), false, true));
    }

    [Fact]
    public void Lock_hours_ending_cancel_the_countdown()
    {
        var e = new LockEnforcer();
        e.OnUnlocked(At(20, 6, 55), lockHours: true);
        Assert.NotNull(e.LockAt);
        Assert.Equal(LockEnforcer.Action.None, e.Tick(At(20, 7), lockHours: false, unlocked: true));
        Assert.Null(e.LockAt);
    }

    [Fact]
    public void The_file_round_trips_and_a_damaged_one_starts_over()
    {
        var dir = Directory.CreateTempSubdirectory("winsolve-screentime").FullName;
        try
        {
            var path = Path.Combine(dir, "screen-time-S-1-5-21-1.json");
            var data = new ScreenTimeData { Schedule = Night(DayOfWeek.Monday) };
            data.Add(DateTime.Now, 120, lockHours: true);
            data.CountUnlock(DateTime.Now);
            Assert.True(ScreenTimeStore.Save(data, path));
            var back = ScreenTimeStore.Load(path);
            Assert.True(back.Schedule.Enabled);
            Assert.Equal([DayOfWeek.Monday], back.Schedule.Days);
            var today = back.Find(DateOnly.FromDateTime(DateTime.Now))!;
            Assert.Equal(120, today.Seconds);
            Assert.Equal(1, today.Unlocks);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

            File.WriteAllText(path, "{ not json");
            var fresh = ScreenTimeStore.Load(path);
            Assert.False(fresh.Schedule.Enabled);
            Assert.Empty(fresh.Days);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_page_and_the_chart_draw_without_errors()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var data = new ScreenTimeData();
                var today = DateTime.Now;
                data.Add(today, 3 * 3600, lockHours: false);
                data.Add(today.AddDays(-1), 5 * 3600, lockHours: true);
                using var bars = new WinSolve.UI.Pages.UsageBars { Size = new System.Drawing.Size(700, 220) };
                bars.SetDays(data.LastDays(DateOnly.FromDateTime(today)));
                using (var bmp = new System.Drawing.Bitmap(700, 220)) bars.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, 700, 220));
                bars.SetDays([]);
                using (var bmp = new System.Drawing.Bitmap(700, 220)) bars.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, 700, 220));

                using var form = new System.Windows.Forms.Form { Size = new System.Drawing.Size(1100, 800) };
                var page = new WinSolve.UI.Pages.ScreenTimePage();
                form.Controls.Add(page);
                form.CreateControl();
                using var shot = new System.Drawing.Bitmap(1100, 800);
                page.DrawToBitmap(shot, new System.Drawing.Rectangle(0, 0, 1100, 800));
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }

    [Fact]
    public void Every_screen_time_string_has_a_Spanish_translation()
    {
        string[] keys =
        [
            "Screen time", "Lock hours", "The PC locks in 1 minute", "Unlocked during lock hours", "Lock the PC now",
            "Lock hours until {0}. You can unlock it, but it locks again every {1} minutes.",
        ];
        foreach (var k in keys) Assert.True(WinSolve.Localization.Spanish.Table.ContainsKey(k), k);
    }
}

[Trait("Category", "E2E")]
public class ScreenTimeE2ETests(Xunit.Abstractions.ITestOutputHelper output)
{
    [E2EFact]
    public void The_watcher_registers_for_session_events_counts_and_saves()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                WinSolve.UI.ScreenTimeWatcher.Start();
                var w = WinSolve.UI.ScreenTimeWatcher.Instance;
                Assert.NotNull(w);
                Assert.False(w!.Data.Schedule.Enabled);
                System.Windows.Forms.Application.DoEvents();
                w.Tick();   // reads idle time and the display state from Windows; the schedule is off, so it never locks
                Assert.Null(w.LockAt);

                // Pictures of the page with a week of sample usage, in English and Spanish, to look at.
                var now = DateTime.Now;
                int[] minutes = [190, 245, 130, 310, 275, 420, 95];
                for (var i = 0; i < 7; i++) w.Data.Add(now.AddDays(i - 6), minutes[i] * 60, lockHours: false);
                w.Data.Add(now.AddDays(-1), 50 * 60, lockHours: true);
                w.Data.CountUnlock(now.AddDays(-1));
                w.Data.CountUnlock(now.AddDays(-1));
                w.Data.Schedule = new LockSchedule { Enabled = true };
                var language = WinSolve.Core.AppSettings.Current.Language;
                try
                {
                    foreach (var lang in new[] { "en", "es" })
                    {
                        WinSolve.Core.AppSettings.Current.Language = lang;
                        using var form = new System.Windows.Forms.Form { Size = new System.Drawing.Size(1100, 900), BackColor = WinSolve.UI.Theme.Background };
                        var page = new WinSolve.UI.Pages.ScreenTimePage();
                        form.Controls.Add(page);
                        form.CreateControl();
                        page.Fill();
                        form.PerformLayout();
                        using var shot = new System.Drawing.Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                        page.DrawToBitmap(shot, new System.Drawing.Rectangle(0, 0, shot.Width, shot.Height));
                        using var ms = new MemoryStream();
                        shot.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        if (Environment.GetEnvironmentVariable("WINSOLVE_E2E_OUT") is { Length: > 0 } outDir)
                            File.WriteAllBytes(Path.Combine(outDir, $"screentime-{lang}.png"), ms.ToArray());
                        output.WriteLine($"SCREENTIME-PNG-{lang}:{Convert.ToBase64String(ms.ToArray())}");
                    }
                }
                finally { WinSolve.Core.AppSettings.Current.Language = language; }
                w.Data.Schedule = new LockSchedule();
                w.Data.Days.Clear();

                WinSolve.UI.ScreenTimeWatcher.Stop();
                Assert.True(File.Exists(ScreenTimeStore.CurrentPath), ScreenTimeStore.CurrentPath);
                output.WriteLine($"Saved {ScreenTimeStore.CurrentPath}: {File.ReadAllText(ScreenTimeStore.CurrentPath).Length} bytes");
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }
}
