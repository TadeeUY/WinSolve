using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>How risky removing a Store app is.</summary>
public enum StoreAppRisk
{
    /// <summary>An ordinary app.</summary>
    Normal,
    /// <summary>A built-in app people rely on: removable, but only after a warning naming the consequence.</summary>
    Warn,
    /// <summary>Windows needs it (the Store, winget, sign-in, codecs, shell parts, frameworks): never removed.</summary>
    Required,
}

public sealed class StoreApp
{
    public string Name { get; set; } = "";
    public string PackageFullName { get; set; } = "";
    public string Publisher { get; set; } = "";
    public bool IsBloat { get; set; }
    public string FriendlyName { get; set; } = "";
    public StoreAppRisk Risk { get; set; }
    /// <summary>What stops working without it (for <see cref="StoreAppRisk.Warn"/> and <see cref="StoreAppRisk.Required"/>).</summary>
    public string Consequence { get; set; } = "";
}

public sealed class InstalledProgram
{
    public required string DisplayName { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public string DisplayIcon { get; init; } = "";
    public string InstallSource { get; init; } = "";
    public long SizeBytes { get; init; }
    public string? MsiProductCode { get; init; }
    public RegistryHive Hive { get; init; }
    public RegistryView View { get; init; }
    public required string KeyPath { get; init; }
    public string KeyName => KeyPath[(KeyPath.LastIndexOf('\\') + 1)..];
    public string Scope => Hive == RegistryHive.CurrentUser ? "Current user" : View == RegistryView.Registry32 ? "All users (32-bit)" : "All users";
}

/// <summary>What a desktop program is, for deciding how it may be removed.</summary>
public enum ProgramKind
{
    Normal,
    /// <summary>.NET, Visual C++, WebView2, DirectX: other programs need it. No force uninstall; strong warning.</summary>
    SharedRuntime,
    /// <summary>Graphics, audio, chipset and other driver packages. No force uninstall; strong warning.</summary>
    Driver,
    /// <summary>WinSolve itself: removed with its own uninstaller only.</summary>
    WinSolve,
}

public enum LeftoverKind
{
    /// <summary>The program's own install folder.</summary>
    InstallFolder,
    /// <summary>A folder in AppData or ProgramData with the program's name: may hold the user's data.</summary>
    UserData,
}

/// <summary>A folder a force uninstall offers to delete.</summary>
public sealed record Leftover(InstalledProgram Program, string Path, LeftoverKind Kind)
{
    /// <summary>Only the install folder is ticked by default; folders that may hold the user's data never are.</summary>
    public bool CheckedByDefault => Kind == LeftoverKind.InstallFolder;

    public string Note => Kind == LeftoverKind.InstallFolder
        ? "Program files"
        : "May contain your data (settings, saves, documents)";
}

/// <summary>Questions a long uninstall may need to ask the person (called from a worker thread).</summary>
public sealed class UninstallPrompts
{
    /// <summary>The uninstaller of the named program is still running: true to keep waiting.</summary>
    public Func<string, bool>? KeepWaiting { get; init; }

    /// <summary>These folders can't go to the Recycle Bin: true to delete them permanently.</summary>
    public Func<IReadOnlyList<string>, bool>? ConfirmPermanentDelete { get; init; }
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

    /// <summary>
    /// Packages Windows needs: the Store and winget (no way to reinstall anything afterwards),
    /// Windows Security, sign-in and account brokers, the Start menu and shell hosts, media codecs.
    /// </summary>
    public static readonly HashSet<string> RequiredStoreApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller", "Microsoft.StorePurchaseApp", "Microsoft.Winget.Source",
        "Microsoft.SecHealthUI", "Microsoft.XboxIdentityProvider",
        "Microsoft.HEVCVideoExtension", "Microsoft.HEIFImageExtension", "Microsoft.WebpImageExtension", "Microsoft.VP9VideoExtensions",
        "Microsoft.RawImageExtension", "Microsoft.AV1VideoExtension", "Microsoft.MPEG2VideoExtension", "Microsoft.WebMediaExtensions",
        "Microsoft.AVCEncoderVideoExtension",
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl", "Microsoft.LockApp", "Microsoft.Windows.CloudExperienceHost", "Microsoft.Windows.ContentDeliveryManager",
        "Microsoft.Windows.Search", "Microsoft.Windows.SecureAssessmentBrowser", "Microsoft.Win32WebViewHost", "Microsoft.Windows.Apprep.ChxApp",
        "Microsoft.Windows.AssignedAccessLockApp", "Microsoft.Windows.OOBENetworkCaptivePortal", "Microsoft.Windows.OOBENetworkConnectionFlow",
        "Microsoft.Windows.PeopleExperienceHost", "Microsoft.Windows.CapturePicker", "Microsoft.Windows.PinningConfirmationDialog",
        "Microsoft.Windows.XGpuEjectDialog", "Microsoft.Windows.ParentalControls", "Microsoft.Windows.CallingShellApp",
        "Microsoft.Windows.NarratorQuickStart", "Microsoft.CredDialogHost", "Microsoft.ECApp", "Microsoft.BioEnrollment",
        "Microsoft.MicrosoftEdge", "Microsoft.MicrosoftEdge.Stable", "Microsoft.MicrosoftEdgeDevToolsClient",
        "Windows.PrintDialog", "Windows.CBSPreview", "windows.immersivecontrolpanel", "NcsiUwpApp",
        // Windows 11 shell parts (Start, taskbar, File Explorer, setup). Widgets (Client.WebExperience)
        // and Phone Link's CrossDevice stay removable.
        "MicrosoftWindows.Client.CBS", "MicrosoftWindows.Client.Core", "MicrosoftWindows.Client.FileExp", "MicrosoftWindows.UndockedDevKit",
        "MicrosoftWindows.Client.AIX", "MicrosoftWindows.Client.OOBE", "MicrosoftWindows.Client.Photon", "MicrosoftWindows.Client.LKG",
        "MicrosoftWindows.LKG.DesktopSpotlight", "MicrosoftWindows.LKG.TwinSxS", "MicrosoftWindows.LKG.SpeechRuntime",
    };

    /// <summary>Frameworks and Windows parts that come in many versioned names.</summary>
    private static readonly string[] RequiredStorePrefixes =
    [
        "Microsoft.VCLibs", "Microsoft.NET.Native", "Microsoft.UI.Xaml", "Microsoft.WindowsAppRuntime", "Microsoft.WinAppRuntime",
        "Microsoft.Services.Store.Engagement", "Microsoft.DirectXRuntime",
    ];

    /// <summary>Built-in apps that can go, but only after the person reads what stops working.</summary>
    public static readonly Dictionary<string, (string Friendly, string Consequence)> WarnStoreApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Windows.Photos"] = ("Photos", "Pictures no longer open with a double-click until you choose another viewer or reinstall Photos from the Microsoft Store."),
        ["Microsoft.WindowsCalculator"] = ("Calculator", "Windows has no calculator until you reinstall it from the Microsoft Store."),
        ["Microsoft.WindowsNotepad"] = ("Notepad", "Text files (.txt, .log, .ini) have no editor: Windows 11 has no other Notepad. Reinstall it from the Microsoft Store."),
        ["Microsoft.Paint"] = ("Paint", "Images can no longer be edited with Paint until you reinstall it from the Microsoft Store."),
        ["Microsoft.WindowsTerminal"] = ("Terminal", "If Terminal is the default terminal, Command Prompt and PowerShell windows may stop opening until you reinstall it or change the default."),
        ["Microsoft.ScreenSketch"] = ("Snipping Tool", "Screenshots with Win+Shift+S and the Print Screen key stop working."),
        ["Microsoft.WindowsCamera"] = ("Camera", "The Camera app goes away; other apps can still use the webcam."),
    };

    public const string RequiredByWindows = "Required by Windows";

    /// <summary>Classifies a Store package by its name (Get-AppxPackage Name, not the full name).</summary>
    public static StoreAppRisk ClassifyStoreApp(string name, out string consequence)
    {
        consequence = "";
        name = name.Trim();
        if (RequiredStoreApps.Contains(name) || RequiredStorePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || IsCodec(name))
        {
            consequence = RequiredByWindows;
            return StoreAppRisk.Required;
        }
        if (WarnStoreApps.TryGetValue(name, out var warn))
        {
            consequence = warn.Consequence;
            return StoreAppRisk.Warn;
        }
        return StoreAppRisk.Normal;
    }

    /// <summary>Microsoft image, video and audio extensions (HEVC, AV1, WebP...): pictures and videos stop opening without them.</summary>
    private static bool IsCodec(string name)
        => name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
           && (name.EndsWith("Extension", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Extensions", StringComparison.OrdinalIgnoreCase))
           && new[] { "Video", "Image", "Media", "Audio", "Codec" }.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>PowerShell splat that targets the signed-in user (not the elevating admin) when they differ.</summary>
    private static string UserParam() => InteractiveUser.Other is { } o
        ? $"$user = @{{ User = {Quote(o.Sid.Value)} }}"
        : "$user = @{}";

    /// <summary>Always names the person at the keyboard, so lists never show other accounts' apps.</summary>
    private static string ThisUserParam() => $"$me = @{{ User = {Quote(InteractiveUser.Sid.Value)} }}";

    // ═══════════════════ Store apps ═══════════════════

    public static async Task<List<StoreApp>> GetStoreAppsAsync(CancellationToken ct = default)
    {
        var script = ThisUserParam() + "\n" + """
            $apps = Get-AppxPackage @me | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' } |
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
                a.Risk = ClassifyStoreApp(a.Name, out var consequence);
                a.Consequence = consequence;
                if (a.Risk == StoreAppRisk.Required) a.IsBloat = false;
                if (friendly is null && WarnStoreApps.TryGetValue(a.Name, out var warn)) friendly = warn.Friendly;
                a.FriendlyName = friendly ?? a.Name;
            }
            return apps.OrderByDescending(a => a.IsBloat).ThenBy(a => a.Risk == StoreAppRisk.Required).ThenBy(a => a.FriendlyName).ToList();
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not read Store apps: {ex.Message}");
            return [];
        }
    }

    /// <summary>Never removes a package Windows needs, whatever the caller asked.</summary>
    private static bool RefuseRequired(StoreApp app, Action<string> log)
    {
        if (ClassifyStoreApp(app.Name, out _) != StoreAppRisk.Required) return false;
        log($"Skipped {app.Name}: it is required by Windows.");
        return true;
    }

    /// <summary>Uninstall for the person at the keyboard only (other accounts keep the app).</summary>
    public static async Task<ProcessResult> RemoveStoreAppAsync(StoreApp app, Action<string> log, CancellationToken ct = default)
    {
        if (RefuseRequired(app, log)) return new ProcessResult(1, "");
        return await ProcessRunner.PowerShellAsync($$"""
            $name = {{Quote(app.Name)}}
            $found = $false
            {{UserParam()}}
            Get-AppxPackage -Name $name @user | ForEach-Object {
                $found = $true
                try { Remove-AppxPackage -Package $_.PackageFullName @user -ErrorAction Stop; "Removed: $($_.PackageFullName)" }
                catch { "Error: $($_.Exception.Message)" }
            }
            if (-not $found) { 'Not installed for the current user.' }
            """, log, ct);
    }

    /// <summary>
    /// Force uninstall, still for this user only: every version of the package (including ones
    /// left staged after a failed removal) and the app's data. Other accounts and new accounts
    /// keep their copy; WinSolve doesn't touch them (never -AllUsers, never the provisioned copy).
    /// </summary>
    public static async Task<ProcessResult> ForceRemoveStoreAppAsync(StoreApp app, Action<string> log, CancellationToken ct = default)
    {
        if (RefuseRequired(app, log)) return new ProcessResult(1, "");
        return await ProcessRunner.PowerShellAsync($$"""
            $name = {{Quote(app.Name)}}
            {{ThisUserParam()}}
            $found = $false
            Get-AppxPackage -Name $name @me -PackageTypeFilter Main,Bundle | ForEach-Object {
                $found = $true
                try { Remove-AppxPackage -Package $_.PackageFullName @me -ErrorAction Stop; "Removed: $($_.PackageFullName)" }
                catch { "Error: $($_.Exception.Message)" }
            }
            if (-not $found) { 'Not installed for the current user.' }
            """, log, ct);
    }

    // ═══════════════════ Desktop programs ═══════════════════

    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, RegistryView View)[] UninstallRoots =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Registry64),
    ];

    public static List<InstalledProgram> GetPrograms()
    {
        var list = new List<InstalledProgram>();
        foreach (var (hive, view) in UninstallRoots)
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
                        DisplayIcon = k.GetValue("DisplayIcon") as string ?? "",
                        InstallSource = k.GetValue("InstallSource") as string ?? "",
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

    // ─────────── What a program is ───────────

    private static readonly Regex DotNetName = new(@"^Microsoft (\.NET|ASP\.NET|Windows Desktop Runtime)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VcRedistName = new(@"^Microsoft Visual C\+\+ .*(Redistributable|Runtime)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NvidiaDriver = new(@"Driver|PhysX|FrameView|USBC", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AmdDriver = new(@"AMD Software|Driver|Chipset|Radeon", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RealtekDriver = new(@"Audio|Driver|Card Reader|Ethernet|Network|LAN|Bluetooth|Wireless", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IntelDriver = new(@"Driver|Graphics|Chipset|Management Engine|Rapid Storage|Serial IO|Wireless|Wi-Fi|Bluetooth|Thunderbolt|Optane|Dynamic Platform|Arc ", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public const string DotNetConsequence = "Programs built with .NET stop working until it is installed again.";
    public const string DotNetWinSolveConsequence = "Programs built with .NET stop working, including WinSolve itself (it needs the .NET 8 Desktop Runtime).";
    public const string VcConsequence = "Programs and games built with Visual C++ stop starting (errors about missing files such as VCRUNTIME140.dll).";
    public const string WebView2Consequence = "Many programs (Teams, Outlook, widgets, installers) can no longer show their windows or content.";
    public const string DirectXConsequence = "Games and graphics programs that need it stop starting.";
    public const string DriverConsequence = "The device may stop working (screen resolution, sound, network or Bluetooth) until its driver is installed again.";

    /// <summary>
    /// Classifies an installed program from its uninstall entry. <paramref name="keyName"/> is the
    /// name of its Uninstall subkey (WinSolve's own entry is "WinSolve").
    /// </summary>
    public static ProgramKind ClassifyProgram(string displayName, string publisher, string keyName, out string consequence)
    {
        consequence = "";
        var name = displayName.Trim();
        if (keyName.Equals("WinSolve", StringComparison.OrdinalIgnoreCase)
            || (name.Equals("WinSolve", StringComparison.OrdinalIgnoreCase) && publisher.Trim().Equals("WinSolve", StringComparison.OrdinalIgnoreCase)))
            return ProgramKind.WinSolve;

        if (DotNetName.IsMatch(name))
        {
            // WinSolve runs on the shared .NET 8 Desktop Runtime (except the portable build).
            var needed = !Edition.IsPortable && Regex.IsMatch(name, @"\b8\.0") &&
                         Regex.IsMatch(name, @"Desktop Runtime|\.NET Runtime|\.NET Host", RegexOptions.IgnoreCase);
            consequence = needed ? DotNetWinSolveConsequence : DotNetConsequence;
            return ProgramKind.SharedRuntime;
        }
        if (VcRedistName.IsMatch(name)) { consequence = VcConsequence; return ProgramKind.SharedRuntime; }
        if (name.Contains("WebView2 Runtime", StringComparison.OrdinalIgnoreCase)) { consequence = WebView2Consequence; return ProgramKind.SharedRuntime; }
        if (name.Contains("DirectX", StringComparison.OrdinalIgnoreCase)) { consequence = DirectXConsequence; return ProgramKind.SharedRuntime; }

        bool By(string vendor) => publisher.Contains(vendor, StringComparison.OrdinalIgnoreCase);
        var driver = name.StartsWith("Windows Driver Package", StringComparison.OrdinalIgnoreCase)
                     || (By("NVIDIA") && NvidiaDriver.IsMatch(name))
                     || ((By("Advanced Micro Devices") || Regex.IsMatch(publisher, @"\bAMD\b")) && AmdDriver.IsMatch(name))
                     || (By("Realtek") && RealtekDriver.IsMatch(name))
                     || (By("Intel") && IntelDriver.IsMatch(name));
        if (driver)
        {
            consequence = DriverConsequence;
            return ProgramKind.Driver;
        }
        return ProgramKind.Normal;
    }

    public static ProgramKind Classify(InstalledProgram p, out string consequence)
    {
        var kind = ClassifyProgram(p.DisplayName, p.Publisher, p.KeyName, out consequence);
        if (kind == ProgramKind.Normal && IsWinSolveInstall(p.InstallLocation)) return ProgramKind.WinSolve;
        return kind;
    }

    /// <summary>The entry's folder is the one WinSolve runs from.</summary>
    private static bool IsWinSolveInstall(string installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return false;
        try
        {
            var full = Path.GetFullPath(installLocation.Trim().Trim('"')).TrimEnd('\\');
            var own = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)?.TrimEnd('\\') ?? "";
            return own.Length > 0 && full.Equals(own, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>WinSolve's own program folder or data folder (never deleted, and the program is never killed).</summary>
    private static bool IsWinSolveFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\');
            var own = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)?.TrimEnd('\\') ?? "";
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinSolve");
            return (own.Length > 0 && (SafePath.IsSameOrInside(own, full) || SafePath.IsSameOrInside(full, own)))
                   || SafePath.IsSameOrInside(data, full) || SafePath.IsSameOrInside(full, data);
        }
        catch { return true; }
    }

    // ─────────── Running uninstallers ───────────

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

    private static bool IsMsiexec(string exe)
        => exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("msiexec", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Windows Installer product code of an entry: its key name, or the GUID of an msiexec uninstall command.</summary>
    private static string? MsiCode(InstalledProgram p)
    {
        if (p.MsiProductCode is { } code && MsiGuid.IsMatch(code)) return code;
        foreach (var s in new[] { p.UninstallString, p.QuietUninstallString })
        {
            if (SplitCommand(s) is not { } c || !IsMsiexec(c.Exe)) continue;
            var m = Regex.Match(c.Args, @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
            if (m.Success) return m.Value;
        }
        return null;
    }

    private static (string Exe, string Args)? Command(InstalledProgram p, bool silent)
    {
        if (p.MsiProductCode is { } code && MsiGuid.IsMatch(code))
            return ("msiexec.exe", silent ? $"/x {code} /qn /norestart" : $"/x {code}");

        if (silent && p.QuietUninstallString.Length > 0) return SplitCommand(p.QuietUninstallString);
        var cmd = SplitCommand(p.UninstallString);
        if (cmd is not { } c) return null;

        if (IsMsiexec(c.Exe))
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

    private enum RunOutcome { Exited, StoppedWaiting, NotStarted }

    /// <summary>How the uninstaller ended. <see cref="ExitCode"/> is null when it isn't known (started through runas or as another user).</summary>
    private readonly record struct UninstallerResult(RunOutcome Outcome, int? ExitCode)
    {
        public bool Succeeded => Outcome == RunOutcome.Exited && (ExitCode is null || IsSuccessExitCode(ExitCode.Value));
    }

    /// <summary>0, or the Windows Installer codes for "done, restart needed".</summary>
    internal static bool IsSuccessExitCode(int code) => code is 0 or 3010 or 1641;

    /// <summary>
    /// Starts an uninstaller. Per-user (HKCU) entries can be written by any program the user
    /// runs, so they are started with a non-administrator token (runas /trustlevel) instead
    /// of inheriting WinSolve's elevation.
    /// </summary>
    /// <remarks>
    /// A slow uninstaller is never killed (that can leave a program half removed): after
    /// <paramref name="askAfter"/> the person chooses to keep waiting or to stop waiting, and
    /// it keeps running either way. Only the Cancel button stops it.
    /// </remarks>
    private static async Task<UninstallerResult> RunUninstallerAsync(InstalledProgram p, (string Exe, string Args) cmd, Action<string> log,
        TimeSpan askAfter, Func<string, bool>? keepWaiting, CancellationToken ct)
    {
        // Our own token, connected to Cancel only while we wait: once we stop waiting, a later
        // Cancel (for another program) must not stop this uninstaller.
        var own = new CancellationTokenSource();
        var link = ct.Register(own.Cancel);
        var killsOnCancel = false;
        Task<int?> wait;
        try
        {
            if (p.Hive == RegistryHive.CurrentUser && InteractiveUser.IsDifferent)
            {
                // Per-user app of the person signed in (WinSolve was elevated with another
                // account): run its uninstaller as that person, so it removes their copy.
                log($"Running as {InteractiveUser.Other!.Name}: {cmd.Exe} {cmd.Args}");
                var before = ProcessIds(cmd.Exe);
                var started = DateTime.Now;
                var proc = InteractiveUser.StartAsUser(cmd.Exe, cmd.Args);
                if (proc is null)
                {
                    log("Could not start the uninstaller as the signed-in user.");
                    return new UninstallerResult(RunOutcome.NotStarted, null);
                }
                wait = WaitAsUserAsync(proc, cmd.Exe, before, started, own.Token);
            }
            else if (p.Hive == RegistryHive.CurrentUser)
            {
                log($"Running without administrator rights: {cmd.Exe} {cmd.Args}");
                var psi = new ProcessStartInfo(ProcessRunner.Resolve("runas.exe")) { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("/trustlevel:0x20000");
                psi.ArgumentList.Add($"\"{cmd.Exe}\" {cmd.Args}".Trim());
                var before = ProcessIds(cmd.Exe);
                var started = DateTime.Now;
                var runas = Process.Start(psi);
                if (runas is null) return new UninstallerResult(RunOutcome.NotStarted, null);
                wait = WaitAsUserAsync(runas, cmd.Exe, before, started, own.Token);
            }
            else
            {
                log($"> {cmd.Exe} {cmd.Args}");
                killsOnCancel = true;
                wait = RunElevatedAsync(cmd, log, own.Token);
            }

            while (true)
            {
                var done = await Task.WhenAny(wait, Task.Delay(askAfter, ct));
                if (done == wait)
                {
                    var code = await wait; // throws if canceled
                    if (code is { } c)
                    {
                        log($"Uninstaller exit code: {c}");
                        if (c == -1) return new UninstallerResult(RunOutcome.NotStarted, c);
                    }
                    return new UninstallerResult(RunOutcome.Exited, code);
                }
                ct.ThrowIfCancellationRequested();
                if (keepWaiting?.Invoke(p.DisplayName) == true)
                {
                    log("Still waiting for the uninstaller...");
                    continue;
                }
                log("Stopped waiting for the uninstaller. It keeps running; nothing else is removed for this program.");
                link.Dispose(); // Cancel no longer reaches it
                if (!killsOnCancel) own.Cancel(); // only stops our own polling
                _ = wait.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                return new UninstallerResult(RunOutcome.StoppedWaiting, null);
            }
        }
        finally
        {
            link.Dispose();
        }
    }

    private static async Task<int?> RunElevatedAsync((string Exe, string Args) cmd, Action<string> log, CancellationToken ct)
        => (await ProcessRunner.RunAsync(cmd.Exe, cmd.Args, log, ct)).ExitCode;

    private static async Task<int?> WaitAsUserAsync(Process proc, string exe, HashSet<int> before, DateTime started, CancellationToken ct)
    {
        using (proc) await proc.WaitForExitAsync(ct);
        // runas and Inno/NSIS stubs return at once; wait for the uninstaller itself.
        await WaitForNewProcessesAsync(exe, before, started, ct);
        return null;
    }

    /// <summary>Runs the program's own uninstaller (interactive).</summary>
    public static async Task UninstallAsync(InstalledProgram p, Action<string> log, CancellationToken ct = default, UninstallPrompts? prompts = null)
    {
        if (Classify(p, out _) == ProgramKind.WinSolve)
        {
            log("WinSolve can't uninstall itself from here. Use Settings > Apps > Installed apps, or WinSolve's uninstaller.");
            return;
        }
        if (Command(p, silent: false) is not { } cmd)
        {
            log("This program has no usable uninstall command. Use Force uninstall instead.");
            return;
        }
        var r = await RunUninstallerAsync(p, cmd, log, TimeSpan.FromMinutes(30), prompts?.KeepWaiting, ct);
        if (r.Outcome == RunOutcome.Exited) log("Uninstaller finished.");
    }

    // ─────────── Leftover folders ───────────

    /// <summary>Folder names Windows keeps in AppData itself: never treated as a program's folder.</summary>
    private static readonly HashSet<string> WindowsAppDataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Temp", "VirtualStore", "CrashDumps", "D3DSCache", "ConnectedDevicesPlatform", "Comms",
        "PlaceholderTileLogoFolder", "Publishers", "Programs", "Application Data", "History", "Temporary Internet Files",
    };

    /// <summary>
    /// A per-user (HKCU) entry can be written by any program the user runs, so it may only point
    /// at a program's own folder: AppData\Local\Programs\&lt;x&gt;, AppData\Local\&lt;x&gt; or
    /// AppData\Roaming\&lt;x&gt; (and deeper), never at AppData itself or Windows' folders in it.
    /// </summary>
    internal static bool IsAllowedPerUserFolder(string full, string localAppData, string roamingAppData)
    {
        full = full.TrimEnd('\\');
        var programs = Path.Combine(localAppData, "Programs");
        foreach (var root in new[] { programs, localAppData, roamingAppData })
        {
            var r = root.TrimEnd('\\');
            if (!full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            var first = full[(r.Length + 1)..].Split('\\')[0];
            if (first.Length == 0) return false;
            if (root == programs) return !first.Equals("Common", StringComparison.OrdinalIgnoreCase);
            return !WindowsAppDataNames.Contains(first);
        }
        return false;
    }

    /// <summary>Paths an uninstall entry points at: its folder, uninstaller, icon and install source.</summary>
    internal static List<string> ReferencedPaths(string installLocation, string uninstallString, string displayIcon, string installSource)
    {
        var list = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(path)) return;
            try { list.Add(Path.GetFullPath(path).TrimEnd('\\')); } catch { }
        }
        Add(installLocation);
        if (SplitCommand(uninstallString) is { } c && !IsMsiexec(c.Exe)) Add(c.Exe);
        var icon = displayIcon.Trim().Trim('"');
        var comma = icon.LastIndexOf(',');
        if (comma > 2 && int.TryParse(icon[(comma + 1)..].Trim(), out _)) icon = icon[..comma];
        Add(icon);
        Add(installSource);
        return list;
    }

    /// <summary>True when another uninstall entry points into the folder (or at it): deleting it would break that program.</summary>
    internal static bool IsReferencedByOthers(string folder, IEnumerable<IReadOnlyList<string>> otherEntries)
        => otherEntries.Any(paths => paths.Any(p => SafePath.IsSameOrInside(p, folder)));

    /// <summary>Every other uninstall entry's paths, hidden components and updates included.</summary>
    private static List<IReadOnlyList<string>> OtherEntryPaths(InstalledProgram p)
    {
        var result = new List<IReadOnlyList<string>>();
        foreach (var (hive, view) in UninstallRoots)
        {
            using var root = InteractiveUser.OpenBase(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall is null) continue;
            foreach (var sub in uninstall.GetSubKeyNames())
            {
                if (hive == p.Hive && view == p.View && (UninstallPath + "\\" + sub).Equals(p.KeyPath, StringComparison.OrdinalIgnoreCase)) continue;
                using var k = uninstall.OpenSubKey(sub);
                if (k is null) continue;
                // The same product registered again in the other registry view is not "another program".
                if (k.GetValue("DisplayName") is string n && n.Trim().Equals(p.DisplayName, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(ReferencedPaths(k.GetValue("InstallLocation") as string ?? "", k.GetValue("UninstallString") as string ?? "",
                    k.GetValue("DisplayIcon") as string ?? "", k.GetValue("InstallSource") as string ?? ""));
            }
        }
        return result;
    }

    /// <summary>
    /// Folders a force uninstall offers to delete. Paths come from the registry, so they are
    /// validated: never system or shell folders, never junctions, never a folder another
    /// program uses, and for per-user entries only the program's own folder in AppData.
    /// Only the install folder is ticked by default; AppData/ProgramData folders with the same
    /// name may hold the person's data and are offered unticked.
    /// </summary>
    public static List<Leftover> GetLeftovers(InstalledProgram p)
    {
        var result = new List<Leftover>();
        if (Classify(p, out _) != ProgramKind.Normal) return result;

        List<IReadOnlyList<string>> others;
        try { others = OtherEntryPaths(p); }
        catch (Exception ex)
        {
            Logger.Write($"Could not read the other uninstall entries: {ex.Message}");
            return result; // can't tell what is shared: offer nothing
        }

        var folder = ValidateFolder(p, p.InstallLocation);
        if (folder is null || IsSharedInstallFolder(folder, others)) return result;
        result.Add(new Leftover(p, folder, LeftoverKind.InstallFolder));

        var leaf = Path.GetFileName(folder);
        if (leaf.Length >= 4 && !CommonFolderNames.Contains(leaf) && !WindowsAppDataNames.Contains(leaf) && !GenericLeafNames.Contains(leaf))
        {
            foreach (var baseDir in new[]
                     {
                         InteractiveUser.RoamingAppData,
                         InteractiveUser.LocalAppData,
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     })
            {
                if (ValidateFolder(p, Path.Combine(baseDir, leaf), allowProfileForMachine: true) is { } d
                    && !result.Any(r => r.Path.Equals(d, StringComparison.OrdinalIgnoreCase))
                    && !IsReferencedByOthers(d, others))
                    result.Add(new Leftover(p, d, LeftoverKind.UserData));
            }
        }
        return result;
    }

    /// <summary>
    /// True for folders other software depends on: anything in Common Files, generic names
    /// ("Microsoft Office", vendor folders), or a folder another uninstall entry points into
    /// (its install folder, uninstaller, icon or install source).
    /// </summary>
    private static bool IsSharedInstallFolder(string folder, IEnumerable<IReadOnlyList<string>> others)
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
        return IsReferencedByOthers(folder, others);
    }

    private static string? ValidateFolder(InstalledProgram p, string path, bool allowProfileForMachine = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))).TrimEnd('\\'); }
        catch { return null; }

        if (!Directory.Exists(full) || SafePath.IsProtectedFolder(full) || SafePath.HasReparsePoint(full) || IsWinSolveFolder(full)) return null;

        var profile = InteractiveUser.Profile;
        if (p.Hive == RegistryHive.CurrentUser && !IsAllowedPerUserFolder(full, InteractiveUser.LocalAppData, InteractiveUser.RoamingAppData)) return null;
        if (p.Hive == RegistryHive.LocalMachine)
        {
            if (!allowProfileForMachine && SafePath.IsSameOrInside(full, Path.GetDirectoryName(profile)!)) return null;
            // In a profile, only the signed-in user's own AppData folders.
            if (allowProfileForMachine && SafePath.IsSameOrInside(full, Path.GetDirectoryName(profile)!)
                && !IsAllowedPerUserFolder(full, InteractiveUser.LocalAppData, InteractiveUser.RoamingAppData)) return null;
        }
        return full;
    }

    // ─────────── Force uninstall ───────────

    /// <summary>
    /// Force uninstall: silent uninstaller (if any), then closes the program's processes, moves
    /// the chosen folders (from <see cref="GetLeftovers"/>) to the Recycle Bin, removes its
    /// shortcuts and, only when the program is really gone, its entry in the installed list.
    /// </summary>
    public static async Task ForceUninstallAsync(InstalledProgram p, IReadOnlyList<string> folders, Action<string> log,
        CancellationToken ct = default, UninstallPrompts? prompts = null)
    {
        if (Classify(p, out _) is var kind && kind != ProgramKind.Normal)
        {
            log(kind == ProgramKind.WinSolve
                ? "WinSolve can't uninstall itself from here. Use Settings > Apps > Installed apps, or WinSolve's uninstaller."
                : "Force uninstall is not available for this program: other programs or devices need it. Use Uninstall instead.");
            return;
        }

        // 1) Silent uninstall. A missing uninstaller (already deleted) is skipped, not "failed".
        UninstallerResult? uninstaller = null;
        if (Command(p, silent: true) is { } silent)
        {
            if (Path.IsPathFullyQualified(silent.Exe) && !File.Exists(silent.Exe))
                log("The program's uninstaller is missing; skipping it.");
            else
            {
                uninstaller = await RunUninstallerAsync(p, silent, log, TimeSpan.FromMinutes(5), prompts?.KeepWaiting, ct);
                if (uninstaller.Value.Outcome == RunOutcome.StoppedWaiting)
                {
                    log("Nothing was deleted. Run Force uninstall again after the uninstaller finishes.");
                    return;
                }
                if (uninstaller.Value.Outcome == RunOutcome.Exited && !uninstaller.Value.Succeeded)
                    log("The uninstaller reported an error.");
            }
        }
        // Inno/NSIS uninstallers copy themselves to %TEMP% and keep running after the parent exits.
        await Task.Delay(3000, ct);

        // 2) Close processes running from the folders, in this session only (not other signed-in users'), never WinSolve.
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.Id == Environment.ProcessId || proc.SessionId != session) continue;
                var path = proc.MainModule?.FileName;
                if (path is not null && folders.Any(f => SafePath.IsSameOrInside(path, f)))
                {
                    log($"Closing {proc.ProcessName} (PID {proc.Id})");
                    proc.Kill(entireProcessTree: false);
                }
            }
            catch { /* protected or already gone */ }
            finally { proc.Dispose(); }
        }

        // 3) The folders (re-checked right before): to the Recycle Bin where possible.
        var permanent = new List<string>();
        foreach (var dir in folders)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(dir)) continue;
            if (SafePath.IsProtectedFolder(dir) || SafePath.HasReparsePoint(dir) || IsWinSolveFolder(dir))
            {
                log($"Skipped {dir}: protected location or junction.");
                continue;
            }
            if (CanRecycle(dir) && RecycleOnSta(dir)) log($"Moved to the Recycle Bin: {dir}");
            else permanent.Add(dir);
        }
        if (permanent.Count > 0)
        {
            if (prompts?.ConfirmPermanentDelete?.Invoke(permanent) == true)
                foreach (var dir in permanent) DeletePermanently(dir, log, ct);
            else
                foreach (var dir in permanent) log($"Kept (not deleted): {dir}");
        }

        // 4) Start menu and desktop shortcuts that point into the removed folders.
        foreach (var folder in folders.Where(f => !Directory.Exists(f)))
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

        // 5) The entry in Settings > Apps, only when the program is really gone.
        var msi = MsiCode(p);
        var msiRegistered = msi is not null && IsMsiRegistered(msi);
        bool? succeeded = uninstaller is { } u ? u.Succeeded : null;
        if (MayDeleteUninstallEntry(msi is not null, msiRegistered, succeeded, FilesGone(p)))
        {
            try
            {
                using var root = InteractiveUser.OpenBase(p.Hive, p.View);
                using var still = root.OpenSubKey(p.KeyPath);
                if (still is not null)
                {
                    root.DeleteSubKeyTree(p.KeyPath, throwOnMissingSubKey: false);
                    log("Removed the entry from the installed programs list.");
                }
            }
            catch (Exception ex)
            {
                log($"Could not remove the registry entry: {ex.Message}");
            }
        }
        else if (msiRegistered)
            log("Windows Installer still has this program registered, so its entry stays in the list. Try Uninstall, or run Force uninstall again after a restart.");
        else
            log("The program does not look fully removed (its uninstaller failed or its files are still there), so its entry stays in the list.");

        Logger.Write($"Force uninstall: {p.DisplayName} ({string.Join("; ", folders)})");
    }

    /// <summary>
    /// The uninstall entry is deleted by hand only when the program is gone: for Windows Installer
    /// products, when Windows Installer no longer has them registered (never behind its back);
    /// otherwise when the uninstaller succeeded (or there was none to run) and the files are gone.
    /// </summary>
    internal static bool MayDeleteUninstallEntry(bool isMsi, bool msiRegistered, bool? uninstallerSucceeded, bool filesGone)
    {
        if (isMsi) return !msiRegistered;
        return uninstallerSucceeded != false && filesGone;
    }

    /// <summary>The install folder and the uninstaller no longer exist (and at least one of them was known).</summary>
    private static bool FilesGone(InstalledProgram p)
    {
        var known = false;
        foreach (var path in ReferencedPaths(p.InstallLocation, p.UninstallString, "", ""))
        {
            known = true;
            if (Directory.Exists(path) || File.Exists(path)) return false;
        }
        return known;
    }

    /// <summary>
    /// Only folders the Recycle Bin really takes (fixed drive, bin on, big enough): for those,
    /// RecycleBin.Send never asks its own "delete permanently?" question. Everything else goes
    /// through the one confirmation in <see cref="UninstallPrompts.ConfirmPermanentDelete"/>.
    /// </summary>
    private static bool CanRecycle(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(dir)!).DriveType == DriveType.Fixed && RecycleBin.WhyNotRecyclable(dir) is null; }
        catch { return false; }
    }

    /// <summary>The shell's file operations want a single-threaded apartment.</summary>
    private static bool RecycleOnSta(string dir)
    {
        var ok = false;
        var t = new Thread(() =>
        {
            try { ok = RecycleBin.Send(dir, IntPtr.Zero) && !Directory.Exists(dir); }
            catch { ok = false; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return ok;
    }

    private static void DeletePermanently(string dir, Action<string> log, CancellationToken ct)
    {
        // Folders a standard user can change are never queued for deletion at boot: by then a
        // folder could have been swapped for a link to somewhere else.
        var userWritable = SafePath.IsUserWritableLocation(dir);
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

    // ─────────── Windows Installer ───────────

    /// <summary>The "packed" form of a product code Windows Installer uses as a registry key name.</summary>
    internal static string PackedGuid(string productCode)
    {
        var g = productCode.Trim().Trim('{', '}').Replace("-", "").ToUpperInvariant();
        if (g.Length != 32) throw new ArgumentException("Not a product code.", nameof(productCode));
        static string Rev(string s) { var a = s.ToCharArray(); Array.Reverse(a); return new string(a); }
        var sb = new System.Text.StringBuilder(32);
        sb.Append(Rev(g[..8])).Append(Rev(g[8..12])).Append(Rev(g[12..16]));
        for (int i = 16; i < 32; i += 2) sb.Append(g[i + 1]).Append(g[i]);
        return sb.ToString();
    }

    /// <summary>True while Windows Installer still knows the product (for this machine, this user or another user).</summary>
    private static bool IsMsiRegistered(string productCode)
    {
        try
        {
            var state = MsiQueryProductState(productCode);
            if (state is INSTALLSTATE_ADVERTISED or INSTALLSTATE_ABSENT or INSTALLSTATE_DEFAULT) return true;
        }
        catch { /* msi.dll not usable: fall back to the registry */ }

        try
        {
            var packed = PackedGuid(productCode);
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var sid in new[] { "S-1-5-18", InteractiveUser.Sid.Value })
            {
                using var k = hklm.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\{sid}\Products\{packed}");
                if (k is not null) return true;
            }
            using (var machine = hklm.OpenSubKey($@"SOFTWARE\Classes\Installer\Products\{packed}"))
                if (machine is not null) return true;
            return false;
        }
        catch
        {
            return true; // can't tell: keep the entry
        }
    }

    private const int INSTALLSTATE_ADVERTISED = 1, INSTALLSTATE_ABSENT = 2, INSTALLSTATE_DEFAULT = 5;

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiQueryProductState(string product);

    private static readonly HashSet<string> CommonFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Google", "Adobe", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "Packages", "Programs", "Temp", "Common Files",
        "Mozilla", "Apple", "Windows", "Steam", "Epic Games", "Riot Games", "Roaming", "Local", "LocalLow", "Application Data",
        "Microsoft Office", "Microsoft Visual Studio", "Windows Kits", "Dell", "HP", "Hewlett-Packard", "Lenovo", "ASUS", "Acer",
        "Realtek", "Logitech", "Corsair", "Razer", "MSI", "Gigabyte", "Oracle", "Java", "Common",
    };

    /// <summary>Install folder names too generic to find the program's data folders by name (%APPDATA%\App...).</summary>
    private static readonly HashSet<string> GenericLeafNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "App", "Application", "bin", "Data", "Config", "Settings", "Cache", "Logs", "Program", "current", "x64", "x86", "Client",
        "Launcher", "Setup", "Update", "Updater", "Tools", "Plugins", "Runtime",
    };

    private static void ScheduleDeleteOnReboot(string dir)
    {
        if (SafePath.IsUserWritableLocation(dir) || SafePath.HasReparsePoint(dir)) return;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string? newName, int flags);
}
