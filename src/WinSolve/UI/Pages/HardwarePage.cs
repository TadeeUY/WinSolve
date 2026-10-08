using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

public sealed class HardwarePage : Page
{
    private readonly Panel _host = new() { BackColor = Color.Transparent };
    private readonly List<FlatBtn> _tabs = [];
    private readonly Dictionary<string, Func<Control>> _sections;
    private readonly Dictionary<string, Control> _built = [];
    private string _current = "disks";

    public override string Key => "hardware";

    public HardwarePage() : base("Hardware",
        "Drive health (S.M.A.R.T.), devices with errors, memory, battery and logged failures.")
    {
        _sections = new()
        {
            ["disks"] = () => new DisksSection(),
            ["devices"] = () => new DevicesSection(),
            ["memory"] = () => new MemorySection(),
            ["events"] = () => new EventsSection(),
        };

        AddRow(Theme.Row(
            Tab("disks", "Drives"),
            Tab("devices", "Devices"),
            Tab("memory", "Memory & battery"),
            Tab("events", "Critical events")));
        AddRow(_host, fill: true);
        Show("disks");
    }

    private FlatBtn Tab(string key, string text)
    {
        var b = Theme.Button(text, (_, _) => Show(key));
        b.Tag = key;
        _tabs.Add(b);
        return b;
    }

    private void Show(string key)
    {
        _current = key;
        if (!_built.TryGetValue(key, out var section))
        {
            section = _sections[key]();
            section.Dock = DockStyle.Fill;
            _built[key] = section;
        }
        _host.Controls.Clear();
        _host.Controls.Add(section);
        foreach (var t in _tabs) t.Primary = Equals(t.Tag, key);
        if (section is ISection s) s.Refresh(false);
    }

    public override void OnShown()
    {
        if (_built.TryGetValue(_current, out var s) && s is ISection sec) sec.Refresh(false);
    }

    private interface ISection
    {
        void Refresh(bool force);
    }

    // ═════════════════════ Drives (CrystalDiskInfo style) ═════════════════════

    private sealed class DisksSection : TableLayoutPanel, ISection
    {
        private readonly FlowLayoutPanel _diskButtons = Theme.Row();
        private readonly StackCard _detail = new();
        private readonly DataGridView _attrs = new();
        private List<DiskInfo> _disks = [];
        private bool _loading;
        private bool _loaded;

        public DisksSection()
        {
            ColumnCount = 1;
            BackColor = Color.Transparent;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Theme.StyleGrid(_attrs);
            _attrs.ReadOnly = true;
            _attrs.Columns.Add("id", "ID");
            _attrs.Columns.Add("name", "Attribute");
            _attrs.Columns.Add("cur", "Current");
            _attrs.Columns.Add("worst", "Worst");
            _attrs.Columns.Add("thr", "Threshold");
            _attrs.Columns.Add("raw", "Raw value");
            _attrs.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _attrs.Columns["name"]!.FillWeight = 300;

            Controls.Add(_diskButtons, 0, 0);
            Controls.Add(_detail, 0, 1);
            Controls.Add(_attrs, 0, 2);
            _diskButtons.Dock = DockStyle.Fill;
            _detail.Dock = DockStyle.Fill;
            _attrs.Dock = DockStyle.Fill;
        }

        public async void Refresh(bool force)
        {
            if (_loading || (_loaded && !force)) return;
            _loading = true;
            _diskButtons.Controls.Clear();
            _diskButtons.Controls.Add(Theme.Label("Reading S.M.A.R.T. data...", Theme.Body, Theme.Muted));
            try
            {
                _disks = await DiskHealthService.ScanAsync();
                _loaded = true;
            }
            finally
            {
                _loading = false;
            }

            _diskButtons.Controls.Clear();
            for (int i = 0; i < _disks.Count; i++)
            {
                var idx = i;
                var d = _disks[i];
                var b = Theme.Button($"{d.Model}  ·  {d.HealthText}" + (d.TemperatureC is { } t ? $"  ·  {t} °C" : ""), (_, _) => ShowDisk(idx));
                b.Tag = idx;
                _diskButtons.Controls.Add(b);
            }
            _diskButtons.Controls.Add(Theme.Button("Refresh", (_, _) => Refresh(true)));
            _detail.Visible = _attrs.Visible = _disks.Count > 0;
            if (_disks.Count > 0) ShowDisk(0);
            else _diskButtons.Controls.Add(Theme.Label("No drives found. Is WinSolve running as administrator?", Theme.Body, Theme.Warn));
        }

        private void ShowDisk(int index)
        {
            foreach (Control c in _diskButtons.Controls)
                if (c is FlatBtn b && b.Tag is int i) b.Primary = i == index;

            var d = _disks[index];
            _detail.Body.Controls.Clear();

            var grid = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, BackColor = Color.Transparent };
            for (int c = 0; c < 4; c++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            void Kv(string k, string v)
            {
                var cell = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 8) };
                cell.Controls.Add(Theme.Label(k, Theme.Small, Theme.Muted));
                cell.Controls.Add(Theme.Label(v, Theme.BodyBold));
                grid.Controls.Add(cell);
            }
            Kv("Health", d.HealthText + (d.WearPercent is { } w ? $" ({100 - w}% life left)" : ""));
            Kv("Temperature", d.TemperatureC is { } t ? $"{t} °C" + (d.TemperatureMaxC is { } tm ? $" (max {tm} °C)" : "") : "—");
            Kv("Power-on hours", d.PowerOnHours is { } h ? $"{h:N0} h" : "—");
            Kv("Power cycles", d.PowerCycles is { } pc ? $"{pc:N0}" : "—");
            Kv("Type / interface", $"{d.MediaType} / {d.BusType}");
            Kv("Capacity", Format.Bytes(d.SizeBytes));
            Kv("Firmware", d.Firmware.Length > 0 ? d.Firmware : "—");
            Kv("Serial number", d.Serial.Length > 0 ? d.Serial : "—");

            _detail.Add(
                Theme.Label(d.Model, Theme.H2),
                Theme.Status($"{d.HealthText}  ·  Windows status: {(d.WindowsHealth.Length > 0 ? d.WindowsHealth : "—")}  ·  Failure predicted: {d.PredictFailure switch { true => "YES", false => "No", _ => "—" }}",
                    Theme.For(d.Health)),
                new Divider(),
                grid);
            foreach (var f in d.Findings) _detail.Add(Theme.Paragraph(f, Theme.Warn));
            if (d.Attributes.Count == 0)
                _detail.Add(Theme.Paragraph("This drive does not expose raw S.M.A.R.T. attributes to Windows (typical for NVMe and USB drives). Reliability counters are shown instead."));

            _attrs.Rows.Clear();
            foreach (var a in d.Attributes)
            {
                var i = _attrs.Rows.Add($"{a.Id:X2}", a.Name, a.Current, a.Worst, a.Threshold, a.Raw);
                if (a.Failing) _attrs.Rows[i].DefaultCellStyle.ForeColor = Theme.Bad;
                else if (a.Raw > 0 && a.Id is 0x05 or 0xC5 or 0xC6 or 0xBB) _attrs.Rows[i].DefaultCellStyle.ForeColor = Theme.Warn;
            }
            _attrs.Visible = d.Attributes.Count > 0;
        }
    }

    // ═════════════════════ Devices ═════════════════════

    private sealed class DevicesSection : TableLayoutPanel, ISection
    {
        private readonly DataGridView _grid = new();
        private readonly Control _summaryHost = new Panel { Height = 24, BackColor = Color.Transparent };
        private readonly TaskRunnerView _runner = new();

        public DevicesSection()
        {
            ColumnCount = 1;
            BackColor = Color.Transparent;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            RowStyles.Add(new RowStyle(SizeType.Percent, 45));

            Theme.StyleGrid(_grid);
            _grid.ReadOnly = true;
            _grid.Columns.Add("name", "Device");
            _grid.Columns.Add("cls", "Class");
            _grid.Columns.Add("code", "Code");
            _grid.Columns.Add("expl", "Problem");
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.Columns["code"]!.FillWeight = 30;
            _grid.Columns["cls"]!.FillWeight = 50;
            _grid.Columns["expl"]!.FillWeight = 200;

            var toolbar = Theme.Row(
                Theme.Button("Restart device", async (_, _) => await OnSelected("Restart device", HardwareService.RestartDeviceAsync)),
                Theme.Button("Reinstall device", async (_, _) => await OnSelected("Reinstall device",
                    async (id, log, ct) => { await HardwareService.ReinstallDeviceAsync(id, log, ct); return new ProcessResult(0, ""); })),
                Theme.Button("Scan for hardware changes", async (_, _) =>
                {
                    await _runner.RunTasksAsync([TaskCatalog.Find("repair-rescan-devices")!]);
                    Refresh(true);
                }),
                Theme.Button("Device Manager", (_, _) => ProcessRunner.ShellOpen("devmgmt.msc")),
                Theme.Button("Refresh", (_, _) => Refresh(true)));

            Controls.Add(toolbar, 0, 0);
            Controls.Add(_summaryHost, 0, 1);
            Controls.Add(_grid, 0, 2);
            Controls.Add(_runner, 0, 3);
            _grid.Dock = DockStyle.Fill;
        }

        public void Refresh(bool force)
        {
            var devices = HardwareService.GetProblemDevices();
            _grid.Rows.Clear();
            foreach (var d in devices)
            {
                var i = _grid.Rows.Add(d.Name, d.Class, d.ErrorCode, d.Explanation);
                _grid.Rows[i].Tag = d;
            }
            _summaryHost.Controls.Clear();
            _summaryHost.Controls.Add(devices.Count == 0
                ? Theme.Status("All devices are working properly.", Theme.Good)
                : Theme.Status($"{devices.Count} device(s) have problems. Select one and try Restart, then Reinstall.", Theme.Warn));
        }

        private async Task OnSelected(string title, Func<string, Action<string>, CancellationToken, Task<ProcessResult>> action)
        {
            if (_grid.CurrentRow?.Tag is not ProblemDevice d)
            {
                MessageBox.Show(this, "Select a device in the list first.", "WinSolve");
                return;
            }
            await _runner.RunAsync($"{title}: {d.Name}", async (log, _, ct) => { await action(d.InstanceId, log, ct); });
            Refresh(true);
        }
    }

    // ═════════════════════ Memory & battery ═════════════════════

    private sealed class MemorySection : Stack, ISection
    {
        private bool _loaded;

        public MemorySection() : base(scroll: true) { }

        public void Refresh(bool force)
        {
            if (_loaded && !force) return;
            _loaded = true;
            Controls.Clear();

            var mem = HardwareService.GetMemory();
            var memCard = new StackCard();
            memCard.Add(Theme.Label($"Memory  ·  {Format.Bytes(mem.Sum(m => m.CapacityBytes))} in {mem.Count} module(s)", Theme.H2));
            foreach (var m in mem)
                memCard.Add(Theme.Label($"{m.Slot}:  {Format.Bytes(m.CapacityBytes)}  ·  {m.SpeedMhz} MT/s  ·  {m.Manufacturer} {m.PartNumber}".Trim(), Theme.Body));
            if (mem.Count == 1)
                memCard.Add(Theme.Paragraph("A single memory module runs in single-channel mode. Adding a matching second module (dual channel) noticeably improves performance, especially with integrated graphics."));
            memCard.Add(Theme.Row(Theme.Button("Schedule a memory test", (_, _) => ProcessRunner.ShellOpen("mdsched.exe"))));
            Add(memCard);

            var batteries = HardwareService.GetBatteries();
            var batCard = new StackCard();
            if (batteries.Count == 0)
                batCard.Add(Theme.Label("Battery", Theme.H2), Theme.Paragraph("No battery detected (desktop PC)."));
            foreach (var b in batteries)
            {
                batCard.Add(Theme.Label($"Battery  ·  {b.Name}", Theme.H2));
                batCard.Add(Theme.Label($"Charge: {b.ChargePercent}%", Theme.Body));
                if (b.HealthPercent is { } h)
                {
                    var color = h >= 80 ? Theme.Good : h >= 60 ? Theme.Warn : Theme.Bad;
                    batCard.Add(Theme.Status($"Health {h}%  ·  full charge {b.FullChargeCapacity:N0} mWh of {b.DesignCapacity:N0} mWh design capacity", color));
                    if (h < 60) batCard.Add(Theme.Paragraph("The battery has lost much of its capacity. Consider replacing it.", Theme.Warn));
                }
            }
            if (batteries.Count > 0)
            {
                batCard.Add(Theme.Row(Theme.Button("Generate battery report", async (_, _) =>
                {
                    var file = Path.Combine(SafePath.CreateAdminOnlyFolder("Reports"), "battery-report.html");
                    await ProcessRunner.RunAsync("powercfg.exe", $"/batteryreport /output \"{file}\"");
                    ProcessRunner.ShellOpen(file);
                })));
            }
            Add(batCard);
        }
    }

    // ═════════════════════ Events ═════════════════════

    private sealed class EventsSection : TableLayoutPanel, ISection
    {
        private readonly DataGridView _grid = new();
        private readonly Panel _summaryHost = new() { Height = 24, BackColor = Color.Transparent };
        private bool _loaded;

        public EventsSection()
        {
            ColumnCount = 1;
            BackColor = Color.Transparent;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Theme.StyleGrid(_grid);
            _grid.ReadOnly = true;
            _grid.Columns.Add("time", "Date");
            _grid.Columns.Add("src", "Source");
            _grid.Columns.Add("id", "ID");
            _grid.Columns.Add("sum", "What happened");
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.Columns["time"]!.FillWeight = 60;
            _grid.Columns["src"]!.FillWeight = 70;
            _grid.Columns["id"]!.FillWeight = 20;
            _grid.Columns["sum"]!.FillWeight = 250;

            Controls.Add(Theme.Row(
                Theme.Button("Refresh", (_, _) => Refresh(true)),
                Theme.Button("Event Viewer", (_, _) => ProcessRunner.ShellOpen("eventvwr.msc")),
                Theme.Button("Reliability Monitor", (_, _) => ProcessRunner.ShellOpen("perfmon.exe", "/rel"))), 0, 0);
            Controls.Add(_summaryHost, 0, 1);
            Controls.Add(_grid, 0, 2);
            _grid.Dock = DockStyle.Fill;
        }

        public async void Refresh(bool force)
        {
            if (_loaded && !force) return;
            _loaded = true;
            _summaryHost.Controls.Clear();
            _summaryHost.Controls.Add(Theme.Label("Reading the event log...", Theme.Body, Theme.Muted));
            var events = await Task.Run(() => HardwareService.GetCriticalEvents(30));
            _grid.Rows.Clear();
            foreach (var e in events)
                _grid.Rows.Add(e.Time.ToString("g"), e.Source.Replace("Microsoft-Windows-", ""), e.EventId, e.Summary);
            _summaryHost.Controls.Clear();
            _summaryHost.Controls.Add(events.Count == 0
                ? Theme.Status("No blue screens, unexpected shutdowns or hardware errors in the last 30 days.", Theme.Good)
                : Theme.Status($"{events.Count} critical event(s) in the last 30 days.", Theme.Warn));
        }
    }
}
