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
    private const string GalleryClsid = @"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}";

    /// <summary>WinUtil-like "Standard" preset: safe, reversible essentials.</summary>
    public static readonly string[] StandardPreset =
    [
        "ess-temp-files", "ess-consumer-features", "priv-telemetry", "priv-activity-history", "ess-folder-discovery",
        "perf-game-dvr", "ess-location", "ess-wifi-sense", "ui-end-task", "ess-services-manual",
        "ess-ps7-telemetry", "priv-advertising-id", "priv-tailored", "priv-suggestions",
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
                "Turns off the location service and stops apps from reading your location.",
                [
                    S(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny", "Allow"),
                    D(HKLM, @"SYSTEM\CurrentControlSet\Services\lfsvc\Service\Configuration", "Status", 0, 1),
                    D(HKLM, @"SYSTEM\Maps", "AutoUpdateEnabled", 0, 1),
                ]),
            RegTweak("ess-storage-sense", "System", "Disable Storage Sense",
                "Stops Windows from deleting files automatically (Downloads, Recycle Bin) when space runs low.",
                [D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 0, 1)]),
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
                "Turns off Edge's sidebar, shopping assistant, startup boost, first-run screens, recommendations and diagnostic data (Edge stays installed).",
                [
                    D(HKLM, EdgePolicy, "HubsSidebarEnabled", 0, null),
                    D(HKLM, EdgePolicy, "EdgeShoppingAssistantEnabled", 0, null),
                    D(HKLM, EdgePolicy, "StartupBoostEnabled", 0, null),
                    D(HKLM, EdgePolicy, "HideFirstRunExperience", 1, null),
                    D(HKLM, EdgePolicy, "ShowRecommendationsEnabled", 0, null),
                    D(HKLM, EdgePolicy, "PersonalizationReportingEnabled", 0, null),
                    D(HKLM, EdgePolicy, "UserFeedbackAllowed", 0, null),
                    D(HKLM, EdgePolicy, "DiagnosticData", 0, null),
                    D(HKLM, EdgePolicy, "EdgeCollectionsEnabled", 0, null),
                    D(HKLM, @"SOFTWARE\Policies\Microsoft\EdgeUpdate", "CreateDesktopShortcutDefault", 0, null),
                ]),
            ServicesManualTweak(),

            // ───────────── Advanced (caution) ─────────────
            RegTweak("adv-fso-off", "Performance", "Disable fullscreen optimizations",
                "Uses true exclusive fullscreen in games. Can lower input latency on some games; may break Alt+Tab overlays.",
                [D(HKCU, @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 1, 0)]),
            RegTweak("adv-ipv6-off", "Network", "Disable IPv6",
                "Turns IPv6 off on every adapter. Only useful for specific network problems; some features (HomeGroup, DirectAccess) need IPv6. Requires a restart.",
                [D(HKLM, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 255, 0)], reboot: true),
            RegTweak("adv-prefer-ipv4", "Network", "Prefer IPv4 over IPv6",
                "Keeps IPv6 enabled but makes Windows try IPv4 first. Requires a restart.",
                [D(HKLM, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 32, 0)], reboot: true),
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
                reboot: true),
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
                IsApplied = () => !Reg.KeyExists(HKLM, HomeNamespace)
                                  && Reg.ValueEquals(Reg.Get(HKCU, GalleryClsid, "System.IsPinnedToNameSpaceTree"), 0),
                Apply = () =>
                {
                    Reg.DeleteKeyTree(HKLM, HomeNamespace);
                    Reg.Set(HKCU, GalleryClsid, "System.IsPinnedToNameSpaceTree", 0, RegistryValueKind.DWord);
                },
                Revert = () =>
                {
                    Reg.Set(HKLM, HomeNamespace, "", "CLSID_MSGraphHomeFolder", RegistryValueKind.String);
                    Reg.Delete(HKCU, GalleryClsid, "System.IsPinnedToNameSpaceTree");
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
                new(RegistryHive.Users, @".DEFAULT\Control Panel\Keyboard", "InitialKeyboardIndicators", "2", "0", RegistryValueKind.String),
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
            [D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0, 1)], explorer: true));

        return list;
    }

    // ───────────── Services to Manual ─────────────

    /// <summary>
    /// Services that normally start automatically but are only needed on demand. Manual start
    /// means Windows starts them when something asks for them. DiagTrack (telemetry) is disabled.
    /// </summary>
    private static readonly (string Name, int Start)[] ManualServices =
    [
        ("DiagTrack", 4), ("dmwappushservice", 3), ("MapsBroker", 3), ("lfsvc", 3), ("PcaSvc", 3), ("TrkWks", 3),
        ("iphlpsvc", 3), ("CDPSvc", 3), ("WerSvc", 3), ("RetailDemo", 3), ("Fax", 3), ("WMPNetworkSvc", 3),
        ("wisvc", 3), ("edgeupdate", 3), ("edgeupdatem", 3), ("gupdate", 3), ("gupdatem", 3),
        ("XblAuthManager", 3), ("XblGameSave", 3), ("XboxNetApiSvc", 3), ("XboxGipSvc", 3),
        ("SEMgrSvc", 3), ("PhoneSvc", 3), ("WpcMonSvc", 3),
    ];

    private static string ServicesBackupPath => Path.Combine(SafePath.DataFolder, "services-backup.json");

    private static int? ServiceStart(string name)
        => Reg.Get(HKLM, $@"SYSTEM\CurrentControlSet\Services\{name}", "Start") is int v ? v : null;

    private static void SetServiceStart(string name, int start)
    {
        var mode = start switch { 2 => "auto", 3 => "demand", 4 => "disabled", _ => "demand" };
        using var p = Process.Start(new ProcessStartInfo(ProcessRunner.Resolve("sc.exe"), $"config \"{name}\" start= {mode}")
        { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(10000);
    }

    private static Tweak ServicesManualTweak() => new()
    {
        Id = "ess-services-manual", Category = "Performance",
        Title = "Set unneeded services to Manual",
        Description = "Sets ~25 background services (Xbox, Maps, Fax, telemetry, updaters...) to start only when needed. The original startup types are saved and restored by Undo.",
        IsApplied = () => ManualServices.All(s => ServiceStart(s.Name) is not { } v || v >= s.Start),
        Apply = () =>
        {
            // Save the original startup types once, so Undo can restore them exactly.
            if (!File.Exists(ServicesBackupPath))
            {
                var original = ManualServices.Select(s => (s.Name, Start: ServiceStart(s.Name)))
                    .Where(s => s.Start is not null).ToDictionary(s => s.Name, s => s.Start!.Value);
                File.WriteAllText(ServicesBackupPath, JsonSerializer.Serialize(original));
            }
            foreach (var (name, start) in ManualServices)
                if (ServiceStart(name) is { } current && current < start) SetServiceStart(name, start);
        },
        Revert = () =>
        {
            if (!File.Exists(ServicesBackupPath)) return;
            var original = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(ServicesBackupPath)) ?? [];
            foreach (var (name, start) in original)
                if (ManualServices.Any(s => s.Name == name)) SetServiceStart(name, start);
            File.Delete(ServicesBackupPath);
        },
    };

    // ───────────── OneDrive ─────────────

    private static string? OneDriveSetup()
    {
        string[] candidates =
        [
            Path.Combine(Environment.SystemDirectory, "OneDriveSetup.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "OneDriveSetup.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool OneDriveInstalled()
        => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive", "OneDrive.exe"))
           || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive", "OneDrive.exe"))
           || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft OneDrive", "OneDrive.exe"));

    private static Tweak OneDriveTweak() => new()
    {
        Id = "adv-onedrive-remove", Category = "System",
        Title = "Remove OneDrive",
        Description = "Uninstalls the OneDrive app. Files already in your OneDrive folder stay on the PC; online-only files will no longer be available. Undo reinstalls it.",
        Warning = "Make sure every OneDrive file you need is downloaded to this PC first.",
        IsApplied = () => !OneDriveInstalled(),
        Apply = () =>
        {
            foreach (var p in Process.GetProcessesByName("OneDrive")) { try { p.Kill(); } catch { } finally { p.Dispose(); } }
            if (OneDriveSetup() is { } setup)
            {
                using var p = Process.Start(new ProcessStartInfo(setup, "/uninstall") { UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(120000);
            }
            Reg.Delete(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Run", "OneDrive");
        },
        Revert = () =>
        {
            if (OneDriveSetup() is { } setup)
                Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true })?.Dispose();
            else
                ProcessRunner.ShellOpen("https://www.microsoft.com/microsoft-365/onedrive/download");
        },
    };
}
