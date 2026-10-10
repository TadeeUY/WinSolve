using System.Xml;
using System.Xml.Linq;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed record BatteryCapacityPoint(DateTime Date, long FullCharge, long Design);

public sealed record BatteryDetails(
    string Name, string Chemistry, long DesignCapacity, long FullChargeCapacity, int? CycleCount,
    TimeSpan? RuntimeNow, TimeSpan? RuntimeWhenNew, List<BatteryCapacityPoint> History)
{
    /// <summary>Capacity left compared with when it was new (0-100+).</summary>
    public double HealthPercent => DesignCapacity > 0 ? FullChargeCapacity * 100.0 / DesignCapacity : 0;
}

/// <summary>Battery wear and battery life, from Windows' own battery report (powercfg).</summary>
public static class BatteryReport
{
    public static async Task<List<BatteryDetails>> ReadAsync(CancellationToken ct = default)
    {
        var folder = SafePath.CreateAdminOnlyFolder("Battery");
        var file = Path.Combine(folder, "battery.xml");
        try
        {
            var r = await ProcessRunner.RunAsync("powercfg.exe", $"/batteryreport /xml /output \"{file}\"", null, ct);
            if (!File.Exists(file))
                throw new InvalidOperationException(r.Output.Trim().Length > 0 ? r.Output.Trim() : "Windows could not create the battery report.");
            return Parse(XDocument.Load(file));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    /// <summary>Windows' full HTML battery report, saved on the desktop.</summary>
    public static async Task<string> SaveHtmlAsync(CancellationToken ct = default)
    {
        var file = Path.Combine(InteractiveUser.Desktop, $"Battery report - {DateTime.Now:yyyy-MM-dd}.html");
        var r = await ProcessRunner.RunAsync("powercfg.exe", $"/batteryreport /output \"{file}\"", null, ct);
        if (!File.Exists(file)) throw new InvalidOperationException(r.Output.Trim());
        return file;
    }

    internal static List<BatteryDetails> Parse(XDocument doc)
    {
        static XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
        static IEnumerable<XElement> Children(XElement? e, string name) => e?.Elements().Where(x => x.Name.LocalName == name) ?? [];
        static long Long(XElement? e) => long.TryParse(e?.Value, out var v) ? v : 0;
        static TimeSpan? Duration(XElement? e)
        {
            try { return e is null || e.Value.Length == 0 ? null : XmlConvert.ToTimeSpan(e.Value); } catch { return null; }
        }

        var root = doc.Root;
        var estimates = Child(root, "RuntimeEstimates");
        var runtimeNow = Duration(Child(Child(estimates, "FullChargeCapacity"), "ActiveRuntime"));
        var runtimeNew = Duration(Child(Child(estimates, "DesignCapacity"), "ActiveRuntime"));

        var history = new List<BatteryCapacityPoint>();
        foreach (var h in Children(Child(root, "History"), "HistoryEntry"))
        {
            var date = h.Attribute("EndDate")?.Value ?? h.Attribute("StartDate")?.Value;
            var full = long.TryParse(h.Attribute("FullChargeCapacity")?.Value, out var f) ? f : 0;
            var design = long.TryParse(h.Attribute("DesignCapacity")?.Value, out var dc) ? dc : 0;
            if (DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) && full > 0)
                history.Add(new BatteryCapacityPoint(d, full, design));
        }

        var list = new List<BatteryDetails>();
        foreach (var b in Children(Child(root, "Batteries"), "Battery"))
        {
            var design = Long(Child(b, "DesignCapacity"));
            var full = Long(Child(b, "FullChargeCapacity"));
            if (design <= 0 && full <= 0) continue;
            var name = string.Join(" ", new[] { Child(b, "Manufacturer")?.Value, Child(b, "Id")?.Value }
                .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            int? cycles = int.TryParse(Child(b, "CycleCount")?.Value, out var c) && c > 0 ? c : null;
            list.Add(new BatteryDetails(name.Length > 0 ? name : "Battery", Child(b, "Chemistry")?.Value?.Trim() ?? "",
                design, full, cycles, runtimeNow, runtimeNew, history));
        }
        return list;
    }
}
