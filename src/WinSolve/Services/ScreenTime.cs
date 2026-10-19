using System.Globalization;
using System.Text.Json;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>
/// Hours when the PC locks itself. It can be unlocked, but it locks again every
/// <see cref="RelockMinutes"/> minutes until the schedule ends.
/// </summary>
public sealed class LockSchedule
{
    public bool Enabled { get; set; }

    /// <summary>Minutes after midnight when the lock hours start (23:00 by default).</summary>
    public int StartMinute { get; set; } = 23 * 60;

    /// <summary>Minutes after midnight when they end; earlier than the start means the next morning.</summary>
    public int EndMinute { get; set; } = 7 * 60;

    /// <summary>Days the lock hours start on (an overnight schedule ends the morning after).</summary>
    public List<DayOfWeek> Days { get; set; } = [.. Enum.GetValues<DayOfWeek>()];

    public int RelockMinutes { get; set; } = 10;

    public static readonly int[] RelockChoices = [5, 10, 15, 30];

    /// <summary>Puts values from a hand-edited or damaged file back in range.</summary>
    public void Normalize()
    {
        StartMinute = Math.Clamp(StartMinute, 0, 24 * 60 - 1);
        EndMinute = Math.Clamp(EndMinute, 0, 24 * 60 - 1);
        RelockMinutes = Math.Clamp(RelockMinutes, 1, 120);
        Days = Days is null ? [.. Enum.GetValues<DayOfWeek>()] : Days.Where(Enum.IsDefined).Distinct().ToList();
    }

    /// <summary>The start of the lock hours <paramref name="now"/> falls in, or null outside them.</summary>
    public DateTime? StartedAt(DateTime now)
    {
        if (StartMinute == EndMinute || Days.Count == 0) return null;
        var minute = now.Hour * 60 + now.Minute;
        var today = now.Date;
        if (StartMinute < EndMinute)
            return minute >= StartMinute && minute < EndMinute && Days.Contains(today.DayOfWeek) ? today.AddMinutes(StartMinute) : null;
        // Overnight (23:00 to 07:00): the evening part belongs to today, the morning part to yesterday.
        if (minute >= StartMinute && Days.Contains(today.DayOfWeek)) return today.AddMinutes(StartMinute);
        if (minute < EndMinute && Days.Contains(today.AddDays(-1).DayOfWeek)) return today.AddDays(-1).AddMinutes(StartMinute);
        return null;
    }

    public bool IsActive(DateTime now) => Enabled && StartedAt(now) is not null;

    /// <summary>When the lock hours <paramref name="now"/> falls in end.</summary>
    public DateTime? EndsAt(DateTime now)
    {
        if (StartedAt(now) is not { } start) return null;
        var end = start.Date.AddMinutes(EndMinute);
        return end > start ? end : end.AddDays(1);
    }

    public static string Clock(int minute) => $"{minute / 60:00}:{minute % 60:00}";
}

/// <summary>Screen time of one day.</summary>
public sealed class DayUsage
{
    /// <summary>Seconds the screen was on and in use (unlocked).</summary>
    public int Seconds { get; set; }

    /// <summary>Part of <see cref="Seconds"/> that fell in the lock hours.</summary>
    public int LockSeconds { get; set; }

    /// <summary>Times the PC was unlocked during the lock hours.</summary>
    public int Unlocks { get; set; }
}

public sealed record DayTotal(DateOnly Date, TimeSpan Total, TimeSpan LockHours, int Unlocks);

/// <summary>Screen time and lock schedule of one Windows user (each user has a file of their own).</summary>
public sealed class ScreenTimeData
{
    public const int KeepDays = 120;

    public LockSchedule Schedule { get; set; } = new();

    /// <summary>Usage per day, keyed "yyyy-MM-dd".</summary>
    public Dictionary<string, DayUsage> Days { get; set; } = [];

    private static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public DayUsage Day(DateOnly day)
    {
        var key = Key(day);
        if (!Days.TryGetValue(key, out var usage)) Days[key] = usage = new DayUsage();
        return usage;
    }

    public DayUsage? Find(DateOnly day) => Days.GetValueOrDefault(Key(day));

    public void Add(DateTime when, int seconds, bool lockHours)
    {
        if (seconds <= 0) return;
        var day = Day(DateOnly.FromDateTime(when));
        day.Seconds = Math.Min(day.Seconds + seconds, 24 * 3600);
        if (lockHours) day.LockSeconds = Math.Min(day.LockSeconds + seconds, day.Seconds);
    }

    public void CountUnlock(DateTime when) => Day(DateOnly.FromDateTime(when)).Unlocks++;

    /// <summary>Drops days older than <see cref="KeepDays"/> and anything unreadable.</summary>
    public void Trim(DateOnly today)
    {
        var oldest = today.AddDays(-KeepDays);
        foreach (var key in Days.Keys.ToList())
        {
            if (!DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                || d < oldest || d > today.AddDays(1) || Days[key] is null)
                Days.Remove(key);
        }
    }

    /// <summary>The <paramref name="count"/> days up to and including <paramref name="today"/>, oldest first.</summary>
    public List<DayTotal> LastDays(DateOnly today, int count = 7)
    {
        var list = new List<DayTotal>(count);
        for (var i = count - 1; i >= 0; i--)
        {
            var d = today.AddDays(-i);
            var u = Find(d);
            list.Add(new DayTotal(d, TimeSpan.FromSeconds(u?.Seconds ?? 0), TimeSpan.FromSeconds(u?.LockSeconds ?? 0), u?.Unlocks ?? 0));
        }
        return list;
    }

    public static string Format(TimeSpan t)
    {
        var minutes = (int)t.TotalMinutes;
        return minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m";
    }
}

/// <summary>
/// When to warn and lock during the lock hours: one minute after they start while the PC is in
/// use, and again every few minutes after each unlock.
/// </summary>
public sealed class LockEnforcer
{
    public static readonly TimeSpan Warning = TimeSpan.FromMinutes(1);

    private DateTime? _lockAt;
    private bool _warned;

    public TimeSpan Relock { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>When the PC locks next (null while it isn't counting down).</summary>
    public DateTime? LockAt => _lockAt;

    public void OnLocked()
    {
        _lockAt = null;
        _warned = false;
    }

    /// <summary>The user unlocked the PC. True when it was during the lock hours (it counts, and locks again later).</summary>
    public bool OnUnlocked(DateTime now, bool lockHours)
    {
        _warned = false;
        _lockAt = lockHours ? now + Relock : null;
        return lockHours;
    }

    public enum Action { None, Warn, Lock }

    public Action Tick(DateTime now, bool lockHours, bool unlocked)
    {
        if (!lockHours)
        {
            OnLocked();
            return Action.None;
        }
        if (!unlocked) return Action.None;
        if (_lockAt is null)
        {
            // The lock hours started (or WinSolve started) while the PC was in use.
            _lockAt = now + Warning;
            _warned = true;
            return Action.Warn;
        }
        if (now >= _lockAt)
        {
            OnLocked();
            return Action.Lock;
        }
        if (!_warned && now >= _lockAt - Warning)
        {
            _warned = true;
            return Action.Warn;
        }
        return Action.None;
    }
}

/// <summary>Reads and writes the signed-in user's screen time file.</summary>
public static class ScreenTimeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>One file per Windows account: the PC's other users never see or change it.</summary>
    public static string PathFor(string sid) => System.IO.Path.Combine(DataLocation.UserFolder, $"screen-time-{sid}.json");

    public static string CurrentPath => PathFor(InteractiveUser.Sid.Value);

    public static ScreenTimeData Load(string path)
    {
        ScreenTimeData data;
        try
        {
            data = File.Exists(path) ? JsonSerializer.Deserialize<ScreenTimeData>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex)
        {
            Logger.Write($"[screen time] Could not read {path}, starting over: {ex.Message}");
            data = new();
        }
        data.Schedule ??= new();
        data.Schedule.Normalize();
        data.Days ??= [];
        data.Trim(DateOnly.FromDateTime(DateTime.Now));
        return data;
    }

    public static bool Save(ScreenTimeData data, string path)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            Logger.Write($"[screen time] Could not save: {ex.Message}");
            return false;
        }
    }
}
