using System.Text.Json;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed record OldDriver(string Inf, string OriginalName, string Provider, string Class, Version Version, DateTime? Date, long Size, string NewerVersion);

/// <summary>
/// Old copies of third-party drivers that Windows keeps in the driver store after each update
/// (graphics drivers can take gigabytes). Only packages that no device uses AND that have a
/// newer version of the same driver installed are offered; pnputil (without /force) refuses
/// to delete anything a device still uses.
/// </summary>
public static class DriverStoreCleaner
{
    private sealed class PsDriver
    {
        public string? Driver { get; set; }
        public string? OriginalFileName { get; set; }
        public string? ProviderName { get; set; }
        public string? ClassName { get; set; }
        public string? Version { get; set; }
        public string? Date { get; set; }
    }

    public static async Task<List<OldDriver>> FindAsync(CancellationToken ct = default)
    {
        const string script = """
            $d = Get-WindowsDriver -Online | Select-Object Driver, OriginalFileName, ProviderName, ClassName, Version, @{n='Date';e={$_.Date.ToString('s')}}
            ConvertTo-Json -InputObject @($d) -Compress
            """;
        var r = await ProcessRunner.PowerShellAsync(script, ct: ct);
        var start = r.Output.IndexOf('[');
        if (start < 0) throw new InvalidOperationException(r.Output.Trim().Length > 0 ? r.Output.Trim() : "Windows didn't list the installed drivers.");
        var packages = JsonSerializer.Deserialize<List<PsDriver>>(r.Output[start..], new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        // Driver packages that a device on this PC is using right now.
        var inUse = await Task.Run(() => Wmi.Query("SELECT InfName FROM Win32_PnPSignedDriver")
            .Select(d => d.Str("InfName")).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase), ct);
        return Pick(packages.Select(p => (p.Driver ?? "", p.OriginalFileName ?? "", p.ProviderName ?? "", p.ClassName ?? "", p.Version ?? "", p.Date)).ToList(), inUse, FolderSize);
    }

    internal static List<OldDriver> Pick(List<(string Inf, string Original, string Provider, string Class, string Version, string? Date)> packages,
        HashSet<string> inUse, Func<string, long> size)
    {
        var result = new List<OldDriver>();
        var parsed = packages
            .Where(p => p.Inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase) && p.Original.Length > 0 && System.Version.TryParse(p.Version, out _))
            .Select(p => (p.Inf, p.Original, p.Provider, p.Class, Version: System.Version.Parse(p.Version), p.Date, Name: Path.GetFileName(p.Original)))
            .ToList();
        // Same driver = same original INF name from the same provider.
        foreach (var group in parsed.GroupBy(p => (p.Name.ToLowerInvariant(), p.Provider.ToLowerInvariant())))
        {
            var newest = group.Max(p => p.Version)!;
            foreach (var p in group)
            {
                if (p.Version >= newest || inUse.Contains(p.Inf)) continue; // never the newest, never one in use
                DateTime? date = DateTime.TryParse(p.Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
                result.Add(new OldDriver(p.Inf, p.Name, p.Provider, p.Class, p.Version, date, size(p.Original), newest.ToString()));
            }
        }
        return result.OrderByDescending(d => d.Size).ToList();
    }

    private static long FolderSize(string infPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(infPath);
            var store = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
            if (dir is null || !SafePath.IsSameOrInside(dir, store) || !Directory.Exists(dir)) return 0;
            return new DirectoryInfo(dir).EnumerateFiles("*", SafePath.NoLinks(recursive: true)).Sum(f => f.Length);
        }
        catch { return 0; }
    }

    /// <summary>Deletes the packages with pnputil, which refuses any a device still uses.</summary>
    public static async Task<long> DeleteAsync(IReadOnlyList<OldDriver> drivers, Action<string> log, CancellationToken ct)
    {
        long freed = 0;
        foreach (var d in drivers)
        {
            ct.ThrowIfCancellationRequested();
            if (!System.Text.RegularExpressions.Regex.IsMatch(d.Inf, @"^oem\d+\.inf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            log($"> {d.Provider} {d.OriginalName} {d.Version} ({d.Inf})");
            var r = await ProcessRunner.RunAsync("pnputil.exe", $"/delete-driver {d.Inf}", l => log("  " + l), ct);
            if (r.Success) freed += d.Size;
            else log("  Kept: Windows says it's still needed.");
        }
        log($"Done. {Format.Bytes(freed)} freed.");
        ActivityLog.Cleaned(freed, "old drivers");
        return freed;
    }
}
