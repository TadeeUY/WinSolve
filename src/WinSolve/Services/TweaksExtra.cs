using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>
/// Tweaks inspired by Chris Titus Tech's WinUtil (Essential / Advanced / Preferences), plus presets.
/// </summary>
public static partial class TweakCatalog
{
    private const string EdgePolicy = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string HomeNamespace = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
    private const string HomeClsid = @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
    private const string GalleryClsid = @"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}";

    /// <summary>WinUtil-like "Standard" preset: safe, reversible essentials.</summary>
    public static readonly string[] StandardPreset =
    [
        "ess-temp-files", "ess-consumer-features", "priv-telemetry", "priv-activity-history", "ess-folder-discovery",
        "perf-game-dvr", "ess-location", "ess-wifi-sense", "ui-end-task", "ess-services-manual",
        "ess-ps7-telemetry", "priv-advertising-id", "priv-tailored", "priv-suggestions",
    ];

    /// <summary>
    /// "Maximum": Standard plus every other low-risk privacy and performance tweak. Leaves out
    /// the ones with real trade-offs (hibernation, fast startup, GPU scheduling, driver updates,
    /// Storage Sense and the Advanced list).
    /// </summary>
    public static readonly string[] MaximumPreset =
    [
        .. StandardPreset,
        "ess-edge-debloat", "perf-game-mode", "perf-menu-delay", "perf-startup-delay", "sys-long-paths", "sys-no-auto-reboot",
        "priv-bing-search", "priv-copilot", "priv-recall", "perf-background-apps", "perf-visual-effects", "perf-network-throttling",
    ];

    /// <summary>WinUtil-like "Minimal" preset.</summary>
    public static readonly string[] MinimalPreset =
    [
        "ess-consumer-features", "priv-telemetry", "priv-activity-history", "ui-end-task", "ess-temp-files",
    ];

    private static readonly Dictionary<string, TweakGroup> Groups = new()
    {
        // Essential
        ["ess-temp-files"] = TweakGroup.Essential,
        ["ess-disk-cleanup"] = TweakGroup.Essential,
        ["ess-consumer-features"] = TweakGroup.Essential,
        ["priv-telemetry"] = TweakGroup.Essential,
        ["priv-activity-history"] = TweakGroup.Essential,
        ["priv-advertising-id"] = TweakGroup.Essential,
        ["priv-tailored"] = TweakGroup.Essential,
        ["priv-suggestions"] = TweakGroup.Essential,
        ["ess-folder-discovery"] = TweakGroup.Essential,
        ["perf-game-dvr"] = TweakGroup.Essential,
        ["sys-hibernation-off"] = TweakGroup.Essential,
        ["ess-location"] = TweakGroup.Essential,
        ["ess-storage-sense"] = TweakGroup.Essential,
        ["ess-wifi-sense"] = TweakGroup.Essential,
        ["ui-end-task"] = TweakGroup.Essential,
        ["ess-services-manual"] = TweakGroup.Essential,
        ["ess-ps7-telemetry"] = TweakGroup.Essential,
        ["ess-edge-debloat"] = TweakGroup.Essential,
        ["perf-game-mode"] = TweakGroup.Essential,
        ["perf-menu-delay"] = TweakGroup.Essential,
        ["perf-startup-delay"] = TweakGroup.Essential,
        ["sys-long-paths"] = TweakGroup.Essential,
        ["sys-no-auto-reboot"] = TweakGroup.Essential,

        // Preferences (toggles)
        ["ui-dark-mode"] = TweakGroup.Preference,
        ["pref-numlock"] = TweakGroup.Preference,
        ["ui-verbose-status"] = TweakGroup.Preference,
        ["pref-hide-recommended"] = TweakGroup.Preference,
        ["pref-snap-assist-off"] = TweakGroup.Preference,
        ["pref-mouse-accel-off"] = TweakGroup.Preference,
        ["ui-sticky-keys"] = TweakGroup.Preference,
        ["ui-hidden-files"] = TweakGroup.Preference,
        ["ui-file-extensions"] = TweakGroup.Preference,
        ["ui-this-pc"] = TweakGroup.Preference,
        ["pref-hide-search-button"] = TweakGroup.Preference,
        ["ui-no-task-view"] = TweakGroup.Preference,
        ["ui-taskbar-left"] = TweakGroup.Preference,
        ["ui-no-widgets"] = TweakGroup.Preference,
        ["sys-bsod-details"] = TweakGroup.Preference,
        // Everything else is Advanced.
    };

    private static void AssignGroups(List<Tweak> list)
    {
        foreach (var t in list) t.Group = Groups.GetValueOrDefault(t.Id, TweakGroup.Advanced);
    }

    private static Tweak ActionTweak(string id, string category, string title, string description, Action run) => new()
    {
        Id = id, Category = category, Title = title, Description = description, IsAction = true,
        IsApplied = () => false,
        Apply = run,
        Revert = () => { },
    };

    private static void RunTask(string taskId)
    {
        if (TaskCatalog.Find(taskId) is { } task)
            task.Run(new TaskContext(l => Logger.Write("[tweak] " + l), CancellationToken.None)).GetAwaiter().GetResult();
    }

    private static List<Tweak> BuildExtra()
    {
        var list = new List<Tweak>
        {
            // ───────────── Essential ─────────────
            ActionTweak("ess-temp-files", "System", "Delete temporary files",
                "Deletes the user and Windows temporary folders (files in use are skipped).",
                () => { RunTask("clean-temp-user"); RunTask("clean-temp-windows"); }),
            ActionTweak("ess-disk-cleanup", "System", "Run disk cleanup",
                "Removes superseded Windows components (DISM component cleanup). Can take several minutes.",
                () => RunTask("clean-component-store")),
            RegTweak("ess-consumer-features", "Privacy", "Disable Consumer Features",
                "Stops Windows from automatically installing suggested games and third-party apps (Candy Crush, etc.).",
                [D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1, null)]),
            RegTweak("ess-folder-discovery", "Interface", "Disable Explorer automatic folder discovery",
                "File Explorer stops guessing folder types (Pictures, Music...), which makes large folders open faster.",
                [S(HKCU, @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell", "FolderType", "NotSpecified", null)],
                explorer: true),
            RegTweak("ess-location", "Privacy", "Disable location tracking",
                "Stops apps from reading your location (your account only; other users keep their own setting).",
                [S(HKCU, @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny", "Allow")]),
            RegTweak("ess-storage-sense", "System", "Disable Storage Sense",
                "Stops Windows from deleting files automatically (Downloads, Recycle Bin) when space runs low.",
                [D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 0, null)]),
            RegTweak("ess-wifi-sense", "Privacy", "Disable Wi-Fi Sense",
                "Stops automatic connections to suggested open hotspots and hotspot reporting.",
                [
                    D(HKLM, @"SOFTWARE\Microsoft\PolicyManager\default\WiFi\AllowWiFiHotSpotReporting", "Value", 0, 1),
                    D(HKLM, @"SOFTWARE\Microsoft\PolicyManager\default\WiFi\AllowAutoConnectToWiFiSenseHotspots", "Value", 0, 1),
                ]),
            RegTweak("ess-ps7-telemetry", "Privacy", "Disable PowerShell 7 telemetry",
                "Sets POWERSHELL_TELEMETRY_OPTOUT so PowerShell 7 does not send usage data.",
                [S(HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "POWERSHELL_TELEMETRY_OPTOUT", "1", null)]),
            RegTweak("ess-edge-debloat", "Privacy", "Debloat Microsoft Edge",
                "Turns off Edge's sidebar, shopping assistant, startup boost, first-run screens, recommendations and diagnostic data (Edge stays installed). These are policies, so Edge will show \"Managed by your organization\".",
                // User policies (HKCU): only this account's Edge changes.
                [
                    D(HKCU, EdgePolicy, "HubsSidebarEnabled", 0, null),
                    D(HKCU, EdgePolicy, "EdgeShoppingAssistantEnabled", 0, null),
                    D(HKCU, EdgePolicy, "StartupBoostEnabled", 0, null),
                    D(HKCU, EdgePolicy, "HideFirstRunExperience", 1, null),
                    D(HKCU, EdgePolicy, "ShowRecommendationsEnabled", 0, null),
                    D(HKCU, EdgePolicy, "PersonalizationReportingEnabled", 0, null),
                    D(HKCU, EdgePolicy, "UserFeedbackAllowed", 0, null),
                    D(HKCU, EdgePolicy, "DiagnosticData", 0, null),
                    D(HKCU, EdgePolicy, "EdgeCollectionsEnabled", 0, null),
                ]),
            ServicesManualTweak(),

            // ───────────── Advanced (caution) ─────────────
            RegTweak("adv-fso-off", "Performance", "Disable fullscreen optimizations",
                "Uses true exclusive fullscreen in games. Can lower input latency on some games; may break Alt+Tab overlays.",
                [D(HKCU, @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 1, 0)]),
            // Both use bits of the same DisabledComponents value: change only their own bits so one
            // doesn't silently undo the other.
            Ipv6BitsTweak("adv-ipv6-off", "Disable IPv6",
                "Turns IPv6 off on every adapter. Only useful for specific network problems. Requires a restart.",
                0xDF,
                "Microsoft doesn't support disabling IPv6, and parts of Windows expect it. It breaks DirectAccess, Always On VPN and IP-HTTPS, "
                + "can leave you without internet on providers that use DS-Lite, 464XLAT or IPv6-only connections, and breaks Xbox features that use Teredo. "
                + "Requires a restart."),
            Ipv6BitsTweak("adv-prefer-ipv4", "Prefer IPv4 over IPv6",
                "Keeps IPv6 enabled but makes Windows try IPv4 first. Requires a restart.",
                0x20),
            RegTweak("adv-teredo-off", "Network", "Disable Teredo",
                "Disables the Teredo IPv6 tunnel. Can reduce latency in some games; Xbox party chat may report a 'Teredo' NAT warning.",
                [S(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\TCPIP\v6Transition", "Teredo_State", "Disabled", null)], reboot: true),
            RegTweak("adv-notifications-off", "Interface", "Disable notifications and the notification center",
                "Hides the notification center and turns off toast notifications from apps.",
                [
                    D(HKCU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableNotificationCenter", 1, null),
                    D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0, 1),
                ], explorer: true),
            RegTweak("adv-utc-time", "System", "Set the hardware clock to UTC",
                "For PCs that dual-boot Linux: Windows treats the BIOS clock as UTC so the time stops jumping between systems.",
                [new(HKLM, @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation", "RealTimeIsUniversal", 1L, null, RegistryValueKind.QWord)],
                reboot: true,
                warning: "Only for PCs that dual-boot with Linux set to keep the hardware clock in UTC. On any other PC the clock will be off by your time zone's difference from UTC."),
            OneDriveTweak(),
        };

        if (IsWindows11)
        {
            list.Add(new Tweak
            {
                Id = "adv-explorer-home-gallery", Category = "Interface",
                Title = "Remove Home and Gallery from File Explorer",
                Description = "Hides the Home and Gallery entries in the File Explorer navigation pane (Windows 11).",
                NeedsExplorerRestart = true,
                // Unpinned for this user only (older versions deleted Home for everyone).
                IsApplied = () => Reg.ValueEquals(Reg.Get(HKCU, HomeClsid, "System.IsPinnedToNameSpaceTree"), 0)
                                  && Reg.ValueEquals(Reg.Get(HKCU, GalleryClsid, "System.IsPinnedToNameSpaceTree"), 0),
                Apply = () =>
                {
                    Reg.Set(HKCU, HomeClsid, "System.IsPinnedToNameSpaceTree", 0, RegistryValueKind.DWord);
                    Reg.Set(HKCU, GalleryClsid, "System.IsPinnedToNameSpaceTree", 0, RegistryValueKind.DWord);
                },
                Revert = () =>
                {
                    Reg.Delete(HKCU, HomeClsid, "System.IsPinnedToNameSpaceTree");
                    Reg.Delete(HKCU, GalleryClsid, "System.IsPinnedToNameSpaceTree");
                    // Put back Home for everyone if an older version removed it.
                    if (!Reg.KeyExists(HKLM, HomeNamespace))
                        Reg.Set(HKLM, HomeNamespace, "", "CLSID_MSGraphHomeFolder", RegistryValueKind.String);
                },
            });
            list.Add(RegTweak("pref-hide-recommended", "Interface", "Hide the Recommended section in Start",
                "Removes recent files and suggested apps from the Start menu (Windows 11).",
                [
                    D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecommendedSection", 1, null),
                    D(HKLM, @"SOFTWARE\Microsoft\PolicyManager\current\device\Start", "HideRecommendedSection", 1, null),
                ], explorer: true));
        }

        // ───────────── Preferences ─────────────
        list.Add(RegTweak("pref-numlock", "Interface", "NumLock on at startup",
            "Turns NumLock on at the sign-in screen and when you sign in.",
            [
                new(RegistryHive.Users, @".DEFAULT\Control Panel\Keyboard", "InitialKeyboardIndicators", "2", "2147483648", RegistryValueKind.String),
                S(HKCU, @"Control Panel\Keyboard", "InitialKeyboardIndicators", "2", "0"),
            ]));
        list.Add(RegTweak("pref-snap-assist-off", "Interface", "Turn off Snap Assist suggestions",
            "Snapping a window no longer suggests other windows to fill the rest of the screen.",
            [D(HKCU, Adv, "SnapAssist", 0, 1)]));
        list.Add(RegTweak("pref-mouse-accel-off", "Interface", "Turn off mouse acceleration",
            "Disables 'Enhance pointer precision' so the pointer moves exactly with the mouse (preferred for gaming).",
            [
                S(HKCU, @"Control Panel\Mouse", "MouseSpeed", "0", "1"),
                S(HKCU, @"Control Panel\Mouse", "MouseThreshold1", "0", "6"),
                S(HKCU, @"Control Panel\Mouse", "MouseThreshold2", "0", "10"),
            ]));
        list.Add(RegTweak("pref-hide-search-button", "Interface", "Hide the search box on the taskbar",
            "Removes the search box/button from the taskbar (search still works from Start).",
            // Undo restores the previous mode; without a backup it uses the default search box (2)
            // of both Windows 11 and Windows 10.
            [D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0, 2)], explorer: true));

        return list;
    }

    // ───────────── Services to Manual ─────────────

    /// <summary>
    /// Services that normally start automatically but are only needed on demand. Manual start
    /// means Windows starts them when something asks for them. DiagTrack (telemetry) is disabled.
    /// (Not IP Helper: DirectAccess, IP-HTTPS and some VPNs need it running.)
    /// </summary>
    private static readonly (string Name, int Start)[] ManualServices =
    [
        ("DiagTrack", 4), ("dmwappushservice", 3), ("MapsBroker", 3), ("lfsvc", 3), ("PcaSvc", 3), ("TrkWks", 3),
        ("CDPSvc", 3), ("WerSvc", 3), ("RetailDemo", 3), ("Fax", 3), ("WMPNetworkSvc", 3),
        ("wisvc", 3), ("edgeupdate", 3), ("edgeupdatem", 3), ("gupdate", 3), ("gupdatem", 3),
        ("XblAuthManager", 3), ("XblGameSave", 3), ("XboxNetApiSvc", 3), ("XboxGipSvc", 3),
        ("SEMgrSvc", 3), ("PhoneSvc", 3), ("WpcMonSvc", 3),
    ];

    /// <summary>Services older versions also changed: their saved startup type is still restored.</summary>
    private static readonly string[] LegacyManualServices = ["iphlpsvc"];

    private static string ServicesBackupPath => Path.Combine(SafePath.DataFolder, "services-backup.json");

    private static string ServiceKey(string name) => $@"SYSTEM\CurrentControlSet\Services\{name}";

    private static int? ServiceStart(string name)
        => Reg.Get(HKLM, ServiceKey(name), "Start") is int v ? v : null;

    private static bool ServiceDelayed(string name)
        => Reg.Get(HKLM, ServiceKey(name), "DelayedAutostart") is int d && d != 0;

    /// <summary>Startup type of a service as saved before WinSolve changed it.</summary>
    internal sealed record ServiceBackup(int Start, bool Delayed);

    /// <summary>Reads services-backup.json (current format, or the name → start type map older versions wrote).</summary>
    internal static Dictionary<string, ServiceBackup> ParseServicesBackup(string json)
    {
        var result = new Dictionary<string, ServiceBackup>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        // Any other shape (array, number...) is a damaged file, reported like unparsable JSON.
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("The backup is not a JSON object.");
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var start))
                result[p.Name] = new ServiceBackup(start, false);
            else if (p.Value.ValueKind == JsonValueKind.Object
                     && p.Value.TryGetProperty("Start", out var s) && s.TryGetInt32(out var st))
                result[p.Name] = new ServiceBackup(st, p.Value.TryGetProperty("Delayed", out var d) && d.ValueKind == JsonValueKind.True);
        }
        return result;
    }

    /// <summary>sc.exe "start=" mode, or null for types sc config doesn't set (boot / system drivers).</summary>
    internal static string? ScStartMode(int start, bool delayed) => start switch
    {
        2 => delayed ? "delayed-auto" : "auto",
        3 => "demand",
        4 => "disabled",
        _ => null,
    };

    /// <summary>Changes a service's startup type. Returns an error description, or null on success.</summary>
    private static string? SetServiceStart(string name, int start, bool delayed = false)
    {
        if (ScStartMode(start, delayed) is not { } mode) return $"{name}: startup type {start}";
        var r = ProcessRunner.RunAsync("sc.exe", $"config \"{name}\" start= {mode}").GetAwaiter().GetResult();
        return r.Success ? null : $"{name}: {r.Output.Trim()} (exit code {r.ExitCode})";
    }

    private static void WriteAtomically(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    private static InvalidOperationException ServiceErrors(string intro, List<string> errors)
        => new(Localization.Loc.T(intro) + "\n" + string.Join("\n", errors));

    private const string Tcpip6Parameters = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters";

    private static Tweak Ipv6BitsTweak(string id, string title, string description, int bits, string? warning = null) => new()
    {
        Id = id, Category = "Network", Title = title, Description = description, NeedsReboot = true, Warning = warning,
        IsApplied = () => (Ipv6Components() & bits) == bits,
        Apply = () => SetIpv6Components(Ipv6Components() | bits),
        Revert = () => SetIpv6Components(Ipv6Components() & ~bits),
    };

    private static int Ipv6Components() => Reg.Get(HKLM, Tcpip6Parameters, "DisabledComponents") is int v ? v : 0;

    private static void SetIpv6Components(int value)
    {
        if (value == 0) Reg.Delete(HKLM, Tcpip6Parameters, "DisabledComponents"); // 0 / absent = Windows default
        else Reg.Set(HKLM, Tcpip6Parameters, "DisabledComponents", value, RegistryValueKind.DWord);
    }

    private static bool ServicesAreManual() => ManualServices.All(s => ServiceStart(s.Name) is not { } v || v >= s.Start);

    private static Tweak ServicesManualTweak() => new()
    {
        Id = "ess-services-manual", Category = "Performance",
        Title = "Set unneeded services to Manual",
        Description = "Sets ~25 background services (Xbox, Maps, Fax, telemetry, updaters...) to start only when needed. The original startup types are saved and restored by Undo.",
        IsApplied = ServicesAreManual,
        // Undo stays possible even if an update has since set one service back to Automatic, and is
        // offered without a backup too, so it can explain why it can't restore them.
        CanUndo = () => File.Exists(ServicesBackupPath) || ServicesAreManual(),
        Apply = () =>
        {
            // Save each service's original startup type before its first change, so Undo can
            // restore it exactly; services installed since the last run are added to the backup.
            // Nothing is changed if the backup can't be read (I/O error) or written.
            var backup = new Dictionary<string, ServiceBackup>(StringComparer.OrdinalIgnoreCase);
            var exists = File.Exists(ServicesBackupPath);
            if (exists)
            {
                try { backup = ParseServicesBackup(File.ReadAllText(ServicesBackupPath)); }
                catch (JsonException ex)
                {
                    // Damaged: keep it aside and start a fresh backup of the current startup types.
                    var aside = ServicesBackupPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                    File.Move(ServicesBackupPath, aside, overwrite: true);
                    Logger.Write($"services-backup.json was damaged ({ex.Message}); kept it as {aside} and saved the current startup types.");
                    exists = false;
                }
            }
            var added = false;
            foreach (var (name, _) in ManualServices)
            {
                if (backup.ContainsKey(name) || ServiceStart(name) is not { } st) continue;
                backup[name] = new ServiceBackup(st, ServiceDelayed(name));
                added = true;
            }
            if (added || !exists) WriteAtomically(ServicesBackupPath, JsonSerializer.Serialize(backup));

            var errors = new List<string>();
            foreach (var (name, start) in ManualServices)
                if (ServiceStart(name) is { } current && current < start && SetServiceStart(name, start) is { } error)
                    errors.Add(error);

            // Older versions also set IP Helper to Manual: put it back as it was.
            foreach (var name in LegacyManualServices)
                if (backup.GetValueOrDefault(name) is { } b && ServiceStart(name) is { } now && now != b.Start
                    && SetServiceStart(name, b.Start, b.Delayed) is { } error)
                    errors.Add(error);

            if (errors.Count > 0) throw ServiceErrors("Some services could not be changed:", errors);
        },
        Revert = () =>
        {
            if (!File.Exists(ServicesBackupPath))
                throw new InvalidOperationException(Localization.Loc.T(
                    "The saved startup types of these services (services-backup.json in WinSolve's data folder) are missing, so Undo can't restore them exactly. If you need one of them, set it back to Automatic in Services (services.msc)."));
            Dictionary<string, ServiceBackup> original;
            try { original = ParseServicesBackup(File.ReadAllText(ServicesBackupPath)); }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(Localization.Loc.T(
                    "The saved startup types of these services (services-backup.json in WinSolve's data folder) are damaged, so Undo can't restore them. If you need one of them, set it back to Automatic in Services (services.msc).") + $" ({ex.Message})");
            }

            var errors = new List<string>();
            foreach (var (name, b) in original)
            {
                if (!ManualServices.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    && !LegacyManualServices.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (ServiceStart(name) is null) continue; // removed since (e.g. an uninstalled updater)
                if (SetServiceStart(name, b.Start, b.Delayed) is { } error) errors.Add(error);
            }
            // Keep the backup when something failed, so Undo can be tried again.
            if (errors.Count > 0) throw ServiceErrors("Some services could not be restored:", errors);
            File.Delete(ServicesBackupPath);
        },
    };

    // ───────────── OneDrive ─────────────

    private const string ShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    /// <summary>Folders OneDrive's "back up important PC folders" (Known Folder Move) moves, with their names.</summary>
    private static readonly (string Value, string Name)[] BackedUpFolders =
    [
        ("Desktop", "Desktop"), ("Personal", "Documents"), ("{F42EE2D3-909F-4907-8871-4C22FC0BF756}", "Documents"),
        ("My Pictures", "Pictures"), ("{0DDD015D-B06C-45D5-8C4C-F59713854639}", "Pictures"),
        ("My Music", "Music"), ("{A0C69A99-21C8-4671-8703-7934162FCF1D}", "Music"),
        ("My Video", "Videos"), ("{35286A68-3C57-41A1-BBB1-0EAE73D76C95}", "Videos"),
    ];

    private static string OneDriveRemovedFlag => TweakBackup.UserFlag("adv-onedrive-remove", "removed");

    private static bool OneDriveInstalled()
        => File.Exists(Path.Combine(InteractiveUser.LocalAppData, "Microsoft", "OneDrive", "OneDrive.exe"))
           || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive", "OneDrive.exe"))
           || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft OneDrive", "OneDrive.exe"));

    /// <summary>The setup program Windows ships (used to reinstall, and to uninstall per-user installs).</summary>
    private static string? OneDriveSetup()
    {
        string[] candidates =
        [
            Path.Combine(Environment.SystemDirectory, "OneDriveSetup.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "OneDriveSetup.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The uninstaller and its arguments. Only copies standard users can't replace (it may run
    /// elevated): the one Windows ships, or that of a per-machine install in Program Files.
    /// </summary>
    private static (string Exe, string Args, bool PerMachine)? OneDriveUninstaller()
    {
        if (OneDriveSetup() is { } setup) return (setup, "/uninstall", false);
        foreach (var key in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OneDriveSetup.exe", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\OneDriveSetup.exe" })
        {
            if (Reg.Get(HKLM, key, "UninstallString") is string command && AppsService.SplitCommand(command) is { } c
                && File.Exists(c.Exe) && !SafePath.IsUserWritableLocation(c.Exe))
                return (c.Exe, c.Args, true);
        }
        return null;
    }

    /// <summary>
    /// Names of the shell folders (User Shell Folders values) that point inside one of the OneDrive
    /// folders, i.e. that OneDrive backs up. Removing OneDrive would leave them in a folder that no
    /// longer syncs, with online-only files missing.
    /// </summary>
    internal static List<string> FoldersInsideOneDrive(IReadOnlyDictionary<string, string> shellFolders, IEnumerable<string> oneDriveRoots, string profile)
    {
        string Expand(string p) => System.Text.RegularExpressions.Regex.Replace(p.Trim(), "%USERPROFILE%", profile.Replace("$", "$$"),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).TrimEnd('\\');
        var roots = oneDriveRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Expand).Where(r => r.Length > 3).ToList();
        var names = new List<string>();
        foreach (var (value, name) in BackedUpFolders)
        {
            if (!shellFolders.TryGetValue(value, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
            var folder = Expand(raw);
            if (roots.Any(r => SafePath.IsSameOrInside(folder, r)) && !names.Contains(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>The signed-in user's folders that OneDrive backs up (Known Folder Move).</summary>
    private static List<string> OneDriveBackedUpFolders()
    {
        var shell = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (value, _) in BackedUpFolders)
            if (Reg.Get(HKCU, ShellFoldersKey, value) is string p) shell[value] = p;

        var profile = InteractiveUser.Profile;
        var roots = new List<string>();
        foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Reg.Get(HKCU, "Environment", name) is string p) roots.Add(p);
        using (var root = Reg.Root(HKCU))
        using (var accounts = root.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts"))
        {
            foreach (var account in accounts?.GetSubKeyNames() ?? [])
            {
                using var a = accounts!.OpenSubKey(account);
                if (a?.GetValue("UserFolder") is string folder) roots.Add(folder);
            }
        }
        try
        {
            // "OneDrive", "OneDrive - Contoso"...
            roots.AddRange(Directory.EnumerateDirectories(profile, "OneDrive*", SafePath.NoLinks(recursive: false)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return FoldersInsideOneDrive(shell, roots, profile);
    }

    private static Tweak OneDriveTweak() => new()
    {
        Id = "adv-onedrive-remove", Category = "System",
        Title = "Remove OneDrive",
        Description = "Uninstalls the OneDrive app. Files already in your OneDrive folder stay on the PC; online-only files will no longer be available. Undo reinstalls it.",
        Warning = "Make sure every OneDrive file you need is downloaded to this PC first.",
        // Only shown when there is something to do: OneDrive is installed, or WinSolve removed it (for Undo).
        IsAvailable = () => OneDriveInstalled() || TweakBackup.HasFlag(OneDriveRemovedFlag),
        IsApplied = () => TweakBackup.HasFlag(OneDriveRemovedFlag) && !OneDriveInstalled(),
        CanUndo = () => TweakBackup.HasFlag(OneDriveRemovedFlag),
        Apply = () =>
        {
            if (!OneDriveInstalled())
                throw new InvalidOperationException(Localization.Loc.T("OneDrive isn't installed for this account, so there is nothing to remove."));

            var backedUp = OneDriveBackedUpFolders();
            if (backedUp.Count > 0)
                throw new InvalidOperationException(string.Format(Localization.Loc.T(
                    "OneDrive is backing up these folders: {0}. They now live inside the OneDrive folder, so WinSolve won't remove OneDrive. First stop backing them up in OneDrive (Settings > Sync and backup > Manage back up), then try again."),
                    string.Join(", ", backedUp.Select(Localization.Loc.T))));

            var uninstaller = OneDriveUninstaller()
                ?? throw new InvalidOperationException(Localization.Loc.T(
                    "OneDrive's uninstaller (OneDriveSetup.exe) wasn't found, so nothing was changed. Uninstall OneDrive from Settings > Apps > Installed apps instead."));

            var session = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcessesByName("OneDrive")) { try { if (p.SessionId == session) p.Kill(); } catch { } finally { p.Dispose(); } }
            if (uninstaller.PerMachine)
            {
                var r = ProcessRunner.RunAsync(uninstaller.Exe, uninstaller.Args).GetAwaiter().GetResult();
                Logger.Write($"OneDrive per-machine uninstall: exit code {r.ExitCode}. {r.Output.Trim()}");
            }
            else
            {
                // OneDrive is installed per user: uninstall it for the person signed in.
                InteractiveUser.Run(uninstaller.Exe, uninstaller.Args, 120000);
            }

            // The uninstaller doesn't report failures: check that OneDrive is really gone.
            for (int i = 0; i < 30 && OneDriveInstalled(); i++) Thread.Sleep(1000);
            if (OneDriveInstalled())
                throw new InvalidOperationException(Localization.Loc.T(
                    "OneDrive is still installed: its uninstaller didn't remove it. Uninstall it from Settings > Apps > Installed apps instead."));

            Reg.Delete(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Run", "OneDrive");
            TweakBackup.SetFlag(OneDriveRemovedFlag, true);
        },
        Revert = () =>
        {
            if (OneDriveSetup() is { } setup)
            {
                if (InteractiveUser.IsDifferent) InteractiveUser.StartAsUser(setup, "")?.Dispose();
                else Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true })?.Dispose();
            }
            else
                ProcessRunner.ShellOpen("https://www.microsoft.com/microsoft-365/onedrive/download");
            TweakBackup.SetFlag(OneDriveRemovedFlag, false);
        },
    };
}
