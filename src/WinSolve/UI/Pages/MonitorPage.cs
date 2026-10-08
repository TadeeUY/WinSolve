using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

public sealed class MonitorPage : Page
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1500 };
    private readonly TableLayoutPanel _grid = new() { ColumnCount = 3, BackColor = Color.Transparent };
    private readonly Dictionary<string, (Label Value, Label Detail, Sparkline Chart)> _tiles = [];
    private readonly Label _note = Theme.Label("", Theme.Small, Theme.Muted);
    private bool _sampling;

    public override string Key => "monitor";

    public MonitorPage() : base("Monitor", "Live usage and temperatures, updated every 1.5 seconds.")
    {
        for (int i = 0; i < 3; i++) _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        for (int i = 0; i < 3; i++) _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));

        AddTile("cpu", "Processor", 100);
        AddTile("cputemp", "CPU temperature", 110);
        AddTile("ram", "Memory", 100);
        AddTile("gpu", "Graphics", 100);
        AddTile("gputemp", "GPU temperature", 110);
        AddTile("disk", "Disk activity", 100);
        AddTile("net", "Network", 0);

        AddRow(_grid, fill: true);
        AddRow(_note);
        _note.Text = "CPU temperature comes from the ACPI thermal zone reported by the firmware. It is approximate and some PCs do not report it. " +
                     "GPU temperature is read like Task Manager does (WDDM 2.4+ drivers).";
        _timer.Tick += async (_, _) => await TickAsync();
    }

    private void AddTile(string key, string caption, double max)
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 8), Padding = new Padding(14, 12, 14, 12) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var value = Theme.Label("—", Theme.Big);
        var detail = Theme.Label("", Theme.Small, Theme.Muted);
        var chart = new Sparkline { Dock = DockStyle.Fill, Max = max, Margin = new Padding(0, 6, 0, 0) };
        layout.Controls.Add(Theme.Label(caption, Theme.Body, Theme.Muted), 0, 0);
        layout.Controls.Add(value, 0, 1);
        layout.Controls.Add(detail, 0, 2);
        layout.Controls.Add(chart, 0, 3);
        card.Controls.Add(layout);
        _grid.Controls.Add(card);
        _tiles[key] = (value, detail, chart);
    }

    public override async void OnShown()
    {
        _timer.Start();
        await TickAsync();
    }

    public override void OnHidden() => _timer.Stop();

    private async Task TickAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var s = await Task.Run(LiveMonitor.Sample);
            if (IsDisposed) return;

            Set("cpu", $"{s.CpuPercent:0}%", s.CpuGhz is { } ghz ? $"{ghz:0.00} GHz" : "", s.CpuPercent);
            Set("cputemp", s.CpuTempC is { } ct ? $"{ct:0} °C" : "N/A", s.CpuTempC is null ? "Not reported by this PC" : "ACPI thermal zone", s.CpuTempC ?? 0, TempColor(s.CpuTempC, 85));
            Set("ram", $"{s.RamUsedPercent:0}%", $"{Format.Bytes(s.RamUsedBytes)} of {Format.Bytes(s.RamTotalBytes)}", s.RamUsedPercent);
            Set("gpu", s.GpuPercent is { } gp ? $"{gp:0}%" : "N/A", s.Gpus.FirstOrDefault()?.Name ?? "", s.GpuPercent ?? 0);

            var hottest = s.Gpus.Where(g => g.TemperatureC is not null).OrderByDescending(g => g.TemperatureC).FirstOrDefault();
            Set("gputemp", hottest?.TemperatureC is { } gt ? $"{gt:0} °C" : "N/A",
                hottest is null ? "Not reported by the driver" : hottest.Name + (hottest.FanRpm is { } rpm ? $"  ·  fan {rpm} RPM" : ""),
                hottest?.TemperatureC ?? 0, TempColor(hottest?.TemperatureC, 85));

            Set("disk", $"{s.DiskPercent:0}%", "All physical disks", s.DiskPercent);
            Set("net", s.NetMbps >= 1 ? $"{s.NetMbps:0.0} Mbps" : $"{s.NetMbps * 1000:0} Kbps", "Send + receive, all adapters", s.NetMbps);
        }
        catch (Exception ex)
        {
            Logger.Write($"Monitor sample failed: {ex.Message}");
        }
        finally
        {
            _sampling = false;
        }
    }

    private static Color? TempColor(double? c, double hot) => c is null ? null : c >= hot ? Theme.Bad : c >= hot - 10 ? Theme.Warn : null;

    private void Set(string key, string value, string detail, double chartValue, Color? color = null)
    {
        var t = _tiles[key];
        t.Value.Text = value;
        t.Value.ForeColor = color ?? Theme.Text;
        t.Detail.Text = detail;
        t.Chart.Add(chartValue);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
