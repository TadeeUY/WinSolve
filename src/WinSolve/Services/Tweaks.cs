using Microsoft.Win32;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>
/// A reversible system tweak (Wintoys style): it can read its current state,
/// apply itself and revert itself.
/// </summary>
public enum TweakGroup { Essential, Advanced, Preference }

public sealed class Tweak
{
    public required string Id { get; init; }

    /// <summary>Where the tweak appears on the Tweaks page (WinUtil-style sections).</summary>
    public TweakGroup Group { get; internal set; } = TweakGroup.Advanced;

    /// <summary>One-shot action (e.g. delete temp files): never shows as applied and cannot be undone.</summary>
    public bool IsAction { get; init; }

    /// <summary>Extra warning shown before running it.</summary>
    public string? Warning { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required Func<bool> IsApplied { get; init; }
    public required Action Apply { get; init; }
    public required Action Revert { get; init; }

    /// <summary>Part of the default custom one-click list.</summary>
    public bool Recommended { get; init; }

    /// <summary>Explorer must be restarted to see the change.</summary>
    public bool NeedsExplorerRestart { get; init; }

    public bool NeedsReboot { get; init; }

    /// <summary>Current state, never throws.</summary>
    public bool SafeIsApplied()
    {
        try { return IsApplied(); } catch { return false; }
    }
}

/// <summary>A registry value that is part of a tweak.</summary>
public sealed record RegValue(
    RegistryHive Hive,
    string Path,
    string Name,
    object On,
    object? Off,                 // null = delete the value when reverting
    RegistryValueKind Kind = RegistryValueKind.DWord);

public static partial class TweakCatalog
{
    private const RegistryHive HKCU = RegistryHive.CurrentUser;
    private const RegistryHive HKLM = RegistryHive.LocalMachine;

    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ClassicMenuKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    // Lazy: the catalog uses static tables declared in TweaksExtra.cs, whose initialization
    // order relative to this file is not guaranteed.
    private static readonly Lazy<IReadOnlyList<Tweak>> Catalog = new(Build);

    public static IReadOnlyList<Tweak> All => Catalog.Value;

    public static Tweak? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>Builds a tweak from one or more registry values.</summary>
    private static Tweak RegTweak(string id, string category, string title, string description,
        RegValue[] values, bool recommended = false, bool explorer = false, bool reboot = false) => new()
    {
        Id = id, Category = category, Title = title, Description = description,
        Recommended = recommended, NeedsExplorerRestart = explorer, NeedsReboot = reboot,
        IsApplied = () => values.All(v => Reg.ValueEquals(Reg.Get(v.Hive, v.Path, v.Name), v.On)),
        Apply = () =>
        {
            foreach (var v in values) Reg.Set(v.Hive, v.Path, v.Name, v.On, v.Kind);
        },
        Revert = () =>
        {
            foreach (var v in values)
            {
                if (v.Off is null) Reg.Delete(v.Hive, v.Path, v.Name);
                else Reg.Set(v.Hive, v.Path, v.Name, v.Off, v.Kind);
            }
        },
    };

    private static RegValue D(RegistryHive hive, string path, string name, int on, int? off)
        => new(hive, path, name, on, off);

    private static RegValue S(RegistryHive hive, string path, string name, string on, string? off)
        => new(hive, path, name, on, off, RegistryValueKind.String);

    private static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    private static List<Tweak> Build()
    {
        var list = new List<Tweak>
        {
            // ───────────── Privacy ─────────────
            RegTweak("priv-telemetry", "Privacy", "Minimize telemetry",
                "Limits diagnostic data sent to Microsoft to the lowest level your edition allows.",
                [D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, null)],
                recommended: true),
            RegTweak("priv-advertising-id", "Privacy", "Disable advertising ID",
                "Stops apps from using an identifier to show you personalized ads.",
                [D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1)],
                recommended: true),
            RegTweak("priv-activity-history", "Privacy", "Disable activity history",
                "Windows stops storing and uploading the apps and documents you open.",
                [
                    D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, null),
                    D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, null),
                    D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, null),
                ], recommended: true),
            RegTweak("priv-tailored", "Privacy", "Disable tailored experiences",
                "Microsoft won't use your diagnostic data for tips and ads.",
                [D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1)],
                recommended: true),
            RegTweak("priv-suggestions", "Privacy", "Remove Windows suggestions and ads",
                "Turns off suggested apps in Start, tips, the post-update welcome screen and promoted content.",
                [
                    D(HKCU, Cdm, "SubscribedContent-338388Enabled", 0, 1),
                    D(HKCU, Cdm, "SubscribedContent-338389Enabled", 0, 1),
                    D(HKCU, Cdm, "SubscribedContent-353694Enabled", 0, 1),
                    D(HKCU, Cdm, "SubscribedContent-353696Enabled", 0, 1),
                    D(HKCU, Cdm, "SystemPaneSuggestionsEnabled", 0, 1),
                    D(HKCU, Cdm, "SilentInstalledAppsEnabled", 0, 1),
                    D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, 1),
                ], recommended: true),
            RegTweak("priv-bing-search", "Privacy", "Remove Bing results from Start search",
                "Start menu search shows local results only.",
                [D(HKCU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null)],
                explorer: true),
            RegTweak("priv-copilot", "Privacy", "Disable Copilot",
                "Hides and disables Windows Copilot.",
                [
                    D(HKCU, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),
                    D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),
                ], explorer: true),
            RegTweak("priv-recall", "Privacy", "Disable Recall",
                "Prevents Windows from saving screen snapshots for Recall.",
                [D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, null)],
                recommended: true),

            // ───────────── Performance ─────────────
            RegTweak("perf-game-mode", "Performance", "Enable Game Mode",
                "Prioritizes the game in the foreground and holds back driver installs and notifications while playing.",
                [D(HKCU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 0)],
                recommended: true),
            RegTweak("perf-game-dvr", "Performance", "Disable background recording (Game DVR)",
                "Turns off continuous Xbox Game Bar capture, which costs GPU time and disk writes.",
                [
                    D(HKCU, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                    D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1),
                ], recommended: true),
            RegTweak("perf-hags", "Performance", "Hardware-accelerated GPU scheduling",
                "Lowers latency on supported GPUs (NVIDIA GTX 10 series+, AMD RX 5000+). Requires a restart.",
                [D(HKLM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1)],
                reboot: true),
            RegTweak("perf-background-apps", "Performance", "Block background apps",
                "Store apps won't run in the background unless you open them.",
                [D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, 0)]),
            RegTweak("perf-menu-delay", "Performance", "Faster menus",
                "Reduces the submenu delay from 400 ms to 50 ms.",
                [S(HKCU, @"Control Panel\Desktop", "MenuShowDelay", "50", "400")],
                recommended: true),
            RegTweak("perf-startup-delay", "Performance", "Remove startup app delay",
                "Startup apps launch without the artificial delay Windows adds.",
                [D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0, null)],
                recommended: true),
            RegTweak("perf-visual-effects", "Performance", "Visual effects: best performance",
                "Turns off animations and shadows. Only recommended on slow PCs.",
                [D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2, 0)],
                explorer: true),
            RegTweak("perf-network-throttling", "Performance", "Disable multimedia network throttling",
                "Removes the packet limit Windows applies while media is playing (helps online games).",
                [new(HKLM, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), 10)]),
            RegTweak("perf-fast-startup-off", "Performance", "Disable Fast Startup",
                "Fast Startup can cause driver, update and dual-boot problems. Shutting down becomes a full shutdown.",
                [D(HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, 1)]),

            // ───────────── Interface ─────────────
            RegTweak("ui-file-extensions", "Interface", "Show file extensions",
                "Shows .exe, .pdf and so on. Helps spot disguised malicious files.",
                [D(HKCU, Adv, "HideFileExt", 0, 1)], recommended: true, explorer: true),
            RegTweak("ui-hidden-files", "Interface", "Show hidden files",
                "Shows folders such as AppData in File Explorer.",
                [D(HKCU, Adv, "Hidden", 1, 2)], explorer: true),
            RegTweak("ui-this-pc", "Interface", "Open File Explorer to This PC",
                "File Explorer opens to This PC instead of Home / Quick access.",
                [D(HKCU, Adv, "LaunchTo", 1, null)]),
            RegTweak("ui-dark-mode", "Interface", "Dark mode",
                "Uses the dark theme for Windows and apps.",
                [D(HKCU, Personalize, "AppsUseLightTheme", 0, 1), D(HKCU, Personalize, "SystemUsesLightTheme", 0, 1)]),
            RegTweak("ui-sticky-keys", "Interface", "Disable the Sticky Keys prompt",
                "Pressing Shift five times no longer opens the Sticky Keys prompt.",
                [S(HKCU, @"Control Panel\Accessibility\StickyKeys", "Flags", "506", "510")],
                recommended: true),
            RegTweak("ui-verbose-status", "Interface", "Verbose startup and shutdown messages",
                "Shows what Windows is doing during boot and shutdown — useful for diagnosing hangs.",
                [D(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "VerboseStatus", 1, null)]),

            // ───────────── System ─────────────
            RegTweak("sys-long-paths", "System", "Enable long paths (>260 characters)",
                "Avoids 'path too long' errors in modern programs.",
                [D(HKLM, @"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1, 0)],
                recommended: true),
            RegTweak("sys-no-auto-reboot", "System", "No automatic restarts for updates",
                "Windows Update won't restart the PC while someone is signed in.",
                [D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1, null)],
                recommended: true),
            RegTweak("sys-no-driver-updates", "System", "Don't install drivers through Windows Update",
                "Useful when Windows replaces your GPU (or other) drivers with generic versions.",
                [D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1, null)]),
            RegTweak("sys-bsod-details", "System", "Detailed blue screens",
                "Shows technical parameters on blue screens and disables the automatic restart so you can read them.",
                [
                    D(HKLM, @"SYSTEM\CurrentControlSet\Control\CrashControl", "DisplayParameters", 1, null),
                    D(HKLM, @"SYSTEM\CurrentControlSet\Control\CrashControl", "AutoReboot", 0, 1),
                ]),
            new Tweak
            {
                Id = "sys-hibernation-off", Category = "System",
                Title = "Disable hibernation",
                Description = "Frees the space used by hiberfil.sys (roughly the size of your RAM). Also disables Fast Startup.",
                IsApplied = () => !File.Exists(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "hiberfil.sys"))
                                  && Reg.ValueEquals(Reg.Get(HKLM, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled"), 0),
                Apply = () => ProcessRunner.RunAsync("powercfg.exe", "/hibernate off").GetAwaiter().GetResult(),
                Revert = () => ProcessRunner.RunAsync("powercfg.exe", "/hibernate on").GetAwaiter().GetResult(),
            },
        };

        if (IsWindows11)
        {
            list.AddRange(
            [
                new Tweak
                {
                    Id = "ui-classic-context-menu", Category = "Interface",
                    Title = "Classic context menu (Windows 11)",
                    Description = "Shows the full right-click menu without going through 'Show more options'.",
                    NeedsExplorerRestart = true,
                    IsApplied = () => Reg.KeyExists(HKCU, ClassicMenuKey + @"\InprocServer32"),
                    Apply = () => Reg.Set(HKCU, ClassicMenuKey + @"\InprocServer32", "", "", RegistryValueKind.String),
                    Revert = () => Reg.DeleteKeyTree(HKCU, ClassicMenuKey),
                },
                RegTweak("ui-taskbar-left", "Interface", "Left-aligned taskbar (Windows 11)",
                    "Moves the Start button to the corner, like Windows 10.",
                    [D(HKCU, Adv, "TaskbarAl", 0, 1)]),
                RegTweak("ui-no-widgets", "Interface", "Disable Widgets (Windows 11)",
                    "Removes the Widgets / news panel from the taskbar.",
                    [D(HKLM, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, null)],
                    explorer: true),
                RegTweak("ui-end-task", "Interface", "'End task' on the taskbar (Windows 11)",
                    "Adds 'End task' to the right-click menu of open apps to close frozen programs.",
                    [D(HKCU, Adv + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, null)],
                    recommended: true),
                RegTweak("ui-no-task-view", "Interface", "Hide the Task View button",
                    "Removes the virtual desktops button from the taskbar.",
                    [D(HKCU, Adv, "ShowTaskViewButton", 0, 1)]),
            ]);
        }

        list.AddRange(BuildExtra());
        AssignGroups(list);
        return list;
    }
}
