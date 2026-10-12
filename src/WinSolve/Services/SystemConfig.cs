using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed record DnsProvider(string Name, string[] IPv4, string[] IPv6)
{
    /// <summary>"Default (DHCP)": restores the saved settings (or DHCP) instead of setting servers.</summary>
    public bool IsDefault => IPv4.Length == 0;
}

/// <summary>A connected physical network adapter and its DNS settings.</summary>
public sealed class DnsAdapter
{
    public string Name { get; set; } = "";
    public int Index { get; set; }
    public string Guid { get; set; } = "";

    /// <summary>The IPv4 address comes from DHCP (false: static IP).</summary>
    public bool Dhcp { get; set; }

    /// <summary>
    /// No IPv4 settings of its own (e.g. the network card bound to a Hyper-V external switch,
    /// whose traffic goes through the vEthernet adapter): skipped.
    /// </summary>
    public bool NoIp { get; set; }

    /// <summary>Connected to a domain network (DNS is the organization's; never changed).</summary>
    public bool Domain { get; set; }

    public string[] V4 { get; set; } = [];
    public string[] V6 { get; set; } = [];

    /// <summary>The IPv4 / IPv6 DNS servers were set by hand (false: they come from DHCP / the router).</summary>
    public bool ManualV4 { get; set; }
    public bool ManualV6 { get; set; }
}

/// <summary>An adapter's DNS settings before WinSolve first changed them.</summary>
public sealed class DnsBackupEntry
{
    public string Name { get; set; } = "";
    public bool ManualV4 { get; set; }
    public bool ManualV6 { get; set; }
    public string[] V4 { get; set; } = [];
    public string[] V6 { get; set; } = [];
    public DateTime Saved { get; set; }
}

public enum DnsRestoreKind { SetSaved, ResetToDhcp, Keep }

/// <summary>What "Default (DHCP)" does on one adapter.</summary>
public sealed record DnsRestorePlan(DnsAdapter Adapter, DnsRestoreKind Kind, string[] Servers, bool ResetFirst);

/// <summary>
/// DNS server presets for the connected physical adapters. The settings each adapter had before
/// WinSolve first changed it are saved, and "Default (DHCP)" puts them back.
/// </summary>
public static class DnsService
{
    public static readonly DnsProvider[] Providers =
    [
        new("Default (DHCP)", [], []),
        new("Cloudflare", ["1.1.1.1", "1.0.0.1"], ["2606:4700:4700::1111", "2606:4700:4700::1001"]),
        new("Cloudflare (block malware)", ["1.1.1.2", "1.0.0.2"], ["2606:4700:4700::1112", "2606:4700:4700::1002"]),
        new("Cloudflare (block malware and adult content)", ["1.1.1.3", "1.0.0.3"], ["2606:4700:4700::1113", "2606:4700:4700::1003"]),
        new("Google", ["8.8.8.8", "8.8.4.4"], ["2001:4860:4860::8888", "2001:4860:4860::8844"]),
        new("Quad9", ["9.9.9.9", "149.112.112.112"], ["2620:fe::fe", "2620:fe::9"]),
        new("AdGuard (block ads)", ["94.140.14.14", "94.140.15.15"], ["2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff"]),
        new("OpenDNS", ["208.67.222.222", "208.67.220.220"], ["2620:119:35::35", "2620:119:53::53"]),
    ];

    private static string BackupPath => Path.Combine(SafePath.DataFolder, "dns-backup.json");

    /// <summary>Name of the provider currently configured (or "Custom" / "Default (DHCP)").</summary>
    public static async Task<string> CurrentAsync()
    {
        var r = await ProcessRunner.PowerShellAsync("""
            $a = Get-NetAdapter | Where-Object Status -eq 'Up' | Sort-Object -Property @{ Expression = { -not $_.HardwareInterface } } | Select-Object -First 1
            if ($a) { (Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4).ServerAddresses -join ',' }
            """);
        var servers = r.Output.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (servers.Length == 0) return "Default (DHCP)";
        var match = Providers.FirstOrDefault(p => p.IPv4.Length > 0 && p.IPv4[0] == servers[0]);
        if (match is not null) return match.Name;
        // Servers set by hand vs. the ones DHCP handed out.
        var dhcp = await ProcessRunner.PowerShellAsync("""
            $a = Get-NetAdapter | Where-Object Status -eq 'Up' | Sort-Object -Property @{ Expression = { -not $_.HardwareInterface } } | Select-Object -First 1
            if ($a) { (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$($a.InterfaceGuid)" -ErrorAction SilentlyContinue).NameServer }
            """);
        return dhcp.Output.Trim().Length > 0 ? "Custom" : "Default (DHCP)";
    }

    /// <summary>Connected physical adapters with their DNS settings, IP addressing and network profile.</summary>
    public static async Task<List<DnsAdapter>> GetAdaptersAsync(CancellationToken ct = default)
    {
        var r = await ProcessRunner.PowerShellAsync("""
            $physical = @(Get-NetAdapter -Physical -ErrorAction Stop | Where-Object Status -eq 'Up')
            # A Hyper-V external switch carries the traffic through a vEthernet adapter: include those
            # that have IPv4 and a default gateway (not the internal "Default Switch" or WSL ones).
            $virtual = @(Get-NetAdapter -ErrorAction Stop | Where-Object { $_.Status -eq 'Up' -and $_.Name -like 'vEthernet*' } | Where-Object {
                $c = Get-NetIPConfiguration -InterfaceIndex $_.ifIndex -ErrorAction SilentlyContinue
                $c -and $c.IPv4Address -and $c.IPv4DefaultGateway
            })
            $out = foreach ($a in @($physical + $virtual)) {
                $ip = Get-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
                $profiles = @(Get-NetConnectionProfile -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue)
                $v4 = @((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses | Where-Object { $_ })
                $v6 = @((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv6 -ErrorAction SilentlyContinue).ServerAddresses | Where-Object { $_ })
                $k4 = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$($a.InterfaceGuid)" -ErrorAction SilentlyContinue).NameServer
                $k6 = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\$($a.InterfaceGuid)" -ErrorAction SilentlyContinue).NameServer
                [pscustomobject]@{
                    Name = "$($a.Name)"; Index = [int]$a.ifIndex; Guid = "$($a.InterfaceGuid)"
                    Dhcp = ("$($ip.Dhcp)" -eq 'Enabled'); NoIp = ($null -eq $ip)
                    Domain = (@($profiles | Where-Object { "$($_.NetworkCategory)" -eq 'DomainAuthenticated' }).Count -gt 0)
                    V4 = [string[]]$v4; V6 = [string[]]$v6
                    ManualV4 = [bool]"$k4".Trim(); ManualV6 = [bool]"$k6".Trim()
                }
            }
            ConvertTo-Json -InputObject @($out) -Compress -Depth 3
            """, ct: ct);
        var start = r.Output.IndexOf('[');
        if (!r.Success || start < 0)
            throw new InvalidOperationException(Localization.Loc.T("Could not read the network adapters.") + " " + r.Output.Trim());
        var list = JsonSerializer.Deserialize<List<DnsAdapter>>(r.Output[start..], new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        foreach (var a in list)
        {
            a.V4 = (a.V4 ?? []).Where(IsAddress).ToArray();
            a.V6 = (a.V6 ?? []).Where(IsAddress).ToArray();
        }
        return list;
    }

    private static bool IsAddress(string? s) => !string.IsNullOrWhiteSpace(s) && IPAddress.TryParse(s, out _);

    // ───────────── Backup of the previous settings ─────────────

    public static Dictionary<string, DnsBackupEntry> LoadBackup()
    {
        var result = new Dictionary<string, DnsBackupEntry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(BackupPath)) return result;
        try
        {
            foreach (var (guid, entry) in JsonSerializer.Deserialize<Dictionary<string, DnsBackupEntry>>(File.ReadAllText(BackupPath)) ?? [])
                if (entry is not null) result[guid] = entry;
        }
        catch (JsonException ex)
        {
            var aside = BackupPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Move(BackupPath, aside, overwrite: true);
            Logger.Write($"{BackupPath} was damaged ({ex.Message}); kept it as {aside}.");
        }
        return result;
    }

    private static void SaveBackup(Dictionary<string, DnsBackupEntry> backup)
    {
        if (backup.Count == 0)
        {
            File.Delete(BackupPath);
            return;
        }
        var temp = BackupPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, BackupPath, overwrite: true);
    }

    /// <summary>
    /// What "Default (DHCP)" does on an adapter: put back the servers saved before WinSolve changed
    /// them, or go back to DHCP. A static-IP adapter is never reset (it would be left without DNS).
    /// </summary>
    public static DnsRestorePlan PlanDefault(DnsAdapter adapter, DnsBackupEntry? backup)
    {
        if (backup is not null)
        {
            var saved = (backup.ManualV4 ? backup.V4 : []).Concat(backup.ManualV6 ? backup.V6 : []).Where(IsAddress).ToArray();
            if (saved.Length > 0)
                // On a DHCP adapter, reset first so the family that wasn't set by hand comes from DHCP again.
                return new(adapter, DnsRestoreKind.SetSaved, saved, ResetFirst: adapter.Dhcp && !(backup.ManualV4 && backup.ManualV6));
        }
        return adapter.Dhcp
            ? new(adapter, DnsRestoreKind.ResetToDhcp, [], false)
            : new(adapter, DnsRestoreKind.Keep, [], false);
    }

    private static string Ps(string s) => "'" + s.Replace("'", "''") + "'";

    private static string Addresses(IEnumerable<string> servers)
        => "@(" + string.Join(",", servers.Where(IsAddress).Select(s => Ps(IPAddress.Parse(s).ToString()))) + ")";

    /// <summary>
    /// Sets the provider's servers on the given adapters, saving each adapter's current settings
    /// first (once: the original is kept). Nothing is changed when the backup can't be saved.
    /// </summary>
    public static async Task ApplyAsync(DnsProvider provider, IReadOnlyList<DnsAdapter> adapters, Action<string> log, CancellationToken ct = default)
    {
        var backup = LoadBackup();
        foreach (var a in adapters.Where(a => !backup.ContainsKey(a.Guid)))
            backup[a.Guid] = new DnsBackupEntry { Name = a.Name, ManualV4 = a.ManualV4, ManualV6 = a.ManualV6, V4 = a.V4, V6 = a.V6, Saved = DateTime.Now };
        SaveBackup(backup);
        log("Previous DNS settings saved.");

        log($"Setting DNS: {provider.Name}");
        var steps = adapters.Select(a => (a, $"Set-DnsClientServerAddress -InterfaceIndex {a.Index} -ServerAddresses {Addresses(provider.IPv4.Concat(provider.IPv6))} -ErrorAction Stop",
            $"DNS set on {a.Name}")).ToList();
        var (_, ok) = await RunStepsAsync(steps, log, ct);
        if (!ok) throw StepsFailed();
    }

    /// <summary>Carries out "Default (DHCP)" plans; each adapter's backup is dropped once it is restored.</summary>
    public static async Task RestoreAsync(IReadOnlyList<DnsRestorePlan> plans, Action<string> log, CancellationToken ct = default)
    {
        var steps = new List<(DnsAdapter, string, string)>();
        foreach (var p in plans)
        {
            switch (p.Kind)
            {
                case DnsRestoreKind.SetSaved:
                    steps.Add((p.Adapter,
                        (p.ResetFirst ? $"Set-DnsClientServerAddress -InterfaceIndex {p.Adapter.Index} -ResetServerAddresses -ErrorAction Stop; " : "")
                        + $"Set-DnsClientServerAddress -InterfaceIndex {p.Adapter.Index} -ServerAddresses {Addresses(p.Servers)} -ErrorAction Stop",
                        $"Previous DNS restored on {p.Adapter.Name}"));
                    break;
                case DnsRestoreKind.ResetToDhcp:
                    steps.Add((p.Adapter, $"Set-DnsClientServerAddress -InterfaceIndex {p.Adapter.Index} -ResetServerAddresses -ErrorAction Stop",
                        $"DNS from DHCP on {p.Adapter.Name}"));
                    break;
                default:
                    log($"{p.Adapter.Name}: static IP address, its DNS servers were kept (resetting would leave it without DNS).");
                    break;
            }
        }
        var (done, ok) = await RunStepsAsync(steps, log, ct);

        // Adapters that got their settings back no longer need the backup; failed ones keep it.
        var backup = LoadBackup();
        foreach (var guid in done) backup.Remove(guid);
        SaveBackup(backup);
        if (!ok) throw StepsFailed();
    }

    private static InvalidOperationException StepsFailed()
        => new(Localization.Loc.T("The DNS could not be changed on every adapter (see the errors above)."));

    /// <summary>Runs one PowerShell command per adapter and reports every failure. Returns the adapters changed.</summary>
    private static async Task<(List<string> Done, bool Ok)> RunStepsAsync(List<(DnsAdapter Adapter, string Command, string Ok)> steps, Action<string> log, CancellationToken ct)
    {
        const string marker = "#winsolve-ok ";
        var script = string.Join("\n", steps.Select(s =>
            $"try {{ {s.Command}; {Ps(s.Ok)}; {Ps(marker + s.Adapter.Guid)} }} catch {{ 'ERROR ' + {Ps(s.Adapter.Name)} + ': ' + $_.Exception.Message; $failed = $true }}"))
            + "\ntry { Clear-DnsClientCache -ErrorAction Stop } catch { 'Could not clear the DNS cache: ' + $_.Exception.Message }\nif ($failed) { exit 1 }";
        var done = new List<string>();
        var r = await ProcessRunner.PowerShellAsync(script, line =>
        {
            if (line.StartsWith(marker, StringComparison.Ordinal)) lock (done) done.Add(line[marker.Length..].Trim());
            else log(line);
        }, ct);
        return (done, r.Success);
    }
}

/// <summary>Windows Update policy presets (as in WinUtil's Updates tab).</summary>
public static class UpdatePolicy
{
    private const string Wu = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string Au = Wu + @"\AU";

    /// <summary>Owner of the previous values saved in <see cref="TweakBackup"/>.</summary>
    private const string Owner = "wu-security";

    private static readonly (string Path, string Name, int Value)[] SecurityValues =
    [
        (Wu, "DeferFeatureUpdates", 1),
        (Wu, "DeferFeatureUpdatesPeriodInDays", 365),
        (Wu, "DeferQualityUpdates", 1),
        (Wu, "DeferQualityUpdatesPeriodInDays", 4),
        (Wu, "ExcludeWUDriversInQualityUpdate", 1),
        (Au, "NoAutoRebootWithLoggedOnUsers", 1),
    ];

    /// <summary>Windows Home (EditionID Core*), which ignores the update deferral policies.</summary>
    public static bool IsHomeEdition
        => (Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID") as string)
           ?.StartsWith("Core", StringComparison.OrdinalIgnoreCase) == true;

    private static bool DeferralSet()
        => Reg.ValueEquals(Reg.Get(RegistryHive.LocalMachine, Wu, "DeferFeatureUpdatesPeriodInDays"), 365);

    /// <summary>Security settings are in effect (never on Home, which ignores them).</summary>
    public static bool IsSecurityMode() => DeferralSet() && !IsHomeEdition;

    /// <summary>
    /// Security settings: feature updates are delayed one year, security/quality updates four days,
    /// drivers are not installed through Windows Update and no automatic restarts while signed in.
    /// The previous values are saved so Default can put them back.
    /// </summary>
    public static void ApplySecurity()
    {
        var hklm = RegistryHive.LocalMachine;
        TweakBackup.Remember(Owner, SecurityValues.Select(v => (hklm, v.Path, v.Name, (object)v.Value)));
        foreach (var (path, name, value) in SecurityValues) Reg.Set(hklm, path, name, value, RegistryValueKind.DWord);
        Logger.Write("Windows Update policy: security settings applied.");
    }

    /// <summary>
    /// Undoes what Security settings changed: each value it set goes back to what it was. Values it
    /// didn't change (e.g. set by the "Don't install drivers" or "No automatic restarts" tweaks) stay.
    /// </summary>
    public static void ApplyDefault()
    {
        var hklm = RegistryHive.LocalMachine;
        var restored = 0;
        foreach (var (path, name, value) in SecurityValues)
        {
            if (TweakBackup.TryRestore(Owner, hklm, path, name)) restored++;
            // Nothing saved (applied by an older version): a deferral value still at what Security
            // sets is removed; nothing else in WinSolve sets those. The other two belong to tweaks too.
            else if (name.StartsWith("Defer", StringComparison.Ordinal) && Reg.ValueEquals(Reg.Get(hklm, path, name), value))
                Reg.Delete(hklm, path, name);
        }
        // Applied by an older version (nothing saved at all): like before, the driver and restart
        // values Security set are removed too, or drivers would stay blocked with "Default" shown.
        if (restored == 0)
            foreach (var (path, name, value) in SecurityValues)
                if (!name.StartsWith("Defer", StringComparison.Ordinal) && Reg.ValueEquals(Reg.Get(hklm, path, name), value))
                    Reg.Delete(hklm, path, name);
        TweakBackup.Forget(Owner, SecurityValues.Select(v => (hklm, v.Path, v.Name)));
        Logger.Write($"Windows Update policy: default settings restored ({restored} saved value(s) put back).");
    }
}

public sealed record WindowsFeature(string Name, string Description, string[] FeatureNames)
{
    public bool? Enabled { get; set; }

    /// <summary>What stops working when the feature is turned off (shown before disabling it).</summary>
    public string DisableEffect { get; init; } = "";
}

/// <summary>Optional Windows features (DISM), as in WinUtil's Config tab.</summary>
public static class WindowsFeatures
{
    public static List<WindowsFeature> Catalog() =>
    [
        new(".NET Framework 3.5", "Needed by many older programs and games.", ["NetFx3"])
        {
            DisableEffect = "Older programs and games that need .NET Framework 3.5 won't start.",
        },
        new("Windows Subsystem for Linux (WSL)", "Run Linux distributions on Windows.", ["Microsoft-Windows-Subsystem-Linux", "VirtualMachinePlatform"])
        {
            DisableEffect = "Your WSL Linux distributions stop working (their files stay, but can't be opened until it's turned back on). Docker Desktop, Android apps and other apps that use the Virtual Machine Platform stop working too.",
        },
        new("Hyper-V", "Microsoft's virtualization platform (Pro/Enterprise editions).", ["Microsoft-Hyper-V-All"])
        {
            DisableEffect = "Existing Hyper-V virtual machines can't start, and Docker Desktop in Hyper-V mode stops working.",
        },
        new("Windows Sandbox", "A disposable desktop for running untrusted apps safely (Pro/Enterprise).", ["Containers-DisposableClientVM"])
        {
            DisableEffect = "Windows Sandbox is no longer available.",
        },
        new("Legacy components (DirectPlay)", "Needed by some old games.", ["LegacyComponents", "DirectPlay"])
        {
            DisableEffect = "Old games that need DirectPlay stop working.",
        },
    ];

    private sealed class PsFeature
    {
        public string? FeatureName { get; set; }
        public string? State { get; set; }
    }

    public static async Task LoadStatesAsync(List<WindowsFeature> features)
    {
        var names = features.SelectMany(f => f.FeatureNames).Distinct().ToArray();
        var list = string.Join(",", names.Select(n => "'" + n + "'"));
        var r = await ProcessRunner.PowerShellAsync($$"""
            $out = foreach ($n in @({{list}})) { $f = Get-WindowsOptionalFeature -Online -FeatureName $n -ErrorAction SilentlyContinue; if ($f) { [pscustomobject]@{ FeatureName = $f.FeatureName; State = "$($f.State)" } } }
            ConvertTo-Json -InputObject @($out) -Compress
            """);
        var start = r.Output.IndexOf('[');
        if (start < 0) return;
        var states = JsonSerializer.Deserialize<List<PsFeature>>(r.Output[start..], new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        foreach (var f in features)
        {
            var mine = states.Where(s => f.FeatureNames.Contains(s.FeatureName, StringComparer.OrdinalIgnoreCase)).ToList();
            // "EnablePending" = turned on, waiting for the restart (don't flip the switch back off).
            f.Enabled = mine.Count == 0 ? null : mine.All(s => s.State is "Enabled" or "EnablePending");
        }
    }

    public static Task SetAsync(WindowsFeature feature, bool enable, Action<string> log, CancellationToken ct = default)
    {
        var verb = enable ? "Enable-WindowsOptionalFeature" : "Disable-WindowsOptionalFeature";
        var all = enable ? "-All" : "";
        var script = string.Join("\n", feature.FeatureNames.Select(n =>
            $"try {{ $r = {verb} -Online -FeatureName '{n}' {all} -NoRestart -ErrorAction Stop; \"{n}: done\" + $(if ($r.RestartNeeded) {{ ' (restart required)' }}) }} catch {{ \"{n}: $($_.Exception.Message)\" }}"));
        log($"{(enable ? "Enabling" : "Disabling")} {feature.Name}...");
        return ProcessRunner.PowerShellAsync(script, log, ct);
    }
}

/// <summary>Power plan helpers for the Tweaks page.</summary>
public static class PowerPlans
{
    /// <summary>Fixed GUID for WinSolve's copy of Ultimate Performance (language-independent lookup).</summary>
    public const string UltimateGuid = "a6e0c9d2-57a1-4f3e-9c4b-57696e536f6c";

    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string UltimateTemplateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private static string BackupPath => Path.Combine(SafePath.DataFolder, "power-plan-backup.json");

    private sealed class SavedPlan
    {
        public string Plan { get; set; } = "";
        public string? Overlay { get; set; }
    }

    private static readonly Regex GuidPattern = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    /// <summary>GUID of the active power plan, or null.</summary>
    public static async Task<string?> ActivePlanAsync(CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync("powercfg.exe", "/getactivescheme", null, ct);
        var m = GuidPattern.Match(r.Output);
        return r.Success && m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>The power mode (slider overlay) Windows uses on AC power, or null when unknown.</summary>
    private static string? ActiveOverlay()
        => Reg.Get(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActiveOverlayAcPowerScheme") is string s
           && Guid.TryParse(s, out var g) ? g.ToString() : null;

    /// <summary>
    /// Call before switching to one of <paramref name="targets"/>: remembers the plan in use so it can
    /// be put back. When a WinSolve plan is already active, the earlier saved plan is kept.
    /// </summary>
    public static async Task RememberActivePlanAsync(Action<string> log, CancellationToken ct, params string[] targets)
    {
        try
        {
            var active = await ActivePlanAsync(ct);
            if (active is null) return;
            string[] ours = [.. targets, UltimateGuid, UltimateTemplateGuid, HighPerformanceGuid];
            if (ours.Contains(active, StringComparer.OrdinalIgnoreCase) && File.Exists(BackupPath)) return;
            if (targets.Contains(active, StringComparer.OrdinalIgnoreCase) && ActiveOverlay() is null) return; // nothing would change
            var temp = BackupPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new SavedPlan { Plan = active, Overlay = ActiveOverlay() }));
            File.Move(temp, BackupPath, overwrite: true);
            log($"Current power plan saved ({active}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Could not save the current power plan: {ex.Message}");
        }
    }

    private static SavedPlan? Saved()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;
            var saved = JsonSerializer.Deserialize<SavedPlan>(File.ReadAllText(BackupPath));
            if (saved is null || !Guid.TryParse(saved.Plan, out var plan)) return null;
            saved.Plan = plan.ToString();
            saved.Overlay = Guid.TryParse(saved.Overlay, out var overlay) ? overlay.ToString() : null;
            return saved;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Logger.Write($"Could not read {BackupPath}: {ex.Message}");
            return null;
        }
    }

    public static bool HasSavedPlan => Saved() is not null;

    /// <summary>PowerShell that activates the saved plan if it still exists, otherwise Balanced.</summary>
    private static string RestoreScript(SavedPlan? saved, IEnumerable<string> exclude)
    {
        var plan = saved is not null && !exclude.Contains(saved.Plan, StringComparer.OrdinalIgnoreCase) ? saved.Plan : "";
        return $$"""
            $prev = '{{plan}}'
            if ($prev -and (powercfg /list | Out-String) -match $prev) {
                powercfg /setactive $prev
                if ($LASTEXITCODE) { "powercfg /setactive $prev failed ($LASTEXITCODE)."; exit $LASTEXITCODE }
                if ('{{saved?.Overlay}}') {
                    powercfg /overlaysetactive '{{saved?.Overlay}}'
                    if ($LASTEXITCODE) { "The power mode could not be restored ($LASTEXITCODE)." }
                }
                "Previous power plan restored ($prev)."
            } else {
                powercfg /setactive {{BalancedGuid}}
                if ($LASTEXITCODE) { "powercfg /setactive failed ($LASTEXITCODE)."; exit $LASTEXITCODE }
                'Balanced plan active.'
            }
            """;
    }

    /// <summary>Goes back to the plan that was active before WinSolve switched plans.</summary>
    public static async Task RestorePreviousAsync(Action<string> log, CancellationToken ct = default)
    {
        var saved = Saved();
        if (saved is null)
        {
            log("No saved power plan: WinSolve hasn't switched plans on this PC.");
            return;
        }
        var r = await ProcessRunner.PowerShellAsync(RestoreScript(saved, []), log, ct);
        if (!r.Success) throw new InvalidOperationException($"powercfg (exit code {r.ExitCode})");
        File.Delete(BackupPath);
    }

    /// <summary>
    /// Deletes the Ultimate Performance plans WinSolve made. If one of them is active, the plan used
    /// before is activated first (Balanced when that one no longer exists).
    /// </summary>
    public static async Task RemoveUltimateAsync(Action<string> log, CancellationToken ct = default)
    {
        var saved = Saved();
        var r = await ProcessRunner.PowerShellAsync($$"""
            $ids = @()
            if ((powercfg /list | Out-String) -match '{{UltimateGuid}}') { $ids += '{{UltimateGuid}}' }
            # Copies made by older versions (found by their translated name).
            # (Never a built-in plan: "Höchstleistung" is German for High performance, not Ultimate.)
            $builtin = @('{{BalancedGuid}}','{{HighPerformanceGuid}}','a1841308-3541-4fab-bc81-f71556f20b4a','{{UltimateTemplateGuid}}')
            foreach ($l in (powercfg /list | Select-String 'Ultimate Performance|Máximo rendimiento|Performances optimales|Ultimative Leistung')) {
                if ("$l" -match '([0-9a-f-]{36})' -and $builtin -notcontains $Matches[1]) { $ids += $Matches[1] }
            }
            $ids = @($ids | Select-Object -Unique)
            $active = if ((powercfg /getactivescheme | Out-String) -match '([0-9a-f-]{36})') { $Matches[1] } else { '' }
            if ($ids -contains $active -or $active -eq '{{UltimateTemplateGuid}}') {
            {{RestoreScript(saved, [UltimateGuid, UltimateTemplateGuid])}}
            }
            $failed = 0
            foreach ($id in $ids) {
                powercfg /delete $id
                if ($LASTEXITCODE) { "Could not remove $id ($LASTEXITCODE)."; $failed = $LASTEXITCODE } else { "Removed $id" }
            }
            if ($failed) { exit $failed }
            """, log, ct);
        if (!r.Success) throw new InvalidOperationException($"powercfg (exit code {r.ExitCode})");
        // The saved plan has been used (or still applies to another WinSolve plan): keep it unless restored.
        if (saved is not null && !(await ActivePlanAsync(ct) is { } now && (now == UltimateGuid || now == UltimateTemplateGuid || now == HighPerformanceGuid)))
            File.Delete(BackupPath);
    }
}
