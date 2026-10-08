namespace WinSolve.Core;

/// <summary>
/// Start with Windows through an elevated scheduled task (a Run key cannot start
/// an app that requires administrator rights without a UAC prompt).
/// </summary>
public static class AutoStart
{
    private const string TaskName = "WinSolve";

    public static bool IsEnabled()
        => ProcessRunner.RunAsync("schtasks.exe", $"/query /tn \"{TaskName}\"").GetAwaiter().GetResult().Success;

    /// <summary>
    /// The task runs WinSolve elevated without a UAC prompt. If the executable lives where a
    /// standard user can replace it (e.g. a per-user install), that would hand any program
    /// silent administrator rights, so it is only allowed from Program Files.
    /// </summary>
    public static bool IsAllowed => !SafePath.IsUserWritableLocation(Environment.ProcessPath ?? Application.ExecutablePath);

    public static async Task<bool> SetAsync(bool enabled)
    {
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        if (enabled && !IsAllowed)
        {
            Logger.Write($"Start with Windows refused: {exe} is in a user-writable location.");
            return false;
        }
        var r = enabled
            ? await ProcessRunner.RunAsync("schtasks.exe",
                $"/create /f /tn \"{TaskName}\" /sc onlogon /rl highest /delay 0000:30 /tr \"\\\"{exe}\\\" --tray\"")
            : await ProcessRunner.RunAsync("schtasks.exe", $"/delete /f /tn \"{TaskName}\"");
        Logger.Write($"Start with Windows {(enabled ? "enabled" : "disabled")}: {r.Output.Trim()}");
        return r.Success;
    }
}
