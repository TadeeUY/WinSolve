using System.Net;
using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

/// <summary>Internet and router: connection check, devices on the network and the router's own admin page.</summary>
public sealed class InternetPage : Page
{
    private readonly Label _gateway = Theme.Label("—", Theme.Big), _gatewayDetail = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly Label _latency = Theme.Label("—", Theme.Big), _latencyDetail = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly Label _count = Theme.Label("—", Theme.Big), _countDetail = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly Label _verdict = Theme.Paragraph("", Theme.Text);
    private readonly Label _status = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly Label _deviceStatus = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly TextBox _address = Theme.TextBox("192.168.1.1"), _user = Theme.TextBox("admin"), _password = Theme.TextBox("Password");
    private readonly DataGridView _grid = new();
    private readonly FlatBtn _checkBtn, _scanBtn;
    private LanInfo? _lan;
    private bool _busy;
    private bool _identifying, _identified;

    private readonly Label _detected = Theme.Label("", Theme.BodyBold);
    private readonly ComboBox _band = Theme.Combo("2.4 GHz", "5 GHz");
    private readonly ChannelBars _channels = new() { Height = 240, Margin = new Padding(0, 6, 0, 6) };
    private readonly Label _wifiVerdict = Theme.Paragraph("", Theme.Text);
    private readonly Label _wifiStatus = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly FlatBtn _wifiBtn, _dnsBtn;
    private readonly DataGridView _dnsGrid = new();
    private readonly Label _dnsVerdict = Theme.Paragraph("", Theme.Text);
    private readonly Label _dnsStatus = Theme.Label("", Theme.Small, Theme.Muted);
    private WifiScan? _wifi;
    private readonly CheckBox _alertToggle = Theme.Toggle("", false), _recordToggle = Theme.Toggle("", false);
    private readonly ComboBox _range = Theme.Combo("Last hour", "Last 24 hours", "Last 7 days");
    private readonly QualityChart _history = new() { Margin = new Padding(0, 6, 0, 6) };
    private readonly Label _historySummary = Theme.Paragraph("", Theme.Text), _historyCuts = Theme.Paragraph("", Theme.Muted, Theme.Small);
    private bool _loadingToggles;

    public override string Key => "internet";

    public InternetPage() : base("Internet", "Check your connection, see who is connected and manage your router: Wi-Fi password, devices, everything the router offers.")
    {
        _password.UseSystemPasswordChar = true;
        _address.Width = 200;
        _user.Width = 160;
        _password.Width = 200;

        var tiles = new TableLayoutPanel { ColumnCount = 3, Height = 150, BackColor = Color.Transparent, Margin = new Padding(0) };
        for (var i = 0; i < 3; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        tiles.Controls.Add(Tile("Router", _gateway, _gatewayDetail, 8), 0, 0);
        tiles.Controls.Add(Tile("Internet", _latency, _latencyDetail, 8), 1, 0);
        tiles.Controls.Add(Tile("Devices", _count, _countDetail, 0), 2, 0);

        _checkBtn = Theme.Button("Check my Internet", async (_, _) => await CheckAsync(), primary: true, glyph: "");
        var check = new StackCard().Add(
            Theme.SectionHeader("", "Connection", "Tests the router, the Internet and the DNS, and tells you where the problem is."),
            _verdict,
            Theme.Row(
                _checkBtn,
                Theme.Button("Flush DNS cache", async (_, _) => await FlushDnsAsync(), glyph: ""),
                Theme.Button("Network test", (_, _) => OpenTool("network"), glyph: ""),
                Theme.Button("Saved Wi-Fi passwords", (_, _) => OpenTool("wifi"), glyph: ""),
                Theme.Button("Wi-Fi QR code", (_, _) => OpenTool("wifi-qr"), glyph: "\uE701"),
                Theme.Button("Block a website", (_, _) => OpenTool("hosts"), glyph: "")));

        var router = new StackCard().Add(
            Theme.SectionHeader("", "Your router", "WinSolve opens your router's own settings page already signed in. From there you can change anything: Wi-Fi name and password, remove or block devices, restart it."),
            _detected,
            Theme.SettingRow("Router address", "Usually 192.168.1.1 or 192.168.0.1. Only addresses on your own network are accepted.", _address),
            Theme.SettingRow("User", "The router's user, often 'admin'. It is on the sticker under the router.", _user),
            Theme.SettingRow("Password", "Saved encrypted for your Windows user only. Leave it empty to type it in the router's page yourself.", _password),
            new Divider(),
            Theme.Row(
                Theme.Button("Open my router", (_, _) => OpenRouter(), primary: true, glyph: ""),
                Theme.Button("Save", (_, _) => SaveLogin(), glyph: ""),
                Theme.Button("Forget saved password", (_, _) => Forget(), glyph: "")),
            _status);

        ConfigureGrid();
        _scanBtn = Theme.Button("Scan the network", async (_, _) => await ScanAsync(), primary: true, glyph: "");
        var gridHost = Theme.InCard(_grid);
        gridHost.Height = 300;
        gridHost.Dock = DockStyle.None;
        gridHost.Margin = new Padding(0, 4, 0, 4);
        var devices = new StackCard().Add(
            Theme.SectionHeader("", "Devices on your network", "Everything connected to your router right now, by cable or Wi-Fi."),
            Theme.Row(
                _scanBtn,
                Theme.Button("Copy address", (_, _) => CopySelected(), glyph: ""),
                Theme.Button("Mark all as known", (_, _) => MarkAllKnown(), glyph: "\uE73E"),
                Theme.Button("Block or remove in the router", (_, _) => BlockSelected(), glyph: "")),
            Theme.Paragraph("Double-click a device's name to give it your own (for example 'Mom's phone'). WinSolve remembers it.", Theme.Muted, Theme.Small),
            gridHost,
            _deviceStatus,
            Theme.SettingRow("Alert me when a new device joins my network", "WinSolve looks at your network every 15 minutes while it is running (also from the notification area) and warns about a device it has never seen. The first time, everything connected now counts as known.", _alertToggle),
            Theme.Paragraph("Windows can't disconnect other devices by itself: removing or blocking one is done in the router. The button copies the device's address and opens the router so you can paste it in its MAC filter.", Theme.Muted, Theme.Small));

        _band.Width = 110;
        _wifiBtn = Theme.Button("Analyze Wi-Fi channels", async (_, _) => await AnalyzeWifiAsync(), primary: true, glyph: "\uE701");
        _band.SelectedIndexChanged += (_, _) => ShowChannels();
        var wifi = new StackCard().Add(
            Theme.SectionHeader("\uE701", "Wi-Fi channels", "Shows which channels your neighbors' Wi-Fi uses and which one is least crowded for your router."),
            Theme.Row(_wifiBtn, Theme.Label("Band", Theme.Body, Theme.Muted), _band),
            _channels,
            _wifiVerdict,
            _wifiStatus,
            Theme.Row(Theme.Button("Open my router", (_, _) => OpenRouter(), glyph: "\uE8A7")));

        ConfigureDnsGrid();
        _dnsBtn = Theme.Button("Test DNS speed", async (_, _) => await TestDnsAsync(), primary: true, glyph: "\uE9D9");
        var dnsHost = Theme.InCard(_dnsGrid);
        dnsHost.Height = 270;
        dnsHost.Dock = DockStyle.None;
        dnsHost.Margin = new Padding(0, 4, 0, 4);
        var speed = new StackCard().Add(
            Theme.SectionHeader("\uE945", "Speed up your network", "Times the DNS servers (the 'phone book' your PC asks before opening any page) and tells you if a faster one exists."),
            Theme.Row(
                _dnsBtn,
                Theme.Button("Change DNS in Tweaks", (_, _) => Main.Navigate("tweaks"), glyph: "\uE9E9"),
                Theme.Button("Network repairs", (_, _) => Main.Navigate("tools"), glyph: "\uE90F")),
            dnsHost,
            _dnsVerdict,
            _dnsStatus);

        _range.Width = 150;
        _range.SelectedIndex = 1;
        _range.SelectedIndexChanged += (_, _) => ShowHistory();
        _alertToggle.CheckedChanged += (_, _) => SaveToggles();
        _recordToggle.CheckedChanged += (_, _) => SaveToggles();
        var history = new StackCard().Add(
            Theme.SectionHeader("\uE9D9", "Connection history", "Records the router and the Internet once a minute while WinSolve runs, and shows when the connection dropped. Useful as proof for your Internet provider."),
            Theme.SettingRow("Record my connection quality", "A tiny ping to the router and to 1.1.1.1 (Cloudflare) every minute, kept for 7 days on this PC.", _recordToggle),
            Theme.Row(
                Theme.Label("Show", Theme.Body, Theme.Muted), _range,
                Theme.Button("Refresh", (_, _) => ShowHistory(), glyph: "\uE72C"),
                Theme.Button("Copy report", (_, _) => CopyReport(), glyph: "\uE8C8"),
                Theme.Button("Delete the history", (_, _) => ClearHistory(), glyph: "\uE74D")),
            _history,
            _historySummary,
            _historyCuts);

        var stack = new Stack(scroll: true).Add(tiles, check, router, devices, wifi, speed, history);
        AddRow(stack, fill: true);
    }

    private static Card Tile(string caption, Label value, Label detail, int right)
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, right, 8), Padding = new Padding(14, 12, 14, 12) };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        layout.Controls.Add(Theme.Label(caption, Theme.Body, Theme.Muted));
        layout.Controls.Add(value);
        layout.Controls.Add(detail);
        card.Controls.Add(layout);
        return card;
    }

    private void ConfigureGrid()
    {
        Theme.StyleGrid(_grid);
        // Only the name can be edited (double-click it): it is saved against the device's physical address.
        _grid.ReadOnly = false;
        _grid.CellBeginEdit += (_, e) =>
        {
            if (e.ColumnIndex != 0 || _grid.Rows[e.RowIndex].Tag is not LanDevice { IsRouter: false, IsThisPc: false }) e.Cancel = true;
        };
        _grid.CellEndEdit += RenameEnded;
        _grid.CellDoubleClick += (_, e) => { if (e.ColumnIndex == 0 && e.RowIndex >= 0) _grid.BeginEdit(true); };
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (var (name, header, weight) in new[] { ("name", "Device", 30), ("ip", "IP address", 18), ("mac", "Physical address", 22), ("note", "Details", 30) })
        {
            var i = _grid.Columns.Add(name, header);
            _grid.Columns[i].FillWeight = weight;
        }
        for (var c = 1; c < _grid.Columns.Count; c++) _grid.Columns[c].ReadOnly = true;
        Theme.EmptyState(_grid, "Press 'Scan the network' to see the devices connected to your router.");
    }

    public override void OnShown()
    {
        if (_address.Text.Length == 0)
        {
            var saved = RouterVault.Load();
            if (saved is not null)
            {
                _address.Text = saved.Address;
                _user.Text = saved.User;
                _password.Text = saved.Password;
            }
        }
        _ = LoadConnectionAsync();
        _loadingToggles = true;
        _alertToggle.Checked = AppSettings.Current.NewDeviceAlerts;
        _recordToggle.Checked = AppSettings.Current.RecordQuality;
        _loadingToggles = false;
        ShowHistory();
    }

    private async Task LoadConnectionAsync()
    {
        _lan = await Task.Run(RouterService.Detect);
        if (IsDisposed) return;
        if (_lan is null)
        {
            _gateway.Text = "—";
            _gatewayDetail.Text = Loc.T("No network connection");
            return;
        }
        _gateway.Text = _lan.Gateway.ToString();
        _gatewayDetail.Text = Loc.T($"Through {_lan.Adapter}. This PC: {_lan.Local}");
        if (_address.Text.Length == 0) _address.Text = _lan.Gateway.ToString();
        _ = IdentifyRouterAsync();
    }

    /// <summary>Finds the brand and model of the router on its own, once.</summary>
    private async Task IdentifyRouterAsync()
    {
        if (_lan is null || _identifying || _identified) return;
        _identifying = true;
        _detected.Text = Loc.T("Identifying your router...");
        try
        {
            var id = await RouterDetector.DetectAsync(_lan);
            if (IsDisposed) return;
            _identified = true;
            if (id is null)
            {
                _detected.Text = Loc.T("Your router's brand could not be identified, but you can still open it.");
                return;
            }
            _detected.Text = string.Format(Loc.T("Detected router: {0}"), id.Display);
            _gatewayDetail.Text = Loc.T($"{id.Display} - through {_lan.Adapter}");
            Logger.Write($"Router detected ({id.Source}): {id.Display}");
        }
        catch { if (!IsDisposed) _detected.Text = ""; }
        finally { _identifying = false; }
    }

    // ───────────── Connection ─────────────

    private async Task CheckAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _verdict.Text = Loc.T("Checking...");
        try
        {
            _lan ??= await Task.Run(RouterService.Detect);
            var result = await RouterService.CheckInternetAsync(_lan?.Gateway);
            if (IsDisposed) return;
            _verdict.Text = Loc.T(result.Verdict);
            _latency.Text = result.InternetMs is { } ms ? $"{ms} ms" : Loc.T("No Internet");
            _latencyDetail.Text = Loc.T(result.DnsWorks ? "DNS works" : "DNS does not answer");
            if (result.RouterMs is { } r) _gatewayDetail.Text = Loc.T($"Answers in {r} ms");
        }
        catch (Exception ex) { _verdict.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private async Task FlushDnsAsync()
    {
        var result = await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns");
        _verdict.Text = Loc.T(result.Success ? "The DNS cache was cleared." : "The DNS cache could not be cleared.");
    }

    private void OpenTool(string id)
    {
        var tool = Toolbox.All.FirstOrDefault(t => t.Id == id);
        tool?.Open(Main);
    }

    // ───────────── Devices ─────────────

    private async Task ScanAsync()
    {
        if (_busy) return;
        _lan ??= await Task.Run(RouterService.Detect);
        if (_lan is null)
        {
            _deviceStatus.Text = Loc.T("No network connection");
            return;
        }
        SetBusy(true);
        _grid.Rows.Clear();
        try
        {
            var progress = new Progress<int>(p => _deviceStatus.Text = Loc.T($"Scanning the network... {p}%"));
            var devices = await RouterService.ScanAsync(_lan, progress);
            if (IsDisposed) return;
            // Remember what is on the network: whatever was never seen before shows as new.
            var fresh = await Task.Run(() => KnownDevices.Update(devices, DateTime.Now));
            var known = KnownDevices.Load();
            foreach (var d in devices)
            {
                var name = d.Name.Length > 0 ? d.Name : Loc.T("Unknown device");
                if (d.IsRouter) name = Loc.T("Router") + (d.Name.Length > 0 ? $" ({d.Name})" : "");
                if (d.IsThisPc) name += " " + Loc.T("(this PC)");
                var isNew = known.TryGetValue(d.Mac, out var k) && !k.Trusted;
                var row = _grid.Rows.Add(name, d.Ip, d.Mac, isNew ? Loc.T("New device") + (d.Note.Length > 0 ? " - " + Loc.T(d.Note) : "") : d.Note);
                _grid.Rows[row].Tag = d;
                if (isNew) _grid.Rows[row].Cells[0].Style.ForeColor = Theme.Warn;
            }
            _count.Text = devices.Count.ToString();
            _countDetail.Text = Loc.T("Connected right now");
            _deviceStatus.Text = fresh.Count == 0 ? Loc.T($"{devices.Count} device(s) found.") : Loc.T($"{devices.Count} device(s) found, {fresh.Count} new.");
        }
        catch (Exception ex) { _deviceStatus.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private void RenameEnded(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.ColumnIndex != 0 || e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not LanDevice d) return;
        var text = (_grid[0, e.RowIndex].Value as string ?? "").Trim();
        if (text == Loc.T("Unknown device")) text = "";
        try
        {
            DeviceBook.Set(d.Mac, text);
            _deviceStatus.Text = Loc.T(text.Length == 0 ? "The name was forgotten." : "Name saved.");
        }
        catch (Exception ex) { _deviceStatus.Text = ex.Message; }
        if (text.Length == 0) _grid[0, e.RowIndex].Value = Loc.T("Unknown device");
    }

    private LanDevice? Selected => _grid.CurrentRow?.Tag as LanDevice;

    private void CopySelected()
    {
        if (Selected is not { } d) { _deviceStatus.Text = Loc.T("Select a device first."); return; }
        Clipboard.SetText($"{d.Ip}  {d.Mac}");
        _deviceStatus.Text = Loc.T("Copied.");
    }

    private void BlockSelected()
    {
        if (Selected is not { } d) { _deviceStatus.Text = Loc.T("Select a device first."); return; }
        if (d.IsRouter || d.IsThisPc)
        {
            _deviceStatus.Text = Loc.T("That is the router or this PC: choose another device.");
            return;
        }
        OpenRouter(d.Mac);
    }

    private void MarkAllKnown()
    {
        KnownDevices.TrustAll();
        var prefix = Loc.T("New device");
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Cells[0].Style.ForeColor = Color.Empty;
            if (row.Cells[3].Value is string note && note.StartsWith(prefix, StringComparison.Ordinal))
                row.Cells[3].Value = note.Length > prefix.Length + 3 ? note[(prefix.Length + 3)..] : "";
        }
        _deviceStatus.Text = Loc.T("All devices marked as known.");
    }

    // ───────────── Connection history ─────────────

    private void SaveToggles()
    {
        if (_loadingToggles) return;
        AppSettings.Current.NewDeviceAlerts = _alertToggle.Checked;
        AppSettings.Current.RecordQuality = _recordToggle.Checked;
        AppSettings.Current.Save();
        NetworkWatcher.Instance?.Apply();
        ShowHistory();
    }

    private (DateTime From, DateTime To, List<QualitySample> Samples) HistoryData()
    {
        var span = _range.SelectedIndex switch { 0 => TimeSpan.FromHours(1), 2 => TimeSpan.FromDays(7), _ => TimeSpan.FromHours(24) };
        var to = DateTime.Now;
        return (to - span, to, QualityLog.Load(to - span));
    }

    private void ShowHistory()
    {
        var (from, to, samples) = HistoryData();
        var hours = (to - from).TotalHours;
        _history.SetData(QualityLog.Buckets(samples, from, to, hours <= 1 ? 60 : hours <= 24 ? 96 : 84), from, to);
        if (samples.Count == 0)
        {
            _historySummary.Text = Loc.T(AppSettings.Current.RecordQuality ? "Nothing recorded in this period yet." : "Turn on the recording to start keeping the history.");
            _historyCuts.Text = "";
            return;
        }
        var s = QualityLog.Summarize(samples);
        _historySummary.Text = string.Format(Loc.T("Internet reachable {0:0.0}% of the time. Average answer time: {1:0} ms. Cuts: {2} ({3} min in total)."),
            s.UptimePercent, s.AverageMs ?? 0, s.Outages.Count, Math.Round(s.Downtime.TotalMinutes));
        _historyCuts.Text = string.Join("\n", s.Outages.OrderByDescending(o => o.Start).Take(6).Select(o =>
            $"{o.Start.ToString("g", Loc.Culture)} - {Math.Max(1, (int)Math.Round(o.Length.TotalMinutes))} min - "
            + Loc.T(o.LocalNetwork ? "the router did not answer either" : "the router answered, the Internet did not")));
    }

    private void CopyReport()
    {
        var (from, to, samples) = HistoryData();
        if (samples.Count == 0) { _historySummary.Text = Loc.T("Nothing recorded in this period yet."); return; }
        var s = QualityLog.Summarize(samples);
        var lines = new List<string>
        {
            Loc.T("Connection quality report"),
            string.Format(Loc.T("Period: {0} - {1}"), from.ToString("g", Loc.Culture), to.ToString("g", Loc.Culture)),
            string.Format(Loc.T("Checks: {0} (one per minute while WinSolve was running)"), s.Samples),
            string.Format(Loc.T("Internet reachable: {0:0.0}% of the checks"), s.UptimePercent),
            string.Format(Loc.T("Average answer time: {0:0} ms"), s.AverageMs ?? 0),
            string.Format(Loc.T("Cuts: {0}, {1} minutes without Internet in total"), s.Outages.Count, Math.Round(s.Downtime.TotalMinutes)),
        };
        lines.AddRange(s.Outages.OrderByDescending(o => o.Start).Take(20).Select(o =>
            $"  {o.Start.ToString("g", Loc.Culture)}  {Math.Max(1, (int)Math.Round(o.Length.TotalMinutes))} min  "
            + Loc.T(o.LocalNetwork ? "the router did not answer either" : "the router answered, the Internet did not")));
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
        _historySummary.Text += "  " + Loc.T("Report copied.");
    }

    private void ClearHistory()
    {
        if (!ConfirmDanger(Loc.T("Delete the recorded connection history?"))) return;
        QualityLog.Clear();
        ShowHistory();
    }

    // ───────────── Router ─────────────

    private bool TryGetLogin(out RouterLogin login, out Uri home)
    {
        login = new RouterLogin("", "", "");
        home = null!;
        var text = _address.Text.Trim().Length > 0 ? _address.Text : _lan?.Gateway.ToString() ?? "";
        if (!RouterService.TryParseAddress(text, out home))
        {
            _status.Text = Loc.T("Type the router's address on your own network, like 192.168.1.1.");
            return false;
        }
        login = new RouterLogin(home.ToString(), _user.Text.Trim(), _password.Text);
        return true;
    }

    private void SaveLogin()
    {
        if (!TryGetLogin(out var login, out _)) return;
        try
        {
            RouterVault.Save(login);
            _status.Text = Loc.T(login.Password.Length > 0 ? "Saved. The password is encrypted for your Windows user." : "Saved without a password.");
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private void Forget()
    {
        RouterVault.Forget();
        _password.Text = "";
        _user.Text = "";
        _status.Text = Loc.T("The saved router password was deleted.");
    }

    private void OpenRouter(string? macToBlock = null)
    {
        if (!TryGetLogin(out var login, out var home)) return;
        RouterDialog.UseModernEngine();
        Logger.Write($"Opening the router page {home.Host}");
        using var dialog = new RouterDialog(login, home, macToBlock);
        dialog.ShowDialog(Main);
    }


    // ───────────── Wi-Fi channels ─────────────

    private async Task AnalyzeWifiAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _wifiVerdict.Text = "";
        _wifiStatus.Text = Loc.T("Listening to the Wi-Fi networks around (a few seconds)...");
        try
        {
            _wifi = await WifiScanner.ScanAsync();
            if (IsDisposed) return;
            if (_wifi.Aps.Count == 0)
            {
                _channels.SetLoads([]);
                _wifiStatus.Text = "";
                _wifiVerdict.Text = Loc.T("No Wi-Fi networks found. Check that this PC has Wi-Fi and that Windows Location is on (Windows needs it to scan Wi-Fi).");
                return;
            }
            // Start on the band this PC is connected to.
            var mine = _wifi.Aps.FirstOrDefault(a => a.Bssid == _wifi.ConnectedBssid);
            if (mine is not null) _band.SelectedIndex = mine.Band == WifiBand.Ghz24 ? 0 : 1;
            else if (_band.SelectedIndex < 0) _band.SelectedIndex = 0;
            ShowChannels();
        }
        catch (Exception ex)
        {
            _channels.SetLoads([]);
            _wifiStatus.Text = "";
            _wifiVerdict.Text = Loc.T(ex.Message);
        }
        finally { SetBusy(false); }
    }

    private void ShowChannels()
    {
        if (_wifi is null) return;
        var band = _band.SelectedIndex == 1 ? WifiBand.Ghz5 : WifiBand.Ghz24;
        var inBand = _wifi.Aps.Where(a => a.Band == band).ToList();
        var mine = inBand.FirstOrDefault(a => a.Bssid == _wifi.ConnectedBssid);
        var loads = ChannelPlanner.Score(_wifi.Aps, band, mine?.Channel);
        _channels.SetLoads(inBand.Count == 0 ? [] : loads);
        _wifiStatus.Text = Loc.T($"{inBand.Count} network(s) heard on this band.");
        if (inBand.Count == 0)
        {
            _wifiVerdict.Text = Loc.T("No Wi-Fi networks heard on this band.");
            return;
        }
        var best = loads.First(l => l.IsBest).Channel;
        _wifiVerdict.Text = mine is null ? Loc.T($"The least crowded channel is {best}. Pick it in your router's Wi-Fi settings.")
            : ChannelPlanner.CurrentIsFine(loads) ? Loc.T($"You are on channel {mine.Channel}, which is among the least crowded. No change needed.")
            : Loc.T($"You are on channel {mine.Channel}, which is crowded. Change your router to channel {best}: open the router, go to its Wi-Fi settings and pick that channel.");
    }

    // ───────────── DNS speed ─────────────

    private void ConfigureDnsGrid()
    {
        Theme.StyleGrid(_dnsGrid);
        _dnsGrid.ReadOnly = true;
        _dnsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (var (name, header, weight) in new[] { ("name", "DNS server", 45), ("ip", "IP address", 30), ("ms", "Answer time", 25) })
        {
            var i = _dnsGrid.Columns.Add(name, header);
            _dnsGrid.Columns[i].FillWeight = weight;
        }
        Theme.EmptyState(_dnsGrid, "Press 'Test DNS speed' to compare your DNS with the popular ones.");
    }

    private async Task TestDnsAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _dnsGrid.Rows.Clear();
        _dnsVerdict.Text = "";
        try
        {
            var progress = new Progress<int>(p => _dnsStatus.Text = Loc.T($"Testing DNS servers... {p}%"));
            var results = await DnsBenchmark.RunAsync(progress);
            if (IsDisposed) return;
            foreach (var r in results)
                _dnsGrid.Rows.Add(r.Name, r.Address, r.Ms is { } ms ? $"{ms:0} ms" : Loc.T("No answer"));
            _dnsStatus.Text = "";
            var best = results.FirstOrDefault(r => r.Ms is not null);
            var current = results.FirstOrDefault(r => r.IsCurrent);
            if (best is null) _dnsVerdict.Text = Loc.T("No DNS server answered. Check your connection first.");
            else if (current is null || best.IsCurrent) _dnsVerdict.Text = string.Format(Loc.T("Your current DNS is already the fastest ({0:0} ms)."), best.Ms);
            else if (current.Ms is null) _dnsVerdict.Text = string.Format(Loc.T("Your current DNS did not answer. The fastest is {0} ({1:0} ms): set it in Tweaks."), Loc.T(best.Name), best.Ms);
            else if (current.Ms <= best.Ms * 1.2 || current.Ms - best.Ms < 5)
                _dnsVerdict.Text = string.Format(Loc.T("Your current DNS is fast enough ({0:0} ms); {1} would be {2:0} ms. Changing it would not be noticeable."), current.Ms, Loc.T(best.Name), best.Ms);
            else _dnsVerdict.Text = string.Format(Loc.T("{0} answers in {1:0} ms against {2:0} ms of your current DNS. Setting it in Tweaks makes pages start loading sooner."), Loc.T(best.Name), best.Ms, current.Ms);
        }
        catch (Exception ex) { _dnsVerdict.Text = ex.Message; _dnsStatus.Text = ""; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _checkBtn.Enabled = _scanBtn.Enabled = _wifiBtn.Enabled = _dnsBtn.Enabled = !busy;
    }
}
