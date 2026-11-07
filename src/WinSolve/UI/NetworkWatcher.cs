using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>
/// Background checks of the network while WinSolve runs (also from the notification area), each one
/// only when the user turned it on in the Internet page: a ping a minute to keep the connection
/// history, and a look at the devices every 15 minutes to warn about one never seen before.
/// </summary>
public sealed class NetworkWatcher : IDisposable
{
    public static NetworkWatcher? Instance { get; private set; }

    private readonly System.Windows.Forms.Timer _quality = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer _devices = new() { Interval = 15 * 60_000 };
    private bool _qualityBusy, _devicesBusy;

    private NetworkWatcher()
    {
        _quality.Tick += async (_, _) => await RecordAsync();
        _devices.Tick += async (_, _) => await CheckDevicesAsync();
    }

    /// <summary>Starts watching (once, on the UI thread).</summary>
    public static void Start()
    {
        try
        {
            Instance ??= new NetworkWatcher();
            Instance.Apply();
        }
        catch (Exception ex) { Logger.Write($"[network] Could not start: {ex.Message}"); }
    }

    /// <summary>Follows the switches in the Internet page.</summary>
    public void Apply()
    {
        var s = AppSettings.Current;
        if (s.RecordQuality) QualityLog.Trim();
        _quality.Enabled = s.RecordQuality;
        _devices.Enabled = s.NewDeviceAlerts;
        if (s.RecordQuality) _ = RecordAsync();
        if (s.NewDeviceAlerts) _ = CheckDevicesAsync();
    }

    private async Task RecordAsync()
    {
        if (_qualityBusy) return;
        _qualityBusy = true;
        try
        {
            var lan = await Task.Run(RouterService.Detect);
            long? router = lan is null ? null : await RouterService.PingAsync(lan.Gateway.ToString(), 1500);
            long? internet = null;
            if (lan is not null)
            {
                internet = await RouterService.PingAsync("1.1.1.1", 2000);
                internet ??= await RouterService.PingAsync("8.8.8.8", 2000);
            }
            QualityLog.Append(new QualitySample(DateTime.Now, router is { } r ? (int)r : -1, internet is { } i ? (int)i : -1));
        }
        catch (Exception ex) { Logger.Write($"[network] Quality check failed: {ex.Message}"); }
        finally { _qualityBusy = false; }
    }

    private async Task CheckDevicesAsync()
    {
        if (_devicesBusy) return;
        _devicesBusy = true;
        try
        {
            var lan = await Task.Run(RouterService.Detect);
            if (lan is null) return;
            var devices = await RouterService.ScanAsync(lan, names: false);
            var fresh = await Task.Run(() => KnownDevices.Update(devices, DateTime.Now));
            if (fresh.Count == 0) return;
            Logger.Write($"[network] New device(s): {string.Join(", ", fresh.Select(d => $"{d.Ip} {d.Mac}"))}");
            if (fresh.Count > 3)
            {
                Notify(Loc.T("New devices on your network"), string.Format(Loc.T("{0} devices that were never seen before joined your network."), fresh.Count));
                return;
            }
            foreach (var d in fresh)
            {
                var name = d.Name.Length > 0 ? d.Name : Loc.T("Unknown device");
                var detail = $"{name} - {d.Ip} - {d.Mac}" + (d.Note.Length > 0 ? " - " + Loc.T(d.Note) : "");
                Notify(Loc.T("New device on your network"), detail);
            }
        }
        catch (Exception ex) { Logger.Write($"[network] Device check failed: {ex.Message}"); }
        finally { _devicesBusy = false; }
    }

    private static void Notify(string title, string detail)
    {
        static void OpenInternet()
        {
            if (Application.OpenForms.OfType<MainForm>().FirstOrDefault() is not { } form) return;
            form.ShowFromTray();
            form.Navigate("internet");
        }
        Toast.Show(new ToastOptions
        {
            Title = title,
            Detail = detail,
            Severity = IssueSeverity.Warning,
            Glyph = "",
            PrimaryText = "See devices",
            Primary = OpenInternet,
            Open = OpenInternet,
            AutoCloseSeconds = 0,
        });
    }

    public void Dispose()
    {
        _quality.Dispose();
        _devices.Dispose();
    }
}
