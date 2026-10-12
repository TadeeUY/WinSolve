using System.Text.Json;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed record OldDriver(string Inf, string OriginalName, string Provider, string Class, Version Version, DateTime? Date, long Size, string NewerVersion);

/// <summary>
/// Old copies of third-party drivers that Windows keeps in the driver store after each update
/// (graphics drivers can take gigabytes). Only packages that no device uses AND that are older
/// than the two newest versions of the same driver are offered (the previous version stays for
/// rollback); pnputil (without /force) refuses
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
            // Keep the newest AND the version before it: if the new driver misbehaves, Windows
            // can still roll back to the previous one.
            var kept = group.Select(p => p.Version).Distinct().OrderByDescending(v => v).Take(2).ToList();
            var newest = kept[0];
            foreach (var p in group)
            {
                if (kept.Contains(p.Version) || inUse.Contains(p.Inf)) continue; // never the newest or the previous one, never one in use
                DateTime? date = DateTime.TryParse(p.Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
                result.Add(new OldDriver(p.Inf, p.Name, p.Provider, p.Class, p.Version, date, size(p.Original), newest.ToString()));
            }
        }
        return result.OrderByDescending(d => d.Size).ToList();
    }

    // ───────────── Cleanup only after a restart ─────────────

    private const string PendingKey = @"SOFTWARE\WinSolve\Pending";
    private const string PendingBootId = "DriverCleanupBootId";
    private const string PendingValue = "DriverCleanupBootTime";
    private const string PrefetchKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";

    /// <summary>When Windows last started (UTC), from the uptime counter. Only a fallback: clock changes move it.</summary>
    public static DateTime BootTimeUtc => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Windows' boot counter (increments on every boot), or null if it isn't there.</summary>
    private static int? BootId()
    {
        try { return Reg.Get(Microsoft.Win32.RegistryHive.LocalMachine, PrefetchKey, "BootId") is int id ? id : null; }
        catch { return null; }
    }

    /// <summary>
    /// The previous versions are the rollback until a restart proves the new drivers work:
    /// after an update only remember that a cleanup can be offered, with the current boot.
    /// </summary>
    public static void MarkCleanupPending()
    {
        try
        {
            ClearCleanupPending();
            if (BootId() is { } id)
                Reg.Set(Microsoft.Win32.RegistryHive.LocalMachine, PendingKey, PendingBootId, id, Microsoft.Win32.RegistryValueKind.DWord);
            else
                Reg.Set(Microsoft.Win32.RegistryHive.LocalMachine, PendingKey, PendingValue,
                    BootTimeUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture), Microsoft.Win32.RegistryValueKind.String);
        }
        catch (Exception ex) { Logger.Write($"Could not remember the driver cleanup: {ex.Message}"); }
    }

    /// <summary>True when a cleanup was postponed and Windows has restarted since then.</summary>
    public static bool IsCleanupDue()
    {
        try
        {
            if (Reg.Get(Microsoft.Win32.RegistryHive.LocalMachine, PendingKey, PendingBootId) is int saved)
                return BootId() is { } now ? now != saved : false;
            if (Reg.Get(Microsoft.Win32.RegistryHive.LocalMachine, PendingKey, PendingValue) is not string s
                || !DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var recorded))
                return false;
            return BootedSince(recorded.ToUniversalTime(), BootTimeUtc);
        }
        catch { return false; }
    }

    public static void ClearCleanupPending()
    {
        try
        {
            Reg.Delete(Microsoft.Win32.RegistryHive.LocalMachine, PendingKey, PendingBootId);
            Reg.Delete(Microsoft.Win32.RegistryHive.LocalMachine, PendingKey, PendingValue);
        }
        catch { }
    }

    /// <summary>The uptime-based boot time drifts by seconds: only a clearly later boot counts as a restart.</summary>
    internal static bool BootedSince(DateTime recordedBootUtc, DateTime currentBootUtc)
        => currentBootUtc > recordedBootUtc.AddMinutes(2);

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
