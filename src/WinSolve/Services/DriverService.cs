using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using WinSolve.Core;

namespace WinSolve.Services;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel }

public sealed class GpuInfo
{
    public required string Name { get; init; }
    public GpuVendor Vendor { get; init; }
    public string WindowsDriverVersion { get; init; } = "";
    public DateTime? DriverDate { get; init; }
    public string PnpId { get; init; } = "";

    /// <summary>Version as the vendor shows it (NVIDIA 566.36, AMD Adrenalin 24.12.1...).</summary>
    public string FriendlyVersion { get; init; } = "";

    public string VendorName => Vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        _ => "Unknown",
    };
}

public sealed record DriverRelease(string Version, string DownloadUrl, string? ReleaseDate, string? DetailsUrl);

/// <summary>
/// Graphics driver management: detection, latest version lookup, download,
/// complete removal of the current driver and clean install. Also updates other
/// drivers through Windows Update and backs up installed drivers.
/// </summary>
public static class DriverService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) WinSolve");
        return c;
    }

    // ═══════════════════ Detection ═══════════════════

    public static List<GpuInfo> GetGpus()
    {
        var amdVersion = Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\AMD\CN", "RadeonSoftwareVersion") as string;

        return Wmi.Query("SELECT Name, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController")
            .Select(v =>
            {
                var pnp = v.Str("PNPDeviceID");
                var vendor = pnp.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Nvidia
                    : pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Amd
                    : pnp.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Intel
                    : GpuVendor.Unknown;
                var winVer = v.Str("DriverVersion");
                DateTime? date = null;
                var rawDate = v.Str("DriverDate");
                if (rawDate.Length >= 8 && DateTime.TryParseExact(rawDate[..8], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d))
                    date = d;

                return new GpuInfo
                {
                    Name = v.Str("Name"),
                    Vendor = vendor,
                    WindowsDriverVersion = winVer,
                    DriverDate = date,
                    PnpId = pnp,
                    FriendlyVersion = vendor switch
                    {
                        GpuVendor.Nvidia => NvidiaVersion(winVer),
                        GpuVendor.Amd when amdVersion is { Length: > 0 } => "Adrenalin " + amdVersion,
                        _ => winVer,
                    },
                };
            })
            .Where(g => g.Name.Length > 0 && !g.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                        && !g.Name.Contains("Remote", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>32.0.15.6636 → 566.36 (the last five digits of the last two fields).</summary>
    public static string NvidiaVersion(string windowsVersion)
    {
        var parts = windowsVersion.Split('.');
        if (parts.Length < 4) return windowsVersion;
        var digits = (parts[2] + parts[3].PadLeft(4, '0'));
        if (digits.Length < 5) return windowsVersion;
        digits = digits[^5..];
        return $"{digits[..3]}.{digits[3..]}";
    }

    // ═══════════════════ Latest version ═══════════════════

    /// <summary>Looks up the latest Game Ready (DCH, WHQL) driver on NVIDIA's public driver API.</summary>
    public static async Task<DriverRelease?> GetLatestNvidiaAsync(GpuInfo gpu, Action<string> log, CancellationToken ct)
    {
        log("Looking up the product on nvidia.com...");
        var xml = await Http.GetStringAsync("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3", ct);
        var doc = XDocument.Parse(xml);
        var wanted = Normalize(gpu.Name);

        var products = doc.Descendants("LookupValue")
            .Select(e => (Name: (string?)e.Element("Name") ?? "", Psid: (string?)e.Attribute("ParentID") ?? "", Pfid: (string?)e.Element("Value") ?? ""))
            .Where(p => p.Name.Length > 0)
            .ToList();

        // Never pick a desktop package for a laptop GPU (or the other way round): it may not install.
        // Laptop GPUs before the RTX 30 series report the same name as the desktop card
        // ("GeForce GTX 1060"), so the PC's chassis decides too.
        static bool IsMobile(string n) => n.Contains("laptop", StringComparison.OrdinalIgnoreCase) || n.Contains("notebook", StringComparison.OrdinalIgnoreCase)
            || n.Contains("max-q", StringComparison.OrdinalIgnoreCase) || n.Contains("mobile", StringComparison.OrdinalIgnoreCase);
        bool laptop;
        try { laptop = IsMobile(gpu.Name) || HardwareProfile.Detect().IsLaptop; }
        catch { laptop = IsMobile(gpu.Name); }

        var match = laptop
            // e.g. "GeForce GTX 1060" on a laptop -> "GeForce GTX 1060 (Notebooks)"
            ? products.Where(p => IsMobile(p.Name) && (Normalize(p.Name) == wanted || Normalize(p.Name).StartsWith(wanted + " ")))
                .OrderBy(p => p.Name.Length).FirstOrDefault()
            : default;
        if (match.Name is null or "")
            match = products.FirstOrDefault(p => Normalize(p.Name) == wanted && IsMobile(p.Name) == laptop);
        if (match.Name is null or "")
            match = products.Where(p => IsMobile(p.Name) == laptop && (wanted.Contains(Normalize(p.Name)) || Normalize(p.Name).Contains(wanted)))
                .OrderByDescending(p => p.Name.Length).FirstOrDefault();
        if (match.Name is null or "")
            match = products.FirstOrDefault(p => Normalize(p.Name) == wanted); // last resort: same name, other form factor
        if (match.Name is null or "")
        {
            log($"'{gpu.Name}' was not found in NVIDIA's product list.");
            return null;
        }
        log($"Product: {match.Name} (psid {match.Psid}, pfid {match.Pfid})");

        var url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                  $"?func=DriverManualLookup&psid={match.Psid}&pfid={match.Pfid}&osID=57&languageCode=1033" +
                  "&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1";
        var json = await Http.GetStringAsync(url, ct);
        using var j = JsonDocument.Parse(json);
        if (!j.RootElement.TryGetProperty("IDS", out var ids) || ids.GetArrayLength() == 0) return null;
        var info = ids[0].GetProperty("downloadInfo");
        string? Str(string name) => info.TryGetProperty(name, out var v) ? v.GetString() : null;

        var version = Str("Version") ?? "";
        var download = Str("DownloadURL") ?? "";
        if (version.Length == 0 || download.Length == 0) return null;
        if (laptop && download.Contains("-desktop-", StringComparison.OrdinalIgnoreCase))
            log("Note: NVIDIA returned the desktop package for this laptop GPU. If it doesn't install, download the notebook driver from nvidia.com.");
        return new DriverRelease(version, download, Str("ReleaseDateTime"), Str("DetailsURL"));
    }

    private static string Normalize(string s)
        => Regex.Replace(s.ToLowerInvariant().Replace("nvidia", "").Replace("(r)", "").Replace("(tm)", ""), @"\s+", " ").Trim();

    public static bool IsNewer(string latest, string current)
        => Version.TryParse(latest, out var l) && Version.TryParse(current, out var c) && l > c;

    public const string AmdDownloadPage = "https://www.amd.com/en/support/download/drivers.html";
    public const string NvidiaDownloadPage = "https://www.nvidia.com/en-us/drivers/";
    public const string IntelDownloadPage = "https://www.intel.com/content/www/us/en/support/detect.html";

    // ═══════════════════ Download ═══════════════════

    /// <summary>Only official NVIDIA download hosts over HTTPS are accepted.</summary>
    private static bool IsTrustedDownload(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps
           && (uri.Host.Equals("nvidia.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Downloads an installer into a fresh folder that only administrators can write to,
    /// so a non-elevated process cannot swap the file before it is executed.
    /// </summary>
    public static async Task<string> DownloadAsync(string url, Action<string> log, Action<int, int> progress, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (!IsTrustedDownload(uri)) throw new InvalidOperationException($"Refusing to download from an untrusted location: {uri.Host}");

        var file = Path.Combine(SafePath.CreateAdminOnlyFolder("Drivers"), Path.GetFileName(uri.LocalPath));
        log($"Downloading {url}");

        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (!IsTrustedDownload(response.RequestMessage?.RequestUri ?? uri))
            throw new InvalidOperationException("The download was redirected to an untrusted location.");
        var total = response.Content.Headers.ContentLength ?? 0;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[1 << 16];
        long done = 0;
        int read, lastPct = -1;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
            {
                var pct = (int)(done * 100 / total);
                if (pct != lastPct)
                {
                    lastPct = pct;
                    progress(pct, 100);
                    if (pct % 10 == 0) log($"  {pct}% ({Format.Bytes(done)} of {Format.Bytes(total)})");
                }
            }
        }
        log($"Saved to {file}");
        return file;
    }

    /// <summary>Signer names accepted for each vendor's driver installers.</summary>
    public static string[] TrustedSigners(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => ["NVIDIA Corporation"],
        GpuVendor.Amd => ["Advanced Micro Devices, Inc.", "Advanced Micro Devices Inc.", "Advanced Micro Devices INC."],
        GpuVendor.Intel => ["Intel Corporation"],
        _ => [],
    };

    /// <summary>Checks the Authenticode signature and that the signer is the GPU vendor.</summary>
    public static bool HasValidSignature(string file, GpuVendor vendor, out string? signer)
        => Authenticode.IsSignedBy(file, TrustedSigners(vendor), out signer);

    // ═══════════════════ Clean install ═══════════════════

    /// <summary>An installed display driver package (oemNN.inf) of one vendor.</summary>
    public sealed record DisplayPackage(string Inf, string OriginalName, string Provider, string Version);

    private sealed class PsPackage
    {
        public string? Driver { get; set; }
        public string? OriginalFileName { get; set; }
        public string? ProviderName { get; set; }
        public string? Version { get; set; }
    }

    private static string ProviderPattern(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "^NVIDIA",
        // Anchored: an unanchored "ATI" would also match "Corporation" (Intel/NVIDIA Corporation).
        GpuVendor.Amd => "^(Advanced Micro Devices|AMD|ATI Technologies)",
        GpuVendor.Intel => "^Intel",
        _ => throw new ArgumentException("Unknown vendor"),
    };

    private static readonly Regex OemInf = new(@"^oem\d+\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Every installed display driver package from the vendor (current and older versions in the driver store).</summary>
    public static async Task<List<DisplayPackage>> GetDisplayPackagesAsync(GpuVendor vendor, CancellationToken ct)
    {
        var r = await ProcessRunner.PowerShellAsync($$"""
            $d = Get-WindowsDriver -Online | Where-Object { $_.ClassName -eq 'Display' -and $_.ProviderName -match '{{ProviderPattern(vendor)}}' } | Select-Object Driver, OriginalFileName, ProviderName, Version
            ConvertTo-Json -InputObject @($d) -Compress
            """, ct: ct);
        var start = r.Output.IndexOf('[');
        if (start < 0) throw new InvalidOperationException("Windows didn't list the installed graphics drivers. Nothing was removed.");
        var list = JsonSerializer.Deserialize<List<PsPackage>>(r.Output[start..], new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        return list.Where(p => p.Driver is { } inf && OemInf.IsMatch(inf))
            .Select(p => new DisplayPackage(p.Driver!, Path.GetFileName(p.OriginalFileName ?? ""), p.ProviderName ?? "", p.Version ?? ""))
            .ToList();
    }

    /// <summary>Folder that keeps the copies of removed graphics drivers (administrators only).</summary>
    public static string DriverBackupRoot => Path.Combine(SafePath.DataFolder, "DriverBackup");

    /// <summary>
    /// Exports every package (pnputil /export-driver) into a new administrators-only folder
    /// before anything is removed. Throws if any package could not be saved.
    /// </summary>
    public static async Task<string> BackupDisplayPackagesAsync(IReadOnlyList<DisplayPackage> packages, Action<string> log, CancellationToken ct)
    {
        var folder = SafePath.CreateAdminOnlyFolder(Path.Combine("DriverBackup", DateTime.Now.ToString("yyyy-MM-dd_HHmmss")));
        log($"Backing up the current graphics driver to {folder}");
        try
        {
            foreach (var p in packages)
            {
                ct.ThrowIfCancellationRequested();
                if (!OemInf.IsMatch(p.Inf)) throw new InvalidOperationException($"Unexpected driver name {p.Inf}. Nothing was removed.");
                var target = Path.Combine(folder, Path.GetFileNameWithoutExtension(p.Inf));
                Directory.CreateDirectory(target);
                log($"  {p.Inf} - {p.Provider} {p.Version}");
                var r = await ProcessRunner.RunAsync("pnputil.exe", $"/export-driver {p.Inf} \"{target}\"", l => log("    " + l), ct);
                if (!r.Success || !Directory.EnumerateFiles(target, "*.inf", SearchOption.AllDirectories).Any())
                    throw new InvalidOperationException($"Could not back up the driver {p.Inf} ({p.Provider} {p.Version}). Nothing was removed.");
            }
        }
        catch
        {
            // The <time> folder too: an empty one must not count as a backup when pruning.
            try { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); } catch { }
            throw;
        }
        log($"Backup complete: {packages.Count} driver package(s) saved in {folder}");
        PruneDriverBackups(keep: 3);
        return folder;
    }

    /// <summary>Graphics driver backups take hundreds of MB each: only the newest few are kept.</summary>
    private static void PruneDriverBackups(int keep)
    {
        try
        {
            var root = DriverBackupRoot;
            if (!Directory.Exists(root) || SafePath.HasReparsePoint(root)) return;
            static bool HasDrivers(DirectoryInfo d)
            {
                try { return d.EnumerateFiles("*.inf", SafePath.NoLinks(recursive: true)).Any(); }
                catch { return true; } // unreadable: never counted as empty
            }
            var dirs = new DirectoryInfo(root).EnumerateDirectories("*", SafePath.NoLinks(recursive: false)).ToList();
            // Leftovers of failed backups: removed, never counted among the kept ones.
            foreach (var empty in dirs.Where(d => !HasDrivers(d)))
                try { Directory.Delete(empty.FullName, recursive: true); } catch { }
            foreach (var dir in dirs.Where(HasDrivers).OrderByDescending(d => d.CreationTimeUtc).Skip(keep))
                try { Directory.Delete(dir.FullName, recursive: true); } catch { }
        }
        catch { }
    }

    /// <summary>Installs the drivers saved by <see cref="BackupDisplayPackagesAsync"/> again. Never cancelled.</summary>
    public static async Task<bool> RestoreDisplayBackupAsync(string folder, Action<string> log)
    {
        log("Reinstalling the backed-up graphics driver...");
        var r = await ProcessRunner.RunAsync("pnputil.exe", $"/add-driver \"{Path.Combine(folder, "*.inf")}\" /subdirs /install",
            l => log("  " + l), CancellationToken.None);
        // 3010: installed, a restart completes it.
        return r.ExitCode is 0 or 3010;
    }

    /// <summary>
    /// Creates a restore point; when that fails, asks whether to continue without one (default No)
    /// and stops with a cancellation if not.
    /// </summary>
    private static async Task EnsureRestorePointAsync(string description, Func<string, bool> confirm, Action<string> log, CancellationToken ct)
    {
        var point = await TaskCatalog.CreateRestorePointAsync(new TaskContext(log, ct), description);
        if (point.ProtectionTurnedOn) log(TaskCatalog.ProtectionTurnedOnMessage(point.Drive));
        if (point.Created) return;
        if (!confirm("A restore point could not be created (System Protection may be turned off).\n\nContinue without a restore point?"))
        {
            log("Stopped: no restore point could be created. Nothing was changed.");
            throw new OperationCanceledException();
        }
        log("Continuing without a restore point, as you chose.");
    }

    /// <summary>Deletes the packages and uninstalls them from the devices that use them (like DDU does with pnputil).</summary>
    private static async Task RemovePackagesAsync(GpuVendor vendor, IReadOnlyList<DisplayPackage> packages, Action<string> log, CancellationToken ct)
    {
        log("Removing the current graphics driver (the screen may flicker or change resolution)...");
        foreach (var p in packages)
        {
            ct.ThrowIfCancellationRequested();
            if (!OemInf.IsMatch(p.Inf)) continue;
            log($"Removing {p.Inf} - {p.Provider} {p.Version}");
            await ProcessRunner.RunAsync("pnputil.exe", $"/delete-driver {p.Inf} /uninstall /force", l => log("  " + l), ct);
        }
        ClearShaderCaches(vendor, log);
    }

    /// <summary>
    /// "Remove driver only": every display driver package of the vendor is exported to a backup
    /// folder first (stops if that fails), a restore point is created (asks if that fails), then
    /// the packages are removed. Returns the backup folder, or null if there was nothing to remove.
    /// </summary>
    public static async Task<string?> RemoveDisplayDriversAsync(GpuVendor vendor, Func<string, bool> confirm, Action<string> log, CancellationToken ct)
    {
        var packages = await GetDisplayPackagesAsync(vendor, ct);
        if (packages.Count == 0)
        {
            log("No driver packages found for this vendor.");
            return null;
        }
        await EnsureRestorePointAsync("WinSolve - before removing the graphics driver", confirm, log, ct);
        var backup = await BackupDisplayPackagesAsync(packages, log, ct);
        await RemovePackagesAsync(vendor, packages, log, ct);
        log($"The graphics driver was removed. A copy is saved in {backup}");
        log("To get it back: run the vendor's driver installer, or in Device Manager choose the display adapter > Update driver > Browse my computer, and pick that folder.");
        return backup;
    }

    // ───────────── Installer checks ─────────────

    private static readonly string[] NotADisplayPackage =
    [
        @"chipset", @"(?<![a-z])dsa(?![a-z])", @"support[\s_-]*assistant", @"auto[\s_-]*detect", @"(?<![a-z])web(?![a-z])",
        @"minimal[\s_-]*setup", @"(?<![a-z])online(?![a-z])", @"ryzen[\s_-]*master", @"(?<![a-z])raid(?![a-z])", @"rapid[\s_-]*storage",
        @"management[\s_-]*engine", @"wi[\s_-]*fi", @"bluetooth", @"ethernet", @"(?<![a-z])audio(?![a-z])", @"serial[\s_-]*io", @"thunderbolt",
    ];

    /// <summary>
    /// Why a file can't be used for a clean install of this vendor's graphics driver, or null when
    /// it looks like the full display driver package. Checked before anything is removed: a chipset
    /// driver, a support assistant or a web installer would leave the PC with no graphics driver.
    /// </summary>
    public static string? InstallerProblem(GpuVendor vendor, string file)
    {
        string? product = null, description = null;
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(file);
            product = info.ProductName;
            description = info.FileDescription;
        }
        catch { /* no version resource */ }
        return InstallerProblem(vendor, product, description, Path.GetFileName(file));
    }

    internal static string? InstallerProblem(GpuVendor vendor, string? product, string? description, string fileName)
    {
        // NVIDIA's installer replaces the driver itself, nothing is removed beforehand.
        if (vendor is not (GpuVendor.Amd or GpuVendor.Intel)) return null;

        static string Clean(string? s) => Regex.Replace(Regex.Replace(s ?? "", @"®|™|\((R|TM)\)", " ", RegexOptions.IgnoreCase), @"\s+", " ").Trim();
        var meta = $"{Clean(product)} | {Clean(description)}";
        var all = $"{meta} | {Clean(fileName)}";
        var shown = Clean(product).Length > 0 ? Clean(product) : Clean(description).Length > 0 ? Clean(description) : "no product information";
        var vendorName = vendor == GpuVendor.Amd ? "AMD" : "Intel";

        if (NotADisplayPackage.Any(p => Regex.IsMatch(all, p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            return string.Format("'{0}' is not a full {1} graphics driver package ({2}). Chipset drivers, Driver & Support Assistant, auto-detect tools and web installers can't be used here: download the complete graphics driver installer. Nothing was changed.",
                fileName, vendorName, shown);

        string[] accepted = vendor == GpuVendor.Amd
            ? [@"amd[\s_-]*software", @"radeon", @"adrenalin"]
            : [@"intel.*graphics", @"graphics[\s_-]*driver", @"(?<![a-z])arc(?![a-z])", @"(?<![a-z])iris(?![a-z])", @"gfx[\s_-]*win"];
        if (accepted.Any(p => Regex.IsMatch(all, p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            return null;
        return string.Format("'{0}' doesn't look like a {1} graphics driver installer ({2}). Nothing was changed.", fileName, vendorName, shown);
    }

    // ───────────── Hybrid graphics ─────────────

    /// <summary>Integrated GPUs (Intel UHD/Iris/Arc Graphics, AMD Radeon Graphics / Vega / 780M APUs).</summary>
    public static bool IsLikelyIntegrated(GpuVendor vendor, string name) => vendor switch
    {
        // Arc A380/A770, B580... are graphics cards; "Intel Arc Graphics" (Core Ultra) is integrated.
        GpuVendor.Intel => !Regex.IsMatch(name, @"\bArc\b.*\b[AB]\d{3}", RegexOptions.IgnoreCase),
        GpuVendor.Amd => Regex.IsMatch(name, @"Radeon(\s*\(TM\))?\s+((Vega\s+\d+\s+)?Graphics\s*$|\d{3}M\b)|Vega\s+\d+\s+Graphics", RegexOptions.IgnoreCase),
        _ => false,
    };

    /// <summary>
    /// A warning when the PC has more than one graphics adapter and the driver being removed is
    /// the integrated one: on laptops it usually drives the built-in screen.
    /// </summary>
    public static string? HybridWarning(GpuVendor vendor, IReadOnlyList<GpuInfo> gpus)
    {
        if (gpus.Count < 2 || !gpus.Any(g => g.Vendor == vendor && IsLikelyIntegrated(g.Vendor, g.Name))) return null;
        return string.Format("This PC has more than one graphics adapter ({0}). The {1} one is the integrated GPU: on laptops it usually drives the built-in screen, which can go black or drop to a low resolution until a driver is installed again. Keep an external monitor connected, or know how to start Windows in Safe Mode, before you continue.",
            string.Join(", ", gpus.Select(g => g.Name)), vendor == GpuVendor.Amd ? "AMD" : vendor == GpuVendor.Intel ? "Intel" : vendor.ToString());
    }

    private static void ClearCache(string dir, Action<string> log)
    {
        // A user-writable folder: handle-based delete, never follows a junction swapped in.
        if (!Directory.Exists(dir) || SafePath.HasReparsePoint(dir)) return;
        try
        {
            SafeDelete.DeleteContents(dir, "*", recursive: true, CancellationToken.None);
            log($"Cleared {dir}");
        }
        catch { /* in use */ }
    }

    /// <summary>What a clean install ended with, checked against what Windows reports afterwards.</summary>
    public sealed record InstallOutcome(bool Success, string Message);

    /// <summary>
    /// Clean install: installer check, restore point (asks if it fails), backup and removal of the
    /// old driver (except NVIDIA, whose installer replaces it itself with -clean), then the new
    /// installer, waiting for the whole setup. If the new driver ends up inactive, or the install
    /// fails or is cancelled after the removal, the backed-up driver is reinstalled.
    /// Windows Update is kept from slipping its own driver in meanwhile, the unpacked installer
    /// files are removed, and the result is verified against the driver Windows actually loaded.
    /// The installer is copied into an administrators-only folder and kept open (no writes,
    /// no deletes) while its signature is verified and it runs, so it cannot be swapped.
    /// </summary>
    public static async Task<InstallOutcome> CleanInstallAsync(GpuVendor vendor, string installer, bool requireVendorSignature,
        string? expectedVersion, Func<string, bool> confirm, Action<string> log, CancellationToken ct)
    {
        var protectedRoot = Path.Combine(DataLocation.TrustedPath, "Drivers");
        string safeCopy;
        string? copyFolder = null;
        if (SafePath.IsSameOrInside(installer, protectedRoot))
        {
            safeCopy = installer;
        }
        else
        {
            log("Copying the installer to a protected folder...");
            copyFolder = SafePath.CreateAdminOnlyFolder("Drivers");
            safeCopy = Path.Combine(copyFolder, Path.GetFileName(installer));
            await using var src = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var dst = new FileStream(safeCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await src.CopyToAsync(dst, ct);
        }
        try
        {
            return await InstallCopyAsync(vendor, safeCopy, requireVendorSignature, expectedVersion, confirm, log, ct);
        }
        finally
        {
            // The copy can be over 1 GB; never leave it in ProgramData.
            if (copyFolder is not null) try { Directory.Delete(copyFolder, recursive: true); } catch { }
        }
    }

    private static async Task<InstallOutcome> InstallCopyAsync(GpuVendor vendor, string safeCopy, bool requireVendorSignature,
        string? expectedVersion, Func<string, bool> confirm, Action<string> log, CancellationToken ct)
    {
        // Hold the file open (read sharing only) until the installer has finished.
        await using var lockHandle = new FileStream(safeCopy, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!HasValidSignature(safeCopy, vendor, out var signer))
        {
            if (requireVendorSignature)
                throw new InvalidOperationException($"The installer is not signed by {vendor} (signer: {signer ?? "none"}). Installation stopped.");
            log($"Warning: installer signer is '{signer ?? "none"}', continuing because you confirmed it.");
        }
        else
        {
            log($"Signature verified: {signer}");
        }

        // Before anything is touched: a chipset driver, support assistant or web installer would
        // leave the PC without a graphics driver once the current one is removed.
        if (InstallerProblem(vendor, safeCopy) is { } problem)
            throw new InvalidOperationException(problem);

        // The installer unpacks into C:\NVIDIA or C:\AMD and runs what it unpacked as administrator.
        if (ExtractFolder(vendor) is { } extract)
        {
            try { SafePath.PrepareInstallerFolder(extract, log); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Could not prepare {extract} for the installer ({ex.Message}). The current driver was not touched. Rename or delete that folder and try again.");
            }
        }

        await EnsureRestorePointAsync("WinSolve - before graphics driver clean install", confirm, log, ct);

        var before = DriverState(vendor);
        var started = DateTime.Now;

        // NVIDIA's installer does its own clean install (-clean), so the working driver is only
        // replaced once the new one installs. Other vendors' installers don't, so the current
        // driver is removed first, after saving a copy that is put back if the new one fails.
        List<DisplayPackage> packages = [];
        string? backup = null;
        if (vendor != GpuVendor.Nvidia)
        {
            packages = await GetDisplayPackagesAsync(vendor, ct);
            if (packages.Count > 0) backup = await BackupDisplayPackagesAsync(packages, log, ct);
        }

        var removed = false;
        BlockWindowsUpdateDrivers(log);
        try
        {
            try
            {
                if (packages.Count > 0)
                {
                    removed = true;
                    await RemovePackagesAsync(vendor, packages, log, ct);
                }

                log($"Starting the installer: {Path.GetFileName(safeCopy)}");
                if (vendor == GpuVendor.Nvidia)
                {
                    log("Installing silently with a clean profile (-s -clean -noreboot). This takes a few minutes; the screen will flicker...");
                    var code = await InstallerProcess.RunAndWaitAsync(safeCopy, "-s -clean -noreboot -noeula", log, ct, InstallerDrain);
                    log($"NVIDIA installer finished (exit code {code?.ToString() ?? "unknown"}).");
                }
                else
                {
                    log(vendor == GpuVendor.Amd
                        ? "The AMD installer is opening. Choose 'Factory Reset' if it is offered, then follow the steps. WinSolve waits until it finishes."
                        : "The installer is opening. If it offers a clean installation, choose it. WinSolve waits until it finishes.");
                    await InstallerProcess.RunAndWaitAsync(safeCopy, null, log, ct, InstallerDrain);
                    log("Installer finished.");
                }
            }
            catch (Exception ex) when (removed && backup is not null)
            {
                // Cancelled or failed after the old driver was removed: never leave the PC without one.
                log(ex is OperationCanceledException
                    ? "The installation was stopped after the old driver was removed."
                    : $"The installation failed after the old driver was removed: {ex.Message}");
                if (ex is InstallerCancelledException { StillRunning: true } or InstallerStillRunningException)
                {
                    // Never install the old driver while the vendor's setup is still installing.
                    log($"The installer is still running, so the previous driver is not put back automatically. If the screen stays on the basic driver, in Device Manager choose the display adapter > Update driver > Browse my computer and pick {backup}");
                }
                else if (NoVendorDriverActive(SafeDriverState(vendor)))
                {
                    await PutBackupBackAsync(vendor, backup, log);
                }
                else
                {
                    log($"A {VendorLabel(vendor)} driver is active, so the backup is not reinstalled. Backup: {backup}");
                }
                throw;
            }
        }
        finally
        {
            RestoreWindowsUpdateDrivers(log);
        }

        ClearShaderCaches(vendor, log);
        RemoveExtractedFiles(vendor, started, log);

        log("Checking which driver Windows is using now...");
        await Task.Delay(3000, CancellationToken.None);
        var after = DriverState(vendor);
        var outcome = Verify(vendor, before, after, expectedVersion);
        if (removed && backup is not null && NoVendorDriverActive(after))
        {
            log(outcome.Message);
            log("The new driver is not active, so the previous one is put back.");
            outcome = await PutBackupBackAsync(vendor, backup, log)
                ? new(false, $"The new {VendorLabel(vendor)} driver did not install, so WinSolve put the previous driver back. Run the installer again or try another version. Backup: {backup}")
                : new(false, $"The new {VendorLabel(vendor)} driver did not install and the previous driver could not be put back automatically. In Device Manager choose the display adapter > Update driver > Browse my computer and pick {backup}, or run the installer again.");
        }
        log(outcome.Message);
        return outcome;
    }

    /// <summary>After a cancel, the installer is given this long to close by itself before WinSolve goes on.</summary>
    private static readonly TimeSpan InstallerDrain = TimeSpan.FromMinutes(15);

    private static List<AdapterState> SafeDriverState(GpuVendor vendor)
    {
        try { return DriverState(vendor); }
        catch { return []; }
    }

    private static string VendorLabel(GpuVendor vendor) => vendor switch { GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", GpuVendor.Intel => "Intel", _ => "graphics" };

    /// <summary>No adapter of the vendor runs its driver: none listed, or on Microsoft's basic display driver.</summary>
    private static bool NoVendorDriverActive(List<AdapterState> after) => after.Count == 0 || after.Any(a => a.BasicDriver);

    /// <summary>Reinstalls the backup and checks that the vendor driver is active again.</summary>
    private static async Task<bool> PutBackupBackAsync(GpuVendor vendor, string backup, Action<string> log)
    {
        try
        {
            var added = await RestoreDisplayBackupAsync(backup, log);
            await Task.Delay(3000, CancellationToken.None);
            var active = !NoVendorDriverActive(DriverState(vendor));
            log(active ? "The previous graphics driver is active again."
                : added ? "The previous graphics driver was reinstalled; restart the PC to load it."
                : $"Could not reinstall the previous graphics driver automatically. Its backup is in {backup}");
            return active || added;
        }
        catch (Exception ex)
        {
            log($"Could not reinstall the previous graphics driver automatically ({ex.Message}). Its backup is in {backup}");
            return false;
        }
    }

    // ───────────── Verification ─────────────

    private sealed record AdapterState(string Name, string WindowsVersion, bool BasicDriver)
    {
        public string Friendly(GpuVendor vendor) => vendor == GpuVendor.Nvidia ? NvidiaVersion(WindowsVersion) : WindowsVersion;
    }

    /// <summary>Every display adapter of this vendor, including ones left on Microsoft's basic driver.</summary>
    private static List<AdapterState> DriverState(GpuVendor vendor)
    {
        var ven = vendor switch { GpuVendor.Nvidia => "VEN_10DE", GpuVendor.Amd => "VEN_1002", GpuVendor.Intel => "VEN_8086", _ => "?" };
        return Wmi.Query("SELECT Name, DriverVersion, PNPDeviceID, InfFilename FROM Win32_VideoController")
            .Where(v => v.Str("PNPDeviceID").Contains(ven, StringComparison.OrdinalIgnoreCase))
            .Select(v => new AdapterState(v.Str("Name"), v.Str("DriverVersion"),
                v.Str("Name").Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                v.Str("InfFilename").Equals("display.inf", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static InstallOutcome Verify(GpuVendor vendor, List<AdapterState> before, List<AdapterState> after, string? expected)
    {
        var name = vendor switch { GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", GpuVendor.Intel => "Intel", _ => "graphics" };
        if (after.Count == 0)
            return new(false, $"Windows doesn't list any {name} graphics device right now. Restart the PC and check the Drivers page again.");

        if (after.Any(a => a.BasicDriver))
            return new(false, $"The {name} driver is not active: Windows is using its basic display driver. Restart the PC; if it stays like this, run the installer again.");

        var versions = after.Select(a => a.Friendly(vendor)).Distinct().ToList();
        var now = string.Join(", ", versions);
        if (expected is not null)
        {
            if (versions.All(v => v == expected))
                return new(true, $"Installed and active: {name} driver {expected}.");
            return new(false, $"Windows reports driver {now}, not {expected}. If the installer asked for a restart, restart and check again; otherwise run the installation again.");
        }

        var old = before.Where(b => !b.BasicDriver).Select(b => b.Friendly(vendor)).Distinct().ToList();
        if (old.Count > 0 && old.SequenceEqual(versions))
            return new(true, $"The {name} driver is active, version {now} (same as before; expected if you reinstalled the same version).");
        return new(true, $"Installed and active: {name} driver {now}.");
    }

    // ───────────── Keeping Windows Update out of the way ─────────────

    private const string SearchingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching";
    private const string PendingKey = @"SOFTWARE\WinSolve\Pending";

    /// <summary>
    /// While the old driver is gone, Windows Update would install its own (often older) driver
    /// for the "new" device and break the clean install. Turned off for the duration, then the
    /// previous setting is restored (also on the next start if WinSolve was closed meanwhile).
    /// </summary>
    private static void BlockWindowsUpdateDrivers(Action<string> log)
    {
        try
        {
            // Save the user's setting only once: if an earlier restore hasn't happened yet, the
            // current value is our own 0 and must not overwrite the real one.
            if (Reg.Get(RegistryHive.LocalMachine, PendingKey, "SearchOrderConfig") is not int)
            {
                var previous = Reg.Get(RegistryHive.LocalMachine, SearchingKey, "SearchOrderConfig");
                Reg.Set(RegistryHive.LocalMachine, PendingKey, "SearchOrderConfig", previous is int i ? i : -1, RegistryValueKind.DWord);
            }
            Reg.Set(RegistryHive.LocalMachine, SearchingKey, "SearchOrderConfig", 0, RegistryValueKind.DWord);
            log("Automatic driver downloads from Windows Update paused during the installation.");
        }
        catch (Exception ex)
        {
            log($"Could not pause Windows Update driver downloads: {ex.Message}");
        }
    }

    /// <summary>Restores the Windows Update driver setting saved by <see cref="BlockWindowsUpdateDrivers"/>.</summary>
    public static void RestoreWindowsUpdateDrivers(Action<string>? log = null)
    {
        try
        {
            if (Reg.Get(RegistryHive.LocalMachine, PendingKey, "SearchOrderConfig") is not int previous) return;
            if (previous == -1) Reg.Delete(RegistryHive.LocalMachine, SearchingKey, "SearchOrderConfig");
            else Reg.Set(RegistryHive.LocalMachine, SearchingKey, "SearchOrderConfig", previous, RegistryValueKind.DWord);
            Reg.Delete(RegistryHive.LocalMachine, PendingKey, "SearchOrderConfig");
            log?.Invoke("Windows Update driver downloads restored to their previous setting.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not restore the Windows Update driver setting: {ex.Message}");
        }
    }

    // ───────────── Leftovers ─────────────

    private static void ClearShaderCaches(GpuVendor vendor, Action<string> log)
    {
        string[] dirs = vendor switch
        {
            GpuVendor.Nvidia => [Path.Combine(InteractiveUser.LocalAppData, "NVIDIA", "DXCache"), Path.Combine(InteractiveUser.LocalAppData, "NVIDIA", "GLCache"), Path.Combine(InteractiveUser.LocalAppData, "D3DSCache")],
            GpuVendor.Amd => [Path.Combine(InteractiveUser.LocalAppData, "AMD", "DxCache"), Path.Combine(InteractiveUser.LocalAppData, "AMD", "DxcCache"), Path.Combine(InteractiveUser.LocalAppData, "D3DSCache")],
            _ => [Path.Combine(InteractiveUser.LocalAppData, "D3DSCache")],
        };
        foreach (var dir in dirs) ClearCache(dir, log);
    }

    /// <summary>The folder at the root of the Windows drive the vendor's installer unpacks into, if any.</summary>
    private static string? ExtractFolder(GpuVendor vendor)
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!;
        return vendor switch
        {
            GpuVendor.Nvidia => Path.Combine(drive, "NVIDIA"),
            GpuVendor.Amd => Path.Combine(drive, "AMD"),
            _ => null,
        };
    }

    /// <summary>
    /// NVIDIA unpacks every package to C:\NVIDIA\DisplayDriver\(version) and AMD to C:\AMD
    /// (hundreds of MB to several GB each time) and leaves them there. Only folders created
    /// during this installation are removed.
    /// </summary>
    private static void RemoveExtractedFiles(GpuVendor vendor, DateTime started, Action<string> log)
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!;
        var root = vendor switch
        {
            GpuVendor.Nvidia => Path.Combine(drive, "NVIDIA", "DisplayDriver"),
            GpuVendor.Amd => Path.Combine(drive, "AMD"),
            _ => null,
        };
        if (root is null || !Directory.Exists(root) || SafePath.HasReparsePoint(root)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SafePath.NoLinks(recursive: false)))
            {
                if (Directory.GetCreationTime(dir) < started.AddMinutes(-1)) continue;
                try
                {
                    SafeDelete.DeleteTree(dir, CancellationToken.None);
                    log($"Removed unpacked installer files: {dir}");
                }
                catch (Exception ex)
                {
                    log($"Could not remove {dir}: {ex.Message}");
                }
            }
            // Remove the now-empty parents NVIDIA created (C:\NVIDIA\DisplayDriver, C:\NVIDIA).
            foreach (var dir in new[] { root, Path.GetDirectoryName(root)! })
            {
                if (dir.Length > drive.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    try { Directory.Delete(dir); } catch { }
            }
        }
        catch (Exception ex)
        {
            log($"Could not clean up {root}: {ex.Message}");
        }
    }

    // ═══════════════════ Other drivers ═══════════════════

    private const string WindowsUpdatePrelude = """
        $sm = New-Object -ComObject Microsoft.Update.ServiceManager
        try { $sm.AddService2('7971f918-a847-4430-9279-4a52d1efe18d', 7, '') | Out-Null } catch {}
        $session = New-Object -ComObject Microsoft.Update.Session
        $searcher = $session.CreateUpdateSearcher()
        $searcher.ServiceID = '7971f918-a847-4430-9279-4a52d1efe18d'
        $searcher.SearchScope = 1
        $searcher.ServerSelection = 3
        'Searching Windows Update for driver updates...'
        try { $result = $searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0") }
        catch { "Windows Update could not be reached: $($_.Exception.Message)"; exit 1 }
        """;

    /// <summary>Lists pending driver updates on Windows Update (Microsoft Update catalog).</summary>
    public static Task CheckWindowsUpdateDriversAsync(Action<string> log, CancellationToken ct)
        => ProcessRunner.PowerShellAsync(WindowsUpdatePrelude + """

            if ($result.Updates.Count -eq 0) { 'All drivers are up to date according to Windows Update.' }
            else { "$($result.Updates.Count) driver update(s) available:"; foreach ($u in $result.Updates) { "  - $($u.Title)" } }
            """, log, ct);

    /// <summary>A driver update offered by Windows Update.</summary>
    public sealed record PendingDriverUpdate(string UpdateId, string Title, long Size, bool Optional);

    private sealed class PsUpdate
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public long Size { get; set; }
        public bool BrowseOnly { get; set; }
        public bool AutoSelect { get; set; }
    }

    private const string JsonMarker = "@@WINSOLVE-JSON@@";

    /// <summary>Pending driver updates with their size. Optional ones are those Windows itself wouldn't install automatically.</summary>
    public static async Task<List<PendingDriverUpdate>> GetWindowsUpdateDriversAsync(Action<string> log, CancellationToken ct)
    {
        var r = await ProcessRunner.PowerShellAsync(WindowsUpdatePrelude + $$"""

            $out = foreach ($u in $result.Updates) {
                [pscustomobject]@{ Id = "$($u.Identity.UpdateID)"; Title = "$($u.Title)"; Size = [int64]$u.MaxDownloadSize; BrowseOnly = [bool]$u.BrowseOnly; AutoSelect = [bool]$u.AutoSelectOnWebSites }
            }
            '{{JsonMarker}}' + (ConvertTo-Json -InputObject @($out) -Compress)
            """, l => { if (!l.StartsWith(JsonMarker, StringComparison.Ordinal)) log(l); }, ct);
        var line = r.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(JsonMarker, StringComparison.Ordinal));
        if (!r.Success || line is null) throw new InvalidOperationException("Windows Update could not list the driver updates.");
        var list = JsonSerializer.Deserialize<List<PsUpdate>>(line[JsonMarker.Length..], new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        return list.Where(u => Guid.TryParse(u.Id, out _))
            .Select(u => new PendingDriverUpdate(u.Id!, u.Title ?? "", u.Size, u.BrowseOnly || !u.AutoSelect))
            .ToList();
    }

    /// <summary>
    /// Downloads and installs only the chosen driver updates (matched by update ID), after a
    /// restore point (asks whether to continue if it can't be created).
    /// </summary>
    public static async Task InstallWindowsUpdateDriversAsync(IReadOnlyList<PendingDriverUpdate> selected, Func<string, bool> confirm,
        Action<string> log, CancellationToken ct)
    {
        var ids = selected.Select(u => u.UpdateId).Where(id => Guid.TryParse(id, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
        {
            log("No driver updates selected.");
            return;
        }
        await EnsureRestorePointAsync("WinSolve - before installing driver updates", confirm, log, ct);

        var wanted = string.Join(", ", ids.Select(id => $"'{id}'"));
        var r = await ProcessRunner.PowerShellAsync(WindowsUpdatePrelude + $$"""

            $wanted = @({{wanted}})
            $list = New-Object -ComObject Microsoft.Update.UpdateColl
            foreach ($u in $result.Updates) {
                if ($wanted -contains "$($u.Identity.UpdateID)") { if (-not $u.EulaAccepted) { $u.AcceptEula() }; $list.Add($u) | Out-Null; "Queued: $($u.Title)" }
            }
            if ($list.Count -eq 0) { 'The selected driver updates are no longer offered.'; exit 1 }
            $codes = @{ 0 = 'not started'; 1 = 'in progress'; 2 = 'installed'; 3 = 'installed with errors'; 4 = 'FAILED'; 5 = 'aborted' }
            try {
                'Downloading...'
                $dl = $session.CreateUpdateDownloader(); $dl.Updates = $list; $dlr = $dl.Download()
                $ready = New-Object -ComObject Microsoft.Update.UpdateColl
                for ($i = 0; $i -lt $list.Count; $i++) {
                    $c = $dlr.GetUpdateResult($i).ResultCode
                    if ($c -eq 2 -or $c -eq 3) { $ready.Add($list.Item($i)) | Out-Null } else { "  $($list.Item($i).Title): download $($codes[[int]$c])" }
                }
                if ($ready.Count -eq 0) { 'Nothing could be downloaded.'; exit 1 }
                'Installing...'
                $inst = $session.CreateUpdateInstaller(); $inst.Updates = $ready; $res = $inst.Install()
                $failed = 0
                for ($i = 0; $i -lt $ready.Count; $i++) {
                    $c = $res.GetUpdateResult($i).ResultCode
                    if ($c -ne 2) { $failed++ }
                    "  $($ready.Item($i).Title): $($codes[[int]$c])"
                }
                if ($res.RebootRequired) { 'A restart is required to finish installing drivers.' }
                if ($failed -gt 0) { "$failed driver update(s) did not install correctly."; exit 1 } else { 'All selected driver updates installed.' }
            } catch { "Windows Update error: $($_.Exception.Message)"; exit 1 }
            """, log, ct);
        if (!r.Success) throw new InvalidOperationException("Some driver updates did not install. See the details above.");
    }

    /// <summary>Restarts, and where it's safe reinstalls, the devices in the plan.</summary>
    public static async Task RepairProblemDevicesAsync(IReadOnlyList<HardwareService.DeviceRepairPlan> plans, Action<string> log, CancellationToken ct)
    {
        if (plans.Count == 0)
        {
            log("No devices with problems that a restart or reinstall can fix.");
            return;
        }
        foreach (var p in plans)
        {
            log($"> Restarting {p.Device.Name} (code {p.Device.ErrorCode})");
            await HardwareService.RestartDeviceAsync(p.Device.InstanceId, l => log("  " + l), ct);
        }
        await Task.Delay(3000, ct);

        var stillFailing = HardwareService.GetProblemDevices().ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);
        foreach (var p in plans)
        {
            if (!stillFailing.TryGetValue(p.Device.InstanceId, out var now)) continue;
            // Re-checked with the current code and class: only reinstall what is safe to remove.
            if (HardwareService.RepairActionFor(now.Class, now.ErrorCode) != HardwareService.DeviceRepairAction.RestartThenReinstall)
            {
                log($"> Still failing, not reinstalled (only restarted, to keep this kind of device safe): {now.Name}");
                continue;
            }
            log($"> Still failing, reinstalling: {now.Name}");
            await HardwareService.ReinstallDeviceAsync(now.InstanceId, l => log("  " + l), ct);
        }
        await Task.Delay(2000, ct);
        var ids = plans.Select(p => p.Device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = HardwareService.GetProblemDevices().Count(d => ids.Contains(d.InstanceId));
        log(left == 0 ? "All the repaired devices are working." : $"{left} device(s) still have problems. Try Windows Update drivers or the manufacturer's website.");
    }

    /// <summary>Exports every third-party driver (DISM) so they can be restored later.</summary>
    public static async Task<string> BackupDriversAsync(Action<string> log, CancellationToken ct)
    {
        var folder = Path.Combine(InteractiveUser.Documents, "WinSolve Driver Backup", DateTime.Now.ToString("yyyy-MM-dd_HHmm"));
        Directory.CreateDirectory(folder);
        if (SafePath.HasReparsePoint(folder)) throw new InvalidOperationException("The backup folder is a link; choose another location.");
        log($"Exporting drivers to {folder}");
        var r = await ProcessRunner.RunAsync("dism.exe", $"/Online /Export-Driver /Destination:\"{folder}\" /English", log, ct);
        var count = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.inf", SearchOption.AllDirectories).Length : 0;
        log(r.Success ? $"Backup complete: {count} driver(s) saved." : $"DISM reported an error (exit code {r.ExitCode}); {count} driver(s) were saved.");
        return folder;
    }
}
