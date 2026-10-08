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

    public static async Task<bool> SetAsync(bool enabled)
    {
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var r = enabled
            ? await ProcessRunner.RunAsync("schtasks.exe",
                $"/create /f /tn \"{TaskName}\" /sc onlogon /rl highest /delay 0000:30 /tr \"\\\"{exe}\\\" --tray\"")
            : await ProcessRunner.RunAsync("schtasks.exe", $"/delete /f /tn \"{TaskName}\"");
        Logger.Write($"Start with Windows {(enabled ? "enabled" : "disabled")}: {r.Output.Trim()}");
        return r.Success;
    }
}
