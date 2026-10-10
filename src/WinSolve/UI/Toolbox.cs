using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>The small utilities on the Toolbox page (also reachable from the command palette).</summary>
public static class Toolbox
{
    public sealed record Tool(string Id, string Category, string Glyph, string Title, string Description, string Keywords, Action<IWin32Window> Open);

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("disk-speed", "Disks & files", "", "Disk speed test",
            "Measures sequential and random read/write speed, like CrystalDiskMark.", "benchmark crystaldiskmark ssd hdd velocidad",
            o => Show(o, new DiskSpeedDialog())),
        new("locked-file", "Disks & files", "", "Locked file finder",
            "Shows which program has a file open, closes it or deletes the file at restart.", "unlocker in use bloqueado en uso",
            o => Show(o, new LockedFileDialog())),
        new("shred", "Disks & files", "", "Secure delete",
            "Overwrites files with random data before deleting them, so they can't be recovered.", "shred wipe borrar seguro triturar",
            o => Show(o, new ShredDialog())),
        new("network", "Network", "", "Network test",
            "Ping, download and upload speed, public IP and adapter details.", "speed test ping internet ip velocidad",
            o => Show(o, new NetworkDialog())),
        new("wifi", "Network", "", "Saved Wi-Fi passwords",
            "Shows the passwords of the Wi-Fi networks saved on this PC.", "wifi wireless password key contraseña clave",
            o => Show(o, new WifiDialog())),
        new("hosts", "Network", "", "Hosts file editor",
            "Edit the hosts file or block a website, with an automatic backup.", "hosts block site bloquear sitio",
            o => Show(o, new HostsDialog())),
        new("battery", "Windows", "\uEBA7", "Battery report",
            "How much capacity the battery has lost, charge cycles and how long it lasts now.", "battery bateria salud powercfg notebook laptop",
            o => Show(o, new BatteryDialog())),
        new("context-menu", "Windows", "", "Right-click menu cleanup",
            "Turn off entries that programs added to the right-click menu.", "context menu shell extension menu contextual clic derecho",
            o => Show(o, new ContextMenuDialog())),
        new("restore-points", "Windows", "", "Restore points",
            "See, create and delete System Restore points.", "system restore restauracion",
            o => Show(o, new RestorePointsDialog())),
        new("pc-report", "Windows", "", "PC report",
            "A PDF with every spec of this PC to share or print (no serial numbers or keys).", "specs speccy informe especificaciones hardware",
            CreateReport),
    ];

    private static void Show(IWin32Window owner, Form dialog)
    {
        using (dialog) dialog.ShowDialog(owner);
    }

    private static void CreateReport(IWin32Window owner)
    {
        string? file = null;
        RunDialog.Run(owner, "PC report", async (log, _, ct) => file = await PcReport.CreateAsync(log, ct));
        if (file is not null) ProcessRunner.ShellOpen(file);
    }
}

/// <summary>Common look of the tool windows: icon, title, description, body and a button bar.</summary>
public class ToolDialog : Form
{
    protected readonly TableLayoutPanel Body = new() { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Color.Transparent };
    protected readonly FlowLayoutPanel Buttons = new() { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, BackColor = Color.Transparent };
    protected readonly Label Status = Theme.Label("", Theme.Small, Theme.Muted);

    protected ToolDialog(string glyph, string title, string description, Size size)
    {
        Text = Loc.T(title);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        StartPosition = FormStartPosition.CenterParent;
        Size = size;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Padding = new Padding(20, 16, 20, 14);
        Body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var header = Theme.SectionHeader(glyph, title, description);
        header.Dock = DockStyle.Top;
        header.Margin = new Padding(0, 0, 0, 10);
        Buttons.Controls.Add(Theme.Button("Close", (_, _) => Close()));
        Status.Dock = DockStyle.Bottom;
        Status.Padding = new Padding(0, 6, 0, 0);

        Controls.Add(Body);
        Controls.Add(Status);
        Controls.Add(Buttons);
        Controls.Add(header);
        HandleCreated += (_, _) => { Theme.StyleWindow(this); Loc.Apply(this); };
    }

    protected void AddRow(Control c, bool fill = false)
    {
        Body.RowCount++;
        Body.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        c.Dock = DockStyle.Fill;
        Body.Controls.Add(c, 0, Body.RowCount - 1);
    }

    protected static DataGridView Grid(params (string Name, string Header, int Weight)[] columns)
    {
        var g = new DataGridView();
        Theme.StyleGrid(g);
        g.ReadOnly = true;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (var (name, header, weight) in columns)
        {
            var i = g.Columns.Add(name, header);
            g.Columns[i].FillWeight = weight;
        }
        return g;
    }

    protected bool Ask(string message, bool danger = false)
        => Loc.Show(this, message, Text, MessageBoxButtons.YesNo, danger ? MessageBoxIcon.Warning : MessageBoxIcon.Question) == DialogResult.Yes;

    protected void Tell(string message, bool warning = false)
        => Loc.Show(this, message, Text, MessageBoxButtons.OK, warning ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
}

// ═══════════════════════ Disk speed ═══════════════════════

internal sealed class DiskSpeedDialog : ToolDialog
{
    private readonly ComboBox _drive = Theme.Combo();
    private readonly ComboBox _size = Theme.Combo("256 MB", "1 GB", "4 GB");
    private readonly ProgressLine _progress = new() { Height = 4, Margin = new Padding(0, 6, 0, 6) };
    private readonly ResultTile _seqRead = new("Sequential read"), _seqWrite = new("Sequential write"), _rndRead = new("Random 4K read"), _rndWrite = new("Random 4K write");
    private CancellationTokenSource? _cts;

    public DiskSpeedDialog() : base("", "Disk speed test",
        "Reads and writes a temporary file directly on the drive (Windows' cache is bypassed). Close other programs for accurate results.", new Size(760, 470))
    {
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady))
            _drive.Items.Add(d.RootDirectory.FullName);
        if (_drive.Items.Count > 0) _drive.SelectedIndex = 0;
        _size.SelectedIndex = 1;
        _drive.Width = 120;
        _size.Width = 110;

        var start = Theme.Button("Start", async (s, _) => await RunAsync((FlatBtn)s!), primary: true, glyph: "");
        var stop = Theme.Button("Stop", (_, _) => _cts?.Cancel());
        AddRow(Theme.Row(Theme.Label("Drive", Theme.Body, Theme.Muted), _drive, Theme.Label("Test size", Theme.Body, Theme.Muted), _size, start, stop));
        AddRow(_progress);
        var tiles = new TableLayoutPanel { ColumnCount = 4, Height = 150, BackColor = Color.Transparent };
        for (int i = 0; i < 4; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        foreach (var (t, i) in new[] { _seqRead, _seqWrite, _rndRead, _rndWrite }.Select((t, i) => (t, i)))
        {
            t.Dock = DockStyle.Fill;
            t.Margin = new Padding(i == 0 ? 0 : 6, 0, i == 3 ? 0 : 6, 0);
            tiles.Controls.Add(t, i, 0);
        }
        AddRow(tiles);
        FormClosing += (_, e) => { if (_cts is not null) { _cts.Cancel(); e.Cancel = true; } };
    }

    private async Task RunAsync(FlatBtn start)
    {
        if (_cts is not null || _drive.SelectedItem is not string drive) return;
        var mb = _size.SelectedIndex switch { 0 => 256, 2 => 4096, _ => 1024 };
        _cts = new CancellationTokenSource();
        start.Enabled = false;
        foreach (var t in new[] { _seqRead, _seqWrite, _rndRead, _rndWrite }) t.Set(null, null);
        try
        {
            var r = await DiskBench.RunAsync(drive, mb, s => BeginInvoke(() => Status.Text = Loc.T(s)),
                (v, max) => BeginInvoke(() => _progress.Value = v / (double)max), _cts.Token);
            _seqRead.Set(r.SeqReadMBs, null);
            _seqWrite.Set(r.SeqWriteMBs, null);
            _rndRead.Set(r.RandReadMBs, r.RandReadIops);
            _rndWrite.Set(r.RandWriteMBs, r.RandWriteIops);
            Status.Text = Loc.T("Done. The temporary file was deleted.");
        }
        catch (OperationCanceledException) { Status.Text = Loc.T("Stopped."); }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            start.Enabled = true;
            _progress.Value = 0;
        }
    }

    private sealed class ResultTile : Control
    {
        private static readonly Font Big = new("Segoe UI Semibold", 22f);
        private double? _mbs, _iops;

        public ResultTile(string title)
        {
            Text = title;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void Set(double? mbs, double? iops) { _mbs = mbs; _iops = iops; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Theme.Background);
            using (var path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            {
                using var b = new SolidBrush(Theme.Card);
                g.FillPath(b, path);
                using var pen = new Pen(Theme.Border);
                g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Loc.T(Text), Theme.Small, new Rectangle(14, 14, Width - 28, 18), Theme.Muted, TextFormatFlags.NoPrefix);
            var value = _mbs is { } v ? (v >= 100 ? v.ToString("N0") : v.ToString("0.0")) : "—";
            TextRenderer.DrawText(g, value, Big, new Rectangle(12, 40, Width - 24, 44), Theme.Text, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "MB/s", Theme.Small, new Rectangle(14, 86, Width - 28, 18), Theme.Muted, TextFormatFlags.NoPrefix);
            if (_iops is { } iops)
                TextRenderer.DrawText(g, $"{iops:N0} IOPS", Theme.Small, new Rectangle(14, 108, Width - 28, 18), Theme.Muted, TextFormatFlags.NoPrefix);
        }
    }
}

// ═══════════════════════ Locked files ═══════════════════════

internal sealed class LockedFileDialog : ToolDialog
{
    private readonly DataGridView _grid = Grid(("name", "Program", 140), ("pid", "PID", 40), ("path", "Location", 260));
    private readonly Label _target = Theme.Label("", Theme.BodyBold);
    private List<string> _files = [];
    private string _chosen = "";

    public LockedFileDialog() : base("", "Locked file finder",
        "Find out which program is using a file or folder (\"The action can't be completed because the file is open\").", new Size(860, 560))
    {
        AddRow(Theme.Row(
            Theme.Button("Choose file", (_, _) => PickFile(), primary: true, glyph: ""),
            Theme.Button("Choose folder", (_, _) => PickFolder(), glyph: ""),
            Theme.Button("Refresh", (_, _) => Scan(), glyph: "")));
        AddRow(_target);
        Theme.EmptyState(_grid, "Choose a file or folder to see which programs are using it.");
        AddRow(Theme.InCard(_grid), fill: true);
        AddRow(Theme.Row(
            Theme.Button("Close selected program", (_, _) => CloseSelected(), glyph: ""),
            Theme.Button("Delete at next restart", (_, _) => DeleteOnRestart(), glyph: "")));
    }

    private void PickFile()
    {
        using var dlg = new OpenFileDialog { Title = Loc.T("Choose the locked file"), DereferenceLinks = false };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _chosen = dlg.FileName;
        _files = [dlg.FileName];
        Scan();
    }

    private void PickFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = Loc.T("Choose the locked folder"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _chosen = dlg.SelectedPath;
        try { _files = Directory.EnumerateFiles(dlg.SelectedPath, "*", SafePath.NoLinks(recursive: true)).Take(5000).ToList(); }
        catch (Exception ex) { Tell(ex.Message, warning: true); return; }
        Scan();
    }

    private void Scan()
    {
        if (_files.Count == 0) return;
        _target.Text = _chosen;
        _grid.Rows.Clear();
        try
        {
            var procs = LockFinder.Find(_files);
            foreach (var p in procs)
            {
                var i = _grid.Rows.Add(p.Name + (p.IsService ? " (service)" : ""), p.Pid, p.Path ?? "");
                _grid.Rows[i].Tag = p;
            }
            Status.Text = procs.Count == 0 ? Loc.T("No program is using it right now. If it still can't be deleted, try 'Delete at next restart'.")
                : Loc.T($"{procs.Count} program(s) are using it.");
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
        }
    }

    private void CloseSelected()
    {
        if (_grid.CurrentRow?.Tag is not LockingProcess p) return;
        if (p.IsService)
        {
            // Services often share one process (svchost) with other services: killing it stops them all.
            Tell("This is a Windows service, often sharing its process with other services, so WinSolve won't close it. Use 'Delete at next restart' instead.", warning: true);
            return;
        }
        if (!Ask($"Close {p.Name}? Unsaved work in it will be lost.", danger: true)) return;
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(p.Pid);
            proc.Kill(entireProcessTree: false);
            proc.WaitForExit(5000);
        }
        catch (Exception ex) { Tell(ex.Message, warning: true); }
        Scan();
    }

    private void DeleteOnRestart()
    {
        if (_chosen.Length == 0 || !File.Exists(_chosen))
        {
            Tell("Choose a single file first (folders are deleted file by file).");
            return;
        }
        if (SafePath.IsProtectedFolder(Path.GetDirectoryName(_chosen)!) &&
            !SafePath.IsSameOrInside(_chosen, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
        {
            Tell("This file is in a system or program folder; WinSolve won't delete it.", warning: true);
            return;
        }
        if (!Ask($"Delete this file the next time Windows starts?\n\n{_chosen}", danger: true)) return;
        Tell(LockFinder.DeleteOnRestart(_chosen) ? "It will be deleted when you restart the PC." : "Windows refused to schedule the deletion.");
    }
}

// ═══════════════════════ Secure delete ═══════════════════════

internal sealed class ShredDialog : ToolDialog
{
    private readonly ListBox _list = new() { BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.Body, IntegralHeight = false };
    private readonly ProgressLine _progress = new() { Height = 4, Margin = new Padding(0, 6, 0, 0) };
    private readonly FlatBtn _shred;
    private CancellationTokenSource? _cts;

    public ShredDialog() : base("", "Secure delete",
        "Overwrites files with random data, then deletes them. They don't go to the Recycle Bin and can't be undone.", new Size(800, 540))
    {
        _shred = Theme.Button("Shred", async (_, _) => await ShredAsync(), primary: true, glyph: "");
        AddRow(Theme.Row(
            Theme.Button("Add files", (_, _) => AddFiles(), glyph: ""),
            Theme.Button("Add folder", (_, _) => AddFolder(), glyph: ""),
            Theme.Button("Remove", (_, _) => { if (_list.SelectedItem is { } s) _list.Items.Remove(s); }),
            _shred));
        AddRow(Theme.InCard(_list, 8), fill: true);
        AddRow(_progress);
        AddRow(Theme.Paragraph("On SSDs and USB flash drives the drive itself may keep old copies of the data (wear leveling), so no tool can guarantee erasure there. For a whole SSD, use the manufacturer's Secure Erase.", Theme.Muted, Theme.Small));
        FormClosing += (_, e) => { if (_cts is not null) e.Cancel = true; };
    }

    private void AddFiles()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = Loc.T("Files to shred"), DereferenceLinks = false };
        if (dlg.ShowDialog(this) == DialogResult.OK) foreach (var f in dlg.FileNames) if (!_list.Items.Contains(f)) _list.Items.Add(f);
    }

    private void AddFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = Loc.T("Folder to shred"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) == DialogResult.OK && !_list.Items.Contains(dlg.SelectedPath)) _list.Items.Add(dlg.SelectedPath);
    }

    private async Task ShredAsync()
    {
        var paths = _list.Items.Cast<string>().ToList();
        if (paths.Count == 0 || _cts is not null) return;
        if (!Ask($"Permanently shred {paths.Count} item(s)? This cannot be undone.", danger: true)) return;
        _cts = new CancellationTokenSource();
        _shred.Enabled = false;
        var errors = new List<string>();
        try
        {
            var done = await Task.Run(() => SecureDelete.Shred(paths, errors.Add,
                (v, max) => BeginInvoke(() => _progress.Value = v / (double)Math.Max(1, max)), _cts.Token));
            _list.Items.Clear();
            Status.Text = Loc.T($"{done} file(s) shredded.") + (errors.Count > 0 ? "  " + string.Join("  ", errors.Take(3)) : "");
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _shred.Enabled = true;
        }
    }
}

// ═══════════════════════ Network ═══════════════════════

internal sealed class NetworkDialog : ToolDialog
{
    private readonly DataGridView _adapters = Grid(("name", "Adapter", 150), ("type", "Type", 80), ("ip", "IPv4", 100), ("gw", "Gateway", 100), ("dns", "DNS", 140), ("speed", "Link", 70));
    private readonly DataGridView _results = Grid(("what", "Test", 150), ("value", "Result", 250));
    private readonly FlatBtn _run;
    private CancellationTokenSource? _cts;

    public NetworkDialog() : base("", "Network test",
        "Latency to your router and the internet, download and upload speed and your public IP. The speed test uses Cloudflare's servers.", new Size(900, 620))
    {
        _run = Theme.Button("Run test", async (_, _) => await RunAsync(), primary: true, glyph: "");
        AddRow(Theme.Row(_run));
        AddRow(Theme.Label("Results", Theme.BodyBold));
        AddRow(Theme.InCard(_results), fill: true);
        AddRow(Theme.Label("Adapters", Theme.BodyBold));
        var adapters = Theme.InCard(_adapters);
        adapters.Height = 150;
        AddRow(adapters);
        Theme.EmptyState(_results, "Select Run test.");
        Shown += (_, _) => FillAdapters();
        // Closing the window stops the test instead of leaving downloads running in the background.
        FormClosing += (_, _) => _cts?.Cancel();
    }

    private void FillAdapters()
    {
        _adapters.Rows.Clear();
        foreach (var a in NetworkTest.Adapters()) _adapters.Rows.Add(a.Name, a.Type, a.IPv4, a.Gateway, a.Dns, a.Speed);
    }

    private async Task RunAsync()
    {
        _run.Enabled = false;
        _results.Rows.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        void Row(string what, string value) => _results.Rows.Add(Loc.T(what), value);
        try
        {
            var gateway = NetworkTest.Adapters().FirstOrDefault(a => a.Gateway != "—")?.Gateway;
            var targets = new List<(string Host, string Label)>();
            if (gateway is not null) targets.Add((gateway, "Router"));
            targets.Add(("1.1.1.1", "Cloudflare"));
            targets.Add(("8.8.8.8", "Google"));
            foreach (var (host, label) in targets)
            {
                Status.Text = Loc.T($"Pinging {label}...");
                var p = await NetworkTest.PingAsync(host, label, 10, ct);
                Row($"Ping {label}", p.AvgMs is { } avg ? $"{avg:0} ms  ·  jitter {p.JitterMs:0} ms  ·  {p.LossPercent}% lost" : "No reply");
            }
            Status.Text = Loc.T("Measuring download speed...");
            var down = await NetworkTest.DownloadMbpsAsync(ct);
            Row("Download", $"{down:0.0} Mbps");
            Status.Text = Loc.T("Measuring upload speed...");
            Row("Upload", $"{await NetworkTest.UploadMbpsAsync(down, ct):0.0} Mbps");
            var (ip, country) = await NetworkTest.PublicIpAsync(ct);
            Row("Public IP", $"{ip}  ·  {country}");
            Status.Text = Loc.T("Done.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // window closed
        }
        catch (Exception ex)
        {
            Status.Text = Loc.T("The test could not finish: ") + ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            if (!IsDisposed) _run.Enabled = true;
        }
    }
}

// ═══════════════════════ Wi-Fi passwords ═══════════════════════

internal sealed class WifiDialog : ToolDialog
{
    private readonly DataGridView _grid = Grid(("name", "Network", 200), ("sec", "Security", 120), ("pass", "Password", 200));
    private readonly CheckBox _show = Theme.Toggle("Show passwords", false);
    private List<WifiProfile> _profiles = [];

    public WifiDialog() : base("", "Saved Wi-Fi passwords",
        "Wi-Fi networks this PC has connected to, with their passwords. Handy to connect a new device.", new Size(760, 520))
    {
        _show.CheckedChanged += (_, _) => Fill();
        AddRow(Theme.Row(_show, Theme.Button("Copy password", (_, _) => Copy(), glyph: "")));
        Theme.EmptyState(_grid, "No saved Wi-Fi networks.");
        AddRow(Theme.InCard(_grid), fill: true);
        Shown += (_, _) =>
        {
            try { _profiles = WifiPasswords.Read(); Fill(); Status.Text = Loc.T($"{_profiles.Count} saved network(s)."); }
            catch (Exception ex) { Status.Text = ex.Message; }
        };
    }

    private void Fill()
    {
        _grid.Rows.Clear();
        foreach (var p in _profiles)
        {
            var pass = p.Password is null ? "—" : _show.Checked ? p.Password : new string('•', 10);
            var i = _grid.Rows.Add(p.Name, p.Security, pass);
            _grid.Rows[i].Tag = p;
        }
    }

    private void Copy()
    {
        if (_grid.CurrentRow?.Tag is WifiProfile { Password: { Length: > 0 } pw } p)
        {
            Clipboard.SetText(pw);
            Status.Text = Loc.T($"Password of {p.Name} copied.");
        }
    }
}

// ═══════════════════════ Hosts ═══════════════════════

internal sealed class HostsDialog : ToolDialog
{
    private readonly TextBox _text = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, AcceptsReturn = true, AcceptsTab = true,
        BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Text, Font = new Font("Consolas", 10f),
    };
    private readonly TextBox _site = Theme.TextBox("example.com");

    public HostsDialog() : base("", "Hosts file editor",
        "Lines in the hosts file override DNS. '0.0.0.0 site.com' blocks a site on this PC. A backup is saved every time.", new Size(860, 600))
    {
        _site.Width = 260;
        AddRow(Theme.Row(Theme.Label("Block a website", Theme.Body, Theme.Muted), _site, Theme.Button("Add", (_, _) => Block(), glyph: "")));
        AddRow(Theme.InCard(_text, 8), fill: true);
        Buttons.Controls.Add(Theme.Button("Save", async (_, _) => await SaveAsync(), primary: true, glyph: ""));
        Buttons.Controls.Add(Theme.Button("Restore Windows default", (_, _) =>
        {
            if (Ask("Replace the content with the Windows default? (Save to apply.)")) _text.Text = HostsFile.WindowsDefault.ReplaceLineEndings("\r\n");
        }));
        Shown += (_, _) =>
        {
            try { _text.Text = HostsFile.Read().Replace("\r\n", "\n").Replace("\n", "\r\n"); }
            catch (Exception ex) { Status.Text = ex.Message; }
        };
    }

    private void Block()
    {
        try
        {
            var lines = HostsFile.BlockLines(_site.Text);
            if (!_text.Text.EndsWith("\r\n") && _text.Text.Length > 0) _text.AppendText("\r\n");
            _text.AppendText(lines);
            _site.Clear();
            Status.Text = Loc.T("Added. Select Save to apply.");
        }
        catch (Exception ex) { Status.Text = Loc.T(ex.Message); }
    }

    private async Task SaveAsync()
    {
        try
        {
            var backup = await HostsFile.SaveAsync(_text.Text);
            Status.Text = Loc.T($"Saved. Backup: {backup}");
        }
        catch (Exception ex)
        {
            Tell(Loc.T("Could not save the hosts file (an antivirus may be protecting it): ") + ex.Message, warning: true);
        }
    }
}

// ═══════════════════════ Right-click menu ═══════════════════════

internal sealed class ContextMenuDialog : ToolDialog
{
    private readonly DataGridView _grid = new();
    private bool _filling;

    public ContextMenuDialog() : base("", "Right-click menu cleanup",
        "Entries that programs added to the right-click menu (\"Show more options\" on Windows 11). Turning one off hides it; turn it back on any time.", new Size(900, 600))
    {
        Theme.StyleGrid(_grid);
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "On", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Entry", ReadOnly = true, FillWeight = 200 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "where", HeaderText = "Shows on", ReadOnly = true, FillWeight = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "src", HeaderText = "Program", ReadOnly = true, FillWeight = 120 });
        _grid.CellContentClick += (_, e) => { if (e.ColumnIndex == 0) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_filling || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            var row = _grid.Rows[e.RowIndex];
            var entry = (ContextMenuEntry)row.Tag!;
            try
            {
                ContextMenuItems.SetEnabled(entry, row.Cells[0].Value is true);
                Status.Text = Loc.T("Changed. Restart Explorer to see it.");
            }
            catch (Exception ex)
            {
                Tell(ex.Message, warning: true);
                _filling = true;
                row.Cells[0].Value = entry.Enabled;
                _filling = false;
            }
        };
        Theme.EmptyState(_grid, "No entries added by other programs.");
        AddRow(Theme.InCard(_grid), fill: true);
        Buttons.Controls.Add(Theme.Button("Restart Explorer", async (_, _) =>
            await TaskCatalog.ExplorerRestart(new TaskContext(Logger.Write, default)), glyph: ""));
        Shown += (_, _) => Fill();
    }

    private void Fill()
    {
        _filling = true;
        _grid.Rows.Clear();
        try
        {
            foreach (var e in ContextMenuItems.List())
            {
                var i = _grid.Rows.Add(e.Enabled, e.Name, Loc.T(e.Where), e.Source);
                _grid.Rows[i].Tag = e;
            }
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        _filling = false;
    }
}

// ═══════════════════════ Restore points ═══════════════════════

internal sealed class RestorePointsDialog : ToolDialog
{
    private readonly DataGridView _grid = Grid(("date", "Created", 110), ("desc", "Description", 260), ("type", "Type", 110));

    public RestorePointsDialog() : base("", "Restore points",
        "Restore points let Windows go back to an earlier state of system files, drivers and settings (your documents are not affected).", new Size(840, 560))
    {
        AddRow(Theme.Row(
            Theme.Button("Create restore point", async (_, _) => await CreateAsync(), primary: true, glyph: ""),
            Theme.Button("Delete selected", (_, _) => Delete(), glyph: ""),
            Theme.Button("Open System Restore", (_, _) => ProcessRunner.ShellOpen(Path.Combine(Environment.SystemDirectory, "rstrui.exe")), glyph: "")));
        Theme.EmptyState(_grid, "No restore points. System Protection may be turned off.");
        AddRow(Theme.InCard(_grid), fill: true);
        Shown += (_, _) => Fill();
    }

    private void Fill()
    {
        _grid.Rows.Clear();
        foreach (var p in RestorePoints.List())
        {
            var i = _grid.Rows.Add(p.Created.ToString("g"), p.Description, Loc.T(p.Type));
            _grid.Rows[i].Tag = p;
        }
        Status.Text = Loc.T($"{_grid.Rows.Count} restore point(s).");
    }

    private async Task CreateAsync()
    {
        var ok = false;
        RunDialog.Run(this, "Creating restore point", async (log, _, ct) => ok = await TaskCatalog.CreateRestorePoint(new TaskContext(log, ct), "WinSolve - manual restore point"));
        await Task.Delay(500);
        Fill();
        if (!ok) Status.Text = Loc.T("Could not create a restore point (System Protection may be off).");
    }

    private void Delete()
    {
        if (_grid.CurrentRow?.Tag is not RestorePoint p) return;
        if (!Ask($"Delete the restore point from {p.Created:g}?\n\n{p.Description}", danger: true)) return;
        if (!RestorePoints.Delete(p.Sequence)) Tell("Windows could not delete it.", warning: true);
        Fill();
    }
}

// ═══════════════════════ Battery ═══════════════════════

internal sealed class BatteryDialog : ToolDialog
{
    private readonly FlowLayoutPanel _tiles = new() { AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
    private readonly CapacityChart _chart = new() { Height = 170, Margin = new Padding(0, 10, 0, 0) };
    private static readonly Font ValueFont = new("Segoe UI Semibold", 20f);

    public BatteryDialog() : base("\uEBA7", "Battery report",
        "Battery wear compared with when it was new, from Windows' own battery report.", new Size(760, 520))
    {
        Buttons.Controls.Add(Theme.Button("Full Windows report", async (_, _) => await OpenFullAsync(), glyph: "\uE8A5"));
        AddRow(_tiles);
        AddRow(_chart, fill: true);
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Status.Text = Loc.T("Reading the battery report...");
        try
        {
            var batteries = await BatteryReport.ReadAsync();
            if (batteries.Count == 0)
            {
                Status.Text = Loc.T("This PC has no battery.");
                return;
            }
            var b = batteries[0];
            Tile("Health", $"{b.HealthPercent:0}%", string.Format(Loc.T("{0} of {1} mWh"), b.FullChargeCapacity.ToString("N0"), b.DesignCapacity.ToString("N0")),
                b.HealthPercent >= 80 ? Theme.Good : b.HealthPercent >= 60 ? Theme.Warn : Theme.Bad);
            Tile("Charge cycles", b.CycleCount?.ToString("N0") ?? "—", Loc.T("Most batteries are rated for 300-1000"), null);
            Tile("Lasts now", Runtime(b.RuntimeNow), Loc.T("Estimated at full charge"), null);
            Tile("When new", Runtime(b.RuntimeWhenNew), b.Chemistry, null);
            _chart.SetHistory(b.History);
            Status.Text = Loc.T(b.HealthPercent >= 80 ? "The battery is in good shape."
                : b.HealthPercent >= 60 ? "The battery has lost a noticeable part of its capacity."
                : "The battery is worn out. Consider replacing it.");
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
        }
    }

    private static string Runtime(TimeSpan? t) => t is { } v && v > TimeSpan.Zero ? $"{(int)v.TotalHours} h {v.Minutes:00} min" : "—";

    private void Tile(string title, string value, string detail, Color? color)
    {
        var card = new Card { Width = 168, Height = 112, Margin = new Padding(0, 0, 10, 0), Padding = new Padding(14, 10, 14, 10) };
        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        stack.Controls.Add(Theme.Label(title, Theme.Small, Theme.Muted));
        stack.Controls.Add(Theme.Label(value, ValueFont, color ?? Theme.Text));
        var d = Theme.Label(detail, Theme.Small, Theme.Muted);
        d.MaximumSize = new Size(140, 0);
        stack.Controls.Add(d);
        card.Controls.Add(stack);
        _tiles.Controls.Add(card);
        Loc.Apply(card);
    }

    private async Task OpenFullAsync()
    {
        try { ProcessRunner.ShellOpen(await BatteryReport.SaveHtmlAsync()); }
        catch (Exception ex) { Tell(ex.Message, warning: true); }
    }

    /// <summary>Full-charge capacity over time, as a share of the design capacity.</summary>
    private sealed class CapacityChart : Control
    {
        private List<BatteryCapacityPoint> _points = [];

        public CapacityChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void SetHistory(List<BatteryCapacityPoint> points)
        {
            _points = points.Where(p => p.Design > 0).OrderBy(p => p.Date).ToList();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Theme.Background);
            using (var path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            {
                using var b = new SolidBrush(Theme.Card);
                g.FillPath(b, path);
            }
            TextRenderer.DrawText(g, Loc.T("Capacity over time"), Theme.BodyBold, new Point(14, 10), Theme.Text);
            if (_points.Count < 2)
            {
                TextRenderer.DrawText(g, Loc.T("Windows hasn't recorded enough history yet."), Theme.Small, new Rectangle(0, 0, Width, Height), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            var area = new Rectangle(48, 40, Width - 64, Height - 66);
            const double min = 40, max = 110;
            float Y(double pct) => area.Bottom - (float)((Math.Clamp(pct, min, max) - min) / (max - min) * area.Height);
            using (var grid = new Pen(Theme.Divider))
            {
                foreach (var pct in new[] { 60, 80, 100 })
                {
                    g.DrawLine(grid, area.Left, Y(pct), area.Right, Y(pct));
                    TextRenderer.DrawText(g, $"{pct}%", Theme.Small, new Rectangle(4, (int)Y(pct) - 8, 40, 16), Theme.Muted, TextFormatFlags.Right);
                }
            }
            var t0 = _points[0].Date.Ticks;
            var span = Math.Max(1, _points[^1].Date.Ticks - t0);
            var pts = _points.Select(p => new PointF(area.Left + (float)((p.Date.Ticks - t0) / (double)span * area.Width),
                Y(p.FullCharge * 100.0 / p.Design))).ToArray();
            using (var pen = new Pen(Theme.Accent, 2f)) g.DrawLines(pen, pts);
            TextRenderer.DrawText(g, _points[0].Date.ToString("d"), Theme.Small, new Point(area.Left, area.Bottom + 6), Theme.Muted);
            var last = _points[^1].Date.ToString("d");
            var w = TextRenderer.MeasureText(last, Theme.Small).Width;
            TextRenderer.DrawText(g, last, Theme.Small, new Point(area.Right - w, area.Bottom + 6), Theme.Muted);
        }
    }
}
