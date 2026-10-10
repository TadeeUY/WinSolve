using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed record PendingUpdate(string Id, string Title, string Kind, long SizeBytes, bool Hidden);

public sealed record UpdateHistoryEntry(DateTime Date, string Title, string Operation, string Result, bool Failed);

public sealed record RollbackDevice(string InstanceId, string Name, string Class, string Provider, string Version, DateTime? Date);

/// <summary>
/// Windows Update controls Settings doesn't offer (or hides away): pause up to 5 weeks, hide a
/// problem update so it stops reinstalling, the full history and driver rollback.
/// </summary>
public static class WindowsUpdateService
{
    // Same values the Settings app writes when you press "Pause updates".
    private const string UxSettings = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private static readonly string[] PauseStart = ["PauseUpdatesStartTime", "PauseFeatureUpdatesStartTime", "PauseQualityUpdatesStartTime"];
    private static readonly string[] PauseEnd = ["PauseUpdatesExpiryTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesEndTime"];

    public const int MaxPauseDays = 35;

    /// <summary>When the current pause ends, or null when updates aren't paused.</summary>
    public static DateTime? PausedUntil()
    {
        var value = Reg.Get(RegistryHive.LocalMachine, UxSettings, "PauseUpdatesExpiryTime") as string;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until)
            && until > DateTime.UtcNow)
            return until.ToLocalTime();
        return null;
    }

    public static void Pause(int days)
    {
        days = Math.Clamp(days, 1, MaxPauseDays);
        var now = DateTime.UtcNow;
        string Iso(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        foreach (var name in PauseStart) Reg.Set(RegistryHive.LocalMachine, UxSettings, name, Iso(now), RegistryValueKind.String);
        foreach (var name in PauseEnd) Reg.Set(RegistryHive.LocalMachine, UxSettings, name, Iso(now.AddDays(days)), RegistryValueKind.String);
        Logger.Write($"Windows Update paused for {days} days.");
    }

    public static void Resume()
    {
        foreach (var name in PauseStart.Concat(PauseEnd)) Reg.Delete(RegistryHive.LocalMachine, UxSettings, name);
        Logger.Write("Windows Update resumed.");
    }

    // ───────────── Windows Update Agent (COM) ─────────────

    private static dynamic NewSearcher()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session")
                   ?? throw new InvalidOperationException("Windows Update is not available on this PC.");
        dynamic session = Activator.CreateInstance(type)!;
        session.ClientApplicationID = "WinSolve";
        return session.CreateUpdateSearcher();
    }

    /// <summary>Updates Windows Update offers and that aren't installed yet. Takes up to a few minutes.</summary>
    public static List<PendingUpdate> GetPending(bool hidden)
    {
        var searcher = NewSearcher();
        dynamic result = searcher.Search($"IsInstalled=0 and IsHidden={(hidden ? 1 : 0)}");
        var list = new List<PendingUpdate>();
        dynamic updates = result.Updates;
        for (int i = 0; i < (int)updates.Count; i++)
        {
            dynamic u = updates.Item(i);
            list.Add(new PendingUpdate(
                (string)u.Identity.UpdateID,
                (string)u.Title,
                (int)u.Type == 2 ? "Driver" : "Software",
                Convert.ToInt64(u.MaxDownloadSize),
                hidden));
        }
        return list;
    }

    /// <summary>Hides (or shows again) an update so Windows stops offering and installing it.</summary>
    public static void SetHidden(string updateId, bool hide)
    {
        if (!Guid.TryParse(updateId, out var id)) throw new ArgumentException("Invalid update ID.");
        var searcher = NewSearcher();
        dynamic result = searcher.Search($"UpdateID='{id}' and IsHidden={(hide ? 0 : 1)}");
        dynamic updates = result.Updates;
        if ((int)updates.Count == 0) throw new InvalidOperationException("Windows Update no longer offers this update.");
        updates.Item(0).IsHidden = hide;
        Logger.Write($"Windows Update: {(hide ? "hid" : "unhid")} {(string)updates.Item(0).Title}");
    }

    public static List<UpdateHistoryEntry> GetHistory(int max = 200)
    {
        var searcher = NewSearcher();
        int total = searcher.GetTotalHistoryCount();
        var list = new List<UpdateHistoryEntry>();
        if (total == 0) return list;
        dynamic history = searcher.QueryHistory(0, Math.Min(total, max));
        for (int i = 0; i < (int)history.Count; i++)
        {
            dynamic e = history.Item(i);
            string title = e.Title ?? "";
            if (string.IsNullOrWhiteSpace(title)) continue; // Defender engine entries etc. without a name
            int code = e.ResultCode;
            int operation = e.Operation;
            list.Add(new UpdateHistoryEntry(
                ((DateTime)e.Date).ToLocalTime(),
                title,
                operation == 2 ? "Uninstalled" : "Installed",
                code switch { 2 => "Succeeded", 3 => "Succeeded with errors", 4 => "Failed", 5 => "Canceled", 1 => "In progress", _ => "Not started" },
                code is 4 or 5));
        }
        return list;
    }

    // ───────────── Driver rollback ─────────────

    /// <summary>Devices with a non-Microsoft driver: the ones a bad driver update usually breaks.</summary>
    public static List<RollbackDevice> GetRollbackCandidates()
    {
        var list = new List<RollbackDevice>();
        foreach (var d in Wmi.Query("SELECT DeviceID, DeviceName, DeviceClass, DriverProviderName, DriverVersion, DriverDate FROM Win32_PnPSignedDriver"))
        {
            var name = d.Str("DeviceName");
            var provider = d.Str("DriverProviderName");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(d.Str("DeviceID"))) continue;
            if (provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
            DateTime? date = null;
            try { if (d.Str("DriverDate") is { Length: > 0 } s) date = System.Management.ManagementDateTimeConverter.ToDateTime(s); } catch { }
            list.Add(new RollbackDevice(d.Str("DeviceID"), name, d.Str("DeviceClass"), provider, d.Str("DriverVersion"), date));
        }
        return list.OrderBy(d => d.Class).ThenBy(d => d.Name).ToList();
    }

    /// <summary>
    /// Goes back to the driver the device used before its last update, like "Roll Back Driver" in
    /// Device Manager. Returns true when a restart is needed to finish.
    /// </summary>
    public static bool RollBack(string instanceId)
    {
        var set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref data)) throw new Win32Exception();
            if (!DiRollbackDriver(set, ref data, IntPtr.Zero, ROLLBACK_FLAG_NO_UI, out var reboot))
            {
                var error = Marshal.GetLastWin32Error();
                if (error is ERROR_NO_MORE_ITEMS or ERROR_FILE_NOT_FOUND or ERROR_NO_DRIVER_SELECTED or ERROR_NO_BACKUP)
                    throw new InvalidOperationException("Windows has no previous driver saved for this device, so there is nothing to go back to.");
                throw new Win32Exception(error);
            }
            Logger.Write($"Driver rolled back: {instanceId} (restart needed: {reboot})");
            return reboot;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private const uint ROLLBACK_FLAG_NO_UI = 0x1;
    private const int ERROR_FILE_NOT_FOUND = 2, ERROR_NO_MORE_ITEMS = 259;
    private const int ERROR_NO_DRIVER_SELECTED = unchecked((int)0xE0000203), ERROR_NO_BACKUP = unchecked((int)0xE0000103);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwnd);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiOpenDeviceInfo(IntPtr set, string instanceId, IntPtr hwnd, uint flags, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("newdev.dll", SetLastError = true)]
    private static extern bool DiRollbackDriver(IntPtr set, ref SP_DEVINFO_DATA data, IntPtr hwnd, uint flags, out bool needReboot);
}
