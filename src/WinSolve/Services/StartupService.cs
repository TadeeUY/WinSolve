using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WinSolve.Core;

namespace WinSolve.Services;

public enum StartupSource { Registry, Folder, ScheduledTask }

public sealed class StartupItem
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required string Location { get; init; }
    public required StartupSource Source { get; init; }

    // Registry / folder entries
    public RegistryHive Hive { get; init; }
    public RegistryView View { get; init; } = RegistryView.Registry64;
    public string RunKey { get; init; } = "";
    public string ApprovedKey { get; init; } = "";
    public string FilePath { get; init; } = "";

    // Scheduled tasks
    public string TaskPath { get; init; } = "";
    public bool RunsAsSystem { get; init; }

    public bool Enabled { get; set; }

    /// <summary>Why WinSolve won't remove it (Windows Security, a Windows component); null when it may.</summary>
    public string? ProtectedReason { get; set; }
    public bool IsProtected => ProtectedReason is not null;

    /// <summary>Windows Security: can't be turned off either (turning it back on is always allowed).</summary>
    public bool BlocksDisable => ProtectedReason == StartupService.DefenderReason;

    /// <summary>Starts for every user of the PC, or runs as SYSTEM: disabling is the safer choice.</summary>
    public bool IsMachineWide => RunsAsSystem || (Source != StartupSource.ScheduledTask && Hive == RegistryHive.LocalMachine);
}

/// <summary>A removed startup entry, saved so it can be put back.</summary>
public sealed class StartupBackup
{
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public string Command { get; set; } = "";
    public StartupSource Source { get; set; }
    public RegistryHive Hive { get; set; }
    public RegistryView View { get; set; }
    public string RunKey { get; set; } = "";
    public string ApprovedKey { get; set; } = "";
    public RegistryValueKind ValueKind { get; set; }
    public string? ValueData { get; set; }
    public byte[]? ApprovedData { get; set; }
    public string FilePath { get; set; } = "";
    public string? FileCopy { get; set; }
    public string TaskPath { get; set; } = "";
    public string? TaskXmlFile { get; set; }
    /// <summary>The account whose registry or Startup folder the entry was in.</summary>
    public string UserSid { get; set; } = "";
    public DateTime RemovedAt { get; set; }

    [JsonIgnore] public string BackupFile { get; set; } = "";
}

/// <summary>
/// Programs that start with Windows: Run keys, Startup folders and logon scheduled tasks.
/// Disabling uses the same StartupApproved keys as Task Manager (fully reversible);
/// removing deletes the entry after saving a backup that "Restore removed..." puts back.
/// </summary>
public static class StartupService
{
    private const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static async Task<List<StartupItem>> GetItemsAsync()
    {
        var items = new List<StartupItem>();

        void FromRegistry(RegistryHive hive, RegistryView view, string approvedName, string location)
        {
            try
            {
                using var root = InteractiveUser.OpenBase(hive, view);
                using var key = root.OpenSubKey(RunPath);
                if (key is null) return;
                foreach (var name in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    var item = new StartupItem
                    {
                        Name = name,
                        Command = key.GetValue(name)?.ToString() ?? "",
                        Location = location,
                        Source = StartupSource.Registry,
                        Hive = hive,
                        View = view,
                        RunKey = RunPath,
                        ApprovedKey = ApprovedBase + approvedName,
                    };
                    item.Enabled = IsApproved(item);
                    items.Add(item);
                }
            }
            catch (Exception ex)
            {
                Logger.Write($"Could not read {hive}\\{RunPath}: {ex.Message}");
            }
        }

        FromRegistry(RegistryHive.CurrentUser, RegistryView.Registry64, "Run", "Registry (current user)");
        FromRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, "Run", "Registry (all users)");
        FromRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, "Run32", "Registry (all users, 32-bit)");

        void FromFolder(string folder, RegistryHive hive, string location)
        {
            if (!Directory.Exists(folder) || SafePath.HasReparsePoint(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SafePath.NoLinks(recursive: false)))
            {
                if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var item = new StartupItem
                {
                    Name = Path.GetFileName(file),
                    Command = file,
                    Location = location,
                    Source = StartupSource.Folder,
                    Hive = hive,
                    ApprovedKey = ApprovedBase + "StartupFolder",
                    FilePath = file,
                };
                item.Enabled = IsApproved(item);
                items.Add(item);
            }
        }

        // The signed-in user's folder may not be found (e.g. their profile can't be resolved): list the rest anyway.
        try { FromFolder(InteractiveUser.Startup, RegistryHive.CurrentUser, "Startup folder (current user)"); }
        catch (Exception ex) { Logger.Write($"Could not read the current user's Startup folder: {ex.Message}"); }
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "Startup folder (all users)");

        items.AddRange(await GetScheduledTasksAsync());
        // Signature checks can take a moment: off the UI thread.
        await Task.Run(() => { foreach (var i in items) i.ProtectedReason = ProtectionReason(i); });
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed class PsTask
    {
        public string? TaskName { get; set; }
        public string? TaskPath { get; set; }
        public string? State { get; set; }
        public string? Command { get; set; }
        public string? UserId { get; set; }
    }

    /// <summary>A task that runs as another specific person (not a group, service or this user).</summary>
    private static bool BelongsToAnotherUser(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        try
        {
            var sid = userId.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new System.Security.Principal.SecurityIdentifier(userId)
                : (System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount(userId).Translate(typeof(System.Security.Principal.SecurityIdentifier));
            var person = sid.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal);
            return person && sid != InteractiveUser.Sid;
        }
        catch { return false; }
    }

    /// <summary>SYSTEM, LOCAL SERVICE or NETWORK SERVICE.</summary>
    internal static bool IsServiceAccount(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var u = userId.Trim();
        return u is "S-1-5-18" or "S-1-5-19" or "S-1-5-20"
               || u.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) || u.EndsWith("\\SYSTEM", StringComparison.OrdinalIgnoreCase)
               || u.Equals("LOCAL SERVICE", StringComparison.OrdinalIgnoreCase) || u.EndsWith("\\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase)
               || u.Equals("NETWORK SERVICE", StringComparison.OrdinalIgnoreCase) || u.EndsWith("\\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Third-party scheduled tasks that run at logon or boot (a common hiding place for updaters).</summary>
    private static async Task<List<StartupItem>> GetScheduledTasksAsync()
    {
        const string script = """
            $tasks = Get-ScheduledTask | Where-Object {
                $_.TaskPath -notlike '\Microsoft\*' -and ($_.Triggers | Where-Object { $_.CimClass.CimClassName -in 'MSFT_TaskLogonTrigger','MSFT_TaskBootTrigger' })
            } | ForEach-Object {
                [pscustomobject]@{ TaskName = $_.TaskName; TaskPath = $_.TaskPath; State = "$($_.State)"; UserId = "$($_.Principal.UserId)";
                    Command = (($_.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)".Trim() }) -join ' ; ') }
            }
            ConvertTo-Json -InputObject @($tasks) -Compress
            """;
        try
        {
            var r = await ProcessRunner.PowerShellAsync(script);
            var start = r.Output.IndexOf('[');
            if (start < 0) return [];
            var tasks = JsonSerializer.Deserialize<List<PsTask>>(r.Output[start..], new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            // Tasks that run as another user of this PC are theirs: not listed, never changed.
            return tasks.Where(t => !string.IsNullOrEmpty(t.TaskName) && !BelongsToAnotherUser(t.UserId)).Select(t => new StartupItem
            {
                Name = t.TaskName!,
                Command = t.Command ?? "",
                Location = "Scheduled task " + t.TaskPath,
                Source = StartupSource.ScheduledTask,
                TaskPath = t.TaskPath ?? "\\",
                RunsAsSystem = IsServiceAccount(t.UserId),
                Enabled = !string.Equals(t.State, "Disabled", StringComparison.OrdinalIgnoreCase),
            }).ToList();
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not read scheduled tasks: {ex.Message}");
            return [];
        }
    }

    // ═══════════════════ Protected entries ═══════════════════

    public const string DefenderReason = "Windows Security (Microsoft Defender) needs it to show its icon and warnings.";
    public const string WindowsReason = "Part of Windows.";

    /// <summary>Windows programs that run whatever you give them: entries using them are judged by what they run, not protected.</summary>
    private static readonly HashSet<string> CommandHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "rundll32.exe", "regsvr32.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "conhost.exe",
        "schtasks.exe", "reg.exe", "msiexec.exe", "explorer.exe", "forfiles.exe", "wmic.exe", "certutil.exe", "bitsadmin.exe", "curl.exe",
        "start.exe", "msbuild.exe", "installutil.exe", "regasm.exe", "hh.exe", "cmstp.exe", "wsl.exe", "bash.exe",
    };

    /// <summary>
    /// Why an entry is protected, from its name and command alone: Windows Security's tray icon
    /// and Defender, or a program in the Windows folder (not a script host running something else).
    /// </summary>
    internal static string? ProtectionReasonFor(string name, string command, string windowsDir, string programFiles, string programData)
    {
        var expanded = Environment.ExpandEnvironmentVariables(command);
        if (name.Equals("SecurityHealth", StringComparison.OrdinalIgnoreCase) || name.Equals("Windows Defender", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WindowsDefender", StringComparison.OrdinalIgnoreCase)
            || expanded.Contains("SecurityHealthSystray", StringComparison.OrdinalIgnoreCase) || expanded.Contains("MSASCuiL", StringComparison.OrdinalIgnoreCase)
            || expanded.Contains(Path.Combine(programFiles, "Windows Defender"), StringComparison.OrdinalIgnoreCase)
            || expanded.Contains(Path.Combine(programData, "Microsoft", "Windows Defender"), StringComparison.OrdinalIgnoreCase))
            return DefenderReason;

        // Driver tray apps (Realtek, Waves, Intel...) live in the DriverStore: they are the vendor's, not Windows'.
        if (Executable(expanded) is { } exe && Path.IsPathFullyQualified(exe) && SafePath.IsSameOrInside(exe, windowsDir)
            && !SafePath.IsSameOrInside(exe, Path.Combine(windowsDir, "System32", "DriverStore"))
            && !CommandHosts.Contains(Path.GetFileName(exe)))
            return WindowsReason;
        return null;
    }

    /// <summary>The program a command line starts (the first action for a task).</summary>
    internal static string? Executable(string command)
    {
        var first = command.Split(" ; ")[0].Trim();
        if (first.Length == 0) return null;
        if (first[0] == '"')
        {
            var end = first.IndexOf('"', 1);
            return end > 1 ? first[1..end] : null;
        }
        var exe = first.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? first[..(exe + 4)] : first.Split(' ')[0];
    }

    private static readonly string[] WindowsSigners = ["Microsoft Windows", "Microsoft Windows Publisher"];

    private static string? ProtectionReason(StartupItem item)
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var reason = ProtectionReasonFor(item.Name, item.Command, win,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        if (reason is not null || item.Source == StartupSource.Folder) return reason;

        // Signed as part of Windows (not just by Microsoft: OneDrive or Teams can still be turned off).
        try
        {
            if (Executable(Environment.ExpandEnvironmentVariables(item.Command)) is { } exe && Path.IsPathFullyQualified(exe) && File.Exists(exe)
                && !CommandHosts.Contains(Path.GetFileName(exe)) && Authenticode.IsSignedBy(exe, WindowsSigners, out _))
                return WindowsReason;
        }
        catch { }
        return null;
    }

    private static void ThrowIfProtected(StartupItem item)
    {
        if (item.IsProtected) throw new InvalidOperationException(item.ProtectedReason);
    }

    // ═══════════════════ Enable / disable ═══════════════════

    private static bool IsApproved(StartupItem item)
    {
        // First byte even (02/06) = enabled, odd (03/07) = disabled. No value = enabled.
        return Reg.Get(item.Hive, item.ApprovedKey, item.Name) is not byte[] { Length: > 0 } data || data[0] % 2 == 0;
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    public static async Task SetEnabledAsync(StartupItem item, bool enabled)
    {
        // Windows components can be turned off (like in Task Manager); Windows Security can't.
        // Turning anything back on is always allowed (e.g. Security's icon after malware disabled it).
        if (!enabled && item.BlocksDisable) throw new InvalidOperationException(item.ProtectedReason);
        if (item.Source == StartupSource.ScheduledTask)
        {
            var verb = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
            var r = await ProcessRunner.PowerShellAsync($"{verb} -TaskName {Quote(item.Name)} -TaskPath {Quote(item.TaskPath)} -ErrorAction Stop | Out-Null; 'OK'");
            if (!r.Output.Contains("OK")) throw new InvalidOperationException(r.Output.Trim());
        }
        else
        {
            var data = new byte[12];
            data[0] = enabled ? (byte)0x02 : (byte)0x03;
            // Bytes 4..11 hold the time it was disabled (FILETIME), as Windows does.
            if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            Reg.Set(item.Hive, item.ApprovedKey, item.Name, data, RegistryValueKind.Binary);
        }
        item.Enabled = enabled;
    }

    // ═══════════════════ Remove, with a backup ═══════════════════

    /// <summary>Backups of removed entries, in WinSolve's administrators-only data folder.</summary>
    public static string BackupFolder
    {
        get
        {
            var dir = Path.Combine(SafePath.DataFolder, "StartupBackup");
            if (Directory.Exists(dir) && SafePath.HasReparsePoint(dir)) throw new InvalidOperationException($"{dir} is a link.");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static readonly JsonSerializerOptions BackupJson = new() { WriteIndented = true };

    /// <summary>
    /// Deletes the startup entry (the program itself is not uninstalled). A backup is saved
    /// first; if it can't be, nothing is removed.
    /// </summary>
    public static async Task RemoveAsync(StartupItem item)
    {
        ThrowIfProtected(item);
        var backup = await SaveBackupAsync(item);
        try
        {
            switch (item.Source)
            {
                case StartupSource.Registry:
                    using (var root = InteractiveUser.OpenBase(item.Hive, item.View))
                    using (var key = root.OpenSubKey(item.RunKey, writable: true))
                        key?.DeleteValue(item.Name, throwOnMissingValue: false);
                    Reg.Delete(item.Hive, item.ApprovedKey, item.Name);
                    break;

                case StartupSource.Folder:
                    if (SafePath.HasReparsePoint(Path.GetDirectoryName(item.FilePath)!))
                        throw new InvalidOperationException("The Startup folder is a link; refusing to delete through it.");
                    // Through a handle on the entry itself: the folder can't be swapped for a link in between.
                    SafeDelete.DeleteFile(item.FilePath);
                    Reg.Delete(item.Hive, item.ApprovedKey, item.Name);
                    break;

                case StartupSource.ScheduledTask:
                    var r = await ProcessRunner.PowerShellAsync(
                        $"Unregister-ScheduledTask -TaskName {Quote(item.Name)} -TaskPath {Quote(item.TaskPath)} -Confirm:$false -ErrorAction Stop; 'OK'");
                    if (!r.Output.Contains("OK")) throw new InvalidOperationException(r.Output.Trim());
                    break;
            }
        }
        catch
        {
            // Not removed after all: the backup would only confuse. Kept if the entry is gone or we can't tell.
            if (StillPresent(item)) DeleteBackup(backup);
            throw;
        }
        Logger.Write($"Startup entry removed: {item.Name} ({item.Location}); backup {Path.GetFileName(backup.BackupFile)}");
    }

    private static bool StillPresent(StartupItem item)
    {
        try
        {
            switch (item.Source)
            {
                case StartupSource.Registry:
                    using (var root = InteractiveUser.OpenBase(item.Hive, item.View))
                    using (var key = root.OpenSubKey(item.RunKey))
                        return key?.GetValue(item.Name) is not null;
                case StartupSource.Folder:
                    return File.Exists(item.FilePath);
                default:
                    return false; // a task: can't tell cheaply, keep the backup
            }
        }
        catch { return false; }
    }

    private static async Task<StartupBackup> SaveBackupAsync(StartupItem item)
    {
        var folder = BackupFolder;
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        var b = new StartupBackup
        {
            Name = item.Name, Location = item.Location, Command = item.Command, Source = item.Source, Hive = item.Hive, View = item.View,
            RunKey = item.RunKey, ApprovedKey = item.ApprovedKey, FilePath = item.FilePath, TaskPath = item.TaskPath,
            UserSid = item.Hive == RegistryHive.CurrentUser ? InteractiveUser.Sid.Value : "",
            RemovedAt = DateTime.Now,
            BackupFile = Path.Combine(folder, id + ".json"),
        };
        if (item.ApprovedKey.Length > 0 && Reg.Get(item.Hive, item.ApprovedKey, item.Name) is byte[] approved) b.ApprovedData = approved;

        switch (item.Source)
        {
            case StartupSource.Registry:
                using (var root = InteractiveUser.OpenBase(item.Hive, item.View))
                using (var key = root.OpenSubKey(item.RunKey))
                {
                    if (key?.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string data)
                        throw new InvalidOperationException("Could not read the entry to back it up, so it was not removed.");
                    b.ValueData = data;
                    b.ValueKind = key.GetValueKind(item.Name);
                }
                break;

            case StartupSource.Folder:
                var bytes = ReadStartupFile(item.FilePath)
                            ?? throw new InvalidOperationException("Could not back up the file (it is a link, too large or unreadable), so it was not removed.");
                b.FileCopy = id + ".bin";
                await File.WriteAllBytesAsync(Path.Combine(folder, b.FileCopy), bytes);
                break;

            case StartupSource.ScheduledTask:
                b.TaskXmlFile = id + ".xml";
                var xmlPath = Path.Combine(folder, b.TaskXmlFile);
                var r = await ProcessRunner.PowerShellAsync(
                    $"Export-ScheduledTask -TaskName {Quote(item.Name)} -TaskPath {Quote(item.TaskPath)} -ErrorAction Stop | Out-File -LiteralPath {Quote(xmlPath)} -Encoding Unicode -ErrorAction Stop; 'OK'");
                if (!r.Output.Contains("OK") || !File.Exists(xmlPath))
                    throw new InvalidOperationException("Could not back up the task, so it was not removed.\n" + r.Output.Trim());
                break;
        }
        await File.WriteAllTextAsync(b.BackupFile, JsonSerializer.Serialize(b, BackupJson));
        return b;
    }

    private const int MaxFileBackup = 1024 * 1024;

    /// <summary>
    /// Reads a Startup folder file for the backup: never through a link (the user can change
    /// that folder) and never a hard link to some other file, so the backup can't capture a file
    /// the user couldn't read.
    /// </summary>
    private static byte[]? ReadStartupFile(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || SafePath.HasReparsePoint(Path.GetDirectoryName(path)!)) return null;
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!GetFileInformationByHandle(h, out var info) || info.NumberOfLinks != 1) return null;
            if (FinalPath(h) is not { } final || !final.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return null;
            var length = RandomAccess.GetLength(h);
            if (length > MaxFileBackup) return null;
            var buffer = new byte[length];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = RandomAccess.Read(h, buffer.AsSpan(read), read);
                if (n == 0) break;
                read += n;
            }
            return read == buffer.Length ? buffer : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FinalPath(SafeFileHandle h)
    {
        var sb = new System.Text.StringBuilder(1024);
        var n = GetFinalPathNameByHandle(h, sb, sb.Capacity, 0);
        if (n == 0 || n >= sb.Capacity) return null;
        var s = sb.ToString();
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + s[8..];
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    /// <summary>Saved backups, newest first.</summary>
    public static List<StartupBackup> ListBackups()
    {
        var list = new List<StartupBackup>();
        string folder;
        try { folder = BackupFolder; }
        catch (Exception ex) { Logger.Write($"Startup backups unavailable: {ex.Message}"); return list; }
        foreach (var file in Directory.EnumerateFiles(folder, "*.json", SafePath.NoLinks(recursive: false)))
        {
            try
            {
                var b = JsonSerializer.Deserialize<StartupBackup>(File.ReadAllText(file));
                if (b is null || b.Name.Length == 0) continue;
                b.BackupFile = file;
                list.Add(b);
            }
            catch { /* damaged backup: skip */ }
        }
        return list.OrderByDescending(b => b.RemovedAt).ToList();
    }

    /// <summary>Puts a removed entry back where it was, then deletes its backup.</summary>
    public static async Task RestoreAsync(StartupBackup b)
    {
        if (b.Hive == RegistryHive.CurrentUser && b.UserSid.Length > 0 && b.UserSid != InteractiveUser.Sid.Value)
            throw new InvalidOperationException("This entry was removed from another account. Restore it while that person is signed in.");
        var folder = BackupFolder;

        switch (b.Source)
        {
            case StartupSource.Registry:
                if (b.ValueData is null || b.RunKey.Length == 0) throw new InvalidOperationException("The backup is incomplete.");
                using (var root = InteractiveUser.OpenBase(b.Hive, b.View))
                using (var key = root.CreateSubKey(b.RunKey, writable: true))
                {
                    if (key.GetValue(b.Name) is not null) throw new InvalidOperationException("An entry with this name already exists.");
                    key.SetValue(b.Name, b.ValueData, b.ValueKind == RegistryValueKind.ExpandString ? RegistryValueKind.ExpandString : RegistryValueKind.String);
                }
                break;

            case StartupSource.Folder:
                if (b.FileCopy is null) throw new InvalidOperationException("The backup is incomplete.");
                var bytes = await File.ReadAllBytesAsync(Path.Combine(folder, Path.GetFileName(b.FileCopy)));
                RestoreFile(b, bytes);
                break;

            case StartupSource.ScheduledTask:
                if (b.TaskXmlFile is null) throw new InvalidOperationException("The backup is incomplete.");
                var xml = Path.Combine(folder, Path.GetFileName(b.TaskXmlFile));
                var r = await ProcessRunner.PowerShellAsync(
                    $"Register-ScheduledTask -Xml (Get-Content -LiteralPath {Quote(xml)} -Raw) -TaskName {Quote(b.Name)} -TaskPath {Quote(b.TaskPath)} -ErrorAction Stop | Out-Null; 'OK'");
                if (!r.Output.Contains("OK")) throw new InvalidOperationException(r.Output.Trim());
                break;
        }
        if (b.ApprovedData is { Length: > 0 } approved && b.ApprovedKey.Length > 0)
        {
            try { Reg.Set(b.Hive, b.ApprovedKey, b.Name, approved, RegistryValueKind.Binary); }
            catch (Exception ex) { Logger.Write($"Could not restore the enabled state of {b.Name}: {ex.Message}"); }
        }
        DeleteBackup(b);
        Logger.Write($"Startup entry restored: {b.Name} ({b.Location})");
    }

    private const int MaxFileRestoreAsUser = 6 * 1024;

    private static void RestoreFile(StartupBackup b, byte[] bytes)
    {
        var expected = b.Hive == RegistryHive.CurrentUser ? InteractiveUser.Startup : Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        var dir = Path.GetDirectoryName(b.FilePath) ?? "";
        if (!dir.TrimEnd('\\').Equals(expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || Path.GetFileName(b.FilePath).Length == 0)
            throw new InvalidOperationException("The Startup folder has moved; the entry can't be restored automatically.");
        if (!Directory.Exists(dir) || SafePath.HasReparsePoint(dir)) throw new InvalidOperationException("The Startup folder is a link; refusing to write through it.");
        if (File.Exists(b.FilePath)) throw new InvalidOperationException("An entry with this name already exists.");

        if (b.Hive == RegistryHive.CurrentUser && InteractiveUser.IsDifferent)
        {
            // Their folder, their rights: written as the signed-in user, so a link they plant can't
            // make an administrator write somewhere else.
            if (bytes.Length > MaxFileRestoreAsUser) throw new InvalidOperationException("This file is too large to restore automatically.");
            var script = $"$p = {Quote(b.FilePath)}; if (-not (Test-Path -LiteralPath $p)) {{ [IO.File]::WriteAllBytes($p, [Convert]::FromBase64String('{Convert.ToBase64String(bytes)}')) }}";
            InteractiveUser.RunPowerShellAsUser(script, 30_000);
            if (!File.Exists(b.FilePath)) throw new InvalidOperationException("Could not write the file back.");
            return;
        }
        using var fs = new FileStream(b.FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        fs.Write(bytes);
    }

    private static void DeleteBackup(StartupBackup b)
    {
        try
        {
            var folder = Path.GetDirectoryName(b.BackupFile);
            if (folder is null) return;
            foreach (var extra in new[] { b.FileCopy, b.TaskXmlFile })
                if (extra is not null) File.Delete(Path.Combine(folder, Path.GetFileName(extra)));
            File.Delete(b.BackupFile);
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not delete the startup backup {b.BackupFile}: {ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetFinalPathNameByHandle(SafeFileHandle file, System.Text.StringBuilder path, int length, int flags);
}
