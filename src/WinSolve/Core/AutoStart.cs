using System.Security;
using System.Security.Principal;

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
    public static bool IsAllowed => !Edition.IsPortable && !SafePath.IsUserWritableLocation(Environment.ProcessPath ?? Application.ExecutablePath);

    /// <summary>
    /// Task definition: at this account's sign-in, elevated, also on battery power and with no
    /// time limit (schtasks /create defaults would skip it on battery, stop it when unplugged and
    /// end it after 72 hours).
    /// </summary>
    internal static string TaskXml(string exe, string userSid) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>Starts WinSolve in the notification area when you sign in.</Description></RegistrationInfo>
          <Triggers>
            <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(userSid)}</UserId><Delay>PT30S</Delay></LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author"><UserId>{SecurityElement.Escape(userSid)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--tray</Arguments></Exec>
          </Actions>
        </Task>
        """;

    public static async Task<bool> SetAsync(bool enabled)
    {
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        if (enabled && !IsAllowed)
        {
            Logger.Write($"Start with Windows refused: {exe} is in a user-writable location.");
            return false;
        }
        ProcessResult r;
        if (enabled)
        {
            // The account WinSolve runs as (an administrator), as schtasks /create used before.
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            // Staged where only administrators can write, so the definition can't be swapped before import.
            var file = Path.Combine(SafePath.CreateAdminOnlyFolder("Tasks"), "autostart.xml");
            try
            {
                await File.WriteAllTextAsync(file, TaskXml(exe, sid), System.Text.Encoding.Unicode);
                r = await ProcessRunner.RunAsync("schtasks.exe", $"/create /f /tn \"{TaskName}\" /xml \"{file}\"");
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(file)!, true); } catch { }
            }
        }
        else
            r = await ProcessRunner.RunAsync("schtasks.exe", $"/delete /f /tn \"{TaskName}\"");
        Logger.Write($"Start with Windows {(enabled ? "enabled" : "disabled")}: {r.Output.Trim()}");
        return r.Success;
    }
}
