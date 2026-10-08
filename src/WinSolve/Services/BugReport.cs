using System.IO.Compression;
using System.Text;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>
/// Collects logs, settings and a hardware/software summary into a zip on the desktop that
/// users can attach to a GitHub issue. No product keys or drive serial numbers are included.
/// </summary>
public static class BugReport
{
    public static async Task<string> CreateAsync(Action<string> log, CancellationToken ct = default)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var zipPath = Path.Combine(desktop, $"WinSolve-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (SafePath.HasReparsePoint(desktop)) throw new InvalidOperationException("The desktop folder is a link; refusing to write there.");

        log("Collecting system information...");
        var info = await Task.Run(() => SystemReport(ct), ct);

        log("Creating the zip file...");
        await using var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        void AddText(string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write(text);
        }

        AddText("system.txt", info);
        if (File.Exists(AppSettings.FilePath)) AddText("settings.json", File.ReadAllText(AppSettings.FilePath));

        // Last 7 days of logs.
        if (Directory.Exists(Logger.LogDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(Logger.LogDirectory, "*.log", SafePath.NoLinks(recursive: false))
                         .Where(f => File.GetLastWriteTime(f) > DateTime.Now.AddDays(-7)))
            {
                using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(src);
                AddText("logs/" + Path.GetFileName(file), reader.ReadToEnd());
            }
        }

        log($"Report saved to {zipPath}");
        return zipPath;
    }

    private static string SystemReport(CancellationToken ct)
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.AppendLine(s);

        Line($"WinSolve {UpdateService.CurrentVersion}  ·  report created {DateTime.Now:u}");
        Line($"Elevated: {Admin.IsElevated}  ·  Executable: {Environment.ProcessPath}");
        Line();

        var s = SystemInfoService.Collect();
        Line("== System ==");
        Line($"OS: {s.OsName} {s.OsVersion}");
        Line($"CPU: {s.Cpu} ({s.Cores} cores / {s.Threads} threads)");
        Line($"RAM: {Format.Bytes(s.RamTotal)} ({Format.Bytes(s.RamFree)} free)");
        Line($"Motherboard: {s.Motherboard}  ·  BIOS {s.Bios}");
        foreach (var g in s.Gpus) Line($"GPU: {g}");
        foreach (var d in s.Drives) Line($"Drive {d.Name}: {Format.Bytes(d.Free)} free of {Format.Bytes(d.Total)}");
        Line($"Uptime: {Format.Duration(s.Uptime)}  ·  Restart pending: {SystemInfoService.IsRebootPending()}");
        Line($"Profile: {HardwareProfile.Detect().Describe()}");
        Line();

        ct.ThrowIfCancellationRequested();
        Line("== Graphics drivers ==");
        foreach (var g in DriverService.GetGpus())
            Line($"{g.Name}: {g.FriendlyVersion} (Windows {g.WindowsDriverVersion}, {g.DriverDate:d})");
        Line();

        Line("== Devices with problems ==");
        var devices = HardwareService.GetProblemDevices();
        if (devices.Count == 0) Line("None");
        foreach (var d in devices) Line($"{d.Name} [{d.Class}] code {d.ErrorCode}: {d.Explanation}");
        Line();

        ct.ThrowIfCancellationRequested();
        Line("== Critical events (30 days) ==");
        var events = HardwareService.GetCriticalEvents(30);
        if (events.Count == 0) Line("None");
        foreach (var e in events.Take(100)) Line($"{e.Time:g}  {e.Source} {e.EventId}: {e.Summary}");
        Line();

        Line("== Activation ==");
        var a = ActivationService.GetStatus();
        Line($"{a.Product}  ·  {a.Channel}  ·  {a.StatusText}"); // no keys
        return sb.ToString();
    }
}
