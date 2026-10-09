using System.Text.Json;
using Microsoft.Win32;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed record DnsProvider(string Name, string[] IPv4, string[] IPv6);

/// <summary>DNS server presets applied to every connected network adapter.</summary>
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

    /// <summary>Name of the provider currently configured (or "Custom" / "Default (DHCP)").</summary>
    public static async Task<string> CurrentAsync()
    {
        var r = await ProcessRunner.PowerShellAsync("""
            $a = Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | Select-Object -First 1
            if ($a) { (Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4).ServerAddresses -join ',' }
            """);
        var servers = r.Output.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (servers.Length == 0) return "Default (DHCP)";
        var match = Providers.FirstOrDefault(p => p.IPv4.Length > 0 && p.IPv4[0] == servers[0]);
        return match?.Name ?? "Default (DHCP)";
    }

    public static Task ApplyAsync(DnsProvider provider, Action<string> log, CancellationToken ct = default)
    {
        string Quote(IEnumerable<string> s) => string.Join(",", s.Select(x => "'" + x + "'"));
        var script = provider.IPv4.Length == 0
            ? """
              Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object {
                  Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ResetServerAddresses; "Reset DNS on $($_.Name)"
              }
              Clear-DnsClientCache
              """
            : $$"""
              Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object {
                  Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ServerAddresses @({{Quote(provider.IPv4.Concat(provider.IPv6))}}); "DNS set on $($_.Name)"
              }
              Clear-DnsClientCache
              """;
        log($"Setting DNS: {provider.Name}");
        return ProcessRunner.PowerShellAsync(script, log, ct);
    }
}

/// <summary>Windows Update policy presets (as in WinUtil's Updates tab).</summary>
public static class UpdatePolicy
{
    private const string Wu = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string Au = Wu + @"\AU";

    public static bool IsSecurityMode()
        => Reg.ValueEquals(Reg.Get(RegistryHive.LocalMachine, Wu, "DeferFeatureUpdatesPeriodInDays"), 365);

    /// <summary>
    /// Security settings: feature updates are delayed one year, security/quality updates four days,
    /// drivers are not installed through Windows Update and no automatic restarts while signed in.
    /// </summary>
    public static void ApplySecurity()
    {
        var hklm = RegistryHive.LocalMachine;
        Reg.Set(hklm, Wu, "DeferFeatureUpdates", 1, RegistryValueKind.DWord);
        Reg.Set(hklm, Wu, "DeferFeatureUpdatesPeriodInDays", 365, RegistryValueKind.DWord);
        Reg.Set(hklm, Wu, "DeferQualityUpdates", 1, RegistryValueKind.DWord);
        Reg.Set(hklm, Wu, "DeferQualityUpdatesPeriodInDays", 4, RegistryValueKind.DWord);
        Reg.Set(hklm, Wu, "ExcludeWUDriversInQualityUpdate", 1, RegistryValueKind.DWord);
        Reg.Set(hklm, Au, "NoAutoRebootWithLoggedOnUsers", 1, RegistryValueKind.DWord);
        Logger.Write("Windows Update policy: security settings applied.");
    }

    public static void ApplyDefault()
    {
        var hklm = RegistryHive.LocalMachine;
        foreach (var name in new[] { "DeferFeatureUpdates", "DeferFeatureUpdatesPeriodInDays", "DeferQualityUpdates", "DeferQualityUpdatesPeriodInDays", "ExcludeWUDriversInQualityUpdate" })
            Reg.Delete(hklm, Wu, name);
        Reg.Delete(hklm, Au, "NoAutoRebootWithLoggedOnUsers");
        Logger.Write("Windows Update policy: default settings restored.");
    }
}

public sealed record WindowsFeature(string Name, string Description, string[] FeatureNames)
{
    public bool? Enabled { get; set; }
}

/// <summary>Optional Windows features (DISM), as in WinUtil's Config tab.</summary>
public static class WindowsFeatures
{
    public static List<WindowsFeature> Catalog() =>
    [
        new(".NET Framework 3.5", "Needed by many older programs and games.", ["NetFx3"]),
        new("Windows Subsystem for Linux (WSL)", "Run Linux distributions on Windows.", ["Microsoft-Windows-Subsystem-Linux", "VirtualMachinePlatform"]),
        new("Hyper-V", "Microsoft's virtualization platform (Pro/Enterprise editions).", ["Microsoft-Hyper-V-All"]),
        new("Windows Sandbox", "A disposable desktop for running untrusted apps safely (Pro/Enterprise).", ["Containers-DisposableClientVM"]),
        new("Legacy components (DirectPlay)", "Needed by some old games.", ["LegacyComponents", "DirectPlay"]),
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
            f.Enabled = mine.Count == 0 ? null : mine.All(s => s.State == "Enabled");
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

    public static Task RemoveUltimateAsync(Action<string> log, CancellationToken ct = default)
        => ProcessRunner.PowerShellAsync($$"""
            powercfg /setactive 381b4222-f694-41f0-9685-ff5bb260df2e
            $ids = @()
            if ((powercfg /list | Out-String) -match '{{UltimateGuid}}') { $ids += '{{UltimateGuid}}' }
            # Copies made by older versions (found by their translated name).
            foreach ($l in (powercfg /list | Select-String 'Ultimate Performance|Máximo rendimiento|Rendement optimal|Höchstleistung')) {
                if ("$l" -match '([0-9a-f-]{36})' -and $Matches[1] -ne 'e9a42b02-d5df-448d-aa00-03f14749eb61') { $ids += $Matches[1] }
            }
            foreach ($id in ($ids | Select-Object -Unique)) { powercfg /delete $id; "Removed $id" }
            'Balanced plan active.'
            """, log, ct);
}
