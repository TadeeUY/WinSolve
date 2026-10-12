using Microsoft.Win32;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>Every cleanup, performance, repair, network and security action WinSolve offers.</summary>
public static class TaskCatalog
{
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    // The signed-in user's folders, even when WinSolve was elevated with another account.
    private static string LocalAppData => InteractiveUser.LocalAppData;
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string SystemDrive => Path.GetPathRoot(Win)!.TrimEnd('\\');

    public static IReadOnlyList<SystemTask> All { get; } = Build();

    public static SystemTask? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    // A non-zero exit code is counted in ctx.CommandErrors so results don't claim success for a
    // failed DISM, Defender update, etc. Pass check: false where non-zero is expected.
    private static async Task Run(TaskContext ctx, string file, string args, bool check = true)
    {
        ctx.Log($"> {file} {args}");
        var r = await ProcessRunner.RunAsync(file, args, l => ctx.Log("  " + l), ctx.Token);
        if (!r.Success)
        {
            ctx.Log($"  (exit code {r.ExitCode})");
            if (check) ctx.CommandErrors++;
        }
    }

    private static async Task Ps(TaskContext ctx, string script, bool check = true)
    {
        var r = await ProcessRunner.PowerShellAsync(script, l => ctx.Log("  " + l), ctx.Token);
        if (!r.Success)
        {
            ctx.Log($"  (PowerShell exit code {r.ExitCode})");
            if (check) ctx.CommandErrors++;
        }
    }

    private static async Task Cmd(TaskContext ctx, string command, bool check = true)
    {
        ctx.Log($"> {command}");
        var r = await ProcessRunner.CmdAsync(command, l => ctx.Log("  " + l), ctx.Token);
        if (!r.Success)
        {
            ctx.Log($"  (exit code {r.ExitCode})");
            if (check) ctx.CommandErrors++;
        }
    }

    // "net stop" fails harmlessly when the service is already stopped.
    private static Task StopServices(TaskContext ctx, params string[] services)
        => Cmd(ctx, string.Join(" & ", services.Select(s => $"net stop {s} /y")), check: false);

    private static Task StartServices(TaskContext ctx, params string[] services)
        => Cmd(ctx, string.Join(" & ", services.Select(s => $"net start {s}")), check: false); // "already started" is not an error

    private static List<SystemTask> Build() =>
    [
        // ───────────── Cleanup ─────────────
        new()
        {
            Id = "clean-temp-user", Category = TaskCategory.Cleanup, Recommended = true,
            Title = "User temporary files",
            Description = "Deletes the contents of %TEMP% older than a day. Files in use are skipped.",
            Run = ctx =>
            {
                // %TEMP% can point anywhere (C:\, Documents...): only a real temporary folder is emptied.
                if (InteractiveUser.CleanableTempPath(ctx.Log) is { } temp) FileCleaner.DeleteContents(temp, ctx, minAge: TempMinAge);
                return Task.CompletedTask;
            },
        },
        new()
        {
            Id = "clean-temp-windows", Category = TaskCategory.Cleanup, Recommended = true,
            Title = "Windows temporary files",
            Description = @"Deletes the contents of C:\Windows\Temp older than a day.",
            Run = ctx => { FileCleaner.DeleteContents(Path.Combine(Win, "Temp"), ctx, minAge: TempMinAge); return Task.CompletedTask; },
        },
        new()
        {
            Id = "clean-wu-cache", Category = TaskCategory.Cleanup, Recommended = true,
            Title = "Windows Update cache",
            Description = "Removes downloaded update files that are already installed (SoftwareDistribution\\Download).",
            Run = async ctx =>
            {
                if (UpdateInProgress() is { } busy)
                {
                    ctx.Log($"  Skipped the Windows Update cache: {busy}.");
                    return;
                }
                await StopServices(ctx, "wuauserv", "bits");
                FileCleaner.DeleteContents(Path.Combine(Win, "SoftwareDistribution", "Download"), ctx);
                await StartServices(ctx, "bits", "wuauserv");
            },
        },
        new()
        {
            Id = "clean-delivery-opt", Category = TaskCategory.Cleanup, Recommended = true,
            Title = "Delivery Optimization cache",
            Description = "Clears the peer-to-peer cache Windows uses to share updates.",
            Run = ctx => Ps(ctx, "Delete-DeliveryOptimizationCache -Force; 'Delivery Optimization cache cleared.'"),
        },
        new()
        {
            Id = "clean-thumbnails", Category = TaskCategory.Cleanup, Recommended = true,
            Title = "Thumbnail cache",
            Description = "Deletes thumbcache_*.db. Windows rebuilds thumbnails when folders are opened.",
            Run = ctx =>
            {
                FileCleaner.DeleteContents(Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer"), ctx, "thumbcache_*.db", recursive: false);
                return Task.CompletedTask;
            },
        },
        new()
        {
            Id = "clean-error-reports", Category = TaskCategory.Cleanup, Recommended = true,
            Title = "Windows Error Reporting files",
            Description = "Removes queued and archived error reports.",
            Run = ctx =>
            {
                // Shared queues: Windows' own reports and this user's; reports of other users' apps stay.
                DeleteReports(Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"), ctx);
                DeleteReports(Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"), ctx);
                FileCleaner.DeleteContents(Path.Combine(LocalAppData, "Microsoft", "Windows", "WER"), ctx);
                return Task.CompletedTask;
            },
        },
        new()
        {
            Id = "clean-crash-dumps", Category = TaskCategory.Cleanup,
            Title = "Crash dumps",
            Description = "Deletes Minidump, MEMORY.DMP and CrashDumps. Run it after you have diagnosed any blue screens.",
            Run = ctx =>
            {
                FileCleaner.DeleteContents(Path.Combine(Win, "Minidump"), ctx);
                FileCleaner.DeleteContents(Path.Combine(LocalAppData, "CrashDumps"), ctx);
                var memDump = Path.Combine(Win, "MEMORY.DMP");
                if (File.Exists(memDump))
                {
                    try { var len = new FileInfo(memDump).Length; File.Delete(memDump); ctx.FreedBytes += len; ctx.Log($"  MEMORY.DMP: {Format.Bytes(len)}"); }
                    catch (Exception ex) { ctx.Log($"  Could not delete MEMORY.DMP: {ex.Message}"); }
                }
                return Task.CompletedTask;
            },
        },
        new()
        {
            Id = "clean-browser-cache", Category = TaskCategory.Cleanup,
            Title = "Browser cache (Edge, Chrome, Brave)",
            Description = "Clears the web cache only — passwords, history and cookies are kept. Close your browsers first.",
            Run = ctx =>
            {
                string[] roots =
                [
                    Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data"),
                    Path.Combine(LocalAppData, "Google", "Chrome", "User Data"),
                    Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data"),
                ];
                foreach (var root in roots.Where(Directory.Exists))
                {
                    foreach (var profile in Directory.EnumerateDirectories(root))
                    {
                        FileCleaner.DeleteContents(Path.Combine(profile, "Cache", "Cache_Data"), ctx);
                        FileCleaner.DeleteContents(Path.Combine(profile, "Code Cache"), ctx);
                        FileCleaner.DeleteContents(Path.Combine(profile, "GPUCache"), ctx);
                    }
                }
                return Task.CompletedTask;
            },
        },
        new()
        {
            Id = "clean-recycle-bin", Category = TaskCategory.Cleanup,
            Title = "Empty the Recycle Bin",
            Description = "Permanently deletes everything in the Recycle Bin.",
            Run = ctx => { FileCleaner.EmptyRecycleBin(ctx); return Task.CompletedTask; },
        },
        new()
        {
            Id = "clean-component-store", Category = TaskCategory.Cleanup, Slow = true,
            Title = "Component store cleanup (WinSxS)",
            Description = "DISM /StartComponentCleanup: removes old component versions superseded by updates.",
            Run = ctx => Run(ctx, "dism.exe", "/Online /Cleanup-Image /StartComponentCleanup /English"),
        },

        // ───────────── Performance ─────────────
        new()
        {
            Id = "perf-optimize-drives", Category = TaskCategory.Performance, Recommended = true,
            Title = "Optimize drives (TRIM / defragment)",
            Description = "Runs TRIM on SSDs and defragments hard disks. Windows picks the right operation per drive.",
            Run = ctx => Ps(ctx, """
                Get-Volume | Where-Object { $_.DriveLetter -and $_.DriveType -eq 'Fixed' -and $_.FileSystemType -in 'NTFS','ReFS' } | ForEach-Object {
                    "Optimizing $($_.DriveLetter):"
                    try { Optimize-Volume -DriveLetter $_.DriveLetter -ErrorAction Stop; "  OK" } catch { "  $($_.Exception.Message)" }
                }
                """),
        },
        new()
        {
            Id = "perf-high-power-plan", Category = TaskCategory.Performance,
            Title = "Power plan: High performance",
            Description = "Activates the High performance plan. Uses more power; not recommended on battery.",
            Run = ctx => Cmd(ctx, "powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c || (powercfg -duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c & powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c)"),
        },
        new()
        {
            Id = "perf-ultimate-power-plan", Category = TaskCategory.Performance,
            Title = "Power plan: Ultimate Performance",
            Description = "Creates and activates the hidden Ultimate Performance plan. Intended for desktops.",
            // Found by GUID, not by name: plan names are translated, and a missed match used to
            // create a new copy of the plan on every run.
            Run = ctx => Ps(ctx, $$"""
                $template = 'e9a42b02-d5df-448d-aa00-03f14749eb61'
                $mine = '{{PowerPlans.UltimateGuid}}'
                $list = powercfg /list | Out-String
                if ($list -match $mine) { $id = $mine }
                elseif ($list -match $template) { $id = $template }
                else {
                    # Copies from older versions, by name (never a built-in plan such as High performance).
                    $builtin = @('381b4222-f694-41f0-9685-ff5bb260df2e','8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c','a1841308-3541-4fab-bc81-f71556f20b4a','e9a42b02-d5df-448d-aa00-03f14749eb61')
                    $legacy = powercfg /list | Select-String 'Ultimate Performance|Máximo rendimiento|Performances optimales|Ultimative Leistung' |
                        Where-Object { "$_" -match '([0-9a-f-]{36})' -and $builtin -notcontains $Matches[1] } | Select-Object -First 1
                    if ($legacy -and "$legacy" -match '([0-9a-f-]{36})') { $id = $Matches[1] }
                    else {
                        powercfg -duplicatescheme $template $mine | Out-Null
                        if ((powercfg /list | Out-String) -match $mine) { $id = $mine }
                    }
                }
                if ($id) { powercfg /setactive $id; "Active plan: $id" } else { 'Could not create the Ultimate Performance plan.'; exit 1 }
                """),
        },
        new()
        {
            Id = "perf-laptop-best-performance", Category = TaskCategory.Performance,
            Title = "Laptop: Best performance power mode",
            Description = "Keeps the Balanced plan but switches the power mode slider to Best performance.",
            Run = async ctx =>
            {
                await Cmd(ctx, "powercfg /setactive 381b4222-f694-41f0-9685-ff5bb260df2e");
                await Cmd(ctx, "powercfg /overlaysetactive ded574b5-45a0-4f42-8737-46345c09c238");
            },
        },
        new()
        {
            Id = "perf-balanced-power-plan", Category = TaskCategory.Performance,
            Title = "Power plan: Balanced (default)",
            Description = "Returns to the default Windows Balanced plan.",
            Run = async ctx =>
            {
                await Cmd(ctx, "powercfg /setactive 381b4222-f694-41f0-9685-ff5bb260df2e");
                await Cmd(ctx, "powercfg /overlaysetactive 00000000-0000-0000-0000-000000000000");
            },
        },
        new()
        {
            Id = "perf-restart-explorer", Category = TaskCategory.Performance,
            Title = "Restart Windows Explorer",
            Description = "Fixes a frozen taskbar or desktop and applies visual tweaks.",
            Run = ExplorerRestart,
        },

        // ───────────── Repair ─────────────
        new()
        {
            Id = "repair-system-files", Category = TaskCategory.Repair, Slow = true,
            Title = "Repair system files (DISM + SFC)",
            Description = "Repairs the Windows image with DISM /RestoreHealth, then verifies system files with SFC /scannow. Takes 10–30 minutes.",
            Run = async ctx =>
            {
                await Run(ctx, "dism.exe", "/Online /Cleanup-Image /RestoreHealth /English");
                await Run(ctx, "sfc.exe", "/scannow");
                ctx.RebootRecommended = true;
            },
        },
        new()
        {
            Id = "repair-sfc", Category = TaskCategory.Repair, Slow = true,
            Title = "SFC /scannow only",
            Description = "Checks and repairs protected system files.",
            Run = ctx => Run(ctx, "sfc.exe", "/scannow"),
        },
        new()
        {
            Id = "repair-chkdsk-scan", Category = TaskCategory.Repair, Slow = true,
            Title = "Check the system drive (online)",
            Description = "chkdsk /scan: looks for file system errors without restarting.",
            Run = ctx => Run(ctx, "chkdsk.exe", $"{SystemDrive} /scan"),
        },
        new()
        {
            Id = "repair-chkdsk-reboot", Category = TaskCategory.Repair, NeedsReboot = true,
            Title = "Repair the system drive on restart",
            Description = "Checks the drive's health, then schedules chkdsk /f (file system repair) for the next boot. Can take a while on large drives.",
            Run = RepairSystemDriveOnRestart,
        },
        new()
        {
            Id = "repair-windows-update", Category = TaskCategory.Repair, NeedsReboot = true,
            Title = "Reset Windows Update",
            Description = "Stops the update services, renames SoftwareDistribution and catroot2 and restarts them. Fixes stuck or failing updates.",
            ConfirmWarning = () => "Updates already downloaded are downloaded again and the update history in Settings starts empty. Don't run it while Windows is installing updates.",
            Run = async ctx =>
            {
                string[] services = ["wuauserv", "bits", "cryptsvc", "msiserver"];
                await StopServices(ctx, services);
                foreach (var dir in new[] { Path.Combine(Win, "SoftwareDistribution"), Path.Combine(Win, "System32", "catroot2") })
                {
                    var old = dir + ".old";
                    try
                    {
                        if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
                        if (Directory.Exists(dir)) Directory.Move(dir, old);
                        ctx.Log($"  {dir} -> {Path.GetFileName(old)}");
                    }
                    catch (Exception ex) { ctx.Log($"  Could not rename {dir}: {ex.Message}"); }
                }
                await StartServices(ctx, services);
                ctx.RebootRecommended = true;
            },
        },
        new()
        {
            Id = "repair-icon-cache", Category = TaskCategory.Repair,
            Title = "Rebuild the icon cache",
            Description = "Fixes blank or wrong icons. Restarts Explorer.",
            Run = async ctx =>
            {
                await Cmd(ctx, $"taskkill /f /fi \"SESSION eq {System.Diagnostics.Process.GetCurrentProcess().SessionId}\" /im explorer.exe"); // this session only
                FileCleaner.DeleteContents(Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer"), ctx, "iconcache_*.db", recursive: false);
                var legacy = Path.Combine(LocalAppData, "IconCache.db");
                try { if (File.Exists(legacy)) File.Delete(legacy); } catch { }
                await Task.Run(InteractiveUser.StartExplorer);
                ctx.Log("  Explorer restarted.");
            },
        },
        new()
        {
            Id = "repair-start-menu", Category = TaskCategory.Repair,
            Title = "Repair Start menu and Search",
            Description = "Re-registers the shell components (Start, Search, Action Center).",
            Run = ctx => ForThisUser(ctx, """
                $names = 'Microsoft.Windows.ShellExperienceHost','Microsoft.Windows.StartMenuExperienceHost','MicrosoftWindows.Client.CBS','Microsoft.Windows.Search'
                foreach ($n in $names) {
                    Get-AppxPackage -Name $n | ForEach-Object {
                        try { Add-AppxPackage -DisableDevelopmentMode -Register "$($_.InstallLocation)\AppXManifest.xml" -ErrorAction Stop; "Registered: $($_.Name)" }
                        catch { "Could not register $($_.Name): $($_.Exception.Message)" }
                    }
                }
                """),
        },
        new()
        {
            Id = "repair-store", Category = TaskCategory.Repair,
            Title = "Reset Microsoft Store",
            Description = "Clears the Store cache and resets the app. Use it when the Store won't open or download.",
            Run = ctx => ForThisUser(ctx, """
                $s = Get-AppxPackage Microsoft.WindowsStore
                if ($s -and (Get-Command Reset-AppxPackage -ErrorAction SilentlyContinue)) { $s | Reset-AppxPackage; 'Microsoft Store reset.' }
                else { Start-Process wsreset.exe -Wait; 'wsreset completed.' }
                """),
        },
        new()
        {
            Id = "repair-time-sync", Category = TaskCategory.Repair, Recommended = true,
            Title = "Synchronize the clock",
            Description = "Restarts the time service and forces a sync. A wrong clock breaks HTTPS, the Store and activation.",
            Run = async ctx =>
            {
                await Cmd(ctx, "sc config w32time start= auto & net start w32time", check: false); // may already be running
                await Run(ctx, "w32tm.exe", "/resync /force");
            },
        },
        new()
        {
            Id = "repair-print-spooler", Category = TaskCategory.Repair,
            Title = "Clear a stuck print queue",
            Description = "Stops the spooler, deletes stuck jobs and starts it again.",
            Run = async ctx =>
            {
                await StopServices(ctx, "spooler");
                FileCleaner.DeleteContents(Path.Combine(Win, "System32", "spool", "PRINTERS"), ctx);
                await StartServices(ctx, "spooler");
            },
        },
        new()
        {
            Id = "repair-wmi", Category = TaskCategory.Repair,
            Title = "Verify and repair WMI",
            Description = "Checks the WMI repository (used by many diagnostic tools) and salvages it if inconsistent.",
            Run = ctx => Ps(ctx, """
                winmgmt /verifyrepository
                # Exit code, not the (localized) text: 0 means the repository is consistent.
                if ($LASTEXITCODE -ne 0) { winmgmt /salvagerepository }
                """),
        },
        new()
        {
            Id = "repair-rescan-devices", Category = TaskCategory.Repair, Recommended = true,
            Title = "Scan for hardware changes",
            Description = "Same as 'Scan for hardware changes' in Device Manager.",
            Run = ctx => Run(ctx, "pnputil.exe", "/scan-devices"),
        },
        new()
        {
            Id = "repair-memory-test", Category = TaskCategory.Repair, NeedsReboot = true,
            Title = "Schedule a memory (RAM) test",
            Description = "Opens Windows Memory Diagnostic. The test runs on the next restart.",
            Run = ctx => { ProcessRunner.ShellOpen("mdsched.exe"); ctx.Log("  Windows Memory Diagnostic opened."); return Task.CompletedTask; },
        },

        // ───────────── Network ─────────────
        new()
        {
            Id = "net-flush-dns", Category = TaskCategory.Network, Recommended = true,
            Title = "Flush DNS cache",
            Description = "Fixes websites that stop loading after DNS changes.",
            Run = ctx => Run(ctx, "ipconfig.exe", "/flushdns"),
        },
        new()
        {
            Id = "net-renew-ip", Category = TaskCategory.Network,
            Title = "Renew IP address",
            Description = "Releases and renews the DHCP lease. The connection drops for a few seconds.",
            Run = async ctx =>
            {
                await Run(ctx, "ipconfig.exe", "/release", check: false); // fails on disconnected adapters
                await Run(ctx, "ipconfig.exe", "/renew");
            },
        },
        new()
        {
            Id = "net-reset-stack", Category = TaskCategory.Network, NeedsReboot = true,
            Title = "Reset network stack (Winsock + TCP/IP)",
            Description = "Deep network repair: resets Winsock, TCP/IP, the ARP cache and NetBIOS. Requires a restart.",
            ConfirmWarning = NetworkResetWarning,
            Run = async ctx =>
            {
                // Never reset without a copy of the settings it erases.
                if (await BackupNetworkSettings(ctx) is null)
                {
                    ctx.Log("  The network settings could not be saved, so nothing was reset.");
                    ctx.CommandErrors++;
                    return;
                }
                await Run(ctx, "netsh.exe", "winsock reset");
                // These often report a harmless "Access is denied" on one registry key.
                await Run(ctx, "netsh.exe", "int ip reset", check: false);
                await Run(ctx, "netsh.exe", "int ipv6 reset", check: false);
                await Run(ctx, "netsh.exe", "interface ip delete arpcache");
                await Run(ctx, "nbtstat.exe", "-R", check: false); // fails when NetBIOS over TCP/IP is off
                await Run(ctx, "ipconfig.exe", "/flushdns");
                ctx.RebootRecommended = true;
            },
        },

        // ───────────── Security ─────────────
        new()
        {
            Id = "sec-defender-update", Category = TaskCategory.Security, Recommended = true,
            Title = "Update Microsoft Defender definitions",
            Description = "Downloads the latest antivirus signatures.",
            Run = ctx => Ps(ctx, "Update-MpSignature -ErrorAction Stop; 'Definitions updated.'"),
        },
        new()
        {
            Id = "sec-defender-quick-scan", Category = TaskCategory.Security, Slow = true,
            Title = "Microsoft Defender quick scan",
            Description = "Scans the locations where malware usually hides.",
            Run = ctx => Ps(ctx, "Start-MpScan -ScanType QuickScan; $t = Get-MpThreatDetection; if ($t) { \"Threats detected: $($t.Count). Open Windows Security for details.\" } else { 'No threats found.' }"),
        },
    ];

    // ───────────── Network reset safety ─────────────

    /// <summary>Adapters with a manually set IPv4 address: "Ethernet (192.168.1.10, gateway 192.168.1.1, DNS 1.1.1.1)".</summary>
    public static List<string> StaticIpv4Adapters()
        => Wmi.Query("SELECT Description, IPAddress, DefaultIPGateway, DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE AND DHCPEnabled = FALSE")
            .Select(a =>
            {
                var ipv4 = (a.Get<string[]>("IPAddress") ?? []).Where(ip => ip.Contains('.')).ToList();
                if (ipv4.Count == 0) return null;
                var gateway = string.Join(" ", a.Get<string[]>("DefaultIPGateway") ?? []);
                var dns = string.Join(" ", a.Get<string[]>("DNSServerSearchOrder") ?? []);
                return $"{a.Str("Description")} ({string.Join(" ", ipv4)}" + (gateway.Length > 0 ? $", gateway {gateway}" : "") + (dns.Length > 0 ? $", DNS {dns}" : "") + ")";
            })
            .OfType<string>()
            .ToList();

    private static string NetworkResetWarning()
    {
        var statics = StaticIpv4Adapters();
        var text = "Every manual network setting is lost: static IP addresses, gateways and DNS servers go back to automatic (DHCP). VPN clients, firewalls and security software that hook into the network may need to be reinstalled. The current settings are saved first in ProgramData\\WinSolve\\NetworkBackup.";
        return statics.Count == 0 ? text
            : "This PC uses a static IP address. Write these settings down, you will have to enter them again after the reset:\n" +
              string.Join("\n", statics.Select(a => "• " + a)) + "\n\n" + text;
    }

    /// <summary>
    /// Saves the network configuration (netsh interface dump, IP and DNS settings as JSON) to
    /// ProgramData\WinSolve\NetworkBackup\(time). Returns the folder, or null if it couldn't be saved.
    /// </summary>
    private static async Task<string?> BackupNetworkSettings(TaskContext ctx)
    {
        try
        {
            var folder = SafePath.CreateAdminOnlyFolder(Path.Combine("NetworkBackup", DateTime.Now.ToString("yyyy-MM-dd_HHmmss")));
            var dump = Path.Combine(folder, "netsh-interface-dump.txt");
            var ip = Path.Combine(folder, "ip-configuration.json");
            var dns = Path.Combine(folder, "dns-servers.json");
            ctx.Log("> Saving the current network settings...");
            await ProcessRunner.CmdAsync($"netsh interface dump > \"{dump}\"", l => ctx.Log("  " + l), ctx.Token);
            await ProcessRunner.PowerShellAsync($$$"""
                Get-NetIPConfiguration -Detailed | Select-Object InterfaceAlias, InterfaceIndex, InterfaceDescription,
                    @{n='Dhcp';e={"$($_.NetIPv4Interface.Dhcp)"}}, @{n='IPv4';e={@($_.IPv4Address | ForEach-Object { "$($_.IPAddress)/$($_.PrefixLength)" })}},
                    @{n='IPv6';e={@($_.IPv6Address | ForEach-Object { "$($_.IPAddress)/$($_.PrefixLength)" })}},
                    @{n='Gateway';e={@($_.IPv4DefaultGateway | ForEach-Object { "$($_.NextHop)" })}}, @{n='Dns';e={@($_.DNSServer | ForEach-Object { $_.ServerAddresses })}} |
                    ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 -LiteralPath '{{{ip.Replace("'", "''")}}}'
                Get-DnsClientServerAddress | Select-Object InterfaceAlias, InterfaceIndex, AddressFamily, ServerAddresses |
                    ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 -LiteralPath '{{{dns.Replace("'", "''")}}}'
                """, l => ctx.Log("  " + l), ctx.Token);
            if (!File.Exists(dump) || new FileInfo(dump).Length == 0)
            {
                ctx.Log("  netsh could not export the network configuration.");
                return null;
            }
            foreach (var adapter in StaticIpv4Adapters()) ctx.Log($"  Static IP settings that the reset will remove: {adapter}");
            ctx.Log($"  Network settings saved in {folder}");
            Logger.Write($"Network settings saved before the network reset: {folder}");
            return folder;
        }
        catch (Exception ex)
        {
            ctx.Log($"  Could not save the network settings: {ex.Message}");
            return null;
        }
    }

    // ───────────── System drive repair ─────────────

    /// <summary>
    /// Schedules a file system repair (/f, not the long /r surface scan) of the Windows drive,
    /// after checking its health: on a failing drive a repair at boot can make data loss worse,
    /// so it's refused until the files are backed up. Reports whether it really got scheduled.
    /// </summary>
    private static async Task RepairSystemDriveOnRestart(TaskContext ctx)
    {
        var letter = SystemDrive[0];
        (DiskInfo Disk, string Text, HealthLevel Level)? health = null;
        try { health = await DiskRepairService.HealthOfDriveAsync(letter, ctx.Token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ctx.Log($"  Could not read the drive's health: {ex.Message}"); }

        if (health is null)
            ctx.Log("  The drive's health (S.M.A.R.T.) could not be read. Back up important files before restarting.");
        else if (health.Value.Level == HealthLevel.Bad)
        {
            ctx.Log($"  Not scheduled. {health.Value.Text}");
            ctx.Log("  Back up your files first. The Hardware page shows the drive's details and its repair options.");
            ctx.CommandErrors++;
            return;
        }
        else if (health.Value.Level == HealthLevel.Caution)
            ctx.Log($"  WARNING: {health.Value.Text} Back up important files before restarting.");

        if (await DiskRepairService.ScheduleAtRestartAsync(letter, surfaceScan: false, l => ctx.Log("  " + l), ctx.Token))
            ctx.RebootRecommended = true;
        else
            ctx.CommandErrors++;
    }

    /// <summary>
    /// Per-user app repairs: run as the signed-in user (with their own apps), not as the
    /// administrator whose password elevated WinSolve.
    /// </summary>
    private static Task ForThisUser(TaskContext ctx, string script)
    {
        if (!InteractiveUser.IsDifferent) return Ps(ctx, script);
        ctx.Log("> Running as the signed-in user...");
        return Task.Run(() =>
        {
            if (!InteractiveUser.RunPowerShellAsUser(script, 10 * 60 * 1000))
            {
                ctx.Log("  Could not run it as the signed-in user.");
                ctx.CommandErrors++;
            }
            else ctx.Log("  Done.");
        });
    }

    /// <summary>Temporary files this recent may still be in use by a running installer or program.</summary>
    private static TimeSpan TempMinAge => TimeSpan.FromHours(24);

    /// <summary>
    /// Why the Windows Update cache must not be touched now (an update is being installed or waits
    /// for a restart), or null. Its downloads are still needed then.
    /// </summary>
    private static string? UpdateInProgress()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var cbs = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                if (cbs is not null) return "Windows is waiting for a restart to finish installing updates";
            using (var wu = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                if (wu is not null) return "Windows Update is waiting for a restart";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }

        foreach (var name in new[] { "TrustedInstaller", "TiWorker", "msiexec" })
        {
            var running = System.Diagnostics.Process.GetProcessesByName(name);
            foreach (var p in running) p.Dispose();
            if (running.Length > 0) return "updates or programs are being installed right now";
        }
        return null;
    }

    /// <summary>Deletes error report folders, except reports about programs in another user's profile.</summary>
    private static void DeleteReports(string queue, TaskContext ctx)
    {
        if (!Directory.Exists(queue) || SafePath.HasReparsePoint(queue)) return;
        foreach (var report in Directory.EnumerateDirectories(queue, "*", SafePath.NoLinks(recursive: false)))
        {
            try
            {
                var wer = Path.Combine(report, "Report.wer");
                if (File.Exists(wer))
                {
                    var appPath = File.ReadLines(wer).FirstOrDefault(l => l.StartsWith("AppPath=", StringComparison.OrdinalIgnoreCase))?[8..];
                    if (appPath is not null && InteractiveUser.IsOtherUsersData(appPath)) continue;
                }
                FileCleaner.DeleteContents(report, ctx);
                try { Directory.Delete(report); } catch { /* files in use */ }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static async Task ExplorerRestart(TaskContext ctx)
    {
        await Cmd(ctx, $"taskkill /f /fi \"SESSION eq {System.Diagnostics.Process.GetCurrentProcess().SessionId}\" /im explorer.exe"); // this session only
        await Task.Delay(800, ctx.Token);
        await Task.Run(InteractiveUser.StartExplorer);
        ctx.Log("  Explorer restarted.");
    }

    /// <summary>Creates a System Restore point, lifting the one-per-24h limit first.</summary>
    public static async Task<bool> CreateRestorePoint(TaskContext ctx, string description = "WinSolve")
    {
        ctx.Log("Creating a restore point...");
        try
        {
            Reg.Set(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore",
                "SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            ctx.Log($"  Warning: {ex.Message}");
        }

        var r = await ProcessRunner.PowerShellAsync(
            $"Enable-ComputerRestore -Drive '{SystemDrive}\\' -ErrorAction SilentlyContinue; " +
            $"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop; 'OK'",
            l => ctx.Log("  " + l), ctx.Token);

        var ok = r.Success && r.Output.Contains("OK");
        ctx.Log(ok ? "  Restore point created." : "  Could not create a restore point (is System Protection turned off?).");
        return ok;
    }
}
