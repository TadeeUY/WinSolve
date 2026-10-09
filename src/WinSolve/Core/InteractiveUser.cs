using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WinSolve.Core;

/// <summary>
/// The person actually signed in to this desktop.
/// </summary>
/// <remarks>
/// WinSolve requires administrator rights. When a standard user starts it and types an
/// administrator's password ("over-the-shoulder" elevation), WinSolve runs as that other
/// account: HKCU, %TEMP%, AppData and a restarted Explorer would all belong to the
/// administrator, not to the person at the keyboard. Everything "current user" goes through
/// this class, which redirects to the signed-in account in that case.
/// </remarks>
public static class InteractiveUser
{
    public sealed record Account(SecurityIdentifier Sid, string Name, string ProfilePath);

    private static readonly Lazy<Account?> Detected = new(Detect);

    /// <summary>The signed-in account when it is not the one WinSolve runs as; otherwise null.</summary>
    public static Account? Other => Detected.Value;

    public static bool IsDifferent => Other is not null;

    private static Account? Detect()
    {
        try
        {
            var session = Process.GetCurrentProcess().SessionId;
            var user = QuerySession(session, WTSUserName);
            var domain = QuerySession(session, WTSDomainName);
            if (string.IsNullOrEmpty(user)) return null;

            var sid = (SecurityIdentifier)new NTAccount(domain ?? "", user).Translate(typeof(SecurityIdentifier));
            if (sid == WindowsIdentity.GetCurrent().User) return null;

            var profile = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid.Value)
                ?.GetValue("ProfileImagePath") as string;
            if (string.IsNullOrEmpty(profile)) return null;
            profile = Environment.ExpandEnvironmentVariables(profile);

            // Their registry hive must be loaded (it is while they are signed in).
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            using var hive = users.OpenSubKey(sid.Value);
            if (hive is null) return null;

            Logger.Write($"Signed-in user {domain}\\{user} differs from the elevated account {WindowsIdentity.GetCurrent().Name}; acting on {domain}\\{user}'s profile.");
            return new Account(sid, $"{domain}\\{user}", profile);
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not identify the signed-in user: {ex.Message}");
            return null;
        }
    }

    // ───────────── Registry ─────────────

    /// <summary>Opens a registry root; CurrentUser is the signed-in user's hive.</summary>
    public static RegistryKey OpenBase(RegistryHive hive, RegistryView view)
    {
        if (hive == RegistryHive.CurrentUser && Other is { } o)
        {
            var users = RegistryKey.OpenBaseKey(RegistryHive.Users, view);
            try
            {
                var key = users.OpenSubKey(o.Sid.Value, writable: true);
                if (key is not null) return key;
            }
            finally
            {
                users.Dispose();
            }
        }
        return RegistryKey.OpenBaseKey(hive, view);
    }

    // ───────────── Folders ─────────────

    public static string Profile => Other?.ProfilePath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string LocalAppData => Folder("Local AppData", @"AppData\Local", () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    public static string RoamingAppData => Folder("AppData", @"AppData\Roaming", () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    public static string Startup => Folder("Startup", @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup", () => Environment.GetFolderPath(Environment.SpecialFolder.Startup));
    public static string StartMenu => Folder("Start Menu", @"AppData\Roaming\Microsoft\Windows\Start Menu", () => Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
    public static string Documents => Folder("Personal", "Documents", () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    public static string Desktop => Folder("Desktop", "Desktop", () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    public static string Downloads => Folder("{374DE290-123F-4565-9164-39C4925E467B}", "Downloads",
        () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

    /// <summary>The signed-in user's %TEMP%.</summary>
    public static string TempPath
    {
        get
        {
            if (Other is not { } o) return Path.GetTempPath();
            var value = ReadUserValue(@"Environment", "TEMP");
            return value is not null && Expand(value, o) is { } expanded ? expanded : Path.Combine(LocalAppData, "Temp");
        }
    }

    private static string Folder(string shellFolderName, string relative, Func<string> own)
    {
        if (Other is not { } o) return own();
        var value = ReadUserValue(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", shellFolderName);
        return value is not null && Expand(value, o) is { } expanded ? expanded : Path.Combine(o.ProfilePath, relative);
    }

    private static string? ReadUserValue(string path, string name)
    {
        try
        {
            using var root = OpenBase(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch { return null; }
    }

    /// <summary>Expands %USERPROFILE% to the signed-in user's profile (not ours); null if anything is left unexpanded.</summary>
    private static string? Expand(string value, Account o)
    {
        var s = value.Replace("%USERPROFILE%", o.ProfilePath, StringComparison.OrdinalIgnoreCase);
        if (s.Contains("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase) || s.Contains("%APPDATA%", StringComparison.OrdinalIgnoreCase))
            return null;
        s = Environment.ExpandEnvironmentVariables(s);
        return s.Contains('%') ? null : s;
    }

    // ───────────── Processes ─────────────

    /// <summary>
    /// Starts a program as the signed-in user (used for Explorer and per-user uninstallers).
    /// Returns false if WinSolve already runs as that user, or the user's token isn't available.
    /// </summary>
    public static Process? StartAsUser(string file, string arguments, string? workingDirectory = null)
    {
        if (Other is not { } o) return null;
        using var token = FindUserToken(o.Sid);
        if (token is null) return null;

        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
        var commandLine = $"\"{file}\" {arguments}";
        if (!CreateProcessWithTokenW(token, LOGON_WITH_PROFILE, file, commandLine, 0, IntPtr.Zero,
                workingDirectory ?? Environment.SystemDirectory, ref si, out var pi))
        {
            Logger.Write($"Could not start {file} as {o.Name}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return null;
        }
        CloseHandle(pi.hThread);
        try
        {
            return Process.GetProcessById(pi.dwProcessId);
        }
        catch (ArgumentException)
        {
            return null; // already exited
        }
        finally
        {
            CloseHandle(pi.hProcess);
        }
    }

    /// <summary>
    /// Runs a program for the person at the keyboard: as the signed-in user under
    /// over-the-shoulder elevation, otherwise normally. Waits up to <paramref name="timeoutMs"/>.
    /// </summary>
    public static void Run(string file, string arguments, int timeoutMs)
    {
        if (IsDifferent)
        {
            using var p = StartAsUser(file, arguments);
            p?.WaitForExit(timeoutMs);
            return;
        }
        using var own = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true });
        own?.WaitForExit(timeoutMs);
    }

    /// <summary>Restarts Explorer as the signed-in user (an elevated Explorer would run as the administrator).</summary>
    public static void StartExplorer()
    {
        // Windows usually restarts the shell by itself after it is killed; starting another
        // one then would just open a File Explorer window.
        var session = Process.GetCurrentProcess().SessionId;
        for (int i = 0; i < 10; i++)
        {
            if (ShellRunning(session)) return;
            Thread.Sleep(200);
        }
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (IsDifferent)
        {
            // Never fall back to starting it ourselves: with no shell running it would become the
            // desktop shell, elevated, as the administrator. Windows restarts the shell itself.
            using var p = StartAsUser(explorer, "");
            if (p is null) Logger.Write("Explorer could not be started as the signed-in user; Windows will restart it.");
            return;
        }
        ProcessRunner.ShellOpen("explorer.exe");
    }

    private static bool ShellRunning(int session)
    {
        var all = Process.GetProcessesByName("explorer");
        try { return all.Any(p => { try { return p.SessionId == session; } catch { return false; } }); }
        finally { foreach (var p in all) p.Dispose(); }
    }

    /// <summary>A primary token duplicated from one of the user's processes in this session.</summary>
    private static SafeAccessTokenHandle? FindUserToken(SecurityIdentifier sid)
    {
        var session = Process.GetCurrentProcess().SessionId;
        string[] preferred = ["sihost", "explorer", "ctfmon", "StartMenuExperienceHost", "RuntimeBroker", "taskhostw"];
        var candidates = Process.GetProcesses()
            .Where(p => { try { return p.SessionId == session; } catch { return false; } })
            .OrderBy(p => { var i = Array.IndexOf(preferred, p.ProcessName); return i < 0 ? int.MaxValue : i; })
            .ToList();
        try
        {
            foreach (var p in candidates)
            {
                try
                {
                    using var identity = OwnerOf(p);
                    if (identity is null || sid != identity.User) continue;
                    if (!DuplicateTokenEx(identity.AccessToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
                        continue;
                    return primary;
                }
                catch { /* protected process */ }
            }
            return null;
        }
        finally
        {
            foreach (var p in candidates) p.Dispose();
        }
    }

    private static WindowsIdentity? OwnerOf(Process p)
    {
        if (!OpenProcessToken(p.Handle, TOKEN_QUERY | TOKEN_DUPLICATE, out var token)) return null;
        try { return new WindowsIdentity(token.DangerousGetHandle()); }
        finally { token.Dispose(); }
    }

    // ───────────── Native ─────────────

    private const int WTSUserName = 5, WTSDomainName = 7;
    private const uint TOKEN_QUERY = 0x0008, TOKEN_DUPLICATE = 0x0002, MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;
    private const uint LOGON_WITH_PROFILE = 1;

    private static string? QuerySession(int session, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logonFlags, string application, string commandLine,
        uint creationFlags, IntPtr environment, string currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
