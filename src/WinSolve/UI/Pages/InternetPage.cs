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
                Theme.Button("Block a website", (_, _) => OpenTool("hosts"), glyph: "")));

        var router = new StackCard().Add(
            Theme.SectionHeader("", "Your router", "WinSolve opens your router's own settings page already signed in. From there you can change anything: Wi-Fi name and password, remove or block devices, restart it."),
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
                Theme.Button("Block or remove in the router", (_, _) => BlockSelected(), glyph: "")),
            gridHost,
            _deviceStatus,
            Theme.Paragraph("Windows can't disconnect other devices by itself: removing or blocking one is done in the router. The button copies the device's address and opens the router so you can paste it in its MAC filter.", Theme.Muted, Theme.Small));

        var stack = new Stack(scroll: true).Add(tiles, check, router, devices);
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
        _grid.ReadOnly = true;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (var (name, header, weight) in new[] { ("name", "Device", 30), ("ip", "IP address", 18), ("mac", "Physical address", 22), ("note", "Details", 30) })
        {
            var i = _grid.Columns.Add(name, header);
            _grid.Columns[i].FillWeight = weight;
        }
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
            foreach (var d in devices)
            {
                var name = d.Name.Length > 0 ? d.Name : Loc.T("Unknown device");
                if (d.IsRouter) name = Loc.T("Router") + (d.Name.Length > 0 ? $" ({d.Name})" : "");
                if (d.IsThisPc) name += " " + Loc.T("(this PC)");
                var row = _grid.Rows.Add(name, d.Ip, d.Mac, d.Note);
                _grid.Rows[row].Tag = d;
            }
            _count.Text = devices.Count.ToString();
            _countDetail.Text = Loc.T("Connected right now");
            _deviceStatus.Text = Loc.T($"{devices.Count} device(s) found.");
        }
        catch (Exception ex) { _deviceStatus.Text = ex.Message; }
        finally { SetBusy(false); }
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

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _checkBtn.Enabled = _scanBtn.Enabled = !busy;
    }
}
