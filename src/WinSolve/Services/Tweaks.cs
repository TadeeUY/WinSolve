using System.Runtime.InteropServices;
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

    /// <summary>Changes a machine-wide setting: every user of this PC gets it (shown in its tooltip).</summary>
    public bool AllUsers { get; init; }

    /// <summary>Whether Undo has something to restore (default: the tweak is applied).</summary>
    public Func<bool>? CanUndo { get; init; }

    /// <summary>
    /// Writes Group Policy / MDM (PolicyManager) values. On a PC an organization manages these can
    /// be overwritten by, or conflict with, its settings, so the user is warned first.
    /// </summary>
    public bool TouchesPolicies { get; init; }

    /// <summary>Exactly what the tweak changes (registry values, keys, services, commands), shown in "What does this change?".</summary>
    public IReadOnlyList<TweakChange> Changes { get; init; } = [];

    /// <summary>False when the tweak doesn't apply to this PC (e.g. OneDrive isn't installed); it is then hidden.</summary>
    public Func<bool>? IsAvailable { get; init; }

    public bool SafeIsAvailable()
    {
        try { return IsAvailable?.Invoke() ?? true; } catch { return false; }
    }

    /// <summary>Current state, never throws.</summary>
    public bool SafeIsApplied()
    {
        try { return IsApplied(); } catch { return false; }
    }

    public bool SafeCanUndo()
    {
        try { return CanUndo?.Invoke() ?? IsApplied(); } catch { return false; }
    }
}

public enum TweakChangeKind { RegistryValue, RegistryKey, Service, Command }

/// <summary>
/// One thing a tweak changes, in the form shown to the user: the registry value, key, service or
/// command, what it becomes, and what Undo does.
/// </summary>
public sealed record TweakChange(TweakChangeKind Kind, string Target, string NewValue, string Undo)
{
    public static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.Users => "HKU",
        RegistryHive.ClassesRoot => "HKCR",
        _ => hive.ToString(),
    };

    /// <summary>A registry value as data: <c>DWORD 0 (0x0)</c>, <c>SZ "Disabled"</c>, <c>BINARY 9012...</c>.</summary>
    public static string Describe(object value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => $"DWORD {Convert.ToInt64(value) & 0xFFFFFFFF} (0x{Convert.ToInt64(value) & 0xFFFFFFFF:X})",
        RegistryValueKind.QWord => $"QWORD {value}",
        RegistryValueKind.Binary when value is byte[] b => "BINARY " + Convert.ToHexString(b),
        RegistryValueKind.MultiString when value is string[] m => "MULTI_SZ " + string.Join(" | ", m.Select(x => $"\"{x}\"")),
        _ => $"SZ \"{value}\"",
    };

    public static TweakChange Value(RegistryHive hive, string path, string name, object value, RegistryValueKind kind, string undo)
        => new(TweakChangeKind.RegistryValue, $@"{HiveName(hive)}\{path}\{(name.Length == 0 ? "(Default)" : name)}", Describe(value, kind), undo);

    /// <summary>The usual Undo of a registry value: what was there before, else the Windows default.</summary>
    public static string RestoreUndo(object? off, RegistryValueKind kind)
        => off is null ? "Puts back the previous value (or removes it)" : $"Puts back the previous value (or {Describe(off, kind)})";
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

    /// <summary>
    /// Builds a tweak from one or more registry values. Apply remembers each value as it was
    /// (see <see cref="TweakBackup"/>) and Undo puts that back; only when nothing was recorded
    /// (applied by an older version, or the backup is unreadable) does Undo write the default
    /// (<see cref="RegValue.Off"/>, or delete the value).
    /// </summary>
    private static Tweak RegTweak(string id, string category, string title, string description,
        RegValue[] values, bool recommended = false, bool explorer = false, bool reboot = false,
        string? warning = null, Action? afterRevert = null) => new()
    {
        Id = id, Category = category, Title = title, Description = description, Warning = warning,
        Recommended = recommended, NeedsExplorerRestart = explorer, NeedsReboot = reboot,
        Changes = [.. values.Select(v => TweakChange.Value(v.Hive, v.Path, v.Name, v.On, v.Kind, TweakChange.RestoreUndo(v.Off, v.Kind)))],
        AllUsers = values.Any(v => v.Hive is RegistryHive.LocalMachine or RegistryHive.Users),
        TouchesPolicies = values.Any(v => IsPolicyPath(v.Path)),
        IsApplied = () => values.All(v => Reg.ValueEquals(Reg.Get(v.Hive, v.Path, v.Name), v.On)),
        Apply = () =>
        {
            TweakBackup.Remember(id, values.Select(v => (v.Hive, v.Path, v.Name, v.On)));
            foreach (var v in values) Reg.Set(v.Hive, v.Path, v.Name, v.On, v.Kind);
        },
        Revert = () =>
        {
            foreach (var v in values)
            {
                if (TweakBackup.TryRestore(id, v.Hive, v.Path, v.Name)) continue;
                if (v.Off is null) Reg.Delete(v.Hive, v.Path, v.Name);
                else Reg.Set(v.Hive, v.Path, v.Name, v.Off, v.Kind);
            }
            // Only once every value is back: a failed Undo can be retried with the same backup.
            TweakBackup.Forget(id, values.Select(v => (v.Hive, v.Path, v.Name)));
            afterRevert?.Invoke();
        },
    };

    /// <summary>Group Policy or MDM-managed location.</summary>
    internal static bool IsPolicyPath(string path)
        => path.Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(@"Policies\", StringComparison.OrdinalIgnoreCase)
           || path.Contains("PolicyManager", StringComparison.OrdinalIgnoreCase);

    private static RegValue D(RegistryHive hive, string path, string name, int on, int? off)
        => new(hive, path, name, on, off);

    private static RegValue S(RegistryHive hive, string path, string name, string on, string? off)
        => new(hive, path, name, on, off, RegistryValueKind.String);

    // ───────────── Visual effects ─────────────
    // VisualFXSetting alone is only the radio button in the Performance Options dialog; the
    // effects themselves live in these values (same set WinUtil uses), and running apps only
    // notice them through SystemParametersInfo.

    private static readonly byte[] PerformanceMask = [0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00];
    private static readonly byte[] DefaultMask = [0x9E, 0x1E, 0x07, 0x80, 0x12, 0x00, 0x00, 0x00];

    /// <summary>The values "best performance" sets, with the Windows defaults used when no backup exists.</summary>
    private static readonly RegValue[] VisualEffectsValues =
    [
        new(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 3, 0),
        new(HKCU, @"Control Panel\Desktop", "UserPreferencesMask", PerformanceMask, DefaultMask, RegistryValueKind.Binary),
        new(HKCU, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1", RegistryValueKind.String),
        new(HKCU, Adv, "TaskbarAnimations", 0, 1),
        new(HKCU, Adv, "ListviewAlphaSelect", 0, 1),
        new(HKCU, Adv, "ListviewShadow", 0, 1),
        new(HKCU, @"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0, 1),
    ];

    private static Tweak VisualEffectsTweak() => new()
    {
        Id = "perf-visual-effects", Category = "Performance", Title = "Visual effects: best performance",
        Description = "Turns off animations, fades and shadows (keeps smooth fonts and thumbnails). Only recommended on slow PCs.",
        NeedsExplorerRestart = true,
        Changes =
        [
            .. VisualEffectsValues.Select(v => TweakChange.Value(v.Hive, v.Path, v.Name, v.On, v.Kind, TweakChange.RestoreUndo(v.Off, v.Kind))),
            new(TweakChangeKind.Command, "SystemParametersInfo SPI_SETANIMATION / SPI_SETUIEFFECTS / SPI_SETCLIENTAREAANIMATION", "Off (current session)", "Back on if the restored settings have animations"),
        ],
        IsApplied = () => Reg.Get(HKCU, @"Control Panel\Desktop\WindowMetrics", "MinAnimate") is "0"
                          && Reg.Get(HKCU, Adv, "TaskbarAnimations") is 0,
        Apply = () =>
        {
            // Remembers each value as it was, so Undo puts back the user's own choice, not Windows' defaults.
            TweakBackup.Remember("perf-visual-effects", VisualEffectsValues.Select(v => (v.Hive, v.Path, v.Name, v.On)));
            foreach (var v in VisualEffectsValues) Reg.Set(v.Hive, v.Path, v.Name, v.On, v.Kind);
            ApplyAnimationsNow(on: false);
        },
        Revert = () =>
        {
            foreach (var v in VisualEffectsValues)
            {
                if (TweakBackup.TryRestore("perf-visual-effects", v.Hive, v.Path, v.Name)) continue;
                if (v.Off is null) Reg.Delete(v.Hive, v.Path, v.Name);
                else Reg.Set(v.Hive, v.Path, v.Name, v.Off, v.Kind);
            }
            TweakBackup.Forget("perf-visual-effects", VisualEffectsValues.Select(v => (v.Hive, v.Path, v.Name)));
            ApplyAnimationsNow(on: Reg.Get(HKCU, @"Control Panel\Desktop\WindowMetrics", "MinAnimate") is not "0");
        },
    };

    /// <summary>
    /// Applies the animation switches to the signed-in session right away (only possible when
    /// WinSolve runs as that user; otherwise they take effect at the next sign-in).
    /// </summary>
    private static void ApplyAnimationsNow(bool on)
    {
        if (InteractiveUser.IsDifferent) return;
        var value = on ? 1 : 0;
        var anim = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = value };
        SystemParametersInfo(SPI_SETANIMATION, anim.cbSize, ref anim, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        SystemParametersInfo(SPI_SETUIEFFECTS, 0, (IntPtr)value, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        SystemParametersInfo(SPI_SETCLIENTAREAANIMATION, 0, (IntPtr)value, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO
    {
        public uint cbSize;
        public int iMinAnimate;
    }

    private const uint SPI_SETANIMATION = 0x0049, SPI_SETUIEFFECTS = 0x103F, SPI_SETCLIENTAREAANIMATION = 0x1043;
    private const uint SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref ANIMATIONINFO info, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);

    private static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Runs powercfg and fails with its message when it reports an error.</summary>
    private static void Powercfg(string arguments)
    {
        var r = ProcessRunner.RunAsync("powercfg.exe", arguments).GetAwaiter().GetResult();
        if (!r.Success)
            throw new InvalidOperationException($"powercfg {arguments} failed (exit code {r.ExitCode}): {r.Output.Trim()}");
    }

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
                // The newer Copilot app is an ordinary Store app this policy doesn't cover; there is
                // no simple supported policy for it, so the description says so instead.
                "Turns off the Windows Copilot sidebar for your account. The newer Copilot app is a regular app this setting doesn't remove: uninstall it from Apps if you don't want it.",
                [
                    // Per user only: the machine-wide policy would turn it off for every account.
                    D(HKCU, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),
                ], explorer: true),
            RegTweak("priv-recall", "Privacy", "Disable Recall",
                "Prevents Windows from saving screen snapshots for Recall.",
                [D(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, null)],
                recommended: true),

            // ───────────── Performance ─────────────
            RegTweak("perf-game-mode", "Performance", "Enable Game Mode",
                "Prioritizes the game in the foreground and holds back driver installs and notifications while playing.",
                [D(HKCU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, null)], // absent = on (Windows default)
                recommended: true),
            RegTweak("perf-game-dvr", "Performance", "Disable background recording (Game DVR)",
                "Turns off continuous Xbox Game Bar capture, which costs GPU time and disk writes.",
                [
                    D(HKCU, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                    D(HKCU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1),
                ], recommended: true),
            RegTweak("perf-hags", "Performance", "Hardware-accelerated GPU scheduling",
                "Lowers latency on supported GPUs (NVIDIA GTX 10 series+, AMD RX 5000+). Requires a restart.",
                [D(HKLM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, null)], // absent = driver default
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
            VisualEffectsTweak(),
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
                "Shows technical parameters on blue screens. The PC still restarts automatically afterwards (the details are also saved in the crash dump).",
                [D(HKLM, @"SYSTEM\CurrentControlSet\Control\CrashControl", "DisplayParameters", 1, null)],
                // Older versions also turned off the automatic restart, which can leave a server or
                // unattended PC stuck on a blue screen: Undo turns it back on.
                afterRevert: () =>
                {
                    if (Reg.ValueEquals(Reg.Get(HKLM, @"SYSTEM\CurrentControlSet\Control\CrashControl", "AutoReboot"), 0))
                        Reg.Set(HKLM, @"SYSTEM\CurrentControlSet\Control\CrashControl", "AutoReboot", 1, RegistryValueKind.DWord);
                }),
            new Tweak
            {
                Id = "sys-hibernation-off", Category = "System",
                Title = "Disable hibernation",
                Description = "Frees the space used by hiberfil.sys (roughly the size of your RAM). Also disables Fast Startup.",
                Changes =
                [
                    new(TweakChangeKind.Command, "powercfg /hibernate off", "Deletes hiberfil.sys and sets HibernateEnabled = 0", "powercfg /hibernate on"),
                ],
                IsApplied = () => !File.Exists(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "hiberfil.sys"))
                                  && Reg.ValueEquals(Reg.Get(HKLM, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled"), 0),
                Apply = () => Powercfg("/hibernate off"),
                Revert = () => Powercfg("/hibernate on"),
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
                    Changes =
                    [
                        new(TweakChangeKind.RegistryKey, $@"HKCU\{ClassicMenuKey}\InprocServer32", "Created, (Default) = empty", "Deletes the key"),
                    ],
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
