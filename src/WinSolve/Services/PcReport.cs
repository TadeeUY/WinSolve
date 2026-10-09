using System.Net;
using System.Text;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>
/// Speccy-style hardware and Windows summary as a printable HTML page (print it to PDF from the
/// browser). Serial numbers and product keys are left out so the report can be shared.
/// </summary>
public static class PcReport
{
    public static async Task<string> CreateAsync(Action<string> log, CancellationToken ct)
    {
        log("Reading hardware information...");
        var disks = await DiskHealthService.ScanAsync();
        var html = await Task.Run(() => Build(disks), ct);
        var folder = InteractiveUser.Desktop;
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"PC report - {Environment.MachineName} - {DateTime.Now:yyyy-MM-dd}.html");
        await File.WriteAllTextAsync(file, html, Encoding.UTF8, ct);
        log($"Saved to {file}");
        return file;
    }

    private static string Build(List<DiskInfo> disks)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        void Section(string title, IEnumerable<(string Key, string Value)> rows)
        {
            sb.Append($"<section><h2>{E(title)}</h2><table>");
            foreach (var (k, v) in rows) sb.Append($"<tr><th>{E(k)}</th><td>{E(v)}</td></tr>");
            sb.Append("</table></section>");
        }

        var os = Wmi.Query("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem").FirstOrDefault();
        var cs = Wmi.Query("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        var board = Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Wmi.Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        static string Date(string dmtf)
        {
            try { return System.Management.ManagementDateTimeConverter.ToDateTime(dmtf).ToString("d"); } catch { return "—"; }
        }

        Section("Windows",
        [
            ("Edition", os?.Str("Caption") ?? "—"),
            ("Version", $"{os?.Str("Version")} (build {os?.Str("BuildNumber")}, {os?.Str("OSArchitecture")})"),
            ("Installed", os is null ? "—" : Date(os.Str("InstallDate"))),
            ("Activation", ActivationService.GetStatus().IsActivated ? "Activated" : "Not activated"),
            ("Computer name", Environment.MachineName),
        ]);
        Section("System",
        [
            ("Manufacturer / model", $"{cs?.Str("Manufacturer")} {cs?.Str("Model")}"),
            ("Motherboard", $"{board?.Str("Manufacturer")} {board?.Str("Product")}"),
            ("BIOS / UEFI", $"{bios?.Str("Manufacturer")} {bios?.Str("SMBIOSBIOSVersion")} ({(bios is null ? "—" : Date(bios.Str("ReleaseDate")))})"),
        ]);

        foreach (var cpu in Wmi.Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
            Section("Processor",
            [
                ("Model", cpu.Str("Name")),
                ("Cores / threads", $"{cpu.Get<uint>("NumberOfCores")} / {cpu.Get<uint>("NumberOfLogicalProcessors")}"),
                ("Base clock", $"{cpu.Get<uint>("MaxClockSpeed") / 1000.0:0.00} GHz"),
            ]);

        var modules = Wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator FROM Win32_PhysicalMemory");
        var memRows = new List<(string, string)> { ("Total", Format.Bytes(cs?.Get<ulong>("TotalPhysicalMemory") is { } t ? (long)t : 0)) };
        foreach (var m in modules)
        {
            var speed = m.Get<uint>("ConfiguredClockSpeed") is > 0 and var c ? c : m.Get<uint>("Speed");
            memRows.Add((m.Str("DeviceLocator"), $"{Format.Bytes((long)m.Get<ulong>("Capacity"))}  ·  {speed} MT/s  ·  {m.Str("Manufacturer")} {m.Str("PartNumber")}".Trim()));
        }
        Section("Memory", memRows);

        foreach (var gpu in DriverService.GetGpus())
            Section("Graphics", [("Model", gpu.Name), ("Driver", $"{gpu.FriendlyVersion} ({gpu.DriverDate:d})")]);

        foreach (var d in disks)
            Section("Drive",
            [
                ("Model", d.Model),
                ("Type", $"{d.MediaType} / {d.BusType}  ·  {Format.Bytes(d.SizeBytes)}"),
                ("Health", d.HealthText + (d.WearPercent is { } w ? $" ({100 - w}% life left)" : "")),
                ("Temperature", d.TemperatureC is { } tc ? $"{tc} °C" : "—"),
                ("Power-on hours", d.PowerOnHours is { } h ? $"{h:N0}" : "—"),
            ]);

        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed))
        {
            try { Section("Volume", [(drive.Name, $"{Format.Bytes(drive.AvailableFreeSpace)} free of {Format.Bytes(drive.TotalSize)} ({drive.DriveFormat})")]); }
            catch { }
        }

        foreach (var a in NetworkTest.Adapters())
            Section("Network", [("Adapter", a.Name), ("Type / speed", $"{a.Type}  ·  {a.Speed}")]);

        foreach (var b in Wmi.Query("SELECT Name, EstimatedChargeRemaining FROM Win32_Battery"))
            Section("Battery", [("Model", b.Str("Name")), ("Charge", $"{b.Get<ushort>("EstimatedChargeRemaining")} %")]);

        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>PC report - {{E(Environment.MachineName)}}</title>
            <style>
              body { font-family: "Segoe UI", system-ui, sans-serif; margin: 32px auto; max-width: 860px; color: #1b1b1b; }
              header { display: flex; justify-content: space-between; align-items: baseline; border-bottom: 3px solid #0067c0; padding-bottom: 8px; }
              h1 { margin: 0; font-size: 26px; } header span { color: #666; }
              section { margin-top: 18px; break-inside: avoid; }
              h2 { font-size: 15px; text-transform: uppercase; letter-spacing: .06em; color: #0067c0; margin: 0 0 6px; }
              table { width: 100%; border-collapse: collapse; }
              th { text-align: left; width: 34%; font-weight: 600; color: #444; }
              th, td { padding: 6px 10px; border-bottom: 1px solid #e5e5e5; vertical-align: top; }
              footer { margin-top: 28px; color: #888; font-size: 12px; }
            </style></head><body>
            <header><h1>{{E(Environment.MachineName)}}</h1><span>{{DateTime.Now:f}}</span></header>
            {{sb}}
            <footer>Created with WinSolve. Serial numbers and product keys are not included. Use the browser's Print &gt; Save as PDF to keep a PDF.</footer>
            </body></html>
            """;
    }
}
