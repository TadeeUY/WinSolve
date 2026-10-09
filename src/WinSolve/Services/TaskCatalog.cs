using Microsoft.Win32;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>Every cleanup, performance, repair, network and security action WinSolve offers.</summary>
public static class TaskCatalog
{
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string SystemDrive => Path.GetPathRoot(Win)!.TrimEnd('\\');

    public static IReadOnlyList<SystemTask> All { get; } = Build();

    public static SystemTask? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    private static async Task Run(TaskContext ctx, string file, string args)
    {
        ctx.Log($"> {file} {args}");
        var r = await ProcessRunner.RunAsync(file, args, l => ctx.Log("  " + l), ctx.Token);
        if (!r.Success) ctx.Log($"  (exit code {r.ExitCode})");
    }

    private static async Task Ps(TaskContext ctx, string script)
    {
        var r = await ProcessRunner.PowerShellAsync(script, l => ctx.Log("  " + l), ctx.Token);
        if (!r.Success) ctx.Log($"  (PowerShell exit code {r.ExitCode})");
    }

    private static Task Cmd(TaskContext ctx, string command)
    {
        ctx.Log($"> {command}");
        return ProcessRunner.CmdAsync(command, l => ctx.Log("  " + l), ctx.Token);
    }

    private static Task StopServices(TaskContext ctx, params string[] services)
        => Cmd(ctx, string.Join(" & ", services.Select(s => $"net stop {s} /y")));

    private static Task StartServices(TaskContext ctx, params string[] services)
        => Cmd(ctx, string.Join(" & ", services.Select(s => $"net start {s}")));

    private static List<SystemTask> Build() =>
    [
        // ───────────── Cleanup ─────────────
        new()
        {
            Id = "clean-temp-user", Category = TaskCategory.Cleanup, Recommended = true,
            Title = "User temporary files",
            Description = "Deletes the contents of %TEMP%. Files in use are skipped.",
            Run = ctx => { FileCleaner.DeleteContents(Path.GetTempPath(), ctx); return Task.CompletedTask; },
        },
        new()
        {
            Id = "clean-temp-windows", Category = TaskCategory.Cleanup, Recommended = true,
            Title = "Windows temporary files",
            Description = @"Deletes the contents of C:\Windows\Temp.",
            Run = ctx => { FileCleaner.DeleteContents(Path.Combine(Win, "Temp"), ctx); return Task.CompletedTask; },
        },
        new()
        {
            Id = "clean-wu-cache", Category = TaskCategory.Cleanup, Recommended = true,
            Title = "Windows Update cache",
            Description = "Removes downloaded update files that are already installed (SoftwareDistribution\\Download).",
            Run = async ctx =>
            {
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
                FileCleaner.DeleteContents(Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"), ctx);
                FileCleaner.DeleteContents(Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"), ctx);
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
            Run = ctx => Ps(ctx, """
                $template = 'e9a42b02-d5df-448d-aa00-03f14749eb61'
                $line = powercfg /list | Select-String 'Ultimate Performance|Máximo rendimiento|Rendement optimal|Höchstleistung' | Select-Object -First 1
                if ($line -and "$line" -match '([0-9a-f-]{36})') { $id = $Matches[1] }
                elseif ("$(powercfg -duplicatescheme $template)" -match '([0-9a-f-]{36})') { $id = $Matches[1] }
                if ($id) { powercfg /setactive $id; "Active plan: $id" } else { 'Could not create the Ultimate Performance plan.' }
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
            Description = "Schedules chkdsk /f /r for the next boot. Can take a long time on large drives.",
            Run = async ctx =>
            {
                await Cmd(ctx, $"echo Y| chkdsk {SystemDrive} /f /r");
                // The Y answer only works on English Windows (Spanish expects S, German J...).
                // Marking the volume dirty guarantees at least a /f check at boot on any language.
                await Cmd(ctx, $"fsutil dirty set {SystemDrive}");
                ctx.RebootRecommended = true;
            },
        },
        new()
        {
            Id = "repair-windows-update", Category = TaskCategory.Repair, NeedsReboot = true,
            Title = "Reset Windows Update",
            Description = "Stops the update services, renames SoftwareDistribution and catroot2 and restarts them. Fixes stuck or failing updates.",
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
                await Cmd(ctx, "taskkill /f /im explorer.exe");
                FileCleaner.DeleteContents(Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer"), ctx, "iconcache_*.db", recursive: false);
                var legacy = Path.Combine(LocalAppData, "IconCache.db");
                try { if (File.Exists(legacy)) File.Delete(legacy); } catch { }
                ProcessRunner.ShellOpen("explorer.exe");
                ctx.Log("  Explorer restarted.");
            },
        },
        new()
        {
            Id = "repair-start-menu", Category = TaskCategory.Repair,
            Title = "Repair Start menu and Search",
            Description = "Re-registers the shell components (Start, Search, Action Center).",
            Run = ctx => Ps(ctx, """
                $names = 'Microsoft.Windows.ShellExperienceHost','Microsoft.Windows.StartMenuExperienceHost','MicrosoftWindows.Client.CBS','Microsoft.Windows.Search'
                foreach ($n in $names) {
                    Get-AppxPackage -AllUsers -Name $n | ForEach-Object {
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
            Run = ctx => Ps(ctx, """
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
                await Cmd(ctx, "sc config w32time start= auto & net start w32time");
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
                await Run(ctx, "ipconfig.exe", "/release");
                await Run(ctx, "ipconfig.exe", "/renew");
            },
        },
        new()
        {
            Id = "net-reset-stack", Category = TaskCategory.Network, NeedsReboot = true,
            Title = "Reset network stack (Winsock + TCP/IP)",
            Description = "Deep network repair: resets Winsock, TCP/IP, the ARP cache and NetBIOS. Requires a restart.",
            Run = async ctx =>
            {
                await Run(ctx, "netsh.exe", "winsock reset");
                await Run(ctx, "netsh.exe", "int ip reset");
                await Run(ctx, "netsh.exe", "int ipv6 reset");
                await Run(ctx, "netsh.exe", "interface ip delete arpcache");
                await Run(ctx, "nbtstat.exe", "-R");
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

    public static async Task ExplorerRestart(TaskContext ctx)
    {
        await Cmd(ctx, "taskkill /f /im explorer.exe");
        await Task.Delay(800, ctx.Token);
        ProcessRunner.ShellOpen("explorer.exe");
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
