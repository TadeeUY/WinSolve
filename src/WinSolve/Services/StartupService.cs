using System.Text.Json;
using Microsoft.Win32;
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

    public bool Enabled { get; set; }
}

/// <summary>
/// Programs that start with Windows: Run keys, Startup folders and logon scheduled tasks.
/// Disabling uses the same StartupApproved keys as Task Manager (fully reversible);
/// removing deletes the entry for good.
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
                using var root = RegistryKey.OpenBaseKey(hive, view);
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
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder))
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

        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "Startup folder (current user)");
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "Startup folder (all users)");

        items.AddRange(await GetScheduledTasksAsync());
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed class PsTask
    {
        public string? TaskName { get; set; }
        public string? TaskPath { get; set; }
        public string? State { get; set; }
        public string? Command { get; set; }
    }

    /// <summary>Third-party scheduled tasks that run at logon or boot (a common hiding place for updaters).</summary>
    private static async Task<List<StartupItem>> GetScheduledTasksAsync()
    {
        const string script = """
            $tasks = Get-ScheduledTask | Where-Object {
                $_.TaskPath -notlike '\Microsoft\*' -and ($_.Triggers | Where-Object { $_.CimClass.CimClassName -in 'MSFT_TaskLogonTrigger','MSFT_TaskBootTrigger' })
            } | ForEach-Object {
                [pscustomobject]@{ TaskName = $_.TaskName; TaskPath = $_.TaskPath; State = "$($_.State)";
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
            return tasks.Where(t => !string.IsNullOrEmpty(t.TaskName)).Select(t => new StartupItem
            {
                Name = t.TaskName!,
                Command = t.Command ?? "",
                Location = "Scheduled task " + t.TaskPath,
                Source = StartupSource.ScheduledTask,
                TaskPath = t.TaskPath ?? "\\",
                Enabled = !string.Equals(t.State, "Disabled", StringComparison.OrdinalIgnoreCase),
            }).ToList();
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not read scheduled tasks: {ex.Message}");
            return [];
        }
    }

    private static bool IsApproved(StartupItem item)
    {
        // First byte even (02/06) = enabled, odd (03/07) = disabled. No value = enabled.
        return Reg.Get(item.Hive, item.ApprovedKey, item.Name) is not byte[] { Length: > 0 } data || data[0] % 2 == 0;
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    public static async Task SetEnabledAsync(StartupItem item, bool enabled)
    {
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

    /// <summary>Deletes the startup entry permanently (the program itself is not uninstalled).</summary>
    public static async Task RemoveAsync(StartupItem item)
    {
        switch (item.Source)
        {
            case StartupSource.Registry:
                using (var root = RegistryKey.OpenBaseKey(item.Hive, item.View))
                using (var key = root.OpenSubKey(item.RunKey, writable: true))
                    key?.DeleteValue(item.Name, throwOnMissingValue: false);
                Reg.Delete(item.Hive, item.ApprovedKey, item.Name);
                break;

            case StartupSource.Folder:
                if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
                Reg.Delete(item.Hive, item.ApprovedKey, item.Name);
                break;

            case StartupSource.ScheduledTask:
                var r = await ProcessRunner.PowerShellAsync(
                    $"Unregister-ScheduledTask -TaskName {Quote(item.Name)} -TaskPath {Quote(item.TaskPath)} -Confirm:$false -ErrorAction Stop; 'OK'");
                if (!r.Output.Contains("OK")) throw new InvalidOperationException(r.Output.Trim());
                break;
        }
        Logger.Write($"Startup entry removed: {item.Name} ({item.Location})");
    }
}
