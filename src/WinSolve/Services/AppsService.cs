using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed class StoreApp
{
    public string Name { get; set; } = "";
    public string PackageFullName { get; set; } = "";
    public string Publisher { get; set; } = "";
    public bool IsBloat { get; set; }
    public string FriendlyName { get; set; } = "";
}

public sealed class InstalledProgram
{
    public required string DisplayName { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public long SizeBytes { get; init; }
    public string? MsiProductCode { get; init; }
    public RegistryHive Hive { get; init; }
    public RegistryView View { get; init; }
    public required string KeyPath { get; init; }
    public string Scope => Hive == RegistryHive.CurrentUser ? "Current user" : View == RegistryView.Registry32 ? "All users (32-bit)" : "All users";
}

/// <summary>Store apps and desktop programs: list, uninstall and force-remove.</summary>
public static class AppsService
{
    /// <summary>Preinstalled apps that are safe to remove, with friendly names.</summary>
    public static readonly Dictionary<string, string> KnownBloat = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.BingNews"] = "News",
        ["Microsoft.BingWeather"] = "Weather",
        ["Microsoft.BingSearch"] = "Bing Search",
        ["Microsoft.GetHelp"] = "Get Help",
        ["Microsoft.Getstarted"] = "Tips",
        ["Microsoft.MicrosoftOfficeHub"] = "Microsoft 365 (Office hub)",
        ["Microsoft.MicrosoftSolitaireCollection"] = "Solitaire Collection",
        ["Microsoft.People"] = "People",
        ["Microsoft.PowerAutomateDesktop"] = "Power Automate",
        ["Microsoft.Todos"] = "Microsoft To Do",
        ["Microsoft.WindowsFeedbackHub"] = "Feedback Hub",
        ["Microsoft.WindowsMaps"] = "Maps",
        ["Microsoft.ZuneMusic"] = "Media Player / Groove Music",
        ["Microsoft.ZuneVideo"] = "Movies & TV",
        ["Microsoft.YourPhone"] = "Phone Link",
        ["Microsoft.MixedReality.Portal"] = "Mixed Reality Portal",
        ["Microsoft.SkypeApp"] = "Skype",
        ["Microsoft.549981C3F5F10"] = "Cortana",
        ["Microsoft.WindowsCommunicationsApps"] = "Mail and Calendar (legacy)",
        ["Microsoft.Microsoft3DViewer"] = "3D Viewer",
        ["Microsoft.Office.OneNote"] = "OneNote (Store)",
        ["MicrosoftCorporationII.QuickAssist"] = "Quick Assist",
        ["Clipchamp.Clipchamp"] = "Clipchamp",
        ["MicrosoftTeams"] = "Teams (personal)",
        ["MSTeams"] = "Teams (personal)",
        ["Microsoft.OutlookForWindows"] = "Outlook (new)",
        ["Microsoft.Copilot"] = "Copilot",
        ["Microsoft.Windows.DevHome"] = "Dev Home",
        ["king.com.CandyCrushSaga"] = "Candy Crush Saga",
        ["king.com.CandyCrushSodaSaga"] = "Candy Crush Soda Saga",
        ["SpotifyAB.SpotifyMusic"] = "Spotify",
        ["Disney.37853FC22B2CE"] = "Disney+",
        ["BytedancePte.Ltd.TikTok"] = "TikTok",
        ["Facebook.Facebook"] = "Facebook",
        ["Facebook.Instagram"] = "Instagram",
        ["AmazonVideo.PrimeVideo"] = "Prime Video",
    };

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>PowerShell splat that targets the signed-in user (not the elevating admin) when they differ.</summary>
    private static string UserParam() => InteractiveUser.Other is { } o
        ? $"$user = @{{ User = {Quote(o.Sid.Value)} }}"
        : "$user = @{}";

    // ═══════════════════ Store apps ═══════════════════

    public static async Task<List<StoreApp>> GetStoreAppsAsync(CancellationToken ct = default)
    {
        const string script = """
            $apps = Get-AppxPackage -AllUsers | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' } |
                Sort-Object Name -Unique | Select-Object Name, PackageFullName, Publisher
            ConvertTo-Json -InputObject @($apps) -Compress
            """;
        var r = await ProcessRunner.PowerShellAsync(script, ct: ct);
        var start = r.Output.IndexOf('[');
        if (start < 0) return [];

        try
        {
            var apps = JsonSerializer.Deserialize<List<StoreApp>>(r.Output[start..], new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            foreach (var a in apps)
            {
                a.IsBloat = KnownBloat.TryGetValue(a.Name, out var friendly);
                a.FriendlyName = friendly ?? a.Name;
            }
            return apps.OrderByDescending(a => a.IsBloat).ThenBy(a => a.FriendlyName).ToList();
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not read Store apps: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Removes a Store app for every user and deprovisions it so Windows does not
    /// reinstall it for new accounts or after feature updates.
    /// </summary>
    /// <summary>Normal uninstall: removes the app for the current user only (it stays available to others).</summary>
    public static Task<ProcessResult> RemoveStoreAppAsync(StoreApp app, Action<string> log, CancellationToken ct = default)
        => ProcessRunner.PowerShellAsync($$"""
            $name = {{Quote(app.Name)}}
            $found = $false
            {{UserParam()}}
            Get-AppxPackage -Name $name @user | ForEach-Object {
                $found = $true
                try { Remove-AppxPackage -Package $_.PackageFullName @user -ErrorAction Stop; "Removed: $($_.PackageFullName)" }
                catch { "Error: $($_.Exception.Message)" }
            }
            if (-not $found) { 'Not installed for the current user. Use Force uninstall to remove it for all users.' }
            """, log, ct);

    public static Task<ProcessResult> ForceRemoveStoreAppAsync(StoreApp app, Action<string> log, CancellationToken ct = default)
        => ProcessRunner.PowerShellAsync($$"""
            $name = {{Quote(app.Name)}}
            Get-AppxPackage -AllUsers -Name $name | ForEach-Object {
                try { Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction Stop; "Removed for all users: $($_.PackageFullName)" }
                catch {
                    try { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction Stop; "Removed for the current user: $($_.PackageFullName)" }
                    catch { "Error: $($_.Exception.Message)" }
                }
            }
            Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq $name | ForEach-Object {
                try { Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -ErrorAction Stop | Out-Null; "Deprovisioned (won't come back): $($_.PackageName)" }
                catch { "Could not deprovision: $($_.Exception.Message)" }
            }
            """, log, ct);

    // ═══════════════════ Desktop programs ═══════════════════

    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledProgram> GetPrograms()
    {
        var list = new List<InstalledProgram>();
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                 })
        {
            try
            {
                using var root = InteractiveUser.OpenBase(hive, view);
                using var uninstall = root.OpenSubKey(UninstallPath);
                if (uninstall is null) continue;
                foreach (var sub in uninstall.GetSubKeyNames())
                {
                    using var k = uninstall.OpenSubKey(sub);
                    if (k is null) continue;
                    var name = k.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (k.GetValue("ParentKeyName") is string) continue; // updates/patches
                    var uninstallString = k.GetValue("UninstallString") as string ?? "";
                    if (uninstallString.Length == 0 && k.GetValue("QuietUninstallString") is null) continue;

                    var isMsi = k.GetValue("WindowsInstaller") is int wi && wi == 1;
                    list.Add(new InstalledProgram
                    {
                        DisplayName = name.Trim(),
                        Publisher = k.GetValue("Publisher") as string ?? "",
                        Version = k.GetValue("DisplayVersion") as string ?? "",
                        InstallLocation = (k.GetValue("InstallLocation") as string ?? "").Trim().Trim('"'),
                        UninstallString = uninstallString,
                        QuietUninstallString = k.GetValue("QuietUninstallString") as string ?? "",
                        SizeBytes = (k.GetValue("EstimatedSize") is int kb ? kb : 0) * 1024L,
                        MsiProductCode = isMsi && MsiGuid.IsMatch(sub) ? sub : null,
                        Hive = hive,
                        View = view,
                        KeyPath = UninstallPath + "\\" + sub,
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Write($"Could not read installed programs ({hive}/{view}): {ex.Message}");
            }
        }

        // The same program often appears in more than one view.
        return list.GroupBy(p => (p.DisplayName, p.Version)).Select(g => g.First())
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static readonly Regex MsiGuid = new(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.Compiled);

    /// <summary>
    /// Splits an UninstallString into executable and arguments the way CreateProcess does,
    /// so it can be started directly instead of through cmd.exe (no shell metacharacters).
    /// </summary>
    public static (string Exe, string Args)? SplitCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return null;
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            if (end < 0) return null;
            return (command[1..end], command[(end + 1)..].Trim());
        }
        // Unquoted path with spaces: take the shortest prefix that is an existing file.
        var parts = command.Split(' ');
        for (int i = 1; i <= parts.Length; i++)
        {
            var candidate = string.Join(' ', parts.Take(i));
            if (File.Exists(candidate)) return (candidate, string.Join(' ', parts.Skip(i)));
            if (File.Exists(candidate + ".exe")) return (candidate + ".exe", string.Join(' ', parts.Skip(i)));
        }
        return (parts[0], string.Join(' ', parts.Skip(1)));
    }

    private static (string Exe, string Args)? Command(InstalledProgram p, bool silent)
    {
        if (p.MsiProductCode is { } code && MsiGuid.IsMatch(code))
            return ("msiexec.exe", silent ? $"/x {code} /qn /norestart" : $"/x {code}");

        if (silent && p.QuietUninstallString.Length > 0) return SplitCommand(p.QuietUninstallString);
        var cmd = SplitCommand(p.UninstallString);
        if (cmd is not { } c) return null;

        if (c.Exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) || c.Exe.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            // Only accept "/I{GUID}" or "/X{GUID}" from the registry; never arbitrary msiexec arguments.
            var m = Regex.Match(c.Args, @"\{[0-9A-Fa-f-]{36}\}");
            if (!m.Success) return null;
            return ("msiexec.exe", silent ? $"/x {m.Value} /qn /norestart" : $"/x {m.Value}");
        }
        if (!silent) return c;

        var exeName = Path.GetFileName(c.Exe);
        var extra = exeName.StartsWith("unins0", StringComparison.OrdinalIgnoreCase) ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" // Inno Setup
            : exeName.Contains("uninst", StringComparison.OrdinalIgnoreCase) ? "/S"                                               // NSIS
            : "/S /quiet /silent";
        return (c.Exe, (c.Args + " " + extra).Trim());
    }

    private static HashSet<int> ProcessIds(string exe)
    {
        var all = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        try { return all.Select(p => p.Id).ToHashSet(); }
        finally { foreach (var p in all) p.Dispose(); }
    }

    /// <summary>
    /// Waits for the uninstaller processes started for this run: same name, started after it, in
    /// this desktop session. Never waits on a name alone (the Windows Installer service keeps an
    /// msiexec.exe alive for minutes after any MSI operation).
    /// </summary>
    private static async Task WaitForNewProcessesAsync(string exe, HashSet<int> before, DateTime started, CancellationToken ct)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        var session = Process.GetCurrentProcess().SessionId;
        await Task.Delay(500, ct);
        while (true)
        {
            var all = Process.GetProcessesByName(name);
            bool running;
            try
            {
                running = all.Any(p =>
                {
                    try { return !before.Contains(p.Id) && p.SessionId == session && p.StartTime >= started.AddSeconds(-2) && !p.HasExited; }
                    catch { return false; }
                });
            }
            finally
            {
                foreach (var p in all) p.Dispose();
            }
            if (!running) return;
            await Task.Delay(1000, ct);
        }
    }

    /// <summary>
    /// Starts an uninstaller. Per-user (HKCU) entries can be written by any program the user
    /// runs, so they are started with a non-administrator token (runas /trustlevel) instead
    /// of inheriting WinSolve's elevation.
    /// </summary>
    private static async Task RunUninstallerAsync(InstalledProgram p, (string Exe, string Args) cmd, Action<string> log, TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            if (p.Hive == RegistryHive.CurrentUser && InteractiveUser.IsDifferent)
            {
                // Per-user app of the person signed in (WinSolve was elevated with another
                // account): run its uninstaller as that person, so it removes their copy.
                log($"Running as {InteractiveUser.Other!.Name}: {cmd.Exe} {cmd.Args}");
                var before = ProcessIds(cmd.Exe);
                var started = DateTime.Now;
                using var proc = InteractiveUser.StartAsUser(cmd.Exe, cmd.Args);
                if (proc is null) log("Could not start the uninstaller as the signed-in user.");
                else await proc.WaitForExitAsync(linked.Token);
                await WaitForNewProcessesAsync(cmd.Exe, before, started, linked.Token);
            }
            else if (p.Hive == RegistryHive.CurrentUser)
            {
                log($"Running without administrator rights: {cmd.Exe} {cmd.Args}");
                var psi = new ProcessStartInfo(ProcessRunner.Resolve("runas.exe")) { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("/trustlevel:0x20000");
                psi.ArgumentList.Add($"\"{cmd.Exe}\" {cmd.Args}".Trim());
                var before = ProcessIds(cmd.Exe);
                var started = DateTime.Now;
                using (var runas = Process.Start(psi)) await runas!.WaitForExitAsync(linked.Token);
                // runas returns immediately; wait for the uninstaller itself.
                await WaitForNewProcessesAsync(cmd.Exe, before, started, linked.Token);
            }
            else
            {
                log($"> {cmd.Exe} {cmd.Args}");
                var r = await ProcessRunner.RunAsync(cmd.Exe, cmd.Args, log, linked.Token);
                log($"Uninstaller exit code: {r.ExitCode}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log("The uninstaller took too long; continuing.");
        }
    }

    /// <summary>Runs the program's own uninstaller (interactive).</summary>
    public static async Task UninstallAsync(InstalledProgram p, Action<string> log, CancellationToken ct = default)
    {
        if (Command(p, silent: false) is not { } cmd)
        {
            log("This program has no usable uninstall command. Use Force uninstall instead.");
            return;
        }
        await RunUninstallerAsync(p, cmd, log, TimeSpan.FromMinutes(30), ct);
        log("Uninstaller finished.");
    }

    /// <summary>
    /// Folders that a force uninstall would delete. Paths come from the registry, so they are
    /// validated: never system or shell folders, never junctions, and for per-user entries
    /// only folders inside the current user's profile.
    /// </summary>
    public static List<string> GetForceRemovalFolders(InstalledProgram p)
    {
        var result = new List<string>();
        var folder = ValidateFolder(p, p.InstallLocation);
        if (folder is null || IsSharedInstallFolder(p, folder)) return result;
        result.Add(folder);

        var leaf = Path.GetFileName(folder);
        if (leaf.Length >= 4 && !CommonFolderNames.Contains(leaf))
        {
            foreach (var baseDir in new[]
                     {
                         InteractiveUser.RoamingAppData,
                         InteractiveUser.LocalAppData,
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     })
            {
                if (ValidateFolder(p, Path.Combine(baseDir, leaf), allowProfileForMachine: true) is { } d) result.Add(d);
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// True for folders other software depends on: anything in Common Files, generic names
    /// ("Microsoft Office", vendor folders), or a folder that holds another installed program
    /// (e.g. Visio's InstallLocation is the whole Office folder).
    /// </summary>
    private static bool IsSharedInstallFolder(InstalledProgram p, string folder)
    {
        foreach (var common in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                 })
        {
            if (SafePath.IsSameOrInside(folder, common)) return true;
        }
        if (CommonFolderNames.Contains(Path.GetFileName(folder))) return true;

        try
        {
            foreach (var other in GetPrograms())
            {
                if (other.KeyPath == p.KeyPath && other.Hive == p.Hive && other.View == p.View) continue;
                if (string.IsNullOrWhiteSpace(other.InstallLocation)) continue;
                string otherFull;
                try { otherFull = Path.GetFullPath(other.InstallLocation.Trim().Trim('"')).TrimEnd('\\'); }
                catch { continue; }
                // Same folder or a parent of another program's folder: deleting it would break that program.
                if (SafePath.IsSameOrInside(otherFull, folder)) return true;
            }
        }
        catch { return true; } // can't tell: be safe
        return false;
    }

    private static string? ValidateFolder(InstalledProgram p, string path, bool allowProfileForMachine = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\'); }
        catch { return null; }

        if (!Directory.Exists(full) || SafePath.IsProtectedFolder(full) || SafePath.HasReparsePoint(full)) return null;

        var profile = InteractiveUser.Profile;
        if (p.Hive == RegistryHive.CurrentUser && !SafePath.IsSameOrInside(full, profile)) return null;
        if (p.Hive == RegistryHive.LocalMachine && !allowProfileForMachine && SafePath.IsSameOrInside(full, Path.GetDirectoryName(profile)!)) return null;
        return full;
    }

    /// <summary>
    /// Force uninstall: silent uninstaller (if any), then closes its processes and deletes the
    /// given folders (from <see cref="GetForceRemovalFolders"/>), its shortcuts and its registry entry.
    /// </summary>
    public static async Task ForceUninstallAsync(InstalledProgram p, IReadOnlyList<string> folders, Action<string> log, CancellationToken ct = default)
    {
        // 1) Silent uninstall.
        if (Command(p, silent: true) is { } silent)
            await RunUninstallerAsync(p, silent, log, TimeSpan.FromMinutes(5), ct);
        // Inno/NSIS uninstallers copy themselves to %TEMP% and keep running after the parent exits.
        await Task.Delay(3000, ct);

        // 2) Close processes running from the folders.
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                var path = proc.MainModule?.FileName;
                if (path is not null && folders.Any(f => SafePath.IsSameOrInside(path, f)))
                {
                    log($"Closing {proc.ProcessName} (PID {proc.Id})");
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch { /* protected or already gone */ }
            finally { proc.Dispose(); }
        }

        // 3) Delete the folders (re-checked right before deleting).
        foreach (var dir in folders)
        {
            if (!Directory.Exists(dir)) continue;
            if (SafePath.IsProtectedFolder(dir) || SafePath.HasReparsePoint(dir))
            {
                log($"Skipped {dir}: protected location or junction.");
                continue;
            }
            // Folders a standard user can write to (profiles) are never queued for deletion at
            // boot: by then a folder could have been swapped for a link to somewhere else.
            var userWritable = SafePath.IsSameOrInside(dir, Path.GetDirectoryName(InteractiveUser.Profile)!);
            try
            {
                // Handle-based delete: never follows a junction swapped in during the removal.
                var r = SafeDelete.DeleteTree(dir, ct);
                if (!Directory.Exists(dir)) log($"Deleted folder: {dir}");
                else if (userWritable) log($"Some files in {dir} are in use ({r.Skipped}); delete the rest after closing the program.");
                else
                {
                    log($"Some files in {dir} are in use; they will be removed on restart.");
                    ScheduleDeleteOnReboot(dir);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (userWritable) log($"Could not delete {dir}: {ex.Message}");
                else
                {
                    log($"Could not delete {dir}: {ex.Message} (it will be removed on restart)");
                    ScheduleDeleteOnReboot(dir);
                }
            }
        }

        // 4) Start menu and desktop shortcuts that point into the removed folders.
        foreach (var folder in folders)
        {
            var shortcutScript = $$"""
                $target = {{Quote(folder.TrimEnd('\\') + "\\")}}
                $shell = New-Object -ComObject WScript.Shell
                $roots = @({{Quote(InteractiveUser.StartMenu)}}, [Environment]::GetFolderPath('CommonStartMenu'),
                           {{Quote(InteractiveUser.Desktop)}}, [Environment]::GetFolderPath('CommonDesktopDirectory'))
                foreach ($r in $roots) {
                    Get-ChildItem -Path $r -Filter *.lnk -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
                        $t = $shell.CreateShortcut($_.FullName).TargetPath
                        if ($t -and $t.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $_.FullName -Force; "Removed shortcut: $($_.Name)" }
                    }
                }
                """;
            await ProcessRunner.PowerShellAsync(shortcutScript, log, ct);
        }

        // 5) Registry entry, so it disappears from Settings > Apps.
        try
        {
            using var root = InteractiveUser.OpenBase(p.Hive, p.View);
            root.DeleteSubKeyTree(p.KeyPath, throwOnMissingSubKey: false);
            log("Removed the entry from the installed programs list.");
        }
        catch (Exception ex)
        {
            log($"Could not remove the registry entry: {ex.Message}");
        }

        Logger.Write($"Force uninstall: {p.DisplayName} ({string.Join("; ", folders)})");
    }

    private static readonly HashSet<string> CommonFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Google", "Adobe", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "Packages", "Programs", "Temp", "Common Files",
        "Mozilla", "Apple", "Windows", "Steam", "Epic Games", "Riot Games", "Roaming", "Local", "LocalLow", "Application Data",
        "Microsoft Office", "Microsoft Visual Studio", "Windows Kits", "Dell", "HP", "Hewlett-Packard", "Lenovo", "ASUS", "Acer",
        "Realtek", "Logitech", "Corsair", "Razer", "MSI", "Gigabyte", "Oracle", "Java",
    };

    private static void ScheduleDeleteOnReboot(string dir)
    {
        try
        {
            var opts = SafePath.NoLinks(recursive: true);
            foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
                MoveFileEx(f, null, 4 /* MOVEFILE_DELAY_UNTIL_REBOOT */);
            foreach (var d in Directory.EnumerateDirectories(dir, "*", opts).OrderByDescending(x => x.Length))
                MoveFileEx(d, null, 4);
            MoveFileEx(dir, null, 4);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string? newName, int flags);
}
