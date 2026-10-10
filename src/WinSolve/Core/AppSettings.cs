using System.Text.Json;
using WinSolve.Services;

namespace WinSolve.Core;

/// <summary>Preferences, stored in %ProgramData%\WinSolve\settings.json (administrators-only).</summary>
public sealed class AppSettings
{
    public static string FilePath => Path.Combine(SafePath.DataFolder, "settings.json");

    // Location used by version 0.1.0; read once to migrate.
    private static readonly string LegacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSolve", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>One-click uses the custom lists below instead of the automatic profile.</summary>
    public bool UseCustomOneClick { get; set; }

    /// <summary>Selected device type (null = auto-detect).</summary>
    public DeviceKind? Device { get; set; }

    public OptimizationLevel Level { get; set; } = OptimizationLevel.Balanced;

    /// <summary>Task ids for the custom one-click list. Null = recommended.</summary>
    public List<string>? OneClickTasks { get; set; }

    /// <summary>Tweak ids for the custom one-click list. Null = recommended.</summary>
    public List<string>? OneClickTweaks { get; set; }

    public bool CreateRestorePoint { get; set; } = true;
    public bool ConfirmActions { get; set; } = true;
    public bool ScanOnStartup { get; set; } = true;

    /// <summary>Watch the event log and pop up an alert when Windows reports an error.</summary>
    public bool ErrorAlerts { get; set; } = true;

    /// <summary>Closing the window keeps WinSolve running in the notification area.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Minimize / restore animations.</summary>
    public bool Animations { get; set; } = true;

    /// <summary>Last time the event log was checked (to report what happened while closed).</summary>
    public DateTime? LastErrorCheck { get; set; }

    /// <summary>Alert kinds the user chose not to see again.</summary>
    public List<string> MutedAlerts { get; set; } = [];

    /// <summary>Check GitHub for new versions once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>Install new versions without asking (still SHA-256 verified).</summary>
    public bool AutoInstallUpdates { get; set; }

    /// <summary>Automatic maintenance schedule: Off, Daily, Weekly or Monthly.</summary>
    public string MaintenanceSchedule { get; set; } = "Off";

    /// <summary>Format of the scheduled task this install created (see Maintenance).</summary>
    public int MaintenanceTaskFormat { get; set; }

    /// <summary>Last drive health readings, to alert when a drive gets worse (key: model + serial).</summary>
    public Dictionary<string, Services.DiskBaseline> DiskBaselines { get; set; } = [];

    /// <summary>UI language: "en" or "es".</summary>
    public string Language { get; set; } = "en";

    /// <summary>No settings file yet: WinSolve's first start on this PC (shows the welcome screen).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFirstRun { get; private set; }

    /// <summary>Color theme: System (follows Windows), Dark or Light.</summary>
    public string ThemeMode { get; set; } = "System";

    /// <summary>Accent color as #RRGGBB.</summary>
    public string AccentColor { get; set; } = "#0067C0";

    public static AppSettings Current { get; private set; } = Load();

    public static AppSettings Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : File.Exists(LegacyPath) ? LegacyPath : null;
            if (path is not null)
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not read settings, using defaults: {ex.Message}");
            return new AppSettings();
        }
        return new AppSettings { IsFirstRun = true };
    }

    /// <summary>Saves atomically (temp file + rename), so a crash mid-write can't corrupt the settings.</summary>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not save settings: {ex.Message}");
            return false;
        }
    }

    public static void Reset()
    {
        Current = new AppSettings();
        Current.Save();
    }
}
