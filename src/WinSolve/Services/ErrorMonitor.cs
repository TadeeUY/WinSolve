using System.Diagnostics.Eventing.Reader;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>Pop-up notice about a Windows error.</summary>
public sealed class Alert
{
    /// <summary>Alert kind (used to mute it or avoid repeats).</summary>
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public IssueSeverity Severity { get; init; } = IssueSeverity.Warning;
    public string[] FixTaskIds { get; init; } = [];
    public string? NavigateTo { get; init; }
    public string? DeviceInstanceId { get; init; }

    /// <summary>Key used to avoid repeating the same alert (e.g. the same app crashing).</summary>
    public string DedupeKey { get; init; } = "";

    public bool CanFix => FixTaskIds.Length > 0 || NavigateTo is not null || DeviceInstanceId is not null;
}

/// <summary>
/// Watches the event log and devices in the background and raises an alert
/// whenever Windows reports an error.
/// </summary>
public sealed partial class ErrorMonitor : IDisposable
{
    public static ErrorMonitor Instance { get; } = new();

    private readonly List<EventLogWatcher> _watchers = [];
    private readonly Dictionary<string, DateTime> _recent = [];
    private volatile HashSet<string> _knownDevices = [];
    private System.Threading.Timer? _deviceTimer;
    private SynchronizationContext? _ui;
    private bool _running;

    /// <summary>Raised on the UI thread.</summary>
    public event Action<Alert>? AlertRaised;

    private ErrorMonitor() { }

    /// <summary>Starts or stops monitoring according to the settings.</summary>
    public void Apply()
    {
        _ui ??= SynchronizationContext.Current;
        if (AppSettings.Current.ErrorAlerts && !_running) Start();
        else if (!AppSettings.Current.ErrorAlerts && _running) Stop();
    }

    private void Start()
    {
        _running = true;

        Watch("System", """
            *[System[((Level=1 or Level=2) and (
                Provider[@Name='Microsoft-Windows-WHEA-Logger'] or
                Provider[@Name='disk'] or Provider[@Name='Ntfs'] or Provider[@Name='stornvme'] or Provider[@Name='storahci'] or
                (Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and EventID=20) or
                (Provider[@Name='Service Control Manager'] and (EventID=7031 or EventID=7034))
            )) or (Provider[@Name='Display'] and EventID=4101)]]
            """);
        Watch("Application", "*[System[(Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Application Hang'] and EventID=1002)]]");

        // Whatever happened while the app was closed (blue screens, unexpected shutdowns).
        Task.Run(CheckSinceLastRun);

        // Devices that stop working (every 3 minutes).
        // Seeded off the UI thread (WMI is slow); devices failing already at start aren't alerted.
        Task.Run(() =>
        {
            try { _knownDevices = HardwareService.GetProblemDevices().Select(d => d.InstanceId).ToHashSet(); }
            catch { }
        });
        _deviceTimer = new System.Threading.Timer(_ => CheckDevices(), null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3));
        // Drive health (S.M.A.R.T.): every hour, first a few minutes after start.
        _diskTimer = new System.Threading.Timer(_ => _ = CheckDisksAsync(), null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));
        Logger.Write("Error monitoring enabled.");
    }

    private void Stop()
    {
        _running = false;
        foreach (var w in _watchers)
        {
            try { w.Enabled = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();
        _deviceTimer?.Dispose();
        _deviceTimer = null;
        _diskTimer?.Dispose();
        _diskTimer = null;
        Logger.Write("Error monitoring disabled.");
    }

    private void Watch(string log, string xpath)
    {
        try
        {
            var watcher = new EventLogWatcher(new EventLogQuery(log, PathType.LogName, xpath));
            watcher.EventRecordWritten += (_, e) =>
            {
                if (e.EventRecord is null) return;
                using var rec = e.EventRecord;
                var alert = Classify(rec);
                if (alert is not null) Raise(alert);
            };
            watcher.Enabled = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not watch the {log} log: {ex.Message}");
        }
    }

    private void Raise(Alert alert)
    {
        if (AppSettings.Current.MutedAlerts.Contains(alert.Kind)) return;

        lock (_recent)
        {
            var key = alert.Kind + "|" + alert.DedupeKey;
            if (_recent.TryGetValue(key, out var last) && DateTime.Now - last < TimeSpan.FromMinutes(30)) return;
            _recent[key] = DateTime.Now;
        }

        Logger.Write($"ALERT: {alert.Title} - {alert.Detail}");
        if (_ui is not null) _ui.Post(_ => AlertRaised?.Invoke(alert), null);
        else AlertRaised?.Invoke(alert);
    }

    private static string Prop(EventRecord rec, int index)
    {
        try { return rec.Properties.Count > index ? rec.Properties[index].Value?.ToString() ?? "" : ""; }
        catch { return ""; }
    }

    private static Alert? Classify(EventRecord rec)
    {
        switch (rec.ProviderName, rec.Id)
        {
            case ("Application Error", 1000):
            {
                var app = Prop(rec, 0);
                var module = Prop(rec, 3);
                var systemModule = module.Length > 0 && (module.StartsWith("ntdll", StringComparison.OrdinalIgnoreCase)
                    || module.StartsWith("KERNELBASE", StringComparison.OrdinalIgnoreCase)
                    || module.StartsWith("ucrtbase", StringComparison.OrdinalIgnoreCase));
                return new Alert
                {
                    Kind = "app-crash", DedupeKey = app,
                    Title = $"{(app.Length > 0 ? app : "An application")} closed unexpectedly",
                    Detail = systemModule
                        ? $"The crash happened inside a Windows component ({module}). Repairing the system files may fix it."
                        : $"Faulting module: {(module.Length > 0 ? module : "unknown")}. If it keeps happening, update or reinstall the application.",
                    FixTaskIds = systemModule ? ["repair-system-files"] : [],
                };
            }
            case ("Application Hang", 1002):
            {
                var app = Prop(rec, 0);
                return new Alert
                {
                    Kind = "app-hang", DedupeKey = app, Severity = IssueSeverity.Info,
                    Title = $"{(app.Length > 0 ? app : "An application")} stopped responding",
                    Detail = "Windows had to close it. If this happens often, check free memory and your startup programs.",
                    NavigateTo = "startup",
                };
            }
            case ("Microsoft-Windows-WHEA-Logger", _):
                return new Alert
                {
                    Kind = "whea", Severity = IssueSeverity.Critical,
                    Title = "Hardware error detected (WHEA)",
                    Detail = "The CPU, memory or a PCIe device reported an error. Possible causes: overclocking, high temperatures or a failing component.",
                    NavigateTo = "hardware",
                };
            case ("disk" or "Ntfs" or "stornvme" or "storahci", _):
                return new Alert
                {
                    Kind = "disk", Severity = IssueSeverity.Critical,
                    Title = "Disk errors",
                    Detail = "Windows logged errors while reading or writing a drive. Back up your data and check the drive.",
                    FixTaskIds = ["repair-chkdsk-scan"],
                };
            case ("Display", 4101):
                return new Alert
                {
                    Kind = "gpu",
                    Title = "The display driver stopped responding",
                    Detail = "The screen flickered because the graphics driver was reset. A clean driver reinstall usually fixes it.",
                    NavigateTo = "drivers",
                };
            case ("Microsoft-Windows-WindowsUpdateClient", 20):
                return new Alert
                {
                    Kind = "wu",
                    Title = "A Windows update failed to install",
                    Detail = "Resetting the Windows Update components usually fixes this.",
                    FixTaskIds = ["repair-windows-update"],
                };
            case ("Service Control Manager", 7031 or 7034):
            {
                var service = Prop(rec, 0);
                return new Alert
                {
                    Kind = "service", DedupeKey = service, Severity = IssueSeverity.Info,
                    Title = $"The '{service}' service stopped unexpectedly",
                    Detail = "If it keeps happening, repairing the system files may help.",
                    FixTaskIds = ["repair-system-files"],
                };
            }
            default:
                return null;
        }
    }

    private void CheckSinceLastRun()
    {
        try
        {
            var since = AppSettings.Current.LastErrorCheck ?? DateTime.Now.AddDays(-1);
            var days = Math.Clamp((DateTime.Now - since).TotalDays, 0.01, 30);
            var events = HardwareService.GetCriticalEvents((int)Math.Ceiling(days)).Where(e => e.Time > since).ToList();

            var bsod = events.Count(e => e.EventId == 1001);
            if (bsod > 0)
                Raise(new Alert
                {
                    Kind = "bsod", Severity = IssueSeverity.Critical,
                    Title = bsod == 1 ? "Windows had a blue screen" : $"Windows had {bsod} blue screens",
                    Detail = events.First(e => e.EventId == 1001).Summary + " Repairing the system files is the first step.",
                    FixTaskIds = ["repair-system-files"],
                });

            var power = events.Count(e => e.EventId == 41);
            if (power > 0 && bsod == 0)
                Raise(new Alert
                {
                    Kind = "power-loss",
                    Title = "The PC shut down unexpectedly",
                    Detail = "Windows did not shut down properly (power loss, hang or power button). Check the drive for damage.",
                    FixTaskIds = ["repair-chkdsk-scan"],
                });

            AppSettings.Current.LastErrorCheck = DateTime.Now;
            AppSettings.Current.Save();
        }
        catch (Exception ex)
        {
            Logger.Write($"Error checking past events: {ex.Message}");
        }
    }

    private void CheckDevices()
    {
        try
        {
            var current = HardwareService.GetProblemDevices();
            var known = _knownDevices;
            // Replaced every pass: a device that recovers and fails again alerts again.
            _knownDevices = current.Select(d => d.InstanceId).ToHashSet();
            foreach (var d in current)
            {
                if (known.Contains(d.InstanceId)) continue;
                Raise(new Alert
                {
                    Kind = "device", DedupeKey = d.InstanceId,
                    Title = $"Problem with {d.Name}",
                    Detail = d.Explanation,
                    DeviceInstanceId = d.InstanceId,
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Write($"Error checking devices: {ex.Message}");
        }
    }

    /// <summary>Shows a sample alert (from Settings).</summary>
    public void RaiseTest() => Raise(new Alert
    {
        Kind = "test", DedupeKey = Guid.NewGuid().ToString(),
        Title = "Test alert",
        Detail = "This is how WinSolve notifies you when Windows reports an error. 'Fix' runs the matching repair.",
        FixTaskIds = ["net-flush-dns"],
    });

    public void Dispose() => Stop();
}
