using System.Text.Json;
using WinSolve.Services;

namespace WinSolve.Core;

/// <summary>User preferences, stored in %AppData%\WinSolve\settings.json.</summary>
public sealed class AppSettings
{
    public static string FilePath { get; } = Path.Combine(
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

    /// <summary>Accent color as #RRGGBB.</summary>
    public string AccentColor { get; set; } = "#0067C0";

    public static AppSettings Current { get; private set; } = Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not read settings, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not save settings: {ex.Message}");
        }
    }

    public static void Reset()
    {
        Current = new AppSettings();
        Current.Save();
    }
}
