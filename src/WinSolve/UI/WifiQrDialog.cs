using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>The QR code visitors scan with their phone to join the Wi-Fi without typing the password.</summary>
internal sealed class WifiQrDialog : ToolDialog
{
    private readonly ComboBox _saved = Theme.Combo();
    private readonly TextBox _ssid = Theme.TextBox("Network name"), _password = Theme.TextBox("Password");
    private readonly ComboBox _security = Theme.Combo("WPA / WPA2 / WPA3", "WEP", "No password");
    private readonly CheckBox _hidden = Theme.Check("Hidden network", false);
    private readonly CheckBox _show = Theme.Toggle("", false);
    private readonly PictureBox _picture = new() { BackColor = Color.White, SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Control _savedRow;
    private List<WifiProfile> _profiles = [];
    private Bitmap? _bitmap;
    private bool _filling;

    public WifiQrDialog() : base("\uE701", "Wi-Fi QR code",
        "Visitors scan it with their phone camera to join your Wi-Fi without typing the password.", new Size(760, 780))
    {
        _security.SelectedIndex = 0;
        _password.UseSystemPasswordChar = true;
        _ssid.Width = _password.Width = 260;
        _saved.Width = 260;

        _savedRow = Theme.SettingRow("Saved network", "Choose one this PC knows, or type the name below.", _saved);
        AddRow(_savedRow);
        AddRow(Theme.SettingRow("Network name", null, _ssid));
        AddRow(Theme.SettingRow("Password", null, _password));
        AddRow(Theme.SettingRow("Show password", null, _show));
        AddRow(Theme.SettingRow("Security", null, _security));
        AddRow(_hidden);
        AddRow(_picture, fill: true);
        AddRow(Theme.Row(
            Theme.Button("Copy image", (_, _) => CopyImage(), glyph: "\uE8C8"),
            Theme.Button("Save image", (_, _) => SaveImage(), glyph: "\uE74E")));

        _show.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !_show.Checked;
        _saved.SelectedIndexChanged += (_, _) => PickSaved();
        _ssid.TextChanged += (_, _) => Redraw();
        _password.TextChanged += (_, _) => Redraw();
        _security.SelectedIndexChanged += (_, _) => Redraw();
        _hidden.CheckedChanged += (_, _) => Redraw();

        Shown += (_, _) =>
        {
            LoadSaved();
            Redraw();
            _ = DetectAsync();
        };
    }

    private void LoadSaved()
    {
        try { _profiles = WifiPasswords.Read(); }
        catch { _profiles = []; }   // no Wi-Fi card (a PC on the cable): type the network by hand
        foreach (var p in _profiles) _saved.Items.Add(p.Name);
        _savedRow.Visible = _profiles.Count > 0;
    }

    /// <summary>
    /// Finds the user's own Wi-Fi without asking: the network this PC is on, or the one whose address
    /// matches the router's (so it also works while the PC is on the cable), and fills in its password
    /// from what Windows saved.
    /// </summary>
    private async Task DetectAsync()
    {
        Status.Text = Loc.T("Looking for your Wi-Fi network...");
        string? ssid = null;
        try
        {
            var lan = await Task.Run(RouterService.Detect);
            var gatewayMac = lan is null ? null : await RouterService.GatewayMacAsync(lan);
            ssid = OwnNetwork.Find(await WifiScanner.ScanAsync(), gatewayMac);
        }
        catch { /* no Wi-Fi card, or Wi-Fi is off: only what Windows saved is left */ }
        if (IsDisposed || _ssid.Text.Length > 0) return;   // the user already typed something

        var index = ssid is null ? -1 : _profiles.FindIndex(p => p.Name == ssid);
        if (index >= 0)
        {
            _saved.SelectedIndex = index;
            Status.Text = string.Format(Loc.T("Detected your Wi-Fi network: {0}."), ssid);
        }
        else if (ssid is not null)
        {
            _ssid.Text = ssid;
            Status.Text = string.Format(Loc.T("Detected {0}, but this PC has never connected to it: type its password."), ssid);
        }
        else if (_profiles.Count > 0)
        {
            _saved.SelectedIndex = 0;
            Status.Text = _profiles.Count == 1
                ? string.Format(Loc.T("Found your saved Wi-Fi network: {0}."), _profiles[0].Name)
                : string.Format(Loc.T("Using the first saved network ({0}). Change it above if it is not yours."), _profiles[0].Name);
        }
        else Status.Text = Loc.T("No Wi-Fi network found on this PC. Type the name and password: they are on the router's sticker or in its Wi-Fi settings (Internet > Open my router).");
    }

    private void PickSaved()
    {
        if (_saved.SelectedIndex < 0 || _saved.SelectedIndex >= _profiles.Count) return;
        var p = _profiles[_saved.SelectedIndex];
        _filling = true;
        _ssid.Text = p.Name;
        // Windows only shows these keys to administrators: not to a standard user signed in while WinSolve runs elevated.
        _password.Text = p.Password is not null && !InteractiveUser.IsDifferent ? p.Password : "";
        _security.SelectedIndex = p.Security.StartsWith("Open", StringComparison.Ordinal) ? 2 : 0;
        _filling = false;
        Redraw();
    }

    private void Redraw()
    {
        if (_filling) return;
        var old = _bitmap;
        try
        {
            if (_ssid.Text.Length == 0)
            {
                _picture.Image = null;
                _bitmap = null;
                Status.Text = Loc.T("Type the network name to see its code.");
                return;
            }
            var security = _security.SelectedIndex switch { 1 => WifiQrSecurity.Wep, 2 => WifiQrSecurity.Open, _ => WifiQrSecurity.Wpa };
            _password.Enabled = security != WifiQrSecurity.Open;
            _bitmap = WifiQr.Render(WifiQr.Payload(_ssid.Text, _password.Text, security, _hidden.Checked), 8);
            _picture.Image = _bitmap;
            Status.Text = "";
        }
        catch (Exception ex)
        {
            _picture.Image = null;
            _bitmap = null;
            Status.Text = ex.Message;
        }
        finally { old?.Dispose(); }
    }

    private void CopyImage()
    {
        if (_bitmap is null) { Status.Text = Loc.T("Type the network name to see its code."); return; }
        Clipboard.SetImage(_bitmap);
        Status.Text = Loc.T("Copied.");
    }

    private void SaveImage()
    {
        if (_bitmap is null) { Status.Text = Loc.T("Type the network name to see its code."); return; }
        if (Pickers.PickersBlocked(this)) return;
        using var dialog = new SaveFileDialog { Title = Loc.T("Save image"), Filter = "PNG|*.png", FileName = "wifi-qr.png", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _bitmap.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
            Status.Text = Loc.T("Image saved.");
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _picture.Image = null; _bitmap?.Dispose(); }
        base.Dispose(disposing);
    }
}
