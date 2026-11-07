using System.Globalization;
using System.Text.Json;
using WinSolve.Core;

namespace WinSolve.Services;

// ═══════════════════════ Connection quality history ═══════════════════════

/// <summary>One check of the connection. -1 means "no answer".</summary>
public sealed record QualitySample(DateTime Time, int RouterMs, int InternetMs)
{
    public bool RouterUp => RouterMs >= 0;
    public bool InternetUp => InternetMs >= 0;
}

/// <summary>A stretch where the Internet did not answer. <see cref="LocalNetwork"/>: the router did not answer either.</summary>
public sealed record Outage(DateTime Start, TimeSpan Length, bool LocalNetwork);

public sealed record QualitySummary(int Samples, double UptimePercent, double? AverageMs, IReadOnlyList<Outage> Outages)
{
    public TimeSpan Downtime => TimeSpan.FromTicks(Outages.Sum(o => o.Length.Ticks));
    public TimeSpan Longest => Outages.Count == 0 ? TimeSpan.Zero : Outages.Max(o => o.Length);
}

/// <summary>A slice of time for the chart: how many checks, how many failed, and the average answer time of the good ones.</summary>
public sealed record QualityBucket(DateTime Start, int Samples, int Failures, double? AverageMs);

/// <summary>A ping a minute to the router and the Internet, kept for 7 days, to show when the connection drops.</summary>
public static class QualityLog
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(DataLocation.UserFolder, "quality.csv");

    public static void Append(QualitySample sample)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, Format(sample) + "\n");
        }
    }

    internal static string Format(QualitySample s)
        => string.Create(CultureInfo.InvariantCulture, $"{s.Time.ToUniversalTime().Ticks},{s.RouterMs},{s.InternetMs}");

    internal static QualitySample? Parse(string line)
    {
        var p = line.Split(',');
        if (p.Length != 3
            || !long.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !int.TryParse(p[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var router)
            || !int.TryParse(p[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var internet)
            || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return null;
        return new QualitySample(new DateTime(ticks, DateTimeKind.Utc).ToLocalTime(), router, internet);
    }

    public static List<QualitySample> Load(DateTime since)
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return [];
                return File.ReadLines(FilePath).Select(Parse).OfType<QualitySample>().Where(s => s.Time >= since).ToList();
            }
            catch { return []; }
        }
    }

    /// <summary>Forgets what is older than a week.</summary>
    public static void Trim()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var limit = DateTime.Now - Keep;
                var keep = File.ReadLines(FilePath).Where(l => Parse(l) is { } s && s.Time >= limit).ToList();
                File.WriteAllLines(FilePath, keep);
            }
            catch { /* in use: it is trimmed next time */ }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* in use */ }
        }
    }

    /// <summary>
    /// Uptime, average answer time and the outages: two or more checks in a row without Internet.
    /// A long silence between checks (the PC was off or asleep) is not counted as an outage.
    /// </summary>
    public static QualitySummary Summarize(IReadOnlyList<QualitySample> samples)
    {
        var ordered = samples.OrderBy(s => s.Time).ToList();
        var outages = new List<Outage>();
        DateTime? start = null;
        var last = DateTime.MinValue;
        int failed = 0, local = 0;

        void Close()
        {
            if (start is not null && failed >= 2) outages.Add(new Outage(start.Value, last - start.Value + Interval, local * 2 > failed));
            start = null;
            failed = local = 0;
        }

        foreach (var s in ordered)
        {
            if (start is not null && s.Time - last > Interval * 3) Close();
            if (s.InternetUp) { Close(); continue; }
            start ??= s.Time;
            failed++;
            if (!s.RouterUp) local++;
            last = s.Time;
        }
        Close();

        var good = ordered.Where(s => s.InternetUp).ToList();
        return new QualitySummary(
            ordered.Count,
            ordered.Count == 0 ? 100 : good.Count * 100.0 / ordered.Count,
            good.Count == 0 ? null : good.Average(s => s.InternetMs),
            outages);
    }

    public static List<QualityBucket> Buckets(IReadOnlyList<QualitySample> samples, DateTime from, DateTime to, int count)
    {
        var span = (to - from).Ticks / Math.Max(1, count);
        var slots = Enumerable.Range(0, count).Select(i => new List<QualitySample>()).ToList();
        foreach (var s in samples)
        {
            if (s.Time < from || s.Time >= to || span <= 0) continue;
            var i = (int)((s.Time - from).Ticks / span);
            if (i >= 0 && i < count) slots[i].Add(s);
        }
        return slots.Select((list, i) =>
        {
            var good = list.Where(s => s.InternetUp).ToList();
            return new QualityBucket(from + TimeSpan.FromTicks(span * i), list.Count, list.Count - good.Count,
                good.Count == 0 ? null : good.Average(s => s.InternetMs));
        }).ToList();
    }
}

// ═══════════════════════ Known devices ═══════════════════════

public sealed class KnownDevice
{
    public string Mac { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime FirstSeen { get; set; }

    /// <summary>False until the user says "I know this device"; a device alerted about stays false.</summary>
    public bool Trusted { get; set; }
}

/// <summary>The devices seen on the network, to tell which ones are new.</summary>
public static class KnownDevices
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(DataLocation.UserFolder, "known-devices.json");

    public static Dictionary<string, KnownDevice> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return new(StringComparer.OrdinalIgnoreCase);
                var list = JsonSerializer.Deserialize<List<KnownDevice>>(File.ReadAllText(FilePath)) ?? [];
                return list.Where(k => k.Mac.Length > 0).GroupBy(k => k.Mac, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            }
            catch { return new(StringComparer.OrdinalIgnoreCase); }
        }
    }

    private static void Save(Dictionary<string, KnownDevice> known)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(known.Values.OrderBy(k => k.FirstSeen).ToList()));
    }

    /// <summary>
    /// Adds what was just seen. The very first time everything counts as known (nobody wants the whole
    /// network flagged); after that, a device never seen before is returned, and stays "new" until trusted.
    /// </summary>
    internal static List<LanDevice> Merge(Dictionary<string, KnownDevice> known, IEnumerable<LanDevice> seen, DateTime now, bool firstRun)
    {
        var fresh = new List<LanDevice>();
        foreach (var d in seen)
        {
            if (known.TryGetValue(d.Mac, out var k))
            {
                k.Ip = d.Ip;
                if (d.Name.Length > 0) k.Name = d.Name;
                continue;
            }
            var trusted = firstRun || d.IsRouter || d.IsThisPc;
            known[d.Mac] = new KnownDevice { Mac = d.Mac, Ip = d.Ip, Name = d.Name, FirstSeen = now, Trusted = trusted };
            if (!trusted) fresh.Add(d);
        }
        return fresh;
    }

    public static List<LanDevice> Update(IEnumerable<LanDevice> seen, DateTime now)
    {
        lock (Gate)
        {
            try
            {
                var firstRun = !File.Exists(FilePath);
                var known = Load();
                var fresh = Merge(known, seen, now, firstRun);
                Save(known);
                return fresh;
            }
            catch { return []; }
        }
    }

    public static void TrustAll()
    {
        lock (Gate)
        {
            try
            {
                var known = Load();
                foreach (var k in known.Values) k.Trusted = true;
                Save(known);
            }
            catch { /* the next try will do it */ }
        }
    }
}
