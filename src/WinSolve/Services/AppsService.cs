using System.Diagnostics;
using System.Text.Json;
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
                using var root = RegistryKey.OpenBaseKey(hive, view);
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
                        MsiProductCode = isMsi && sub.StartsWith('{') ? sub : null,
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

    /// <summary>Runs the program's own uninstaller (interactive).</summary>
    public static async Task UninstallAsync(InstalledProgram p, Action<string> log, CancellationToken ct = default)
    {
        var command = p.MsiProductCode is not null ? $"msiexec.exe /x {p.MsiProductCode}" : p.UninstallString;
        log($"> {command}");
        var r = await ProcessRunner.RunAsync("cmd.exe", $"/d /c \"{command}\"", log, ct);
        log(r.Success ? "Uninstaller finished." : $"Uninstaller exited with code {r.ExitCode}.");
    }

    /// <summary>
    /// Force uninstall: silent uninstaller (if any), then kills its processes and deletes the
    /// install folder, leftover data folders, shortcuts and the registry entry.
    /// </summary>
    public static async Task ForceUninstallAsync(InstalledProgram p, Action<string> log, CancellationToken ct = default)
    {
        // 1) Silent uninstall.
        var silent = SilentCommand(p);
        if (silent is not null)
        {
            log($"Running the uninstaller silently: {silent}");
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                var r = await ProcessRunner.RunAsync("cmd.exe", $"/d /c \"{silent}\"", log, timeout.Token);
                log($"Uninstaller exit code: {r.ExitCode}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log("The uninstaller took too long; continuing with forced removal.");
            }
        }
        // Inno/NSIS uninstallers copy themselves to %TEMP% and keep running after the parent exits.
        await Task.Delay(3000, ct);

        // 2) Kill processes running from the install folder.
        var folder = SafeFolder(p.InstallLocation);
        if (folder is not null)
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (path is not null && path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        log($"Closing {proc.ProcessName} (PID {proc.Id})");
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch { /* protected or already gone */ }
                finally { proc.Dispose(); }
            }
        }

        // 3) Delete the install folder and data folders with the same name.
        var leftovers = new List<string>();
        if (folder is not null && Directory.Exists(folder)) leftovers.Add(folder);
        var leaf = folder is null ? null : Path.GetFileName(folder);
        if (leaf is { Length: >= 4 } && !CommonFolderNames.Contains(leaf))
        {
            foreach (var baseDir in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     })
            {
                var candidate = Path.Combine(baseDir, leaf);
                if (Directory.Exists(candidate)) leftovers.Add(candidate);
            }
        }
        foreach (var dir in leftovers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, recursive: true);
                log($"Deleted folder: {dir}");
            }
            catch (Exception ex)
            {
                log($"Could not delete {dir}: {ex.Message} (it will be removed on restart)");
                ScheduleDeleteOnReboot(dir);
            }
        }

        // 4) Shortcuts in the Start menu and on the desktop that point to the removed folder.
        if (folder is not null)
        {
            var shortcutScript = $$"""
                $target = {{Quote(folder)}}
                $shell = New-Object -ComObject WScript.Shell
                $roots = @([Environment]::GetFolderPath('StartMenu'), [Environment]::GetFolderPath('CommonStartMenu'),
                           [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('CommonDesktopDirectory'))
                foreach ($r in $roots) {
                    Get-ChildItem -Path $r -Filter *.lnk -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
                        $t = $shell.CreateShortcut($_.FullName).TargetPath
                        if ($t -and $t.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item $_.FullName -Force; "Removed shortcut: $($_.Name)" }
                    }
                }
                """;
            await ProcessRunner.PowerShellAsync(shortcutScript, log, ct);
        }

        // 5) Registry entry, so it disappears from Settings > Apps.
        try
        {
            using var root = RegistryKey.OpenBaseKey(p.Hive, p.View);
            root.DeleteSubKeyTree(p.KeyPath, throwOnMissingSubKey: false);
            log("Removed the entry from the installed programs list.");
        }
        catch (Exception ex)
        {
            log($"Could not remove the registry entry: {ex.Message}");
        }

        Logger.Write($"Force uninstall: {p.DisplayName}");
    }

    private static readonly HashSet<string> CommonFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Google", "Adobe", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "Packages", "Programs", "Temp", "Common Files",
        "Mozilla", "Apple", "Windows", "Steam", "Epic Games", "Riot Games",
    };

    /// <summary>Only folders at least two levels deep, and never system folders.</summary>
    private static string? SafeFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\');
            var depth = full.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
            if (depth < 3) return null;
            string[] forbidden =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ];
            if (forbidden.Any(f => f.Length > 0 && (full.Equals(f, StringComparison.OrdinalIgnoreCase)
                                                    || f.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))))
                return null;
            if (full.StartsWith(forbidden[0] + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            return full;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Best guess at a silent uninstall command.</summary>
    private static string? SilentCommand(InstalledProgram p)
    {
        if (p.MsiProductCode is not null) return $"msiexec.exe /x {p.MsiProductCode} /qn /norestart";
        if (p.QuietUninstallString.Length > 0) return p.QuietUninstallString;
        var u = p.UninstallString;
        if (u.Length == 0) return null;
        if (u.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            return u.Replace("/I", "/X", StringComparison.OrdinalIgnoreCase) + " /qn /norestart";
        if (u.Contains("unins0", StringComparison.OrdinalIgnoreCase)) return u + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
        if (u.Contains("uninst", StringComparison.OrdinalIgnoreCase) || u.Contains("uninstall.exe", StringComparison.OrdinalIgnoreCase)) return u + " /S";
        return u + " /S /quiet /silent";
    }

    private static void ScheduleDeleteOnReboot(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                MoveFileEx(f, null, 4 /* MOVEFILE_DELAY_UNTIL_REBOOT */);
            foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
                MoveFileEx(d, null, 4);
            MoveFileEx(dir, null, 4);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string? newName, int flags);
}
