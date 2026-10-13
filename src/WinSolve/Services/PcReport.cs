using System.Net;
using System.Text;
using WinSolve.Core;
using WinSolve.Localization;

namespace WinSolve.Services;

/// <summary>
/// Speccy-style hardware and Windows summary as a printable HTML page (print it to PDF from the
/// browser). Serial numbers and product keys are left out so the report can be shared.
/// </summary>
public static class PcReport
{
    /// <summary>
    /// Saves the report on the user's desktop as a PDF (printed by Microsoft Edge). Falls back to
    /// the HTML page when Edge isn't available or can't print it.
    /// </summary>
    public static async Task<string> CreateAsync(Action<string> log, CancellationToken ct)
    {
        log("Reading hardware information...");
        var disks = await DiskHealthService.ScanAsync();
        var systemDisk = await SystemDiskAsync(disks, ct);
        var folder = InteractiveUser.Desktop;
        Directory.CreateDirectory(folder);
        if (SafePath.HasReparsePoint(folder)) throw new InvalidOperationException("The desktop folder is a link; refusing to write there.");
        var name = $"{Loc.T("PC report")} - {Environment.MachineName} - {DateTime.Now:yyyy-MM-dd}";

        if (EdgePath() is { } edge)
        {
            var html = await Task.Run(() => Build(disks, systemDisk, pdf: true), ct);
            var work = SafePath.CreateAdminOnlyFolder("Report");
            var source = Path.Combine(work, "report.html");
            var profile = Path.Combine(work, "edge-profile");
            var pdf = Path.Combine(folder, name + ".pdf");
            // Today's report may already be open in a PDF reader: use a new name then.
            for (int n = 2; File.Exists(pdf) && !TryDelete(pdf); n++) pdf = Path.Combine(folder, $"{name} ({n}).pdf");
            try
            {
                await File.WriteAllTextAsync(source, html, Encoding.UTF8, ct);
                log("Creating the PDF...");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                await ProcessRunner.RunAsync(edge,
                    $"--headless --disable-gpu --no-first-run --disable-extensions --user-data-dir=\"{profile}\" " +
                    $"--no-pdf-header-footer --print-to-pdf-no-header --print-to-pdf=\"{pdf}\" \"{new Uri(source).AbsoluteUri}\"",
                    null, timeout.Token);
                if (File.Exists(pdf) && new FileInfo(pdf).Length > 0)
                {
                    log($"Saved to {pdf}");
                    return pdf;
                }
                log("Edge could not print the report; saving it as a web page instead.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log("Edge took too long; saving the report as a web page instead.");
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        var file = Path.Combine(folder, name + ".html");
        await File.WriteAllTextAsync(file, await Task.Run(() => Build(disks, systemDisk, pdf: false), ct), Encoding.UTF8, ct);
        log($"Saved to {file}");
        return file;
    }

    /// <summary>The physical disk Windows is on (for the rating), or null if it can't be told.</summary>
    private static async Task<DiskInfo?> SystemDiskAsync(List<DiskInfo> disks, CancellationToken ct)
    {
        try
        {
            var letter = Path.GetPathRoot(Environment.SystemDirectory)![0];
            var r = await ProcessRunner.PowerShellAsync($"(Get-Partition -DriveLetter {letter} -ErrorAction Stop).DiskNumber", ct: ct);
            if (int.TryParse(r.Output.Trim().Split('\n').LastOrDefault()?.Trim(), out var number))
                return disks.FirstOrDefault(d => d.DiskNumber == number);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return disks.Count == 1 ? disks[0] : null;
    }

    private static bool TryDelete(string file)
    {
        try { File.Delete(file); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? EdgePath()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var path = Path.Combine(Environment.GetFolderPath(root), @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static PcSpecs ReadSpecs(System.Management.ManagementBaseObject? os, System.Management.ManagementBaseObject? cs, DiskInfo? systemDisk)
    {
        var cpus = Wmi.Query("SELECT Name, NumberOfLogicalProcessors FROM Win32_Processor");
        var cpuName = cpus.FirstOrDefault()?.Str("Name").Trim() ?? "";
        var threads = cpus.Sum(c => (int)c.Get<uint>("NumberOfLogicalProcessors"));
        if (threads <= 0) threads = Environment.ProcessorCount;
        var ramGb = (cs?.Get<ulong>("TotalPhysicalMemory") ?? 0) / 1024.0 / 1024 / 1024;
        double free = -1;
        try
        {
            var c = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            if (c.TotalSize > 0) free = c.AvailableFreeSpace * 100.0 / c.TotalSize;
        }
        catch { }
        int.TryParse(os?.Str("BuildNumber"), out var build);
        List<string> gpus;
        try { gpus = DriverService.GetGpus().Select(g => g.Name).ToList(); } catch { gpus = []; }
        var maker = $"{cs?.Str("Manufacturer")} {cs?.Str("Model")}";
        var virtualMachine = System.Text.RegularExpressions.Regex.IsMatch(maker, @"Virtual Machine|VMware|VirtualBox|innotek|QEMU|KVM|Parallels|Xen", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return new PcSpecs(cpuName, threads, ramGb, systemDisk?.MediaType ?? "", systemDisk?.BusType ?? "",
            systemDisk?.Health ?? HealthLevel.Unknown, free, gpus, build, virtualMachine);
    }

    /// <summary>"How good is this PC": score ring, one tile per part, what it's good for and what to upgrade.</summary>
    private static string RatingCard(PcSpecs specs, Func<string?, string> E)
    {
        var r = PcRating.Rate(specs);
        static string Color(int score) => score >= 70 ? "#0f7b0f" : score >= 45 ? "#c27c0e" : "#c42b1c";
        var sb = new StringBuilder();
        sb.Append($"""<div class="summary"><h2>{E(Loc.T("How good is this PC?"))}</h2><div class="ring"><svg viewBox="0 0 36 36"><circle cx="18" cy="18" r="15.9" fill="none" stroke="#e3e7ec" stroke-width="3"/><circle cx="18" cy="18" r="15.9" fill="none" stroke="{Color(r.Score)}" stroke-width="3" stroke-linecap="round" pathLength="100" stroke-dasharray="{r.Score} 100"/></svg><div class="value"><b>{r.Score}</b><span>{E(Loc.T("of 100"))}</span></div></div>""");
        sb.Append($"""<div class="verdict" style="color:{Color(r.Score)}">{E(Loc.T(r.Verdict))}</div>""");
        if (r.Weakest is { } weakest) sb.Append($"""<p class="limit">{E(string.Format(Loc.T("What holds it back the most: {0}."), Loc.T(weakest)))}</p>""");
        sb.Append("</div>");
        sb.Append("""<div class="grid">""");
        foreach (var p in r.Parts)
            sb.Append($"""<div class="card part"><div class="top"><span>{E(Loc.T(p.Name))}</span><b>{p.Score}</b></div><div class="bar"><i style="width:{p.Score}%;background:{Color(p.Score)}"></i></div><p>{E(Loc.T(p.Detail))}</p></div>""");
        sb.Append($"""</div><div class="grid"><div class="card"><h2>{E(Loc.T("Good for"))}</h2><ul>""");
        foreach (var g in r.GoodFor) sb.Append($"<li>{E(Loc.T(g))}</li>");
        sb.Append($"""</ul></div><div class="card"><h2>{E(Loc.T("Recommendations"))}</h2><ul>""");
        foreach (var t in r.Tips) sb.Append($"<li>{E(Loc.T(t))}</li>");
        sb.Append($"""</ul></div></div><p class="note">{E(Loc.T("The score compares this PC with today's computers: processor, memory, storage and graphics. It is a guide, not a benchmark."))}</p>""");
        return sb.ToString();
    }

    private static string Build(List<DiskInfo> disks, DiskInfo? systemDisk, bool pdf)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        void Section(string title, IEnumerable<(string Key, string Value)> rows)
        {
            sb.Append($"<section><h2>{E(Loc.T(title))}</h2><table>");
            foreach (var (k, v) in rows) sb.Append($"<tr><th>{E(Loc.T(k))}</th><td>{E(v)}</td></tr>");
            sb.Append("</table></section>");
        }
        // "Drive 1", "Drive 2"... when there are several of a kind (already translated: Section
        // looks the title up again and leaves it unchanged).
        static string Numbered(string title, int index, int count) => count > 1 ? $"{Loc.T(title)} {index + 1}" : title;

        var os = Wmi.Query("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem").FirstOrDefault();
        var cs = Wmi.Query("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        var board = Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Wmi.Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        static string Date(string dmtf)
        {
            try { return System.Management.ManagementDateTimeConverter.ToDateTime(dmtf).ToString("d", Loc.Culture); } catch { return "—"; }
        }

        var rating = RatingCard(ReadSpecs(os, cs, systemDisk), E);

        Section("Windows",
        [
            ("Edition", os?.Str("Caption") ?? "—"),
            ("Version", $"{os?.Str("Version")} (build {os?.Str("BuildNumber")}, {os?.Str("OSArchitecture")})"),
            ("Installed on", os is null ? "—" : Date(os.Str("InstallDate"))),
            ("Activation", Loc.T(ActivationService.GetStatus().IsActivated ? "Activated" : "Not activated")),
            ("Computer name", Environment.MachineName),
        ]);
        Section("System",
        [
            ("Manufacturer / model", $"{cs?.Str("Manufacturer")} {cs?.Str("Model")}"),
            ("Motherboard", $"{board?.Str("Manufacturer")} {board?.Str("Product")}"),
            ("BIOS / UEFI", $"{bios?.Str("Manufacturer")} {bios?.Str("SMBIOSBIOSVersion")} ({(bios is null ? "—" : Date(bios.Str("ReleaseDate")))})"),
        ]);

        var cpus = Wmi.Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
        foreach (var (cpu, i) in cpus.Select((c, i) => (c, i)))
            Section(Numbered("Processor", i, cpus.Count),
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
            // Virtual machines and some boards report no speed or "None" as the part number: leave those out.
            var parts = new List<string> { Format.Bytes((long)m.Get<ulong>("Capacity")) };
            if (speed > 0) parts.Add($"{speed} MT/s");
            var make = string.Join(" ", new[] { m.Str("Manufacturer"), m.Str("PartNumber") }
                .Select(x => x.Trim()).Where(x => x.Length > 0 && !x.Equals("None", StringComparison.OrdinalIgnoreCase) && !x.Equals("Unknown", StringComparison.OrdinalIgnoreCase)));
            if (make.Length > 0) parts.Add(make);
            memRows.Add((m.Str("DeviceLocator"), string.Join("  ·  ", parts)));
        }
        Section("Memory", memRows);

        var gpus = DriverService.GetGpus();
        foreach (var (gpu, i) in gpus.Select((g, i) => (g, i)))
            Section(Numbered("Graphics", i, gpus.Count), [("Model", gpu.Name), ("Driver", $"{gpu.FriendlyVersion} ({gpu.DriverDate?.ToString("d", Loc.Culture)})")]);

        foreach (var (d, i) in disks.Select((d, i) => (d, i)))
            Section(Numbered("Drive", i, disks.Count),
            [
                ("Model", d.Model),
                ("Type", $"{d.MediaType} / {d.BusType}  ·  {Format.Bytes(d.SizeBytes)}"),
                ("Health", Loc.T(d.HealthText) + (d.WearPercent is { } w ? " " + string.Format(Loc.T("({0}% life left)"), 100 - w) : "")),
                ("Temperature", d.TemperatureC is { } tc ? $"{tc} °C" : "—"),
                ("Power-on hours", d.PowerOnHours is { } h ? $"{h:N0}" : "—"),
            ]);

        // Every fixed volume in one table.
        var volumes = new List<(string, string)>();
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed))
        {
            try { volumes.Add((drive.Name, string.Format(Loc.T("{0} free of {1}"), Format.Bytes(drive.AvailableFreeSpace), Format.Bytes(drive.TotalSize)) + $" ({drive.DriveFormat})")); }
            catch { }
        }
        if (volumes.Count > 0) Section(volumes.Count > 1 ? "Volumes" : "Volume", volumes);

        var adapters = NetworkTest.Adapters().ToList();
        foreach (var (a, i) in adapters.Select((a, i) => (a, i)))
            Section(Numbered("Network", i, adapters.Count), [("Adapter", a.Name), ("Type / speed", $"{a.Type}  ·  {a.Speed}")]);

        foreach (var b in Wmi.Query("SELECT Name, EstimatedChargeRemaining FROM Win32_Battery"))
            Section("Battery", [("Model", b.Str("Name")), ("Charge", $"{b.Get<ushort>("EstimatedChargeRemaining")} %")]);

        return $$"""
            <!doctype html><html lang="{{(Loc.IsEnglish ? "en" : "es")}}"><head><meta charset="utf-8"><title>{{E(Loc.T("PC report"))}} - {{E(Environment.MachineName)}}</title>
            <style>
              :root { --accent: #0067c0; --text: #1b1b1b; --muted: #6b6b6b; --line: #e6e6e6; --soft: #f5f7fa; }
              * { box-sizing: border-box; }
              body { font-family: "Segoe UI", system-ui, sans-serif; color: var(--text); margin: 32px auto; max-width: 760px; font-size: 14px; line-height: 1.45; }
              header { text-align: center; padding-bottom: 18px; border-bottom: 2px solid var(--accent); }
              header .kicker { color: var(--accent); font-size: 12px; font-weight: 600; letter-spacing: .12em; text-transform: uppercase; }
              header h1 { margin: 4px 0 2px; font-size: 26px; }
              header .date { color: var(--muted); }
              .summary { text-align: center; padding: 22px 0 8px; } .summary h2 { margin-bottom: 12px; }
              .ring { width: 132px; height: 132px; margin: 0 auto; position: relative; }
              .ring svg { width: 100%; height: 100%; transform: rotate(-90deg); }
              .ring .value { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; }
              .ring .value b { font-size: 38px; line-height: 1; }
              .ring .value span { color: var(--muted); font-size: 12px; margin-top: 2px; }
              .verdict { font-size: 22px; font-weight: 700; margin: 12px 0 2px; }
              .limit { color: var(--muted); margin: 0; }
              .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; margin-top: 18px; }
              .card { background: var(--soft); border-radius: 10px; padding: 14px 16px; break-inside: avoid; }
              .part .top { display: flex; justify-content: space-between; align-items: baseline; font-weight: 600; }
              .part .top b { font-size: 18px; }
              .bar { height: 8px; background: #e3e7ec; border-radius: 4px; margin: 8px 0; overflow: hidden; }
              .bar i { display: block; height: 100%; border-radius: 4px; }
              .part p { margin: 0; color: var(--muted); font-size: 13px; white-space: pre-line; }
              h2 { font-size: 12px; font-weight: 700; letter-spacing: .1em; text-transform: uppercase; color: var(--accent); margin: 0 0 8px; }
              ul { margin: 0; padding-left: 18px; } li { margin: 4px 0; }
              .note { text-align: center; color: var(--muted); font-size: 12px; margin: 14px 0 0; }
              .specs { break-before: page; }
              .specs > .title { text-align: center; font-size: 20px; margin: 0 0 6px; padding-bottom: 14px; border-bottom: 2px solid var(--accent); }
              .specs section { margin-top: 18px; break-inside: avoid; }
              table { width: 100%; border-collapse: collapse; table-layout: fixed; }
              th, td { text-align: left; padding: 7px 0; border-bottom: 1px solid var(--line); vertical-align: top; overflow-wrap: anywhere; }
              th { width: 36%; color: var(--muted); font-weight: 600; padding-right: 16px; }
              footer { margin-top: 28px; text-align: center; color: var(--muted); font-size: 12px; }
              @page { margin: 14mm; }
              @media print { body { margin: 0 auto; } }
            </style></head><body>
            <header><div class="kicker">{{E(Loc.T("PC report"))}}</div><h1>{{E(Environment.MachineName)}}</h1><div class="date">{{E(DateTime.Now.ToString("f", Localization.Loc.Culture))}}</div></header>
            {{rating}}
            <div class="specs"><h1 class="title">{{E(Loc.T("Specifications"))}}</h1>
            {{sb}}
            </div>
            <footer>{{E(Loc.T("Created with WinSolve. Serial numbers and product keys are not included."))}}{{(pdf ? "" : " " + E(Loc.T("Use the browser's Print > Save as PDF to keep a PDF.")))}}</footer>
            </body></html>
            """;
    }
}
