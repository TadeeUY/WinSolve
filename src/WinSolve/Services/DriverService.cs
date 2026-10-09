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

        var match = products.FirstOrDefault(p => Normalize(p.Name) == wanted);
        if (match.Name is null or "")
        {
            // Never pick a desktop package for a laptop GPU (or the other way round): it won't install.
            static bool IsMobile(string n) => n.Contains("laptop", StringComparison.OrdinalIgnoreCase) || n.Contains("notebook", StringComparison.OrdinalIgnoreCase)
                || n.Contains("max-q", StringComparison.OrdinalIgnoreCase) || n.Contains("mobile", StringComparison.OrdinalIgnoreCase);
            var mobile = IsMobile(gpu.Name);
            match = products.Where(p => IsMobile(p.Name) == mobile && (wanted.Contains(Normalize(p.Name)) || Normalize(p.Name).Contains(wanted)))
                .OrderByDescending(p => p.Name.Length).FirstOrDefault();
        }
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

    /// <summary>
    /// Removes every installed display driver package from the given vendor (current and
    /// older versions left in the driver store), like DDU does with pnputil.
    /// </summary>
    public static async Task RemoveDisplayDriversAsync(GpuVendor vendor, Action<string> log, CancellationToken ct)
    {
        var provider = vendor switch
        {
            GpuVendor.Nvidia => "^NVIDIA",
            // Anchored: an unanchored "ATI" would also match "Corporation" (Intel/NVIDIA Corporation).
            GpuVendor.Amd => "^(Advanced Micro Devices|AMD|ATI Technologies)",
            GpuVendor.Intel => "^Intel",
            _ => throw new ArgumentException("Unknown vendor"),
        };

        log("Removing the current graphics driver (the screen may flicker or change resolution)...");
        await ProcessRunner.PowerShellAsync($$"""
            $drivers = Get-WindowsDriver -Online | Where-Object { $_.ClassName -eq 'Display' -and $_.ProviderName -match '{{provider}}' }
            if (-not $drivers) { 'No driver packages found for this vendor.' }
            foreach ($d in $drivers) {
                "Removing $($d.Driver) - $($d.ProviderName) $($d.Version)"
                pnputil.exe /delete-driver $d.Driver /uninstall /force | Out-String
            }
            """, log, ct);

        if (vendor == GpuVendor.Nvidia)
        {
            // Leftover shader caches and installer data from the previous driver.
            foreach (var dir in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "DXCache"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "GLCache"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache"),
                     })
            {
                ClearCache(dir, log);
            }
        }
        else if (vendor == GpuVendor.Amd)
        {
            foreach (var dir in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMD", "DxCache"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMD", "DxcCache"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache"),
                     })
            {
                ClearCache(dir, log);
            }
        }
    }

    private static void ClearCache(string dir, Action<string> log)
    {
        if (!Directory.Exists(dir) || SafePath.HasReparsePoint(dir)) return;
        try { Directory.Delete(dir, true); log($"Cleared {dir}"); } catch { /* in use */ }
    }

    /// <summary>
    /// Clean install: restore point, full removal of the old driver, then the new installer.
    /// The installer is copied into an administrators-only folder and kept open (no writes,
    /// no deletes) while its signature is verified and it runs, so it cannot be swapped.
    /// NVIDIA packages install silently with -clean; AMD's installer opens so you can pick
    /// "Factory Reset" and the components you want.
    /// </summary>
    public static async Task CleanInstallAsync(GpuVendor vendor, string installer, bool requireVendorSignature, Action<string> log, CancellationToken ct)
    {
        var protectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinSolve", "Drivers");
        string safeCopy;
        if (SafePath.IsSameOrInside(installer, protectedRoot))
        {
            safeCopy = installer;
        }
        else
        {
            log("Copying the installer to a protected folder...");
            safeCopy = Path.Combine(SafePath.CreateAdminOnlyFolder("Drivers"), Path.GetFileName(installer));
            await using var src = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var dst = new FileStream(safeCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await src.CopyToAsync(dst, ct);
        }

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

        var ctx = new TaskContext(log, ct);
        if (AppSettings.Current.CreateRestorePoint)
            await TaskCatalog.CreateRestorePoint(ctx, "WinSolve - before graphics driver clean install");

        // NVIDIA's installer does its own clean install (-clean), so the working driver is only
        // replaced once the new one installs. Other vendors' installers don't, so remove first.
        if (vendor != GpuVendor.Nvidia)
            await RemoveDisplayDriversAsync(vendor, log, ct);

        log($"Starting the installer: {Path.GetFileName(safeCopy)}");
        if (vendor == GpuVendor.Nvidia)
        {
            log("Installing silently with a clean profile (-s -clean -noreboot). This takes a few minutes...");
            var r = await ProcessRunner.RunAsync(safeCopy, "-s -clean -noreboot -noeula", log, ct);
            log(r.Success ? "NVIDIA driver installed." : $"Installer exit code: {r.ExitCode}. If nothing was installed, run the installer manually.");
        }
        else
        {
            if (vendor == GpuVendor.Amd)
                log("The AMD installer is opening. Choose 'Factory Reset' if it is offered, then follow the steps.");
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(safeCopy)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(safeCopy)!,
            });
            if (p is not null) await p.WaitForExitAsync(ct);
            log("Installer closed.");
        }
        log("Restart the PC to finish the clean install.");
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
        $result = $searcher.Search("IsInstalled=0 and Type='Driver'")
        """;

    /// <summary>Lists pending driver updates on Windows Update (Microsoft Update catalog).</summary>
    public static Task CheckWindowsUpdateDriversAsync(Action<string> log, CancellationToken ct)
        => ProcessRunner.PowerShellAsync(WindowsUpdatePrelude + """

            if ($result.Updates.Count -eq 0) { 'All drivers are up to date according to Windows Update.' }
            else { "$($result.Updates.Count) driver update(s) available:"; foreach ($u in $result.Updates) { "  - $($u.Title)" } }
            """, log, ct);

    /// <summary>Downloads and installs every pending driver update from Windows Update.</summary>
    public static Task InstallWindowsUpdateDriversAsync(Action<string> log, CancellationToken ct)
        => ProcessRunner.PowerShellAsync(WindowsUpdatePrelude + """

            if ($result.Updates.Count -eq 0) { 'No driver updates to install.'; return }
            $list = New-Object -ComObject Microsoft.Update.UpdateColl
            foreach ($u in $result.Updates) { if (-not $u.EulaAccepted) { $u.AcceptEula() }; $list.Add($u) | Out-Null; "Queued: $($u.Title)" }
            'Downloading...'
            $dl = $session.CreateUpdateDownloader(); $dl.Updates = $list; $dl.Download() | Out-Null
            'Installing...'
            $inst = $session.CreateUpdateInstaller(); $inst.Updates = $list; $res = $inst.Install()
            for ($i = 0; $i -lt $list.Count; $i++) { "  $($list.Item($i).Title): result code $($res.GetUpdateResult($i).ResultCode)" }
            if ($res.RebootRequired) { 'A restart is required to finish installing drivers.' }
            """, log, ct);

    /// <summary>Restarts, and if needed reinstalls, every device with an error.</summary>
    public static async Task RepairProblemDevicesAsync(Action<string> log, CancellationToken ct)
    {
        var devices = HardwareService.GetProblemDevices();
        if (devices.Count == 0)
        {
            log("No devices with problems.");
            return;
        }
        foreach (var d in devices)
        {
            log($"> {d.Name} (code {d.ErrorCode})");
            await HardwareService.RestartDeviceAsync(d.InstanceId, l => log("  " + l), ct);
        }
        await Task.Delay(3000, ct);
        foreach (var d in HardwareService.GetProblemDevices())
        {
            log($"> Still failing, reinstalling: {d.Name}");
            await HardwareService.ReinstallDeviceAsync(d.InstanceId, l => log("  " + l), ct);
        }
        var left = HardwareService.GetProblemDevices();
        log(left.Count == 0 ? "All devices are working." : $"{left.Count} device(s) still have problems. Try Windows Update drivers or the manufacturer's website.");
    }

    /// <summary>Exports every third-party driver (DISM) so they can be restored later.</summary>
    public static async Task<string> BackupDriversAsync(Action<string> log, CancellationToken ct)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WinSolve Driver Backup", DateTime.Now.ToString("yyyy-MM-dd_HHmm"));
        Directory.CreateDirectory(folder);
        if (SafePath.HasReparsePoint(folder)) throw new InvalidOperationException("The backup folder is a link; choose another location.");
        log($"Exporting drivers to {folder}");
        await ProcessRunner.RunAsync("dism.exe", $"/Online /Export-Driver /Destination:\"{folder}\" /English", log, ct);
        return folder;
    }
}
